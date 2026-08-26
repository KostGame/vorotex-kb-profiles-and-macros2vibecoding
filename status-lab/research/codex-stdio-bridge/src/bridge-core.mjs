const REQUEST_METHODS = new Set([
  'item/commandExecution/requestApproval',
  'item/fileChange/requestApproval'
]);

const RESPONSE_METHODS = new Set([
  'item/commandExecution/respondApproval',
  'item/fileChange/respondApproval'
]);

const DECISIONS = new Set(['accept', 'acceptForSession', 'decline', 'cancel']);
const MAX_PENDING = 256;
const MAX_PARTIAL_BYTES = 64 * 1024;

function optionalString(value) {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function familyOf(method) {
  return method.split('/').slice(0, 2).join('/');
}

/**
 * Observes fixture-defined JSONL metadata without retaining raw JSON records.
 * The partial buffer is transient stream framing only; it is bounded and never
 * passed to the telemetry sink or written to disk.
 */
export class ApprovalObserver {
  #pending = new Map();
  #partial = Buffer.alloc(0);
  #sink;
  #sinkBusy = false;

  constructor({ telemetrySink = () => {} } = {}) {
    this.#sink = telemetrySink;
  }

  observeServerChunk(chunk) {
    this.#observeChunk(chunk, (message) => this.#trackRequest(message));
  }

  observeClientChunk(chunk) {
    this.#observeChunk(chunk, (message) => this.#resolveResponse(message));
  }

  pendingCount() {
    return this.#pending.size;
  }

  #observeChunk(chunk, handle) {
    const input = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    const combined = this.#partial.length === 0 ? input : Buffer.concat([this.#partial, input]);
    let start = 0;

    for (let index = 0; index < combined.length; index += 1) {
      if (combined[index] !== 0x0a) continue;
      this.#parseLine(combined.subarray(start, index), handle);
      start = index + 1;
    }

    const remainder = combined.subarray(start);
    // An oversize/incomplete record is intentionally unobservable, never a
    // transport error. Forwarding is handled separately by pipe().
    this.#partial = remainder.length > MAX_PARTIAL_BYTES ? Buffer.alloc(0) : Buffer.from(remainder);
  }

  #parseLine(line, handle) {
    try {
      handle(JSON.parse(line.toString('utf8')));
    } catch {
      // Non-JSON fixture traffic remains transparent and produces no telemetry.
    }
  }

  #trackRequest(message) {
    if (!message || !REQUEST_METHODS.has(message.method)) return;
    const params = message.params;
    const requestId = optionalString(params?.requestId);
    if (!requestId) return;

    if (this.#pending.size >= MAX_PENDING && !this.#pending.has(requestId)) return;
    const request = {
      requestId,
      requestType: message.method,
      timestampUtc: new Date().toISOString()
    };
    for (const key of ['threadId', 'turnId', 'itemId']) {
      const value = optionalString(params[key]);
      if (value) request[key] = value;
    }
    this.#pending.set(requestId, request);
  }

  #resolveResponse(message) {
    if (!message || !RESPONSE_METHODS.has(message.method)) return;
    const params = message.params;
    const requestId = optionalString(params?.requestId);
    const decision = optionalString(params?.decision);
    const request = requestId ? this.#pending.get(requestId) : undefined;
    if (!request || !decision || !DECISIONS.has(decision)) return;
    if (familyOf(request.requestType) !== familyOf(message.method)) return;

    this.#pending.delete(requestId);
    const event = {
      timestampUtc: new Date().toISOString(),
      source: 'codex_stdio_bridge',
      event: 'approval_resolved',
      requestId: request.requestId,
      decision
    };
    for (const key of ['threadId', 'turnId', 'itemId']) {
      if (request[key]) event[key] = request[key];
    }
    this.#emitFailOpen(event);
  }

  #emitFailOpen(event) {
    if (this.#sinkBusy) return;
    this.#sinkBusy = true;
    try {
      const result = this.#sink(event);
      if (result && typeof result.then === 'function') {
        Promise.resolve(result)
          .catch(() => {})
          .finally(() => { this.#sinkBusy = false; });
      } else {
        this.#sinkBusy = false;
      }
    } catch {
      this.#sinkBusy = false;
    }
  }
}

/**
 * Connects already-created fake-process streams. Node's pipe() is the entire
 * transport path, so its native backpressure and close behavior remain intact.
 * Observers receive the same Buffer chunks but never write to either transport.
 */
export function connectTransparentBridge({ clientInput, clientOutput, childInput, childOutput, childStderr, stderrOutput, telemetrySink }) {
  const observer = new ApprovalObserver({ telemetrySink });
  clientInput.on('data', (chunk) => observer.observeClientChunk(chunk));
  childOutput.on('data', (chunk) => observer.observeServerChunk(chunk));
  clientInput.pipe(childInput);
  childOutput.pipe(clientOutput);
  if (childStderr && stderrOutput) childStderr.pipe(stderrOutput);
  return observer;
}

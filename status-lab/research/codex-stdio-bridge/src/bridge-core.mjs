import { appendFile as appendFileAsync, mkdir as mkdirAsync } from 'node:fs/promises';
import path from 'node:path';

const REQUEST_FAMILIES = new Map([
  ['item/commandExecution/requestApproval', 'item/commandExecution'],
  ['item/fileChange/requestApproval', 'item/fileChange']
]);

const DECISIONS = new Set(['accept', 'acceptForSession', 'decline', 'cancel']);
const MAX_PENDING = 256;
const MAX_PARTIAL_BYTES = 64 * 1024;
const MAX_FIELD_BYTES = 1024;
export const APPROVAL_SCHEMA_VERSION = 'k15-codex-approval/v1';
export const COMPLETION_SCHEMA_VERSION = 'k15-codex-completion/v1';
const COMPLETION_STATUSES = new Set(['completed', 'interrupted', 'failed']);

function optionalString(value) {
  return typeof value === 'string' && value.length > 0 && Buffer.byteLength(value, 'utf8') <= MAX_FIELD_BYTES
    ? value
    : undefined;
}

function rpcId(value) {
  if (typeof value === 'string') {
    return value.length > 0 && Buffer.byteLength(value, 'utf8') <= MAX_FIELD_BYTES
      ? { type: 'string', value }
      : undefined;
  }
  if (typeof value === 'number' && Number.isSafeInteger(value)) {
    return { type: 'number', value: Object.is(value, -0) ? '-0' : String(value) };
  }
  return undefined;
}

function pendingKey(family, id, metadata = {}) {
  return JSON.stringify([
    family,
    id.type,
    id.value,
    metadata.threadId ?? '',
    metadata.turnId ?? '',
    metadata.itemId ?? ''
  ]);
}

/**
 * Observes the proven live JSON-RPC approval shape without retaining raw JSON
 * records. Requests use method + top-level id; responses use the same id and
 * result.decision, with no response method.
 * The partial buffer is transient stream framing only; it is bounded and never
 * passed to the telemetry sink or written to disk.
 */
export class ApprovalObserver {
  #pending = new Map();
  #serverPartial = Buffer.alloc(0);
  #clientPartial = Buffer.alloc(0);
  #sink;
  #sinkBusy = false;

  constructor({ telemetrySink = () => {} } = {}) {
    this.#sink = telemetrySink;
  }

  observeServerChunk(chunk) {
    this.#observeChunk(chunk, 'server', (message) => {
      this.#trackRequest(message);
      this.#trackCompletion(message);
    });
  }

  observeClientChunk(chunk) {
    this.#observeChunk(chunk, 'client', (message) => this.#resolveResponse(message));
  }

  pendingCount() {
    return this.#pending.size;
  }

  #observeChunk(chunk, direction, handle) {
    const input = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    const partial = direction === 'server' ? this.#serverPartial : this.#clientPartial;
    const combined = partial.length === 0 ? input : Buffer.concat([partial, input]);
    let start = 0;

    for (let index = 0; index < combined.length; index += 1) {
      if (combined[index] !== 0x0a) continue;
      this.#parseLine(combined.subarray(start, index), handle);
      start = index + 1;
    }

    const remainder = combined.subarray(start);
    // An oversize/incomplete record is intentionally unobservable, never a
    // transport error. Forwarding is handled separately by pipe().
    const nextPartial = remainder.length > MAX_PARTIAL_BYTES ? Buffer.alloc(0) : Buffer.from(remainder);
    if (direction === 'server') this.#serverPartial = nextPartial;
    else this.#clientPartial = nextPartial;
  }

  #parseLine(line, handle) {
    if (line.length > MAX_PARTIAL_BYTES) return;
    try {
      handle(JSON.parse(line.toString('utf8')));
    } catch {
      // Non-JSON fixture traffic remains transparent and produces no telemetry.
    }
  }

  #trackRequest(message) {
    const family = REQUEST_FAMILIES.get(message?.method);
    if (!family) return;
    const id = rpcId(message.id);
    if (!id) return;
    const params = message.params;
    if (params !== undefined && (params === null || typeof params !== 'object' || Array.isArray(params))) return;

    const metadata = {};
    for (const key of ['threadId', 'turnId', 'itemId']) {
      const value = optionalString(params?.[key]);
      if (value) metadata[key] = value;
    }
    const key = pendingKey(family, id, metadata);
    if (this.#pending.size >= MAX_PENDING && !this.#pending.has(key)) return;
    // A duplicate request identity must not replace the original correlation
    // metadata with a potentially stale or cross-turn record.
    if (this.#pending.has(key)) return;
    const request = {
      rpcIdType: id.type,
      rpcId: id.value,
      family,
      timestampUtc: new Date().toISOString()
    };
    Object.assign(request, metadata);
    this.#pending.set(key, request);
  }

  #trackCompletion(message) {
    if (!message || typeof message !== 'object' || Array.isArray(message) ||
        message.method !== 'turn/completed' || !message.params ||
        typeof message.params !== 'object' || Array.isArray(message.params)) return;
    const threadId = optionalString(message.params.threadId);
    const turn = message.params.turn;
    const turnId = optionalString(turn?.id);
    const status = optionalString(turn?.status);
    if (!threadId || !turn || typeof turn !== 'object' || Array.isArray(turn) ||
        !turnId || !status || !COMPLETION_STATUSES.has(status)) return;

    this.#emitFailOpen({
      schemaVersion: COMPLETION_SCHEMA_VERSION,
      timestampUtc: new Date().toISOString(),
      source: 'codex_stdio_bridge',
      event: 'turn_completed',
      threadId,
      turnId,
      status
    });
  }

  #resolveResponse(message) {
    if (!message || typeof message !== 'object' || Array.isArray(message) || Object.hasOwn(message, 'method')) return;
    const id = rpcId(message.id);
    const result = message.result;
    const decision = optionalString(result?.decision);
    if (!id || !result || typeof result !== 'object' || Array.isArray(result) || !decision || !DECISIONS.has(decision)) return;

    const requestEntries = [...this.#pending.entries()]
      .filter(([, request]) => request.rpcIdType === id.type && request.rpcId === id.value);
    const request = requestEntries.length === 1 ? requestEntries[0][1] : undefined;
    if (!request || !decision || !DECISIONS.has(decision)) return;

    this.#pending.delete(requestEntries[0][0]);
    const event = {
      schemaVersion: APPROVAL_SCHEMA_VERSION,
      timestampUtc: new Date().toISOString(),
      source: 'codex_stdio_bridge',
      event: 'approval_resolved',
      rpcIdType: request.rpcIdType,
      rpcId: request.rpcId,
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

export function createSanitizedJsonlSink(filePath, { appendFile, makeDirectory } = {}) {
  if (typeof filePath !== 'string' || filePath.length === 0) return () => {};

  const append = appendFile ?? appendFileAsync;
  const mkdir = makeDirectory ?? mkdirAsync;
  let directoryReady;

  return (event) => {
    const sanitized = {
      schemaVersion: event.schemaVersion,
      timestampUtc: event.timestampUtc,
      source: event.source,
      event: event.event,
      decision: event.decision,
      rpcIdType: event.rpcIdType,
      rpcId: event.rpcId
    };
    if (event.event === 'turn_completed') {
      sanitized.schemaVersion = COMPLETION_SCHEMA_VERSION;
      sanitized.threadId = event.threadId;
      sanitized.turnId = event.turnId;
      sanitized.status = event.status;
    }
    for (const key of ['threadId', 'turnId', 'itemId']) {
      if (event[key]) sanitized[key] = event[key];
    }
    const line = JSON.stringify(sanitized) + '\n';
    if (Buffer.byteLength(line, 'utf8') > MAX_PARTIAL_BYTES) return;
    if (!directoryReady) {
      directoryReady = mkdir(path.dirname(filePath), { recursive: true });
    }
    return Promise.resolve(directoryReady).then(() => append(filePath, line, { encoding: 'utf8' }));
  };
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

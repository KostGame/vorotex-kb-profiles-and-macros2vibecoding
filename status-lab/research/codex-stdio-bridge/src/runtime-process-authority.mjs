import net from 'node:net';

export const AUTHORITY_PIPE_NAME = '\\\\.\\pipe\\Vorotex.K15.Runtime.NativeAuthority.v1';
export const AUTHORITY_HEALTH_SCHEMA_VERSION = 'k15-codex-authority-health/v1';
export const AUTHORITY_PIPE_MAX_FRAME_BYTES = 16 * 1024;
export const AUTHORITY_PIPE_QUEUE_CAPACITY = 64;

const STATUS_SCHEMA_VERSION = 'k15-codex-thread-status/v1';
const METADATA_SCHEMA_VERSION = 'k15-codex-thread-metadata/v1';
const STATUS_VALUES = new Set(['notLoaded', 'idle', 'active', 'systemError']);
const FLAG_VALUES = new Set(['waitingOnApproval', 'waitingOnUserInput']);
const CLASSIFICATIONS = new Set(['user', 'service', 'canary']);
const HEALTH_REASONS = new Set([
  'NATIVE_AUTHORITY_DEGRADED_OVERFLOW',
  'NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE',
  'NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE'
]);

function exactKeys(value, keys) {
  return value && typeof value === 'object' && !Array.isArray(value)
    && Object.keys(value).every(key => keys.has(key));
}

function sanitizeStatus(event) {
  const allowed = new Set(['schemaVersion', 'source', 'event', 'threadId', 'status', 'activeFlags', 'timestampUtc', 'classification']);
  if (!exactKeys(event, allowed) || event.schemaVersion !== STATUS_SCHEMA_VERSION
    || event.source !== 'codex_stdio_bridge' || event.event !== 'thread_status_changed'
    || typeof event.threadId !== 'string' || event.threadId.length === 0
    || typeof event.status !== 'string' || !STATUS_VALUES.has(event.status)
    || typeof event.timestampUtc !== 'string' || Buffer.byteLength(event.timestampUtc, 'utf8') > 128
    || (event.classification !== undefined && !CLASSIFICATIONS.has(event.classification))) {
    throw new Error('invalid authority status');
  }
  if (event.status === 'active') {
    if (!Array.isArray(event.activeFlags) || event.activeFlags.length > 8
      || event.activeFlags.some(flag => typeof flag !== 'string' || !FLAG_VALUES.has(flag))) throw new Error('invalid authority flags');
  } else if (event.activeFlags?.length) {
    throw new Error('non-active status has flags');
  }
  return { schemaVersion: event.schemaVersion, source: event.source, event: event.event,
    threadId: event.threadId, status: event.status, activeFlags: event.activeFlags ?? [],
    timestampUtc: event.timestampUtc, ...(event.classification === undefined ? {} : { classification: event.classification }) };
}

function sanitizeMetadata(event) {
  const allowed = new Set(['schemaVersion', 'source', 'event', 'threadId', 'workingDirectory']);
  if (!exactKeys(event, allowed) || event.schemaVersion !== METADATA_SCHEMA_VERSION
    || event.source !== 'codex_stdio_bridge' || event.event !== 'thread_metadata_changed'
    || typeof event.threadId !== 'string' || event.threadId.length === 0
    || typeof event.workingDirectory !== 'string' || event.workingDirectory.length === 0
    || Buffer.byteLength(event.threadId, 'utf8') > 1024 || Buffer.byteLength(event.workingDirectory, 'utf8') > 1024) {
    throw new Error('invalid authority metadata');
  }
  return { schemaVersion: event.schemaVersion, source: event.source, event: event.event,
    threadId: event.threadId, workingDirectory: event.workingDirectory };
}

function lineForEvent(event) {
  const sanitized = event?.schemaVersion === METADATA_SCHEMA_VERSION
    ? sanitizeMetadata(event) : sanitizeStatus(event);
  const line = JSON.stringify(sanitized) + '\n';
  if (Buffer.byteLength(line, 'utf8') > AUTHORITY_PIPE_MAX_FRAME_BYTES) throw new Error('authority frame too large');
  return line;
}

function healthLine(reason) {
  return JSON.stringify({ schemaVersion: AUTHORITY_HEALTH_SCHEMA_VERSION, source: 'codex_stdio_bridge',
    event: 'authority_degraded', reason: HEALTH_REASONS.has(reason) ? reason : 'NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE' }) + '\n';
}

/**
 * Connects to the already-running Runtime-owned authority pipe. This module
 * never imports child_process and cannot create a Runtime process. One pipe
 * connection is held per wrapper; the Runtime server grants ownership to the
 * first connected wrapper and rejects competing producers as pipe-busy.
 */
export function createNamedPipeAuthoritySink({
  pipePath = AUTHORITY_PIPE_NAME,
  connectPipe = path => net.createConnection({ path }),
  queueCapacity = AUTHORITY_PIPE_QUEUE_CAPACITY,
  connectTimeoutMs = 250,
  diagnostics
} = {}) {
  if (typeof pipePath !== 'string' || pipePath.length === 0 || !Number.isInteger(queueCapacity) || queueCapacity <= 0) {
    throw new Error('invalid authority pipe configuration');
  }

  let socket;
  let connecting;
  let pumping = false;
  let closed = false;
  const queue = [];

  const failSocket = () => {
    const current = socket;
    socket = undefined;
    connecting = undefined;
    if (current && !current.destroyed) current.destroy();
  };

  const connect = () => {
    if (socket && !socket.destroyed) return Promise.resolve(socket);
    if (connecting) return connecting;
    diagnostics?.recordPipe('connectAttempts');
    connecting = new Promise((resolve, reject) => {
      let settled = false;
      let candidate;
      try { candidate = connectPipe(pipePath); } catch (error) { reject(error); return; }
      const finish = error => {
        if (settled) return;
        settled = true;
        clearTimeout(timeout);
        if (error) { failSocket(); diagnostics?.recordPipe('connectFailures', error?.message?.includes('timeout') ? 'timeout' : error?.code === 'EBUSY' ? 'busy' : 'unavailable'); reject(error); }
        else { socket = candidate; diagnostics?.recordPipe('connectSuccesses'); resolve(candidate); }
      };
      const timeout = setTimeout(() => finish(new Error('authority pipe connect timeout')), connectTimeoutMs);
      candidate.once?.('connect', () => finish());
      candidate.once?.('error', error => finish(error));
      candidate.once?.('close', () => { if (socket === candidate) failSocket(); });
      if (candidate.readyState === 'open' || candidate.connecting === false) finish();
    }).finally(() => { connecting = undefined; });
    return connecting;
  };

  const pump = async () => {
    if (pumping || closed) return;
    pumping = true;
    while (!closed && queue.length) {
      const item = queue[0];
      try {
        const target = await connect();
        await new Promise((resolve, reject) => {
          try { target.write(item.line, 'utf8', error => error ? reject(error) : resolve()); }
          catch (error) { reject(error); }
        });
        queue.shift(); item.resolve();
      } catch (error) {
        queue.shift(); item.reject(error); diagnostics?.recordAuthority('sinkFailures');
        failSocket();
      }
    }
    pumping = false;
  };

  const sendLine = line => new Promise((resolve, reject) => {
    if (closed || queue.length >= queueCapacity) { diagnostics?.recordAuthority('sinkFailures'); reject(new Error('authority pipe unavailable or full')); return; }
    queue.push({ line, resolve, reject });
    void pump();
  });

  return {
    sink: event => sendLine(lineForEvent(event)),
    markDegraded: reason => { void sendLine(healthLine(reason)).catch(() => {}); },
    close: async () => {
      closed = true;
      const error = new Error('authority sink closed');
      while (queue.length) queue.shift().reject(error);
      failSocket();
    }
  };
}

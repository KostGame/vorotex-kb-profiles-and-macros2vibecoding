import { spawn } from 'node:child_process';
import path from 'node:path';

export const RUNTIME_COMMAND_ENV = 'CODEX_BRIDGE_RUNTIME_COMMAND';
export const RUNTIME_STATUS_STDIN_ENV = 'VOROTEX_K15_RUNTIME_NATIVE_STATUS_STDIN';
export const AUTHORITY_HEALTH_SCHEMA_VERSION = 'k15-codex-authority-health/v1';
const THREAD_METADATA_SCHEMA_VERSION = 'k15-codex-thread-metadata/v1';
const REASONS = new Set([
  'NATIVE_AUTHORITY_DEGRADED_OVERFLOW',
  'NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE',
  'NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE'
]);

function requireAbsoluteCommand(value) {
  if (typeof value !== 'string' || !path.isAbsolute(value))
    throw new Error('runtime command must be absolute');
  return value;
}

function sanitizeEvent(event) {
  if (event?.schemaVersion === THREAD_METADATA_SCHEMA_VERSION && event?.event === 'thread_metadata_changed') {
    if (event.source !== 'codex_stdio_bridge' || typeof event.threadId !== 'string' ||
        typeof event.workingDirectory !== 'string' || event.threadId.length === 0 ||
        event.workingDirectory.length === 0 || Buffer.byteLength(event.threadId, 'utf8') > 1024 ||
        Buffer.byteLength(event.workingDirectory, 'utf8') > 1024) throw new Error('invalid thread metadata event');
    return { schemaVersion: THREAD_METADATA_SCHEMA_VERSION, source: event.source,
      event: event.event, threadId: event.threadId, workingDirectory: event.workingDirectory };
  }
  const sanitized = {
    schemaVersion: event?.schemaVersion,
    source: event?.source,
    event: event?.event,
    threadId: event?.threadId,
    status: event?.status,
    activeFlags: Array.isArray(event?.activeFlags) ? [...event.activeFlags] : [],
    timestampUtc: event?.timestampUtc
  };
  if (event?.classification !== undefined) sanitized.classification = event.classification;
  if (Object.values(sanitized).some(value => value === undefined)) throw new Error('invalid authority event');
  return sanitized;
}

function healthEvent(reason) {
  return JSON.stringify({
    schemaVersion: AUTHORITY_HEALTH_SCHEMA_VERSION,
    source: 'codex_stdio_bridge',
    event: 'authority_degraded',
    reason: REASONS.has(reason) ? reason : 'NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE'
  }) + '\n';
}

/**
 * Explicit opt-in process boundary. It is never constructed unless the
 * caller supplies an absolute runtime command. Runtime stdin is separate
 * from Codex stdin/stdout/stderr and receives only allowlisted JSONL.
 */
export function createRuntimeProcessAuthoritySink({
  commandPath,
  commandArgs = [],
  env = process.env,
  spawnProcess = spawn
} = {}) {
  const child = spawnProcess(requireAbsoluteCommand(commandPath), commandArgs, {
    stdio: ['pipe', 'ignore', 'ignore'],
    env: { ...env, [RUNTIME_STATUS_STDIN_ENV]: '1' },
    windowsHide: true
  });
  let unavailable = false;
  let healthSent = false;
  const sendHealth = (reason) => {
    if (healthSent || unavailable) return;
    healthSent = true;
    try { child.stdin.write(healthEvent(reason)); } catch { /* boundary is already unavailable */ }
  };
  const fail = () => { sendHealth('NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE'); unavailable = true; };
  child.once?.('error', fail);
  child.once?.('close', (code) => { if (code !== 0) fail(); });

  const sink = (event) => new Promise((resolve, reject) => {
    if (unavailable || !child.stdin?.writable) {
      sendHealth('NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE');
      reject(new Error('runtime authority unavailable'));
      return;
    }
    let line;
    try { line = JSON.stringify(sanitizeEvent(event)) + '\n'; } catch (error) { reject(error); return; }
    try {
      child.stdin.write(line, 'utf8', (error) => {
        if (error) { fail(); reject(error); } else resolve();
      });
    } catch (error) { fail(); reject(error); }
  });

  return {
    sink,
    markDegraded: (reason) => sendHealth(reason),
    close: () => new Promise(resolve => {
      if (!child.stdin || child.stdin.destroyed) { resolve(); return; }
      child.once?.('close', resolve);
      child.stdin.end();
    })
  };
}

import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  BridgeDiagnostics, NativeThreadStatusObserver, createSanitizedDiagnosticsSink,
  BRIDGE_DIAGNOSTICS_SCHEMA_VERSION, BRIDGE_DIAGNOSTIC_REJECTION_REASONS
} from '../src/bridge-core.mjs';
import { createNamedPipeAuthoritySink } from '../src/runtime-process-authority.mjs';

const status = (overrides = {}) => JSON.stringify({ method: 'thread/status/changed', params: {
  threadId: 'thread-secret', status: { type: 'active', activeFlags: [] }
}, ...overrides }) + '\n';

test('diagnostic snapshot is bounded, sanitized, and counts framing without changing bytes', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'k15-bridge-diagnostics-'));
  try {
    const target = path.join(root, 'snapshot.json');
    const sink = createSanitizedDiagnosticsSink(target);
    const diagnostics = new BridgeDiagnostics({ sink, generation: '8e5b6932251c2c1c' });
    const observer = new NativeThreadStatusObserver({ diagnostics, authoritySink: async () => {} });
    const raw = status() + '{"prompt":"PRIVATE_PROMPT","model":"PRIVATE_MODEL"}\n';
    const bytes = Buffer.from(raw);
    diagnostics.observeServerChunk(bytes);
    observer.observeServerChunk(bytes);
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setTimeout(resolve, 100));
    const text = await readFile(target, 'utf8');
    const snapshot = JSON.parse(text);
    assert.equal(snapshot.schemaVersion, BRIDGE_DIAGNOSTICS_SCHEMA_VERSION);
    assert.equal(snapshot.generation, '8e5b6932251c2c1c');
    assert.equal(snapshot.serverStdoutChunks, 1);
    assert.equal(snapshot.serverStdoutBytes, bytes.length);
    assert.equal(snapshot.completeRecords, 2);
    assert.equal(snapshot.jsonParseSuccess, 2);
    assert.equal(snapshot.threadStatusSeen, 1);
    assert.equal(snapshot.nativeStatusAccepted, 1);
    assert.equal(snapshot.authorityQueue.accepted, 1);
    assert.equal(snapshot.authorityQueue.delivered, 1);
    assert.doesNotMatch(text, /thread-secret|PRIVATE_PROMPT|PRIVATE_MODEL/);
    assert.equal(Buffer.byteLength(text, 'utf8') < 64 * 1024, true);
  } finally { await new Promise(resolve => setTimeout(resolve, 100)); await rm(root, { recursive: true, force: true }); }
});

test('status rejection counters use only the fixed allowlist and malformed records stay transparent', () => {
  const diagnostics = new BridgeDiagnostics();
  const observer = new NativeThreadStatusObserver({ diagnostics });
  const records = [
    '{"method":"thread/status/changed","params":null}\n',
    status({ params: { threadId: '', status: { type: 'active', activeFlags: [] } } }),
    status({ params: { threadId: 'x', status: { type: 'future', activeFlags: [] } } }),
    status({ params: { threadId: 'x', status: { type: 'active', activeFlags: ['unknown'] } } }),
    status({ emittedAtMs: 'secret' }),
    status({ params: { threadId: 'x', status: { type: 'idle' } } })
  ];
  const bytes = Buffer.from(records.join('') + 'not-json\n');
  diagnostics.observeServerChunk(bytes); observer.observeServerChunk(bytes);
  const snapshot = diagnostics.snapshot();
  assert.equal(snapshot.completeRecords, 7);
  assert.equal(snapshot.jsonParseFailure, 1);
  assert.equal(snapshot.threadStatusSeen, 6);
  assert.deepEqual(Object.keys(snapshot.rejectedStatus).sort(), [...BRIDGE_DIAGNOSTIC_REJECTION_REASONS].sort());
  assert.deepEqual(snapshot.rejectedStatus, {
    invalid_envelope: 1, invalid_thread_id: 1, invalid_status: 1, invalid_active_flags: 1,
    invalid_emitted_at_ms: 1, invalid_classification: 0
  });
  assert.equal(snapshot.nativeStatusAccepted, 1);
});

test('diagnostics remain fail-open on sink errors and named-pipe failures use fixed reasons', async () => {
  const diagnostics = new BridgeDiagnostics({ sink: () => { throw new Error('raw secret must not escape'); } });
  diagnostics.recordAuthority('overflow'); diagnostics.recordAuthority('sinkFailures');
  const connector = () => { const socket = new PassThrough(); process.nextTick(() => socket.emit('error', Object.assign(new Error('private detail'), { code: 'ENOENT' }))); return socket; };
  const boundary = createNamedPipeAuthoritySink({ diagnostics, connectPipe: connector, connectTimeoutMs: 10 });
  await assert.rejects(boundary.sink({ schemaVersion: 'k15-codex-thread-status/v1', source: 'codex_stdio_bridge', event: 'thread_status_changed', threadId: 'x', status: 'idle', activeFlags: [], timestampUtc: new Date().toISOString() }));
  await boundary.close();
  const snapshot = diagnostics.snapshot();
  assert.equal(snapshot.authorityQueue.overflow, 1);
  assert.equal(snapshot.authorityQueue.sinkFailures >= 2, true);
  assert.equal(snapshot.namedPipe.connectAttempts, 1);
  assert.equal(snapshot.namedPipe.connectFailures, 1);
  assert.equal(snapshot.namedPipe.failureReasons.unavailable, 1);
  assert.deepEqual(Object.keys(snapshot.namedPipe.failureReasons).sort(), ['busy', 'timeout', 'unavailable', 'unknown'].sort());
});

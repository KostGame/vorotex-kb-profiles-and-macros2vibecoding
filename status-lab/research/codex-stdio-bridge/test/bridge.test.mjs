import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { ApprovalObserver, connectTransparentBridge } from '../src/bridge-core.mjs';

const request = (method, id, params = {}) => JSON.stringify({ jsonrpc: '2.0', id, method, params });
const commandRequest = (id, extras = {}) => request('item/commandExecution/requestApproval', id, extras);
const fileRequest = (id, extras = {}) => request('item/fileChange/requestApproval', id, extras);
const response = (id, decision, extras = {}) => JSON.stringify({ jsonrpc: '2.0', id, result: { decision, ...extras } });
const legacyResponse = (id, decision) => JSON.stringify({
  method: 'item/commandExecution/respondApproval',
  params: { requestId: id, decision }
});

async function collect(stream) {
  const chunks = [];
  stream.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
  await once(stream, 'end');
  return Buffer.concat(chunks);
}

function observerWith(events, sink = (event) => events.push(event)) {
  return new ApprovalObserver({ telemetrySink: sink });
}

const tick = () => new Promise((resolve) => setImmediate(resolve));

test('transport is byte-transparent in both directions and stderr remains separate', async () => {
  const clientInput = new PassThrough(); const childInput = new PassThrough();
  const childOutput = new PassThrough(); const clientOutput = new PassThrough();
  const childStderr = new PassThrough(); const stderrOutput = new PassThrough();
  connectTransparentBridge({ clientInput, clientOutput, childInput, childOutput, childStderr, stderrOutput, telemetrySink: () => {} });
  const serverBytes = Buffer.from('{"ordinary":true}\nnot-json\n');
  const clientBytes = Buffer.from('{"client":"payload"}\n');
  const stderrBytes = Buffer.from('child diagnostic\n');
  const gotChild = collect(childInput); const gotClient = collect(clientOutput); const gotStderr = collect(stderrOutput);
  clientInput.end(clientBytes); childOutput.end(serverBytes); childStderr.end(stderrBytes);
  assert.deepEqual(await gotChild, clientBytes);
  assert.deepEqual(await gotClient, serverBytes);
  assert.deepEqual(await gotStderr, stderrBytes);
});

test('live numeric approval request and exact result.decision response emit one sanitized event', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(commandRequest(1, {
    threadId: 'T', turnId: 'U', itemId: 'I', command: 'MUST NOT REACH SIDE CHANNEL'
  }) + '\n'));
  observer.observeClientChunk(Buffer.from(response(1, 'accept', { secret: 'MUST NOT REACH SIDE CHANNEL' }) + '\n'));
  await tick();
  assert.equal(events.length, 1);
  assert.deepEqual(Object.keys(events[0]).sort(), [
    'decision', 'event', 'itemId', 'rpcId', 'rpcIdType', 'schemaVersion',
    'source', 'threadId', 'timestampUtc', 'turnId'
  ].sort());
  assert.deepEqual(events[0], {
    schemaVersion: 'k15-codex-approval/v1',
    timestampUtc: events[0].timestampUtc,
    source: 'codex_stdio_bridge',
    event: 'approval_resolved',
    rpcIdType: 'number',
    rpcId: '1',
    decision: 'accept',
    threadId: 'T',
    turnId: 'U',
    itemId: 'I'
  });
  assert.doesNotMatch(JSON.stringify(events[0]), /MUST NOT REACH SIDE CHANNEL/);
  assert.equal(observer.pendingCount(), 0);
});

test('acceptForSession, decline, and cancel remain distinct', async () => {
  const events = []; const observer = observerWith(events);
  for (const [id, decision] of [[2, 'acceptForSession'], [3, 'decline'], [4, 'cancel']]) {
    observer.observeServerChunk(Buffer.from(commandRequest(id) + '\n'));
    observer.observeClientChunk(Buffer.from(response(id, decision) + '\n'));
    await tick();
  }
  assert.deepEqual(events.map((event) => event.decision), ['acceptForSession', 'decline', 'cancel']);
});

test('numeric 1 and string "1" are separate typed correlations', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(commandRequest(1, { threadId: 'numeric' }) + '\n'));
  observer.observeServerChunk(Buffer.from(commandRequest('1', { threadId: 'string' }) + '\n'));
  observer.observeClientChunk(Buffer.from(response('1', 'accept') + '\n' + response(1, 'acceptForSession') + '\n'));
  await tick();
  assert.deepEqual(events.map(({ rpcIdType, rpcId, decision, threadId }) => ({ rpcIdType, rpcId, decision, threadId })), [
    { rpcIdType: 'string', rpcId: '1', decision: 'accept', threadId: 'string' },
    { rpcIdType: 'number', rpcId: '1', decision: 'acceptForSession', threadId: 'numeric' }
  ]);
});

test('same typed id with different request metadata remains ambiguous and fails closed', () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(
    commandRequest(5, { threadId: 'thread-A', turnId: 'turn-A' }) + '\n' +
    commandRequest(5, { threadId: 'thread-B', turnId: 'turn-B' }) + '\n'
  ));
  observer.observeClientChunk(Buffer.from(response(5, 'accept') + '\n'));
  assert.deepEqual(events, []);
  assert.equal(observer.pendingCount(), 2);
});

test('parallel approvals and same-thread concurrent approvals cannot cross-correlate', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(
    commandRequest(10, { threadId: 'thread-A', turnId: 'turn-A' }) + '\n' +
    fileRequest(11, { threadId: 'thread-B', turnId: 'turn-B' }) + '\n' +
    commandRequest(12, { threadId: 'thread-A', turnId: 'turn-A-2' }) + '\n'
  ));
  observer.observeClientChunk(Buffer.from(
    response(12, 'accept') + '\n' + response(11, 'decline') + '\n' + response(10, 'acceptForSession') + '\n'
  ));
  await tick();
  assert.deepEqual(events.map(({ rpcId, decision, threadId, turnId }) => ({ rpcId, decision, threadId, turnId })), [
    { rpcId: '12', decision: 'accept', threadId: 'thread-A', turnId: 'turn-A-2' },
    { rpcId: '11', decision: 'decline', threadId: 'thread-B', turnId: 'turn-B' },
    { rpcId: '10', decision: 'acceptForSession', threadId: 'thread-A', turnId: 'turn-A' }
  ]);
});

test('duplicate, stale, legacy, unmatched, unknown-family, and unknown-decision responses emit nothing', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(commandRequest(20) + '\n' + fileRequest(21) + '\n'));
  observer.observeClientChunk(Buffer.from(
    response(20, 'mystery') + '\n' +
    legacyResponse(20, 'accept') + '\n' +
    response(999, 'accept') + '\n' +
    response(20, 'accept') + '\n' +
    response(20, 'accept') + '\n'
  ));
  await tick();
  assert.equal(events.length, 1);
  assert.equal(events[0].rpcId, '20');
  assert.equal(observer.pendingCount(), 1);
});

test('missing, null, object, array, boolean, unsafe numeric, and oversized string ids are rejected', () => {
  const observer = new ApprovalObserver();
  const invalid = [
    { method: 'item/commandExecution/requestApproval', params: {} },
    { method: 'item/commandExecution/requestApproval', id: null, params: {} },
    { method: 'item/commandExecution/requestApproval', id: {}, params: {} },
    { method: 'item/commandExecution/requestApproval', id: [], params: {} },
    { method: 'item/commandExecution/requestApproval', id: true, params: {} },
    { method: 'item/commandExecution/requestApproval', id: 9007199254740992, params: {} },
    { method: 'item/commandExecution/requestApproval', id: 'x'.repeat(1025), params: {} }
  ];
  observer.observeServerChunk(Buffer.from(invalid.map((item) => JSON.stringify(item)).join('\n') + '\n'));
  assert.equal(observer.pendingCount(), 0);
});

test('unknown request families never become pending, even with a matching response', () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(request('item/unknown/requestApproval', 30) + '\n'));
  observer.observeClientChunk(Buffer.from(response(30, 'accept') + '\n'));
  assert.deepEqual(events, []);
  assert.equal(observer.pendingCount(), 0);
});

test('partial JSONL lines are reconstructed independently by direction', async () => {
  const events = []; const observer = observerWith(events);
  const requestLine = commandRequest(40, { threadId: 'T' }) + '\n';
  const responseLine = response(40, 'accept') + '\n';
  observer.observeServerChunk(Buffer.from(requestLine.slice(0, 12)));
  observer.observeClientChunk(Buffer.from(responseLine.slice(0, 12)));
  observer.observeServerChunk(Buffer.from(requestLine.slice(12)));
  observer.observeClientChunk(Buffer.from(responseLine.slice(12)));
  await tick();
  assert.equal(events.length, 1);
  assert.equal(events[0].rpcId, '40');
});

test('invalid JSON and oversize records remain unobservable while transport observation continues', () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from('not-json\n' + '{"method":"item/commandExecution/requestApproval","id":41,"params":{"padding":"' + 'x'.repeat(70 * 1024) + '"}}\n'));
  observer.observeClientChunk(Buffer.from(response(41, 'accept') + '\n'));
  assert.deepEqual(events, []);
  assert.equal(observer.pendingCount(), 0);
});

test('telemetry sink failure and a busy sink do not break correlation', async () => {
  const events = []; let release;
  const observer = observerWith(events, (event) => {
    events.push(event);
    if (event.rpcId === '50') return new Promise((resolve) => { release = resolve; });
    throw new Error('sink unavailable');
  });
  observer.observeServerChunk(Buffer.from(commandRequest(50) + '\n' + commandRequest(51) + '\n'));
  observer.observeClientChunk(Buffer.from(response(50, 'accept') + '\n' + response(51, 'accept') + '\n'));
  await tick();
  assert.deepEqual(events.map((event) => event.rpcId), ['50']);
  release(); await tick();
  observer.observeServerChunk(Buffer.from(commandRequest(52) + '\n'));
  observer.observeClientChunk(Buffer.from(response(52, 'accept') + '\n'));
  await tick();
  assert.deepEqual(events.map((event) => event.rpcId), ['50', '52']);
});

test('a failing telemetry sink cannot block either transparent transport direction', async () => {
  const clientInput = new PassThrough(); const childInput = new PassThrough();
  const childOutput = new PassThrough(); const clientOutput = new PassThrough();
  let sinkCalls = 0;
  connectTransparentBridge({ clientInput, clientOutput, childInput, childOutput, telemetrySink: () => { sinkCalls += 1; throw new Error('offline sink'); } });
  const clientBytes = Buffer.from(response(60, 'accept') + '\n');
  const serverBytes = Buffer.from(commandRequest(60) + '\n');
  const gotChild = collect(childInput); const gotClient = collect(clientOutput);
  childOutput.end(serverBytes);
  await tick();
  clientInput.end(clientBytes);
  assert.deepEqual(await gotChild, clientBytes);
  assert.deepEqual(await gotClient, serverBytes);
  assert.equal(sinkCalls, 1);
});

test('observer caps pending request and incomplete-line buffering', () => {
  const observer = new ApprovalObserver();
  for (let index = 0; index < 300; index += 1) observer.observeServerChunk(Buffer.from(commandRequest(index) + '\n'));
  observer.observeServerChunk(Buffer.alloc(70 * 1024, 0x61));
  assert.equal(observer.pendingCount(), 256);
});

test('fake-child bridge preserves stdout, stderr, and normal child exit lifecycle', async () => {
  const bridgeCli = fileURLToPath(new URL('../src/bridge-cli.mjs', import.meta.url));
  const child = spawn(process.execPath, [bridgeCli], { stdio: ['pipe', 'pipe', 'pipe'] });
  const stdout = []; const stderr = [];
  child.stdout.on('data', (chunk) => stdout.push(Buffer.from(chunk)));
  child.stderr.on('data', (chunk) => stderr.push(Buffer.from(chunk)));
  child.stdin.end('fixture-line\n');
  const [code, signal] = await once(child, 'exit');
  assert.equal(code, 0); assert.equal(signal, null);
  assert.equal(Buffer.concat(stdout).toString('utf8'), 'fixture-line\n');
  assert.match(Buffer.concat(stderr).toString('utf8'), /fake-app-server: started/);
});

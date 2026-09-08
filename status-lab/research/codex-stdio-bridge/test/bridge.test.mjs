import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { ApprovalObserver, NativeThreadStatusObserver, NativeThreadMetadataObserver, connectTransparentBridge } from '../src/bridge-core.mjs';

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

test('native status notification becomes bounded ordered authority events', async () => {
  const events = [];
  const observer = new NativeThreadStatusObserver({
    authoritySink: async (event) => events.push(event),
    classificationResolver: threadId => threadId === 'thread-native' ? 'canary' : undefined
  });
  const message = (status, activeFlags = status === 'active' ? [] : undefined) => JSON.stringify({
    jsonrpc: '2.0', method: 'thread/status/changed', params: {
      threadId: 'thread-native', status: { type: status, ...(activeFlags === undefined ? {} : { activeFlags }) }
    }, emittedAtMs: 1788854400000
  }) + '\n';
  const first = message('active');
  observer.observeServerChunk(Buffer.from(first.slice(0, 20)));
  observer.observeServerChunk(Buffer.from(first.slice(20)));
  observer.observeServerChunk(Buffer.from(message('active', ['waitingOnApproval']) + message('active', []) + message('idle')));
  for (let attempt = 0; attempt < 10 && events.length < 4; attempt++) await tick();
  assert.deepEqual(events.map(({ status, activeFlags }) => ({ status, activeFlags })), [
    { status: 'active', activeFlags: [] },
    { status: 'active', activeFlags: ['waitingOnApproval'] },
    { status: 'active', activeFlags: [] },
    { status: 'idle', activeFlags: [] }
  ]);
  assert.deepEqual(Object.keys(events[0]).sort(), [
    'activeFlags', 'classification', 'event', 'schemaVersion', 'source', 'status', 'threadId', 'timestampUtc'
  ].sort());
  assert.equal(observer.authorityHealth().healthy, true);
  assert.equal(events[0].classification, 'canary');
});

test('proven thread/started metadata emits only bounded exact Unicode cwd', () => {
  const events = [];
  const observer = new NativeThreadMetadataObserver({ metadataSink: event => events.push(event) });
  observer.observeServerChunk(Buffer.from(JSON.stringify({ jsonrpc: '2.0', method: 'thread/started', params: {
    thread: { id: 'thread-metadata', cwd: 'G:\\Мой диск\\AgentLoop Exchange\\inbox', model: 'PRIVATE', prompt: 'SECRET' }
  } }) + '\n'));
  observer.observeServerChunk(Buffer.from(JSON.stringify({ method: 'thread/started', params: {
    thread: { id: 'thread-private', cwd: 'G:\\ok', turns: ['PRIVATE'] }
  } }) + '\n'));
  assert.deepEqual(events, [{ schemaVersion: 'k15-codex-thread-metadata/v1', source: 'codex_stdio_bridge',
    event: 'thread_metadata_changed', threadId: 'thread-metadata', workingDirectory: 'G:\\Мой диск\\AgentLoop Exchange\\inbox' },
  { schemaVersion: 'k15-codex-thread-metadata/v1', source: 'codex_stdio_bridge',
    event: 'thread_metadata_changed', threadId: 'thread-private', workingDirectory: 'G:\\ok' }]);
  assert.doesNotMatch(JSON.stringify(events), /PRIVATE|SECRET|prompt|model|turns/);
  observer.observeServerChunk(Buffer.from(JSON.stringify({ method: 'thread/started', params: { thread: { id: 'bad', cwd: 'x'.repeat(1025) } } }) + '\n'));
  assert.equal(events.length, 2);
});

test('metadata delivery failures are fail-open and never change native status authority', async () => {
  const nativeEvents = [];
  const status = new NativeThreadStatusObserver({ authoritySink: event => nativeEvents.push(event) });
  const metadata = new NativeThreadMetadataObserver({ metadataSink: () => { throw new Error('metadata sink'); } });
  const rejected = new NativeThreadMetadataObserver({ metadataSink: () => Promise.reject(new Error('async metadata sink')) });
  const started = JSON.stringify({ method: 'thread/started', params: { thread: {
    id: 'metadata-failure', cwd: 'G:\\Мой диск\\AgentLoop Exchange\\inbox'
  } } }) + '\n';
  const statusLine = JSON.stringify({ method: 'thread/status/changed', params: {
    threadId: 'status-survives', status: { type: 'idle' }
  } }) + '\n';
  assert.doesNotThrow(() => metadata.observeServerChunk(Buffer.from(started)));
  assert.doesNotThrow(() => rejected.observeServerChunk(Buffer.from(started)));
  status.observeServerChunk(Buffer.from(statusLine));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(nativeEvents.length, 1);
  assert.equal(status.authorityHealth().healthy, true);
});

test('metadata failure does not interrupt transparent transport lifecycle', async () => {
  for (const authoritySink of [
    () => { throw new Error('metadata delivery unavailable'); },
    () => Promise.reject(new Error('metadata delivery unavailable'))
  ]) {
    const clientInput = new PassThrough(); const clientOutput = new PassThrough();
    const childInput = new PassThrough(); const childOutput = new PassThrough();
    const received = collect(childInput); const returned = collect(clientOutput);
    const observer = connectTransparentBridge({ clientInput, clientOutput, childInput, childOutput,
      authoritySink, telemetrySink: () => {} });
    const server = Buffer.from(JSON.stringify({ method: 'thread/started', params: { thread: {
      id: 'metadata-failure', cwd: 'G:\\Мой диск\\AgentLoop Exchange\\inbox'
    } } }) + '\n');
    const client = Buffer.from('opaque-client-bytes\0\xff');
    childOutput.end(server); clientInput.end(client);
    assert.deepEqual(await received, client);
    assert.deepEqual(await returned, server);
    assert.ok(observer.nativeStatusObserver);
  }
});

test('exact non-active ThreadStatus union shapes are accepted and sanitized', async () => {
  const events = [];
  const observer = new NativeThreadStatusObserver({ authoritySink: event => events.push(event) });
  const send = status => observer.observeServerChunk(Buffer.from(JSON.stringify({
    method: 'thread/status/changed', params: { threadId: `thread-${status}`, status: { type: status } }, emittedAtMs: 1788854400000
  }) + '\n'));
  send('idle'); send('notLoaded'); send('systemError');
  for (let attempt = 0; attempt < 10 && events.length < 3; attempt++) await tick();
  assert.deepEqual(events.map(({ status, activeFlags }) => ({ status, activeFlags })), [
    { status: 'idle', activeFlags: [] },
    { status: 'notLoaded', activeFlags: [] },
    { status: 'systemError', activeFlags: [] }
  ]);
  const enrichedIdle = JSON.stringify({ method: 'thread/status/changed', params: {
    threadId: 'enriched', status: { type: 'idle', activeFlags: [] }
  }}) + '\n';
  observer.observeServerChunk(Buffer.from(enrichedIdle));
  assert.equal(events.length, 3);
});

test('native authority rejects malformed or unknown state without leaking input', () => {
  const events = []; const observer = new NativeThreadStatusObserver({ authoritySink: event => events.push(event) });
  const send = params => observer.observeServerChunk(Buffer.from(JSON.stringify({ method: 'thread/status/changed', params }) + '\n'));
  send({ threadId: 'x', status: 'future', activeFlags: [], timestamp: '2026-09-08T08:00:00Z', prompt: 'SECRET' });
  send({ threadId: 'x', status: 'active', activeFlags: ['futureFlag'], timestamp: '2026-09-08T08:00:00Z' });
  assert.deepEqual(events, []);
  assert.doesNotMatch(JSON.stringify(observer.authorityHealth()), /SECRET|futureFlag/);
});

test('native status uses bounded receipt time only when emittedAtMs is absent', () => {
  const events = [];
  const observer = new NativeThreadStatusObserver({
    authoritySink: event => events.push(event),
    receiptClock: () => new Date('2026-09-08T08:01:02.003Z')
  });
  observer.observeServerChunk(Buffer.from(JSON.stringify({ method: 'thread/status/changed', params: {
    threadId: 'fallback', status: { type: 'idle' }
  }}) + '\n'));
  assert.equal(events[0].timestampUtc, '2026-09-08T08:01:02.003Z');
});

test('slow authority sink preserves order and reports bounded overflow', async () => {
  const events = []; let release;
  const observer = new NativeThreadStatusObserver({ authoritySink: event => {
    events.push(event);
    return events.length === 1 ? new Promise(resolve => { release = resolve; }) : undefined;
  }});
  const line = i => JSON.stringify({ method: 'thread/status/changed', params: {
    threadId: `thread-${i}`, status: { type: 'active', activeFlags: [] }
  }, emittedAtMs: 1788854400000 }) + '\n';
  observer.observeServerChunk(Buffer.from(line(0) + Array.from({ length: 70 }, (_, i) => line(i + 1)).join('')));
  assert.equal(observer.authorityHealth().overflow, 6);
  release(); await tick(); await tick();
  assert.deepEqual(events.slice(0, 3).map(event => event.threadId), ['thread-0', 'thread-1', 'thread-2']);
  assert.equal(observer.authorityHealth().healthy, false);
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

test('exact turn/completed notification emits only bounded sanitized completion metadata', async () => {
  const events = []; const observer = observerWith(events);
  const message = JSON.stringify({
    jsonrpc: '2.0', method: 'turn/completed',
    params: { threadId: 'thread-complete', turn: {
      id: 'turn-complete', status: 'completed',
      items: [{ text: 'MUST NOT REACH SIDE CHANNEL' }],
      error: { message: 'MUST NOT REACH SIDE CHANNEL' }, usage: { secret: true }
    } }
  });
  observer.observeServerChunk(Buffer.from(message.slice(0, 37)));
  observer.observeServerChunk(Buffer.from(message.slice(37) + '\n'));
  await tick();
  assert.equal(events.length, 1);
  assert.deepEqual(Object.keys(events[0]).sort(), [
    'event', 'schemaVersion', 'source', 'status', 'threadId', 'timestampUtc', 'turnId'
  ].sort());
  assert.equal(events[0].schemaVersion, 'k15-codex-completion/v1');
  assert.equal(events[0].event, 'turn_completed');
  assert.equal(events[0].status, 'completed');
  assert.doesNotMatch(JSON.stringify(events[0]), /MUST NOT REACH SIDE CHANNEL|usage|items/);
});

test('only exact terminal turn statuses are observed; wrong method and malformed records are ignored', async () => {
  const events = []; const observer = observerWith(events);
  const send = (value) => observer.observeServerChunk(Buffer.from(JSON.stringify(value) + '\n'));
  send({ method: 'turn/done', params: { threadId: 'T', turn: { id: 'U', status: 'completed' } } });
  send({ method: 'turn/completed', params: { threadId: 'T', turn: { id: 'U', status: 'inProgress' } } });
  send({ method: 'turn/completed', params: { threadId: '', turn: { id: 'U', status: 'completed' } } });
  send({ method: 'turn/completed', params: { threadId: 'T', turn: { id: 'U', status: 'unknown' } } });
  observer.observeServerChunk(Buffer.from('{not-json}\n'));
  await tick();
  assert.equal(events.length, 0);
});

test('oversized completion identifiers are ignored', async () => {
  const events = []; const observer = observerWith(events);
  const valid = { method: 'turn/completed', params: { threadId: 'T', turn: { id: 'U', status: 'completed' } } };
  observer.observeServerChunk(Buffer.from(JSON.stringify({ ...valid, params: {
    ...valid.params, threadId: 'x'.repeat(1025)
  } }) + '\n'));
  observer.observeServerChunk(Buffer.from(JSON.stringify({ ...valid, params: {
    ...valid.params, turn: { ...valid.params.turn, id: 'x'.repeat(1025) }
  } }) + '\n'));
  await tick();
  assert.equal(events.length, 0);
});

test('multiple newline-delimited server records emit exactly one completion event', async () => {
  const events = []; const observer = observerWith(events);
  const completion = { jsonrpc: '2.0', method: 'turn/completed', params: {
    threadId: 'thread-many', turn: { id: 'turn-many', status: 'completed' }
  } };
  observer.observeServerChunk(Buffer.from(JSON.stringify({ jsonrpc: '2.0', method: 'thread/started', params: {} }) + '\n' +
    JSON.stringify(completion) + '\n' + JSON.stringify({ jsonrpc: '2.0', method: 'thread/updated', params: {} }) + '\n'));
  await tick();
  assert.equal(events.length, 1);
  assert.equal(events[0].threadId, 'thread-many');
  assert.equal(events[0].turnId, 'turn-many');
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

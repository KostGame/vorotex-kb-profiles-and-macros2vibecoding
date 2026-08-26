import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { ApprovalObserver, connectTransparentBridge } from '../src/bridge-core.mjs';

const commandRequest = (id, extras = {}) => JSON.stringify({ method: 'item/commandExecution/requestApproval', params: { requestId: id, ...extras } });
const fileRequest = (id, extras = {}) => JSON.stringify({ method: 'item/fileChange/requestApproval', params: { requestId: id, ...extras } });
const commandResponse = (id, decision) => JSON.stringify({ method: 'item/commandExecution/respondApproval', params: { requestId: id, decision } });
const fileResponse = (id, decision) => JSON.stringify({ method: 'item/fileChange/respondApproval', params: { requestId: id, decision } });

async function collect(stream) {
  const chunks = [];
  stream.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
  await once(stream, 'end');
  return Buffer.concat(chunks);
}

function observerWith(events, sink = (event) => events.push(event)) {
  return new ApprovalObserver({ telemetrySink: sink });
}

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

test('ordinary, invalid JSON, and uncorrelated responses create no telemetry', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from('{"method":"ordinary","params":{"secret":"no"}}\nnot-json\n'));
  observer.observeClientChunk(Buffer.from(commandResponse('missing', 'accept') + '\n'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(events, []);
});

test('command decisions are exactly correlated and allowlisted', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(commandRequest('A', { threadId: 'T', turnId: 'U', itemId: 'I', command: 'never emit' }) + '\n'));
  observer.observeClientChunk(Buffer.from(commandResponse('A', 'accept') + '\n'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(events.length, 1);
  assert.deepEqual(Object.keys(events[0]).sort(), ['decision', 'event', 'itemId', 'requestId', 'source', 'threadId', 'timestampUtc', 'turnId'].sort());
  assert.equal(events[0].decision, 'accept'); assert.equal(events[0].requestId, 'A');
});

test('acceptForSession, decline, and cancel remain distinct', async () => {
  const events = []; const observer = observerWith(events);
  for (const [id, decision] of [['S', 'acceptForSession'], ['D', 'decline'], ['C', 'cancel']]) {
    observer.observeServerChunk(Buffer.from(commandRequest(id) + '\n'));
    observer.observeClientChunk(Buffer.from(commandResponse(id, decision) + '\n'));
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.deepEqual(events.map((event) => event.decision), ['acceptForSession', 'decline', 'cancel']);
});

test('file change, reversed parallel responses, and same-thread outstanding requests do not cross-correlate', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(commandRequest('A', { threadId: 'thread-A' }) + '\n' + fileRequest('B', { threadId: 'thread-B' }) + '\n' + commandRequest('C', { threadId: 'thread-A' }) + '\n'));
  observer.observeClientChunk(Buffer.from(commandResponse('C', 'accept') + '\n' + fileResponse('B', 'decline') + '\n' + commandResponse('A', 'acceptForSession') + '\n'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(events.map(({ requestId, decision, threadId }) => ({ requestId, decision, threadId })), [
    { requestId: 'C', decision: 'accept', threadId: 'thread-A' },
    { requestId: 'B', decision: 'decline', threadId: 'thread-B' },
    { requestId: 'A', decision: 'acceptForSession', threadId: 'thread-A' }
  ]);
});

test('partial lines and multi-line chunks are reconstructed without altering transport', async () => {
  const events = []; const observer = observerWith(events);
  const line = commandRequest('partial', { threadId: 'T' }) + '\n';
  observer.observeServerChunk(Buffer.from(line.slice(0, 12)));
  observer.observeServerChunk(Buffer.from(line.slice(12) + commandRequest('multi') + '\n'));
  observer.observeClientChunk(Buffer.from(commandResponse('multi', 'accept') + '\n' + commandResponse('partial', 'accept') + '\n'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(events.map((event) => event.requestId), ['multi', 'partial']);
});

test('unknown decisions and mismatched response families are not inferred', async () => {
  const events = []; const observer = observerWith(events);
  observer.observeServerChunk(Buffer.from(commandRequest('A') + '\n' + fileRequest('B') + '\n'));
  observer.observeClientChunk(Buffer.from(commandResponse('A', 'mystery') + '\n' + commandResponse('B', 'accept') + '\n'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(events, []); assert.equal(observer.pendingCount(), 2);
});

test('telemetry sink failure and a busy sink do not break correlation or transport observation', async () => {
  const events = []; let release;
  const observer = observerWith(events, (event) => {
    events.push(event);
    if (event.requestId === 'A') return new Promise((resolve) => { release = resolve; });
    throw new Error('sink unavailable');
  });
  observer.observeServerChunk(Buffer.from(commandRequest('A') + '\n' + commandRequest('B') + '\n'));
  observer.observeClientChunk(Buffer.from(commandResponse('A', 'accept') + '\n' + commandResponse('B', 'accept') + '\n'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(events.map((event) => event.requestId), ['A']);
  release(); await new Promise((resolve) => setImmediate(resolve));
  observer.observeServerChunk(Buffer.from(commandRequest('C') + '\n'));
  observer.observeClientChunk(Buffer.from(commandResponse('C', 'accept') + '\n'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(events.map((event) => event.requestId), ['A', 'C']);
});

test('a failing telemetry sink cannot block either transparent transport direction', async () => {
  const clientInput = new PassThrough(); const childInput = new PassThrough();
  const childOutput = new PassThrough(); const clientOutput = new PassThrough();
  let sinkCalls = 0;
  connectTransparentBridge({ clientInput, clientOutput, childInput, childOutput, telemetrySink: () => { sinkCalls += 1; throw new Error('offline sink'); } });
  const clientBytes = Buffer.from(commandResponse('A', 'accept') + '\n');
  const serverBytes = Buffer.from(commandRequest('A') + '\n');
  const gotChild = collect(childInput); const gotClient = collect(clientOutput);
  childOutput.end(serverBytes);
  await new Promise((resolve) => setImmediate(resolve));
  clientInput.end(clientBytes);
  assert.deepEqual(await gotChild, clientBytes);
  assert.deepEqual(await gotClient, serverBytes);
  assert.equal(sinkCalls, 1);
});

test('observer caps pending request and incomplete-line buffering while transport remains independent', () => {
  const observer = new ApprovalObserver();
  for (let index = 0; index < 300; index += 1) observer.observeServerChunk(Buffer.from(commandRequest(`id-${index}`) + '\n'));
  observer.observeServerChunk(Buffer.alloc(70 * 1024, 0x61));
  assert.equal(observer.pendingCount(), 256);
});

test('fake-child bridge preserves stdout, stderr, and normal child exit lifecycle', async () => {
  const bridgeCli = fileURLToPath(new URL('../src/bridge-cli.mjs', import.meta.url));
  const child = spawn(process.execPath, [bridgeCli], { stdio: ['pipe', 'pipe', 'pipe'] });
  const stdout = []; const stderr = [];
  child.stdout.on('data', (chunk) => stdout.push(Buffer.from(chunk)));
  child.stderr.on('data', (chunk) => stderr.push(Buffer.from(chunk)));
  child.stdin.end('fixture-line\\n');
  const [code, signal] = await once(child, 'exit');
  assert.equal(code, 0); assert.equal(signal, null);
  assert.equal(Buffer.concat(stdout).toString('utf8'), 'fixture-line\\n');
  assert.match(Buffer.concat(stderr).toString('utf8'), /fake-app-server: started/);
});

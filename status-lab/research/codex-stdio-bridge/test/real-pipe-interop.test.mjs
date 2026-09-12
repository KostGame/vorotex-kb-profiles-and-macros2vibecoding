import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { BridgeDiagnostics } from '../src/bridge-core.mjs';
import { createNamedPipeAuthoritySink } from '../src/runtime-process-authority.mjs';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(testDirectory, '../../../..');
const runtimeTestsProject = path.join(repositoryRoot, 'vnext', 'Vorotex.K15.Runtime.Tests', 'Vorotex.K15.Runtime.Tests.csproj');

function waitForLine(child, predicate, timeoutMs = 20000) {
  const lines = [];
  const waiters = [];
  let buffer = '';
  let closed = false;
  let closeError;

  const dispatch = line => {
    lines.push(line);
    for (let index = waiters.length - 1; index >= 0; index -= 1) {
      if (!waiters[index].predicate(line)) continue;
      const waiter = waiters.splice(index, 1)[0];
      waiter.resolve(line);
    }
  };
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    buffer += chunk;
    for (let newline = buffer.indexOf('\n'); newline >= 0; newline = buffer.indexOf('\n')) {
      dispatch(buffer.slice(0, newline).replace(/\r$/, ''));
      buffer = buffer.slice(newline + 1);
    }
  });
  child.once('error', error => {
    closeError = error;
    closed = true;
    while (waiters.length) waiters.shift().reject(error);
  });
  child.once('close', (code, signal) => {
    closed = true;
    closeError ??= new Error(`harness exited before marker: ${code ?? signal}`);
    while (waiters.length) waiters.shift().reject(closeError);
  });

  const wait = predicate => {
    const existing = lines.find(predicate);
    if (existing !== undefined) return Promise.resolve(existing);
    if (closed) return Promise.reject(closeError ?? new Error('harness closed'));
    return new Promise((resolve, reject) => waiters.push({ predicate, resolve, reject }));
  };

  return { wait, lines };
}

function withTimeout(promise, timeoutMs, message) {
  let timer;
  return Promise.race([
    promise,
    new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), timeoutMs); })
  ]).finally(() => clearTimeout(timer));
}

const status = (value, timestampUtc) => ({
  schemaVersion: 'k15-codex-thread-status/v1',
  source: 'codex_stdio_bridge',
  event: 'thread_status_changed',
  threadId: 'node-interop-thread',
  status: value,
  activeFlags: [],
  timestampUtc
});

test('real Node net.Socket interoperates with .NET authority ingress and drives Runtime lifecycle', { skip: process.platform !== 'win32' }, async () => {
  const child = spawn('dotnet', ['run', '--project', runtimeTestsProject, '-c', 'Release', '--', '--node-net-pipe-harness'], {
    cwd: repositoryRoot,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  });
  const childExit = new Promise(resolve => child.once('close', (code, signal) => resolve({ code, signal })));
  const harness = waitForLine(child, line => line.startsWith('READY '));
  const diagnostics = new BridgeDiagnostics();
  let boundary;
  let completed = false;
  try {
    const readyLine = await withTimeout(harness.wait(line => line.startsWith('READY ')), 30000, 'real pipe harness did not become ready');
    const pipeName = readyLine.slice('READY '.length);
    assert.match(pipeName, /^[A-Za-z0-9._-]{1,128}$/);
    boundary = createNamedPipeAuthoritySink({
      pipePath: `\\\\.\\pipe\\${pipeName}`,
      connectTimeoutMs: 2000,
      diagnostics
    });

    await boundary.sink(status('active', '2026-09-13T08:00:00Z'));
    await withTimeout(harness.wait(line => line === 'PRODUCER_ACCEPTED'), 5000, 'Runtime did not accept the Node producer');
    await withTimeout(harness.wait(line => line === 'FRAME_DECODED_1'), 5000, 'Runtime did not decode the Node frame');
    await withTimeout(harness.wait(line => line === 'QUEUE_ACCEPTED'), 5000, 'Runtime did not accept the decoded frame');
    await withTimeout(harness.wait(line => line === 'RUNNING'), 5000, 'Runtime did not reach RUNNING after Node frame write');
    await boundary.sink(status('idle', '2026-09-13T08:00:01Z'));
    await withTimeout(harness.wait(line => line === 'FRAME_DECODED_2'), 5000, 'Runtime did not decode the Node idle frame');
    await withTimeout(harness.wait(line => line === 'TERMINAL'), 5000, 'Runtime did not reach terminal state after Node idle frame');
    completed = true;

    const snapshot = diagnostics.snapshot();
    assert.equal(snapshot.namedPipe.connectAttempts, 1);
    assert.equal(snapshot.namedPipe.connectSuccesses, 1);
    assert.equal(snapshot.namedPipe.frameWriteAttempts, 2);
    assert.equal(snapshot.namedPipe.frameWriteSuccesses, 2);
    assert.equal(snapshot.namedPipe.frameWriteFailures, 0);
    assert.deepEqual(snapshot.namedPipe.writeFailureReasons, { unavailable: 0, remote_close: 0, unknown: 0 });
  } finally {
    await boundary?.close();
    if (!completed && !child.killed) child.kill();
  }
  const result = await withTimeout(childExit, 5000, 'real pipe harness did not exit');
  assert.equal(result.code, 0);
});

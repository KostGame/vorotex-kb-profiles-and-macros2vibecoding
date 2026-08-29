import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { PassThrough } from 'node:stream';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  APPROVAL_CONFIG_ERROR_EXIT_CODE,
  APPROVAL_SINK_PATH_ENV,
  runApprovalWrapper
} from '../src/approval-wrapper.mjs';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const fakeChild = path.join(testDirectory, 'fixtures', 'fake-child.mjs');

function collect(stream) {
  const chunks = [];
  const ended = new Promise((resolve) => {
    stream.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
    stream.once('end', () => resolve(Buffer.concat(chunks)));
  });
  return ended;
}

test('opt-in approval wrapper preserves transport and emits only sanitized side-channel data', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'k15-codex-approval-'));
  const sinkPath = path.join(root, 'status-lab', 'events.jsonl');
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const output = collect(stdout);
  const diagnostics = [];
  stderr.on('data', (chunk) => diagnostics.push(Buffer.from(chunk)));
  const run = runApprovalWrapper({
    argv: ['app-server'],
    env: {
      ...process.env,
      CODEX_BRIDGE_CHILD_PATH: fakeChild,
      FAKE_CHILD_MODE: 'approval',
      [APPROVAL_SINK_PATH_ENV]: sinkPath
    },
    stdin,
    stdout,
    stderr,
    spawnProcess: (childPath, childArgs, options) =>
      spawn(process.execPath, [childPath, ...childArgs], { ...options })
  });
  stdout.once('data', () => stdin.end(Buffer.from('{"method":"item/commandExecution/respondApproval","params":{"requestId":"fixture-approval","decision":"accept","secret":"MUST NOT REACH SIDE CHANNEL"}}\n')));
  const code = await run;
  const transport = await output;
  await new Promise((resolve) => setImmediate(resolve));
  const records = (await readFile(sinkPath, 'utf8')).trim().split('\n').map((line) => JSON.parse(line));

  assert.equal(code, 0);
  assert.match(transport.toString('utf8'), /fixture-approval/);
  assert.equal(records.length, 1);
  assert.deepEqual(records[0], {
    schemaVersion: 'k15-codex-approval/v1',
    timestampUtc: records[0].timestampUtc,
    source: 'codex_stdio_bridge',
    event: 'approval_resolved',
    decision: 'accept',
    requestId: 'fixture-approval',
    threadId: 'thread-fixture',
    turnId: 'turn-fixture',
    itemId: 'item-fixture'
  });
  assert.doesNotMatch(await readFile(sinkPath, 'utf8'), /MUST NOT REACH SIDE CHANNEL/);
  assert.deepEqual(await readdir(root), ['status-lab']);
  await rm(root, { recursive: true, force: true });
});

test('approval wrapper rejects a relative sink without touching transport', async () => {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const diagnostics = [];
  stderr.on('data', (chunk) => diagnostics.push(Buffer.from(chunk)));
  const code = await runApprovalWrapper({
    env: { CODEX_BRIDGE_CHILD_PATH: fakeChild, [APPROVAL_SINK_PATH_ENV]: 'relative-events.jsonl' },
    stdin,
    stdout,
    stderr,
    spawnProcess: () => { throw new Error('must not spawn'); }
  });
  stdin.end();
  assert.equal(code, APPROVAL_CONFIG_ERROR_EXIT_CODE);
  assert.match(Buffer.concat(diagnostics).toString('utf8'), /invalid approval sink configuration/);
});

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
import { RUNTIME_COMMAND_ENV } from '../src/runtime-process-authority.mjs';

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
      ...Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('CODEX_BRIDGE_'))),
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
  stdout.once('data', () => stdin.end(Buffer.from('{"jsonrpc":"2.0","id":1,"result":{"decision":"accept","secret":"MUST NOT REACH SIDE CHANNEL"}}\n')));
  const code = await run;
  const transport = await output;
  await new Promise((resolve) => setImmediate(resolve));
  const records = (await readFile(sinkPath, 'utf8')).trim().split('\n').map((line) => JSON.parse(line));

  assert.equal(code, 0);
  assert.match(transport.toString('utf8'), /"id":1/);
  assert.equal(records.length, 1);
  assert.deepEqual(records[0], {
    schemaVersion: 'k15-codex-approval/v1',
    timestampUtc: records[0].timestampUtc,
    source: 'codex_stdio_bridge',
    event: 'approval_resolved',
    decision: 'accept',
    rpcIdType: 'number',
    rpcId: '1',
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

test('real approval wrapper seam observes native status while preserving bytes', async () => {
  const stdin = new PassThrough(); const stdout = new PassThrough(); const stderr = new PassThrough();
  const output = collect(stdout); const events = [];
  const run = runApprovalWrapper({
    argv: ['app-server'],
    env: { ...Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('CODEX_BRIDGE_'))), CODEX_BRIDGE_CHILD_PATH: fakeChild, FAKE_CHILD_MODE: 'native' },
    stdin, stdout, stderr,
    authoritySink: event => events.push(event),
    spawnProcess: (childPath, childArgs, options) => spawn(process.execPath, [childPath, ...childArgs], { ...options })
  });
  stdout.once('data', () => stdin.end(Buffer.from('transparent-client-bytes')));
  assert.equal(await run, 0);
  const bytes = await output;
  assert.match(bytes.toString('utf8'), /thread\/status\/changed/);
  assert.equal(events.length, 1);
  assert.equal(events[0].threadId, 'thread-native-fixture');
});

test('opt-in wrapper process boundary delivers sanitized native state to Runtime and stays disabled by default', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'k15-codex-runtime-boundary-'));
  const capturePath = path.join(root, 'runtime-records.jsonl');
  const stdin = new PassThrough(); const stdout = new PassThrough(); const stderr = new PassThrough();
  const output = collect(stdout);
  const runtimeScript = "let value=''; process.stdin.on('data', chunk => value += chunk); process.stdin.on('end', () => require('node:fs').writeFileSync(process.argv[1], value));";
  const run = runApprovalWrapper({
    argv: ['app-server'],
    env: {
      ...Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('CODEX_BRIDGE_'))),
      CODEX_BRIDGE_CHILD_PATH: fakeChild,
      FAKE_CHILD_MODE: 'native',
      [RUNTIME_COMMAND_ENV]: process.execPath
    },
    runtimeCommandArgs: ['-e', runtimeScript, capturePath],
    stdin, stdout, stderr,
    spawnProcess: (childPath, childArgs, options) => spawn(process.execPath, [childPath, ...childArgs], { ...options })
  });
  stdout.once('data', () => stdin.end(Buffer.from('opaque-client-bytes')));
  assert.equal(await run, 0);
  const transport = await output;
  const records = (await readFile(capturePath, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
  assert.match(transport.toString('utf8'), /thread\/status\/changed/);
  assert.equal(records.length, 1);
  assert.equal(records[0].threadId, 'thread-native-fixture');
  assert.equal(records[0].event, 'thread_status_changed');
  assert.doesNotMatch(await readFile(capturePath, 'utf8'), /opaque-client-bytes|fake-child|prompt|command/);
  await rm(root, { recursive: true, force: true });
});

test('runtime process boundary forwards metadata cwd and drops unrelated fields', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'k15-codex-metadata-boundary-'));
  const capturePath = path.join(root, 'runtime-records.jsonl');
  const stdin = new PassThrough(); const stdout = new PassThrough(); const stderr = new PassThrough();
  const output = collect(stdout);
  const runtimeScript = "let value=''; process.stdin.on('data', chunk => value += chunk); process.stdin.on('end', () => require('node:fs').writeFileSync(process.argv[1], value));";
  const run = runApprovalWrapper({ argv: ['app-server'], env: {
    CODEX_BRIDGE_CHILD_PATH: fakeChild, FAKE_CHILD_MODE: 'metadata', [RUNTIME_COMMAND_ENV]: process.execPath
  }, runtimeCommandArgs: ['-e', runtimeScript, capturePath], stdin, stdout, stderr,
  spawnProcess: (childPath, childArgs, options) => spawn(process.execPath, [childPath, ...childArgs], { ...options }) });
  stdout.once('data', () => stdin.end(Buffer.from('opaque-client-bytes')));
  assert.equal(await run, 0); await output;
  const records = (await readFile(capturePath, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
  assert.deepEqual(records[0], { schemaVersion: 'k15-codex-thread-metadata/v1', source: 'codex_stdio_bridge',
    event: 'thread_metadata_changed', threadId: 'thread-metadata-fixture', workingDirectory: 'G:\\Мой диск\\AgentLoop Exchange\\inbox' });
  assert.doesNotMatch(await readFile(capturePath, 'utf8'), /SECRET|PRIVATE|prompt|model/);
  await rm(root, { recursive: true, force: true });
});

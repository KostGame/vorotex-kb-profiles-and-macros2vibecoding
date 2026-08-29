import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { PassThrough } from 'node:stream';
import { tmpdir } from 'node:os';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import test from 'node:test';
import {
  CHILD_FAILURE_EXIT_CODE,
  CONFIG_ERROR_EXIT_CODE,
  normalizeChildExitCode,
  resolveWrapperConfig,
  runTransparentWrapper
} from '../src/transparent-wrapper.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const wrapper = path.join(root, 'src', 'transparent-wrapper.mjs');
const fakeChild = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'fake-child.mjs');
const baseEnv = {
  ...Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('CODEX_BRIDGE_'))),
  CODEX_BRIDGE_CHILD_PATH: fakeChild
};

function collect(stream) {
  const chunks = [];
  const ended = new Promise((resolve) => {
    stream.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
    stream.once('end', () => resolve(Buffer.concat(chunks)));
  });
  return ended;
}

async function runFake({ input = Buffer.alloc(0), args = ['app-server'], env = {}, cwd } = {}) {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const output = collect(stdout);
  const diagnostics = collect(stderr);
  const result = runTransparentWrapper({
    argv: args,
    env: { ...baseEnv, ...env },
    stdin,
    stdout,
    stderr,
    spawnProcess: (childPath, childArgs, options) =>
      spawn(process.execPath, [childPath, ...childArgs], { ...options, cwd })
  });
  stdin.end(input);
  const code = await result;
  return { code, stdout: await output, stderr: await diagnostics };
}

test('wrapper forwards argv including app-server without reserialization', async () => {
  const result = await runFake({
    args: ['app-server', '--future-flag', 'opaque-value'],
    env: { FAKE_CHILD_MODE: 'argv' }
  });
  assert.equal(result.code, 0);
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), ['app-server', '--future-flag', 'opaque-value']);
});

test('wrapper preserves exact binary stdin/stdout, partial lines, non-JSON bytes, and EOF', async () => {
  const input = Buffer.from([0x7b, 0x22, 0x70, 0x22, 0x3a, 0x5b, 0x00, 0xff, 0x0a, 0x6e, 0x6f, 0x74, 0x2d, 0x6a, 0x73, 0x6f, 0x6e]);
  const result = await runFake({ input });
  assert.equal(result.code, 0);
  assert.deepEqual(result.stdout, input);
  assert.equal(result.stderr.toString('utf8'), 'fake-child:echo\n');
});

test('wrapper keeps child stderr on stderr and does not mix it into stdout', async () => {
  const result = await runFake({ input: Buffer.from('fixture\n') });
  assert.equal(result.stdout.toString('utf8'), 'fixture\n');
  assert.equal(result.stderr.toString('utf8'), 'fake-child:echo\n');
});

test('wrapper preserves high-volume ordering and does not truncate backpressured data', async () => {
  const input = Buffer.alloc(3 * 1024 * 1024);
  for (let index = 0; index < input.length; index += 1) input[index] = (index * 31 + 17) % 256;
  const result = await runFake({ input });
  assert.equal(result.code, 0);
  assert.deepEqual(result.stdout, input);
});

test('wrapper closes the Desktop-side stdout when the child closes stdout', async () => {
  const result = await runFake({ env: { FAKE_CHILD_MODE: 'close-stdout' } });
  assert.equal(result.code, 0);
  assert.equal(result.stdout.length, 0);
});

test('wrapper propagates zero and nonzero child exit codes', async () => {
  const ok = await runFake({ env: { FAKE_CHILD_MODE: 'exit', FAKE_CHILD_EXIT_CODE: '0' } });
  const failed = await runFake({ env: { FAKE_CHILD_MODE: 'exit', FAKE_CHILD_EXIT_CODE: '37' } });
  assert.equal(ok.code, 0);
  assert.equal(failed.code, 37);
});

test('wrapper maps a child signal/crash to deterministic exit code 1', async () => {
  const result = await runFake({ env: { FAKE_CHILD_MODE: 'signal' } });
  assert.equal(result.code, CHILD_FAILURE_EXIT_CODE);
  assert.equal(normalizeChildExitCode(null, 'SIGTERM'), CHILD_FAILURE_EXIT_CODE);
});

test('spawn failure exits boundedly without waiting for Desktop stdin EOF', async () => {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const diagnostics = [];
  stderr.on('data', (chunk) => diagnostics.push(Buffer.from(chunk)));
  const started = Date.now();
  const code = await runTransparentWrapper({
    env: baseEnv,
    stdin,
    stdout,
    stderr,
    spawnProcess: () => { throw new Error('synthetic spawn failure'); }
  });
  assert.equal(code, CHILD_FAILURE_EXIT_CODE);
  assert.ok(Date.now() - started < 2000);
  assert.match(Buffer.concat(diagnostics).toString('utf8'), /child spawn failed/);
});

test('configuration fails closed for missing child, recursion, unsupported args, and SHA mismatch', async () => {
  const missingChild = path.join(root, 'test', 'fixtures', 'does-not-exist.mjs');
  await assert.rejects(resolveWrapperConfig({ env: { CODEX_BRIDGE_CHILD_PATH: missingChild } }));
  await assert.rejects(resolveWrapperConfig({ env: { CODEX_BRIDGE_CHILD_PATH: wrapper } }));
  await assert.rejects(resolveWrapperConfig({ env: { ...baseEnv, CODEX_BRIDGE_CHILD_ARGS: 'app-server' } }));
  await assert.rejects(resolveWrapperConfig({ env: { ...baseEnv, CODEX_BRIDGE_CHILD_SHA256: '0'.repeat(64) } }));
});

test('matching explicit child SHA pin is accepted', async () => {
  const bytes = await readFile(fakeChild);
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  const config = await resolveWrapperConfig({ env: { ...baseEnv, CODEX_BRIDGE_CHILD_SHA256: sha256 } });
  assert.equal(config.childPath.toLowerCase(), path.resolve(fakeChild).toLowerCase());
});

test('Phase B entry point has no parser/observer activation and creates no payload files', async () => {
  const source = await readFile(wrapper, 'utf8');
  assert.doesNotMatch(source, /bridge-core|ApprovalObserver|JSON\.parse|telemetrySink/);
  assert.match(source, /\.pipe\(/);

  const cwd = await mkdtemp(path.join(tmpdir(), 'k15-codex-bridge-'));
  try {
    const result = await runFake({ cwd, input: Buffer.from('opaque command and prompt bytes\n') });
    assert.equal(result.code, 0);
    assert.deepEqual(await readdir(cwd), []);
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});

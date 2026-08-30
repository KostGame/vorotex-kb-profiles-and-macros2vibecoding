import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, readdir, rm, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const bridgeDirectory = path.resolve(testDirectory, '..');
const adapterPath = path.join(bridgeDirectory, 'bin', 'adapter-test', 'K15.CodexBridge.WindowsAdapter.exe');
const fakeChildPath = path.join(bridgeDirectory, 'bin', 'fake-child-test', 'K15.CodexBridge.FakeChild.exe');
const wrapperPath = path.join(bridgeDirectory, 'bin', 'adapter-test', 'transparent-wrapper.mjs');
const approvalWrapperPath = path.join(bridgeDirectory, 'bin', 'adapter-test', 'approval-wrapper.mjs');

function environmentFor(overrides = {}, { packagedWrapper = false } = {}) {
  const environment = {
    ...Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('CODEX_BRIDGE_'))),
    CODEX_BRIDGE_NODE_PATH: process.execPath,
    CODEX_BRIDGE_CHILD_PATH: fakeChildPath,
    CODEX_BRIDGE_WRAPPER_PATH: wrapperPath,
    ...overrides
  };
  if (packagedWrapper) delete environment.CODEX_BRIDGE_WRAPPER_PATH;
  return environment;
}

function runExecutable({
  input = Buffer.alloc(0),
  args = ['app-server'],
  env = {},
  cwd = bridgeDirectory,
  packagedWrapper = false,
  closeInput = true,
  timeoutMs = 10000
} = {}) {
  return new Promise((resolve, reject) => {
    const startedAt = Date.now();
    const environment = environmentFor(env, { packagedWrapper });
    const cliPath = environment.CODEX_CLI_PATH ?? adapterPath;
    const child = spawn(cliPath, args, {
      cwd,
      env: environment,
      shell: false,
      stdio: ['pipe', 'pipe', 'pipe']
    });
    const stdout = [];
    const stderr = [];
    let settled = false;
    const timeout = setTimeout(() => {
      child.kill();
      finish(new Error('executable adapter timed out'));
    }, timeoutMs);

    const finish = (error, result) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      if (error) reject(error);
      else resolve({ ...result, durationMs: Date.now() - startedAt });
    };

    child.stdout.on('data', (chunk) => stdout.push(chunk));
    child.stderr.on('data', (chunk) => stderr.push(chunk));
    child.once('error', (error) => finish(error));
    child.once('close', (code, signal) => finish(null, {
      code,
      signal,
      stdout: Buffer.concat(stdout),
      stderr: Buffer.concat(stderr)
    }));
    if (closeInput) child.stdin.end(input);
    else if (input.length > 0) child.stdin.write(input);
  });
}

test('publishes a direct Windows executable and packages the wrapper', async () => {
  assert.equal((await stat(adapterPath)).isFile(), true);
  assert.equal(path.extname(adapterPath).toLowerCase(), '.exe');
  assert.equal((await stat(fakeChildPath)).isFile(), true);
  assert.equal((await stat(wrapperPath)).isFile(), true);
  assert.equal((await stat(approvalWrapperPath)).isFile(), true);
  assert.match(await readFile(wrapperPath, 'utf8'), /runTransparentWrapper/);
  assert.match(await readFile(approvalWrapperPath, 'utf8'), /runApprovalWrapper/);
});

test('direct executable boundary preserves argv without shell association', async () => {
  const result = await runExecutable({
    args: ['app-server', '--future-flag', 'opaque-value'],
    env: { CODEX_CLI_PATH: adapterPath, FAKE_CHILD_MODE: 'argv' }
  });
  assert.equal(result.code, 0);
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), [
    'app-server',
    '--future-flag',
    'opaque-value'
  ]);
  assert.equal(result.stderr.toString('utf8'), 'fake-child:argv\n');
});

test('packaged wrapper is usable when no wrapper override is provided', async () => {
  const result = await runExecutable({
    packagedWrapper: true,
    env: { FAKE_CHILD_MODE: 'argv' },
    args: ['app-server']
  });
  assert.equal(result.code, 0);
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), ['app-server']);
});

test('forwards arbitrary binary stdin and stdout unchanged', async () => {
  const input = Buffer.from([0, 1, 2, 10, 13, 34, 92, 127, 128, 239, 191, 189, 255]);
  const result = await runExecutable({
    input,
    env: { FAKE_CHILD_MODE: 'echo' }
  });
  assert.equal(result.code, 0);
  assert.deepEqual(result.stdout, input);
  assert.equal(result.stderr.toString('utf8'), 'fake-child:echo\n');
});

test('keeps child stderr separate from stdout', async () => {
  const input = Buffer.from('opaque protocol bytes\0\xff', 'utf8');
  const result = await runExecutable({
    input,
    env: { FAKE_CHILD_MODE: 'echo' }
  });
  assert.deepEqual(result.stdout, input);
  assert.equal(result.stderr.toString('utf8'), 'fake-child:echo\n');
});

test('forwards a high-volume stream without truncation', async () => {
  const input = Buffer.alloc(3 * 1024 * 1024);
  for (let index = 0; index < input.length; index += 1) input[index] = (index * 31 + 7) & 0xff;
  const result = await runExecutable({
    input,
    env: { FAKE_CHILD_MODE: 'echo' },
    timeoutMs: 20000
  });
  assert.equal(result.code, 0);
  assert.deepEqual(result.stdout, input);
});

test('completes when the child closes stdout after consuming stdin', async () => {
  const result = await runExecutable({
    input: Buffer.from('end-to-end eof'),
    env: { FAKE_CHILD_MODE: 'close-stdout' }
  });
  assert.equal(result.code, 0);
  assert.equal(result.stdout.length, 0);
});

test('preserves zero and nonzero child exit codes', async () => {
  const successful = await runExecutable({
    env: { FAKE_CHILD_MODE: 'exit', FAKE_CHILD_EXIT_CODE: '0' }
  });
  const failed = await runExecutable({
    env: { FAKE_CHILD_MODE: 'exit', FAKE_CHILD_EXIT_CODE: '37' }
  });
  assert.equal(successful.code, 0);
  assert.equal(failed.code, 37);
});

test('propagates an early child exit without waiting for Desktop stdin EOF', async () => {
  const result = await runExecutable({
    closeInput: false,
    env: { FAKE_CHILD_MODE: 'early-exit', FAKE_CHILD_EXIT_CODE: '37' },
    timeoutMs: 5000
  });
  assert.equal(result.code, 37);
});

test('rejects node self-recursion before spawning the configured node path', async () => {
  const result = await runExecutable({
    env: { CODEX_BRIDGE_NODE_PATH: adapterPath },
    timeoutMs: 5000
  });
  assert.equal(result.code, 2);
  assert.equal(result.stderr.toString('utf8'), 'codex bridge adapter: invalid configuration\n');
});

test('rejects child self-recursion before spawning the wrapper child', async () => {
  const result = await runExecutable({
    env: { CODEX_BRIDGE_CHILD_PATH: adapterPath },
    timeoutMs: 5000
  });
  assert.equal(result.code, 2);
  assert.equal(result.stderr.toString('utf8'), 'codex bridge adapter: invalid configuration\n');
});

test('rejects a missing child before spawning it', async () => {
  const result = await runExecutable({
    env: {
      CODEX_BRIDGE_CHILD_PATH: path.join(bridgeDirectory, 'test', 'fake-child', 'missing.exe')
    }
  });
  assert.equal(result.code, 2);
  assert.match(result.stderr.toString('utf8'), /^codex bridge: invalid child configuration\n$/);
});

test('rejects wrapper recursion', async () => {
  const result = await runExecutable({
    env: { CODEX_BRIDGE_CHILD_PATH: wrapperPath }
  });
  assert.equal(result.code, 2);
  assert.match(result.stderr.toString('utf8'), /^codex bridge: invalid child configuration\n$/);
});

test('rejects a mismatched child SHA-256 pin', async () => {
  const result = await runExecutable({
    env: {
      CODEX_BRIDGE_CHILD_SHA256: '0'.repeat(64)
    }
  });
  assert.equal(result.code, 2);
  assert.match(result.stderr.toString('utf8'), /^codex bridge: invalid child configuration\n$/);
});

test('does not create payload files or logs', async () => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-bridge-'));
  try {
    const result = await runExecutable({
      cwd: temporaryDirectory,
      input: Buffer.from('opaque no-telemetry bytes'),
      env: { FAKE_CHILD_MODE: 'echo' }
    });
    assert.equal(result.code, 0);
    assert.deepEqual(await readdir(temporaryDirectory), []);
  } finally {
    await rm(temporaryDirectory, { recursive: true, force: true });
  }
});

test('wrapper and adapter sources contain no parser, telemetry, or shell bridge', async () => {
  const wrapperSource = await readFile(path.join(bridgeDirectory, 'src', 'transparent-wrapper.mjs'), 'utf8');
  const adapterSource = await readFile(path.join(bridgeDirectory, 'windows-adapter', 'Program.cs'), 'utf8');
  assert.doesNotMatch(wrapperSource, /bridge-core|ApprovalObserver|JSON\.parse|telemetrySink|writeFile|appendFile/);
  assert.doesNotMatch(adapterSource, /cmd\.exe|powershell\.exe|UseShellExecute\s*=\s*true/);
  assert.match(adapterSource, /UseShellExecute\s*=\s*false/);
  assert.match(adapterSource, /ArgumentList/);
  assert.match(adapterSource, /CopyToAsync/);
});

test('test harness uses direct child_process spawning', async () => {
  const testSource = await readFile(fileURLToPath(import.meta.url), 'utf8');
  assert.match(testSource, /shell: false/);
  assert.equal(testSource.includes(['shell', ': ', 'true'].join('')), false);
  assert.equal(testSource.includes(['cmd', '.exe'].join('')), false);
  assert.equal(testSource.includes(['powershell', '.exe'].join('')), false);
});

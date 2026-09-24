import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readFile, readdir, realpath, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const bridgeDirectory = path.resolve(testDirectory, '..');
const adapterPath = path.join(bridgeDirectory, 'bin', 'adapter-test', 'K15.CodexBridge.WindowsAdapter.exe');
const fakeChildPath = path.join(bridgeDirectory, 'bin', 'fake-child-test', 'K15.CodexBridge.FakeChild.exe');
const relocatorProbePath = path.join(bridgeDirectory, 'bin', 'runtime-relocator-probe-test', 'K15.CodexBridge.WindowsAdapter.TestProbe.exe');
const wrapperPath = path.join(bridgeDirectory, 'bin', 'adapter-test', 'transparent-wrapper.mjs');
const desktopCoreExecutableNames = [
  'codex.exe',
  'codex-code-mode-host.exe',
  'codex-windows-sandbox-setup.exe',
  'codex-command-runner.exe'
];
const approvalWrapperPath = path.join(bridgeDirectory, 'bin', 'adapter-test', 'approval-wrapper.mjs');
const authorityModulePath = path.join(bridgeDirectory, 'bin', 'adapter-test', 'runtime-process-authority.mjs');

function environmentFor(overrides = {}, { packagedWrapper = false } = {}) {
  const environment = {
    ...Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('CODEX_BRIDGE_'))),
    CODEX_BRIDGE_NODE_PATH: process.execPath,
    CODEX_BRIDGE_CHILD_PATH: fakeChildPath,
    CODEX_BRIDGE_WRAPPER_PATH: wrapperPath,
    ...overrides
  };
  if (packagedWrapper) delete environment.CODEX_BRIDGE_WRAPPER_PATH;
  for (const [name, value] of Object.entries(environment)) {
    if (value === undefined) delete environment[name];
  }
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

function runRelocatorProbe(resourcesDirectory, runtimeRoot) {
  return new Promise((resolve, reject) => {
    const child = spawn(relocatorProbePath, [resourcesDirectory, runtimeRoot], {
      cwd: bridgeDirectory,
      env: process.env,
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe']
    });
    const stdout = [];
    const stderr = [];
    child.stdout.on('data', (chunk) => stdout.push(chunk));
    child.stderr.on('data', (chunk) => stderr.push(chunk));
    child.once('error', reject);
    child.once('close', (code, signal) => resolve({
      code,
      signal,
      stdout: Buffer.concat(stdout),
      stderr: Buffer.concat(stderr)
    }));
  });
}

function stockRuntimeGeneration(entries) {
  const hash = createHash('sha256');
  for (const entry of entries) {
    hash.update(entry.name, 'utf8');
    hash.update(Buffer.from([0]));
    hash.update(entry.sha256, 'ascii');
    hash.update(Buffer.from([0]));
  }
  return hash.digest('hex').slice(0, 16);
}

async function createSyntheticDesktopResources(rootDirectory) {
  const resourcesDirectory = path.join(rootDirectory, 'resources');
  await mkdir(resourcesDirectory, { recursive: true });
  const fakeBytes = await readFile(fakeChildPath);
  const sha256 = createHash('sha256').update(fakeBytes).digest('hex');
  const entries = [];
  for (const name of desktopCoreExecutableNames) {
    const destination = path.join(resourcesDirectory, name);
    await copyFile(fakeChildPath, destination);
    entries.push({ name, sha256, length: fakeBytes.length });
  }
  return {
    resourcesDirectory,
    entries,
    generation: stockRuntimeGeneration(entries)
  };
}

async function runtimeSnapshot(generationDirectory) {
  const snapshot = [];
  for (const name of desktopCoreExecutableNames) {
    const filePath = path.join(generationDirectory, name);
    const bytes = await readFile(filePath);
    const info = await stat(filePath);
    snapshot.push({
      name,
      length: info.size,
      mtimeMs: info.mtimeMs,
      sha256: createHash('sha256').update(bytes).digest('hex')
    });
  }
  return snapshot;
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

test('cold-start relocator materializes the stock generation and reuses it unchanged', async () => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-runtime-cold-start-'));
  try {
    const { resourcesDirectory, entries, generation } = await createSyntheticDesktopResources(temporaryDirectory);
    const runtimeRoot = path.join(temporaryDirectory, 'localapp', 'OpenAI', 'Codex', 'bin');
    const generationDirectory = path.join(runtimeRoot, generation);

    const first = await runRelocatorProbe(resourcesDirectory, runtimeRoot);
    assert.equal(first.code, 0, first.stderr.toString('utf8'));
    const firstResult = JSON.parse(first.stdout.toString('utf8'));
    const actualChildPath = await realpath(firstResult.path);
    const expectedChildPath = await realpath(path.join(generationDirectory, 'codex.exe'));
    assert.equal(
      path.normalize(actualChildPath).toLowerCase(),
      path.normalize(expectedChildPath).toLowerCase()
    );
    assert.equal(firstResult.sha256, entries[0].sha256);
    assert.deepEqual((await readdir(generationDirectory)).sort(), [...desktopCoreExecutableNames].sort());

    const before = await runtimeSnapshot(generationDirectory);
    for (let index = 0; index < entries.length; index += 1) {
      assert.equal(before[index].name, entries[index].name);
      assert.equal(before[index].length, entries[index].length);
      assert.equal(before[index].sha256, entries[index].sha256);
    }

    const second = await runRelocatorProbe(resourcesDirectory, runtimeRoot);
    assert.equal(second.code, 0, second.stderr.toString('utf8'));
    assert.deepEqual(await runtimeSnapshot(generationDirectory), before);
  } finally {
    await rm(temporaryDirectory, { recursive: true, force: true });
  }
});

test('cold-start relocator repairs an expected-name-only partial generation', async () => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-runtime-partial-'));
  try {
    const { resourcesDirectory, entries, generation } = await createSyntheticDesktopResources(temporaryDirectory);
    const runtimeRoot = path.join(temporaryDirectory, 'localapp', 'OpenAI', 'Codex', 'bin');
    const generationDirectory = path.join(runtimeRoot, generation);
    await mkdir(generationDirectory, { recursive: true });
    await copyFile(path.join(resourcesDirectory, 'codex.exe'), path.join(generationDirectory, 'codex.exe'));

    const result = await runRelocatorProbe(resourcesDirectory, runtimeRoot);
    assert.equal(result.code, 0, result.stderr.toString('utf8'));
    assert.deepEqual((await readdir(generationDirectory)).sort(), [...desktopCoreExecutableNames].sort());

    const snapshot = await runtimeSnapshot(generationDirectory);
    for (let index = 0; index < entries.length; index += 1) {
      assert.equal(snapshot[index].sha256, entries[index].sha256);
    }
  } finally {
    await rm(temporaryDirectory, { recursive: true, force: true });
  }
});

test('cold-start relocator refuses foreign residue in the target generation', async () => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-runtime-foreign-'));
  try {
    const { resourcesDirectory, generation } = await createSyntheticDesktopResources(temporaryDirectory);
    const runtimeRoot = path.join(temporaryDirectory, 'localapp', 'OpenAI', 'Codex', 'bin');
    const generationDirectory = path.join(runtimeRoot, generation);
    const foreignPath = path.join(generationDirectory, 'foreign.txt');
    await mkdir(generationDirectory, { recursive: true });
    await writeFile(foreignPath, 'foreign');

    const result = await runRelocatorProbe(resourcesDirectory, runtimeRoot);
    assert.equal(result.code, 2);
    assert.match(result.stderr.toString('utf8'), /unexpected entries/);
    assert.equal((await stat(foreignPath)).isFile(), true);
  } finally {
    await rm(temporaryDirectory, { recursive: true, force: true });
  }
});

test('cold-start relocator refuses an expected-name directory without deleting it', async () => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-runtime-directory-entry-'));
  try {
    const { resourcesDirectory, generation } = await createSyntheticDesktopResources(temporaryDirectory);
    const runtimeRoot = path.join(temporaryDirectory, 'localapp', 'OpenAI', 'Codex', 'bin');
    const generationDirectory = path.join(runtimeRoot, generation);
    const unsafeEntry = path.join(generationDirectory, 'codex.exe');
    await mkdir(unsafeEntry, { recursive: true });
    await writeFile(path.join(unsafeEntry, 'preserve.txt'), 'preserve');

    const result = await runRelocatorProbe(resourcesDirectory, runtimeRoot);
    assert.equal(result.code, 2);
    assert.match(result.stderr.toString('utf8'), /unexpected entries/);
    assert.equal((await stat(unsafeEntry)).isDirectory(), true);
    assert.equal((await readFile(path.join(unsafeEntry, 'preserve.txt'), 'utf8')), 'preserve');
  } finally {
    await rm(temporaryDirectory, { recursive: true, force: true });
  }
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

test('preserves current Desktop plugin arguments unchanged', async () => {
  const args = [
    '-c', 'features.code_mode_host=true',
    'app-server',
    '--analytics-default-enabled',
    '-c', 'plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled=true'
  ];
  const result = await runExecutable({
    args,
    env: { FAKE_CHILD_MODE: 'argv' }
  });
  assert.equal(result.code, 0);
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), args);
});

test('uses packaged node when CODEX_BRIDGE_NODE_PATH is absent', async () => {
  const packagedNode = path.resolve(path.dirname(adapterPath), '..', 'node', 'node.exe');
  await mkdir(path.dirname(packagedNode), { recursive: true });
  await copyFile(process.execPath, packagedNode);
  try {
    const result = await runExecutable({
      args: ['app-server', '--future-plugin-flag'],
      env: { CODEX_BRIDGE_NODE_PATH: undefined, FAKE_CHILD_MODE: 'argv' }
    });
    assert.equal(result.code, 0);
    assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), ['app-server', '--future-plugin-flag']);
  } finally {
    await rm(path.dirname(packagedNode), { recursive: true, force: true });
  }
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

test('production authority module is included and cannot spawn Runtime', async () => {
  assert.equal((await stat(authorityModulePath)).isFile(), true);
  const source = await readFile(authorityModulePath, 'utf8');
  assert.equal(source.includes("node:child_process"), false);
  assert.doesNotMatch(source, /spawn\s*\(/);
  assert.match(source, /AUTHORITY_PIPE_NAME/);
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
  assert.match(result.stderr.toString('utf8'), /^codex bridge adapter: invalid configuration\n$/);
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

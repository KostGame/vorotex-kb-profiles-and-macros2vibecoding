import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFile, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const execFileAsync = promisify(execFile);
const bridgeRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const script = path.join(bridgeRoot, 'production', 'Activate-CodexBridge.ps1');
const approvalWrapper = path.join(bridgeRoot, 'src', 'approval-wrapper.mjs');
const transparentWrapper = path.join(bridgeRoot, 'src', 'transparent-wrapper.mjs');
const bridgeCore = path.join(bridgeRoot, 'src', 'bridge-core.mjs');

async function powershell(argumentsList) {
  return execFileAsync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', ...argumentsList], { windowsHide: true });
}

async function readJson(filePath) {
  return JSON.parse((await readFile(filePath, 'utf8')).replace(/^\uFEFF/, ''));
}

async function createBundle(temp, approvalSinkPath = '') {
  const child = path.join(temp, 'child.mjs');
  const adapter = path.join(temp, 'adapter.exe');
  const wrapper = path.join(temp, 'approval-wrapper.mjs');
  const transparent = path.join(temp, 'transparent-wrapper.mjs');
  const core = path.join(temp, 'bridge-core.mjs');
  await writeFile(child, 'export default 0;\n', 'utf8');
  await copyFile(process.execPath, adapter);
  await copyFile(approvalWrapper, wrapper);
  await copyFile(transparentWrapper, transparent);
  await copyFile(bridgeCore, core);
  const sha256 = async filePath => createHash('sha256').update(await readFile(filePath)).digest('hex');
  const manifest = path.join(temp, `manifest-${approvalSinkPath ? 'sink' : 'empty'}.json`);
  await writeFile(manifest, JSON.stringify({
    schema: 'k15-codex-bridge/production-manifest-v1',
    adapterPath: adapter,
    adapterSha256: await sha256(adapter),
    nodePath: process.execPath,
    nodeSha256: await sha256(process.execPath),
    wrapperPath: wrapper,
    wrapperSha256: await sha256(wrapper),
    transparentWrapperPath: transparent,
    transparentWrapperSha256: await sha256(transparent),
    bridgeCorePath: core,
    bridgeCoreSha256: await sha256(core),
    childPath: child,
    childSha256: await sha256(child),
    approvalSinkPath
  }), 'utf8');
  return { manifest, paths: { adapter, wrapper, transparent, core, child } };
}

test('production activation validates exact files and pin without touching User or Machine environment', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const result = await powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]);
    assert.match(result.stdout, /VALID=YES/);
    assert.match(result.stdout, /PIN=EXACT/);
    assert.match(result.stdout, /MACHINE_ENV=UNCHANGED/);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('production activation fails closed on child pin drift', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, paths } = await createBundle(temp);
    await writeFile(paths.child, 'changed\n', 'utf8');
    await assert.rejects(powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

async function assertReplacementRejected(pathKey) {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, paths } = await createBundle(temp);
    const replacement = Buffer.concat([await readFile(paths[pathKey]), Buffer.from('\nreplacement-in-place\n')]);
    await writeFile(paths[pathKey], replacement);
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]),
      error => error.code === 2
    );
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
}

test('production activation rejects adapter replacement-in-place', async () => {
  await assertReplacementRejected('adapter');
});

test('production activation rejects approval-wrapper replacement-in-place', async () => {
  await assertReplacementRejected('wrapper');
});

test('production activation rejects transparent-wrapper and bridge-core replacement-in-place', async () => {
  await assertReplacementRejected('transparent');
  await assertReplacementRejected('core');
});

test('activation script documents bounded disable, no Machine env, and no package/injection path', async () => {
  const source = await readFile(script, 'utf8');
  assert.match(source, /Validate.*Enable.*Disable.*Status/);
  assert.match(source, /'User'/);
  assert.doesNotMatch(source, /EnvironmentVariableTarget\.Machine/);
  assert.doesNotMatch(source, /WindowsApps|CreateRemoteThread|OpenProcess|VirtualAllocEx/);
});

test('isolated Enable, Status, Disable round-trip clears stale empty sink and restores exact state', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const original = { CODEX_CLI_PATH: 'stock-codex', CODEX_BRIDGE_APPROVAL_SINK_PATH: 'stale-sink' };
    await writeFile(environment, JSON.stringify(original), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment];
    const enabled = await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    assert.match(enabled.stdout, /ACTIVE=YES/);
    const enabledEnvironment = await readJson(environment);
    assert.equal(Object.hasOwn(enabledEnvironment, 'CODEX_BRIDGE_APPROVAL_SINK_PATH'), false);
    const status = await powershell(['-File', script, '-Mode', 'Status', ...common]);
    assert.match(status.stdout, /ACTIVE=YES/);
    await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.deepEqual(await readJson(environment), original);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('isolated Enable sets non-empty sink and Disable restores absent sink', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const sink = path.join(temp, 'sanitized-events.jsonl');
    const { manifest } = await createBundle(temp, sink);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    await writeFile(environment, JSON.stringify({ CODEX_CLI_PATH: 'stock-codex' }), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment];
    await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    assert.equal((await readJson(environment)).CODEX_BRIDGE_APPROVAL_SINK_PATH, sink);
    await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.deepEqual(await readJson(environment), { CODEX_CLI_PATH: 'stock-codex' });
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('isolated Enable failure restores all pre-existing state and removes activation state', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const original = { CODEX_CLI_PATH: 'stock-codex', CODEX_BRIDGE_APPROVAL_SINK_PATH: 'stale-sink' };
    await writeFile(environment, JSON.stringify(original), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-EnvironmentStoreFailOnSet', 'CODEX_BRIDGE_CHILD_PATH'];
    await assert.rejects(powershell(['-File', script, '-Mode', 'Enable', ...common]));
    assert.deepEqual(await readJson(environment), original);
    await assert.rejects(readFile(state, 'utf8'));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

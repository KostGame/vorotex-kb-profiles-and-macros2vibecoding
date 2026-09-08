import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readFile, rm, symlink, unlink, writeFile } from 'node:fs/promises';
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
const runtimeAuthority = path.join(bridgeRoot, 'src', 'runtime-process-authority.mjs');
const managedVariables = [
  'CODEX_CLI_PATH',
  'CODEX_BRIDGE_NODE_PATH',
  'CODEX_BRIDGE_WRAPPER_PATH',
  'CODEX_BRIDGE_CHILD_PATH',
  'CODEX_BRIDGE_CHILD_SHA256',
  'CODEX_BRIDGE_APPROVAL_SINK_PATH'
];

async function powershell(argumentsList) {
  return execFileAsync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', ...argumentsList], { windowsHide: true });
}

async function readJson(filePath) {
  return JSON.parse((await readFile(filePath, 'utf8')).replace(/^\uFEFF/, ''));
}

async function writeProcessInventory(filePath, entries) {
  await writeFile(filePath, JSON.stringify(entries), 'utf8');
}

function powershellSingleQuoted(value) {
  return "'" + value.replace(/'/g, "''") + "'";
}

function base64(value) {
  return Buffer.from(value, 'utf8').toString('base64');
}

async function registryCommand(command) {
  return powershell(['-Command', command]);
}

async function writeIsolatedRegistryEnvironment(subKey, entries) {
  await registryCommand(
    [
      '$subKey = ' + powershellSingleQuoted(subKey),
      '$entries = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(' + powershellSingleQuoted(base64(JSON.stringify(entries))) + ')) | ConvertFrom-Json',
      '$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subKey, $true)',
      'try {',
      '  foreach ($property in $entries.PSObject.Properties) {',
      "    if ($property.Value.presence -eq 'PRESENT') {",
      '      $kind = [Microsoft.Win32.RegistryValueKind]::$($property.Value.registryKind)',
      '      $key.SetValue($property.Name, [string]$property.Value.value, $kind)',
      '    }',
      '  }',
      '} finally { $key.Dispose() }'
    ].join('; ')
  );
}

async function readIsolatedRegistryEnvironment(subKey) {
  const result = await registryCommand(
    [
      '$subKey = ' + powershellSingleQuoted(subKey),
      '$names = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(' + powershellSingleQuoted(base64(JSON.stringify(managedVariables))) + ')) | ConvertFrom-Json',
      '$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $false)',
      '$output = [ordered]@{}',
      'try {',
      '  foreach ($name in $names) {',
      "    $actualName = @($key.GetValueNames() | Where-Object { [StringComparer]::OrdinalIgnoreCase.Equals($_, $name) }) | Select-Object -First 1",
      '    if ($null -eq $actualName) {',
      "      $output[$name] = [ordered]@{ presence = 'ABSENT'; value = ''; registryKind = 'None' }",
      '    } else {',
      '      $value = $key.GetValue($actualName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)',
      "      $output[$name] = [ordered]@{ presence = 'PRESENT'; value = [string]$value; registryKind = $key.GetValueKind($actualName).ToString() }",
      '    }',
      '  }',
      '  $output | ConvertTo-Json -Compress',
      '} finally { if ($null -ne $key) { $key.Dispose() } }'
    ].join('; ')
  );
  return JSON.parse(result.stdout);
}

async function removeIsolatedRegistryEnvironment(subKey) {
  await registryCommand(
    '[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(' + powershellSingleQuoted(subKey) + ', $false)'
  );
}

async function createBundle(temp, approvalSinkPath = '') {
  const runtimeRoot = path.join(temp, 'runtime-bin');
  const generation = path.join(runtimeRoot, 'generation-a');
  await mkdir(generation, { recursive: true });
  const child = path.join(generation, 'codex.exe');
  const codeModeHost = path.join(generation, 'codex-code-mode-host.exe');
  const adapter = path.join(temp, 'adapter.exe');
  const wrapper = path.join(temp, 'approval-wrapper.mjs');
  const transparent = path.join(temp, 'transparent-wrapper.mjs');
  const core = path.join(temp, 'bridge-core.mjs');
  const authority = path.join(temp, 'runtime-process-authority.mjs');
  await copyFile(process.execPath, child);
  await copyFile(process.execPath, codeModeHost);
  await copyFile(process.execPath, adapter);
  await copyFile(approvalWrapper, wrapper);
  await copyFile(transparentWrapper, transparent);
  await copyFile(bridgeCore, core);
  await copyFile(runtimeAuthority, authority);
  const sha256 = async filePath => createHash('sha256').update(await readFile(filePath)).digest('hex');
  const manifest = path.join(temp, `manifest-${approvalSinkPath ? 'sink' : 'empty'}.json`);
  await writeFile(manifest, JSON.stringify({
    schema: 'k15-codex-bridge/production-manifest-v2',
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
    runtimeAuthorityPath: authority,
    runtimeAuthoritySha256: await sha256(authority),
    childPath: child,
    childSha256: await sha256(child),
    codeModeHostPath: codeModeHost,
    codeModeHostSha256: await sha256(codeModeHost),
    approvalSinkPath
  }), 'utf8');
  return { manifest, runtimeRoot, generation, paths: { adapter, wrapper, transparent, core, authority, child, codeModeHost } };
}

async function updateManifest(manifest, changes) {
  const current = await readJson(manifest);
  await writeFile(manifest, JSON.stringify({ ...current, ...changes }), 'utf8');
  return current;
}

async function sha256(filePath) {
  return createHash('sha256').update(await readFile(filePath)).digest('hex');
}

async function addRuntimeGeneration(runtimeRoot, generationName, { child = false, codeModeHost = false } = {}) {
  const generation = path.join(runtimeRoot, generationName);
  await mkdir(generation, { recursive: true });
  if (child) await copyFile(process.execPath, path.join(generation, 'codex.exe'));
  if (codeModeHost) await copyFile(process.execPath, path.join(generation, 'codex-code-mode-host.exe'));
  return generation;
}

function absentEnvironment() {
  return Object.fromEntries(managedVariables.map(name => [name, { presence: 'ABSENT', value: '', registryKind: 'None' }]));
}

function environmentValues(entries) {
  return Object.fromEntries(Object.entries(entries)
    .filter(([, entry]) => entry.presence === 'PRESENT')
    .map(([name, entry]) => [name, entry.value]));
}

async function writeLegacyState(statePath, manifestPath, original) {
  await writeFile(statePath, JSON.stringify({
    schema: 'k15-codex-bridge/activation-state-v2',
    manifestPath: path.resolve(manifestPath),
    original
  }), 'utf8');
}

test('production activation validates exact files and pin without touching User or Machine environment', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const result = await powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]);
    assert.match(result.stdout, /VALID=YES/);
    assert.match(result.stdout, /PIN=EXACT/);
    assert.match(result.stdout, /CODE_MODE_HOST=PINNED/);
    assert.match(result.stdout, /SAME_GENERATION=YES/);
    assert.match(result.stdout, /MACHINE_ENV=UNCHANGED/);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Status reports INACTIVE without validating or mutating when activation state is absent', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const state = path.join(temp, 'missing-state.json');
    const environment = path.join(temp, 'environment.json');
    const status = await powershell([
      '-File', script,
      '-Mode', 'Status',
      '-ManifestPath', path.join(temp, 'missing-manifest.json'),
      '-StatePath', state,
      '-EnvironmentStorePath', environment
    ]);
    assert.match(status.stdout, /ACTIVE=NO/);
    assert.match(status.stdout, /RUNTIME_HEALTH=INACTIVE/);
    await assert.rejects(readFile(state, 'utf8'));
    await assert.rejects(readFile(environment, 'utf8'));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('production activation fails closed before mutation when the Code Mode host is missing', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, paths } = await createBundle(temp);
    await unlink(paths.codeModeHost);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    await writeFile(environment, JSON.stringify({ CODEX_CLI_PATH: 'stock-codex' }), 'utf8');
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Enable', '-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess']),
      error => error.code === 2
    );
    assert.deepEqual(await readJson(environment), { CODEX_CLI_PATH: 'stock-codex' });
    await assert.rejects(readFile(state, 'utf8'));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('legacy production manifest v1 cannot silently validate or enable', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    await updateManifest(manifest, { schema: 'k15-codex-bridge/production-manifest-v1' });
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]),
      error => error.code === 2
    );
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Enable', '-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess']),
      error => error.code === 2
    );
    await assert.rejects(readFile(state, 'utf8'));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('production activation fails closed on Code Mode host hash mismatch', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, paths } = await createBundle(temp);
    await writeFile(paths.codeModeHost, Buffer.concat([await readFile(paths.codeModeHost), Buffer.from('drift')]));
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]),
      error => error.code === 2
    );
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('production activation rejects a child and Code Mode host from different generations', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, paths, runtimeRoot } = await createBundle(temp);
    const otherGeneration = await addRuntimeGeneration(runtimeRoot, 'generation-b', { codeModeHost: true });
    const sha256 = filePath => readFile(filePath).then(bytes => createHash('sha256').update(bytes).digest('hex'));
    await updateManifest(manifest, {
      codeModeHostPath: path.join(otherGeneration, 'codex-code-mode-host.exe'),
      codeModeHostSha256: await sha256(path.join(otherGeneration, 'codex-code-mode-host.exe'))
    });
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]),
      error => error.code === 2
    );
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

test('production activation has no Runtime command dependency and pins the authority module', async () => {
  const activation = await readFile(script, 'utf8');
  const manifestExample = await readFile(path.join(bridgeRoot, 'production', 'manifest.example.json'), 'utf8');
  assert.equal(activation.includes('CODEX_BRIDGE_RUNTIME_COMMAND'), false);
  assert.equal(activation.includes('VOROTEX_K15_RUNTIME_NATIVE_STATUS_STDIN'), false);
  assert.match(manifestExample, /runtimeAuthorityPath/);
  assert.match(manifestExample, /runtimeAuthoritySha256/);
});

test('production activation rejects runtime authority module replacement-in-place', async () => {
  await assertReplacementRejected('authority');
});

test('production activation rejects reviewed decoy when the real imported sibling is modified before environment mutation', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, paths } = await createBundle(temp);
    const decoyDirectory = path.join(temp, 'reviewed-decoy');
    await mkdir(decoyDirectory);
    const decoy = path.join(decoyDirectory, 'transparent-wrapper.mjs');
    await copyFile(paths.transparent, decoy);
    await updateManifest(manifest, {
      transparentWrapperPath: decoy,
      transparentWrapperSha256: await sha256(decoy)
    });
    await writeFile(paths.transparent, Buffer.concat([await readFile(paths.transparent), Buffer.from('\nmodified-real-sibling\n')]));
    const environment = path.join(temp, 'environment.json');
    await writeFile(environment, JSON.stringify({ CODEX_CLI_PATH: 'unchanged' }), 'utf8');
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Enable', '-ManifestPath', manifest, '-StatePath', path.join(temp, 'state.json'), '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess']),
      error => error.code === 2
    );
    assert.deepEqual(await readJson(environment), { CODEX_CLI_PATH: 'unchanged' });
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

async function assertGuardBlocked(inventory, expectedMessage) {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const inventoryPath = path.join(temp, 'processes.json');
    await writeProcessInventory(inventoryPath, inventory);
    const result = await assert.rejects(
      powershell(['-File', script, '-Mode', 'Enable', '-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-ProcessInventoryPath', inventoryPath, '-BroadcastMode', 'FakeSuccess']),
      error => error.code === 2 && error.stderr.includes(expectedMessage)
    );
    assert.equal(result, undefined);
    await assert.rejects(readFile(state, 'utf8'));
    await assert.rejects(readFile(environment, 'utf8'));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
}

test('CODEX_UI_CHATGPT_ALIVE_BACKEND_ABSENT_BLOCKED', async () => {
  await assertGuardBlocked([{ name: 'ChatGPT.exe', path: '' }], 'CODEX_PROCESS_GUARD=CHATGPT_UI_ALIVE');
});

test('CODEX_BACKEND_ALIVE_BLOCKED', async () => {
  await assertGuardBlocked([{ name: 'codex.exe', path: '' }], 'CODEX_PROCESS_GUARD=BACKEND_ALIVE');
});

test('NO_CODEX_PROCESS_ALLOWED', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const inventoryPath = path.join(temp, 'processes.json');
    await writeProcessInventory(inventoryPath, []);
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-ProcessInventoryPath', inventoryPath, '-BroadcastMode', 'FakeSuccess'];
    const enabled = await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    assert.match(enabled.stdout, /ACTIVE=YES/);
    const disabled = await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.match(disabled.stdout, /ACTIVE=NO/);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('AMBIGUOUS_CHATGPT_IDENTITY_FAILS_CLOSED', async () => {
  await assertGuardBlocked([{ name: 'ChatGPT', path: '' }], 'CODEX_PROCESS_GUARD=CHATGPT_UI_ALIVE');
});

test('ISOLATED_PROCESS_FIXTURE_CANNOT_BYPASS_REAL_TARGET', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const inventoryPath = path.join(temp, 'processes.json');
    await writeProcessInventory(inventoryPath, []);
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Enable', '-ManifestPath', manifest, '-StatePath', path.join(temp, 'state.json'), '-ProcessInventoryPath', inventoryPath]),
      error => error.code === 2
    );
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('production activation rejects noncanonical and reparse child paths before environment mutation', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  const bundle = path.join(temp, 'bundle');
  const junction = path.join(temp, 'bundle-junction');
  try {
    await mkdir(bundle);
    const { manifest } = await createBundle(bundle);
    const noncanonical = await readJson(manifest);
    noncanonical.childPath = bundle + '\\runtime-bin\\generation-a\\..\\generation-a\\codex.exe';
    await writeFile(manifest, JSON.stringify(noncanonical), 'utf8');
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]),
      error => error.code === 2
    );

    await symlink(bundle, junction, 'junction');
    const reparse = await readJson(manifest);
    reparse.childPath = path.join(junction, 'runtime-bin', 'generation-a', 'codex.exe');
    await writeFile(manifest, JSON.stringify(reparse), 'utf8');
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]),
      error => error.code === 2
    );
  } finally {
    await rm(junction, { recursive: true, force: true });
    await rm(temp, { recursive: true, force: true });
  }
});

test('activation script documents bounded disable, no Machine env, and no package/injection path', async () => {
  const source = await readFile(script, 'utf8');
  assert.match(source, /Validate.*Enable.*Disable.*Status/);
  assert.match(source, /Registry\]::CurrentUser/);
  assert.match(source, /DoNotExpandEnvironmentNames/);
  assert.doesNotMatch(source, /EnvironmentVariableTarget\.Machine/);
  assert.doesNotMatch(source, /WindowsApps|CreateRemoteThread|OpenProcess|VirtualAllocEx/);
});

test('isolated Windows registry primitive preserves mixed absent, present-empty, and present values exactly', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  const registrySubKey = 'Software\\KostGame\\K15CodexBridgeTests\\' + createHash('sha256').update(temp).digest('hex').slice(0, 32);
  const baseline = {
    CODEX_CLI_PATH: { presence: 'ABSENT', value: '', registryKind: 'None' },
    CODEX_BRIDGE_NODE_PATH: { presence: 'PRESENT', value: '', registryKind: 'String' },
    CODEX_BRIDGE_WRAPPER_PATH: { presence: 'PRESENT', value: 'stock-wrapper', registryKind: 'String' },
    CODEX_BRIDGE_CHILD_PATH: { presence: 'PRESENT', value: '%USERPROFILE%\\stock-child.exe', registryKind: 'ExpandString' },
    CODEX_BRIDGE_CHILD_SHA256: { presence: 'ABSENT', value: '', registryKind: 'None' },
    CODEX_BRIDGE_APPROVAL_SINK_PATH: { presence: 'PRESENT', value: '', registryKind: 'String' }
  };
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    await writeIsolatedRegistryEnvironment(registrySubKey, baseline);
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-UserEnvironmentRegistrySubKey', registrySubKey, '-BroadcastMode', 'FakeSuccess'];
    const enabled = await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    assert.match(enabled.stdout, /ACTIVE=YES/);
    assert.match(enabled.stdout, /USER_ENV_MUTATED=YES/);
    const activationState = await readJson(state);
    assert.equal(activationState.schema, 'k15-codex-bridge/activation-state-v3');
    assert.equal(activationState.manifestSha256, await sha256(manifest));
    assert.equal(activationState.runtimeBaseline.approvedGeneration, 'generation-a');
    assert.deepEqual(activationState.runtimeBaseline.runtimeInventory, [{
      generation: 'generation-a',
      codexExePresent: true,
      codeModeHostPresent: true
    }]);
    assert.deepEqual(activationState.original, baseline);
    await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.deepEqual(await readIsolatedRegistryEnvironment(registrySubKey), baseline);
    await assert.rejects(readFile(state, 'utf8'));
  } finally {
    await removeIsolatedRegistryEnvironment(registrySubKey);
    await rm(temp, { recursive: true, force: true });
  }
});

test('isolated Enable, Status, Disable round-trip clears stale empty sink and restores exact state', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const original = { CODEX_CLI_PATH: 'stock-codex', CODEX_BRIDGE_APPROVAL_SINK_PATH: 'stale-sink' };
    await writeFile(environment, JSON.stringify(original), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    const enabled = await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    assert.match(enabled.stdout, /ACTIVE=YES/);
    const enabledEnvironment = await readJson(environment);
    assert.equal(Object.hasOwn(enabledEnvironment, 'CODEX_BRIDGE_APPROVAL_SINK_PATH'), false);
    const status = await powershell(['-File', script, '-Mode', 'Status', ...common]);
    assert.match(status.stdout, /ACTIVE=YES/);
    assert.match(status.stdout, /RUNTIME_HEALTH=HEALTHY/);
    await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.deepEqual(await readJson(environment), original);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Status requires the enabled manifest identity when the approved host pin is replaced', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, paths } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    const beforeState = await readFile(state, 'utf8');
    const beforeEnvironment = await readJson(environment);
    await writeFile(paths.codeModeHost, Buffer.concat([await readFile(paths.codeModeHost), Buffer.from('replacement-host')]), 'utf8');
    await updateManifest(manifest, { codeModeHostSha256: await sha256(paths.codeModeHost) });
    const status = await powershell(['-File', script, '-Mode', 'Status', ...common]);
    assert.match(status.stdout, /ACTIVE=YES/);
    assert.match(status.stdout, /RUNTIME_HEALTH=UPDATE_REVALIDATION_REQUIRED/);
    assert.match(status.stdout, /REASON=MANIFEST_CHANGED_SINCE_ENABLE/);
    assert.doesNotMatch(status.stdout, /RUNTIME_HEALTH=HEALTHY/);
    assert.equal(await readFile(state, 'utf8'), beforeState);
    assert.deepEqual(await readJson(environment), beforeEnvironment);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Status requires exact manifest bytes even when only benign JSON formatting changes', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    const beforeState = await readFile(state, 'utf8');
    const beforeEnvironment = await readJson(environment);
    await writeFile(manifest, JSON.stringify(await readJson(manifest), null, 2) + '\n', 'utf8');
    const status = await powershell(['-File', script, '-Mode', 'Status', ...common]);
    assert.match(status.stdout, /RUNTIME_HEALTH=UPDATE_REVALIDATION_REQUIRED/);
    assert.match(status.stdout, /REASON=MANIFEST_CHANGED_SINCE_ENABLE/);
    assert.doesNotMatch(status.stdout, /RUNTIME_HEALTH=HEALTHY/);
    assert.equal(await readFile(state, 'utf8'), beforeState);
    assert.deepEqual(await readJson(environment), beforeEnvironment);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Status requires the canonical manifest path used at Enable', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const alternateManifest = path.join(temp, 'alternate-manifest.json');
    await copyFile(manifest, alternateManifest);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const common = ['-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await powershell(['-File', script, '-Mode', 'Enable', '-ManifestPath', manifest, ...common]);
    const status = await powershell(['-File', script, '-Mode', 'Status', '-ManifestPath', alternateManifest, ...common]);
    assert.match(status.stdout, /ACTIVE=YES/);
    assert.match(status.stdout, /RUNTIME_HEALTH=UPDATE_REVALIDATION_REQUIRED/);
    assert.match(status.stdout, /REASON=MANIFEST_PATH_CHANGED_SINCE_ENABLE/);
    assert.doesNotMatch(status.stdout, /RUNTIME_HEALTH=HEALTHY/);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Disable restores the v3 rollback baseline even when the current manifest is gone', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    await writeFile(environment, JSON.stringify({ CODEX_CLI_PATH: 'stock-codex' }), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    await unlink(manifest);
    const disabled = await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.match(disabled.stdout, /ACTIVE=NO/);
    assert.deepEqual(await readJson(environment), { CODEX_CLI_PATH: 'stock-codex' });
    await assert.rejects(readFile(state, 'utf8'));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Status reports UPDATE_REVALIDATION_REQUIRED for a new coherent generation without adoption or mutation', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, runtimeRoot, paths } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    const beforeState = await readFile(state, 'utf8');
    const beforeManifest = await readFile(manifest, 'utf8');
    const beforeEnvironment = await readJson(environment);
    await addRuntimeGeneration(runtimeRoot, 'generation-b', { child: true, codeModeHost: true });
    const status = await powershell(['-File', script, '-Mode', 'Status', ...common]);
    assert.match(status.stdout, /ACTIVE=YES/);
    assert.match(status.stdout, /RUNTIME_HEALTH=UPDATE_REVALIDATION_REQUIRED/);
    assert.match(status.stdout, /REASON=RUNTIME_INVENTORY_CHANGED/);
    assert.equal(await readFile(state, 'utf8'), beforeState);
    assert.equal(await readFile(manifest, 'utf8'), beforeManifest);
    assert.deepEqual(await readJson(environment), beforeEnvironment);
    assert.equal(beforeEnvironment.CODEX_BRIDGE_CHILD_PATH, paths.child);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Status reports UPDATE_REVALIDATION_REQUIRED for a partial generation without adoption', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest, runtimeRoot } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    const beforeState = await readFile(state, 'utf8');
    await addRuntimeGeneration(runtimeRoot, 'generation-partial', { child: true });
    const status = await powershell(['-File', script, '-Mode', 'Status', ...common]);
    assert.match(status.stdout, /RUNTIME_HEALTH=UPDATE_REVALIDATION_REQUIRED/);
    assert.equal(await readFile(state, 'utf8'), beforeState);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

for (const [label, mutate] of [
  ['approved child missing', async paths => unlink(paths.child)],
  ['approved child bytes changed', async paths => writeFile(paths.child, Buffer.concat([await readFile(paths.child), Buffer.from('drift')]))],
  ['approved host missing', async paths => unlink(paths.codeModeHost)],
  ['approved host bytes changed', async paths => writeFile(paths.codeModeHost, Buffer.concat([await readFile(paths.codeModeHost), Buffer.from('drift')]))]
]) {
  test(`Status reports CHILD_RUNTIME_STALE when ${label}`, async () => {
    const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
    try {
      const { manifest, paths } = await createBundle(temp);
      const state = path.join(temp, 'state.json');
      const environment = path.join(temp, 'environment.json');
      const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
      await powershell(['-File', script, '-Mode', 'Enable', ...common]);
      await mutate(paths);
      const status = await powershell(['-File', script, '-Mode', 'Status', ...common]);
      assert.match(status.stdout, /ACTIVE=YES/);
      assert.match(status.stdout, /RUNTIME_HEALTH=CHILD_RUNTIME_STALE/);
      assert.match(status.stdout, /REASON=APPROVED_RUNTIME_OR_ACTIVATION_INVALID/);
    } finally {
      await rm(temp, { recursive: true, force: true });
    }
  });
}

test('legacy activation-state-v2 Status requires owner revalidation', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    await writeLegacyState(state, manifest, absentEnvironment());
    const status = await powershell(['-File', script, '-Mode', 'Status', '-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment]);
    assert.match(status.stdout, /ACTIVE=YES/);
    assert.match(status.stdout, /RUNTIME_HEALTH=UPDATE_REVALIDATION_REQUIRED/);
    assert.match(status.stdout, /REASON=LEGACY_STATE_NO_RUNTIME_BASELINE/);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('legacy activation-state-v2 Disable restores exact presence including PRESENT empty and retains retryable state until broadcast succeeds', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const original = {
      CODEX_CLI_PATH: { presence: 'PRESENT', value: 'stock-codex', registryKind: 'String' },
      CODEX_BRIDGE_NODE_PATH: { presence: 'PRESENT', value: '', registryKind: 'String' },
      CODEX_BRIDGE_WRAPPER_PATH: { presence: 'ABSENT', value: '', registryKind: 'None' },
      CODEX_BRIDGE_CHILD_PATH: { presence: 'PRESENT', value: 'stock-child', registryKind: 'ExpandString' },
      CODEX_BRIDGE_CHILD_SHA256: { presence: 'ABSENT', value: '', registryKind: 'None' },
      CODEX_BRIDGE_APPROVAL_SINK_PATH: { presence: 'ABSENT', value: '', registryKind: 'None' }
    };
    await writeLegacyState(state, manifest, original);
    await writeFile(environment, JSON.stringify({
      CODEX_CLI_PATH: 'active-codex',
      CODEX_BRIDGE_NODE_PATH: 'active-node',
      CODEX_BRIDGE_WRAPPER_PATH: 'active-wrapper',
      CODEX_BRIDGE_CHILD_PATH: 'active-child',
      CODEX_BRIDGE_CHILD_SHA256: 'active-sha',
      CODEX_BRIDGE_APPROVAL_SINK_PATH: 'active-sink'
    }), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment];
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Disable', ...common, '-BroadcastMode', 'FakeFailure']),
      error => error.code === 2
    );
    assert.deepEqual(await readJson(environment), environmentValues(original));
    await readFile(state, 'utf8');
    const retried = await powershell(['-File', script, '-Mode', 'Disable', ...common, '-BroadcastMode', 'FakeSuccess']);
    assert.match(retried.stdout, /ACTIVE=NO/);
    assert.deepEqual(await readJson(environment), environmentValues(original));
    await assert.rejects(readFile(state, 'utf8'));
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
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
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
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-EnvironmentStoreFailOnSet', 'CODEX_BRIDGE_CHILD_PATH', '-BroadcastMode', 'FakeSuccess'];
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Enable', ...common]),
      error => error.code === 2 && /USER_ENV_MUTATED=YES/.test(error.stderr)
    );
    assert.deepEqual(await readJson(environment), original);
    await assert.rejects(readFile(state, 'utf8'));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('Enable active postcheck failure restores the exact baseline and reports the safe mismatch', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const original = { CODEX_CLI_PATH: 'stock-codex', CODEX_BRIDGE_NODE_PATH: '' };
    await writeFile(environment, JSON.stringify(original), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Enable', ...common, '-EnvironmentStorePostcheckMismatch', 'EnableActive:CODEX_CLI_PATH']),
      error => error.code === 2
        && /VARIABLE=CODEX_CLI_PATH/.test(error.stderr)
        && /EXPECTED=PRESENT/.test(error.stderr)
        && /CURRENT=ABSENT/.test(error.stderr)
        && /VALUE_MATCH=NO/.test(error.stderr)
        && /USER_ENV_MUTATED=YES/.test(error.stderr)
    );
    assert.deepEqual(await readJson(environment), original);
    await assert.rejects(readFile(state, 'utf8'));
  } finally { await rm(temp, { recursive: true, force: true }); }
});

test('Enable broadcasts after exact writes and blocks on broadcast failure without Desktop launch', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    await writeFile(environment, JSON.stringify({ CODEX_CLI_PATH: 'stock-codex' }), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeFailure'];
    await assert.rejects(powershell(['-File', script, '-Mode', 'Enable', ...common]));
    assert.deepEqual(await readJson(environment), { CODEX_CLI_PATH: 'stock-codex' });
    await readFile(state, 'utf8');
  } finally { await rm(temp, { recursive: true, force: true }); }
});

test('Disable restores exact User env but remains loud and retry-safe when broadcast fails', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const original = { CODEX_CLI_PATH: 'stock-codex' };
    await writeFile(environment, JSON.stringify(original), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment];
    await powershell(['-File', script, '-Mode', 'Enable', ...common, '-BroadcastMode', 'FakeSuccess']);
    await assert.rejects(powershell(['-File', script, '-Mode', 'Disable', ...common, '-BroadcastMode', 'FakeFailure']));
    assert.deepEqual(await readJson(environment), original);
    await readFile(state, 'utf8');
  } finally { await rm(temp, { recursive: true, force: true }); }
});

test('Disable restore postcheck failure retains activation state and a retry completes safely', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    const state = path.join(temp, 'state.json');
    const environment = path.join(temp, 'environment.json');
    const original = { CODEX_CLI_PATH: 'stock-codex', CODEX_BRIDGE_NODE_PATH: '' };
    await writeFile(environment, JSON.stringify(original), 'utf8');
    const common = ['-ManifestPath', manifest, '-StatePath', state, '-EnvironmentStorePath', environment, '-BroadcastMode', 'FakeSuccess'];
    await powershell(['-File', script, '-Mode', 'Enable', ...common]);
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Disable', ...common, '-EnvironmentStorePostcheckMismatch', 'DisableBaseline:CODEX_CLI_PATH']),
      error => error.code === 2
        && /VARIABLE=CODEX_CLI_PATH/.test(error.stderr)
        && /EXPECTED=PRESENT/.test(error.stderr)
        && /CURRENT=ABSENT/.test(error.stderr)
        && /VALUE_MATCH=NO/.test(error.stderr)
        && /USER_ENV_MUTATED=YES/.test(error.stderr)
    );
    await readFile(state, 'utf8');
    assert.deepEqual(await readJson(environment), original);
    const retried = await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.match(retried.stdout, /ACTIVE=NO/);
    assert.match(retried.stdout, /USER_ENV_MUTATED=YES/);
    const repeated = await powershell(['-File', script, '-Mode', 'Disable', ...common]);
    assert.match(repeated.stdout, /ACTIVE=NO/);
    assert.match(repeated.stdout, /USER_ENV_MUTATED=NO/);
  } finally { await rm(temp, { recursive: true, force: true }); }
});

test('fake broadcast modes require an isolated environment store before mutation', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  try {
    const { manifest } = await createBundle(temp);
    for (const mode of ['FakeSuccess', 'FakeFailure']) {
      const state = path.join(temp, `${mode}.state.json`);
      await assert.rejects(
        powershell(['-File', script, '-Mode', 'Enable', '-ManifestPath', manifest, '-StatePath', state, '-BroadcastMode', mode]),
        error => error.code === 2
      );
      await assert.rejects(readFile(state, 'utf8'));
    }
  } finally { await rm(temp, { recursive: true, force: true }); }
});

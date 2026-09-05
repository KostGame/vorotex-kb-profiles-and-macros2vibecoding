import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
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

test('production activation rejects noncanonical and reparse child paths before environment mutation', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'k15-codex-production-'));
  const bundle = path.join(temp, 'bundle');
  const junction = path.join(temp, 'bundle-junction');
  try {
    await mkdir(bundle);
    const { manifest } = await createBundle(bundle);
    const noncanonical = await readJson(manifest);
    noncanonical.childPath = bundle + '\\..\\bundle\\child.mjs';
    await writeFile(manifest, JSON.stringify(noncanonical), 'utf8');
    await assert.rejects(
      powershell(['-File', script, '-Mode', 'Validate', '-ManifestPath', manifest]),
      error => error.code === 2
    );

    await symlink(bundle, junction, 'junction');
    const reparse = await readJson(manifest);
    reparse.childPath = path.join(junction, 'child.mjs');
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
    assert.equal(activationState.schema, 'k15-codex-bridge/activation-state-v2');
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

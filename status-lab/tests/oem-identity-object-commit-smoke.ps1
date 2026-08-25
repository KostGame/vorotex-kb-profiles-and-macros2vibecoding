$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$analyzer = Join-Path $root 'hid-research-lab\OemIdentityObjectCommitTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemIdentityObjectCommitUi.cs'

foreach ($path in @($analyzer, $ui)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$analyzerText = Get-Content -LiteralPath $analyzer -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$all = $analyzerText + "`n" + $uiText

foreach ($required in @(
    'IDENTITY_OBJECT_COMMIT_COMPLETE',
    'IDENTITY_OBJECT_COMMIT_PARTIAL',
    'STAGING_TO_JOIN_TRACED',
    'IDENTITY_OBJECT_COMMIT_UNRESOLVED',
    'FindRepeatedForwardJoin',
    'TraceCaseCfg',
    'StagingSlots',
    'GuardObjectBase',
    'direct-member-write',
    'semantic-cduistring-copy',
    'A direct member-offset match is not proof',
    'oem-identity-object-commit.json',
    'oem-identity-object-commit.txt',
    'Run object commit trace'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Required object-commit marker missing: $required" }
}

foreach ($forbidden in @(
    'HidD_SetFeature',
    'HidD_GetFeature',
    'DeviceIoControl',
    'CreateFile(',
    'DebugActiveProcess',
    'OpenProcess(',
    'WriteProcessMemory',
    'VirtualAllocEx',
    'CreateRemoteThread',
    'SetWindowsHookEx',
    'RegistryKey',
    'SelectActiveSlot(',
    'ResetDevice('
)) {
    if ($analyzerText -match [regex]::Escape($forbidden)) { throw "Forbidden device/process mutation surface found in object commit analyzer: $forbidden" }
}

foreach ($requiredSafety in @(
    'firmwareModified\s*=\s*false',
    'deviceOpened\s*=\s*false',
    'processAttached\s*=\s*false',
    'processInjected\s*=\s*false',
    'hidWritesPerformed\s*=\s*false',
    'productStringSpoofed\s*=\s*false',
    'registryModified\s*=\s*false'
)) {
    if ($analyzerText -notmatch $requiredSafety) { throw "Missing explicit safety assertion: $requiredSafety" }
}

if ($analyzerText -notmatch 'var proven = explicitChains.Count > 0') {
    throw 'Side-level PROVEN predicate must require an explicit staging-to-member chain.'
}
if ($analyzerText -notmatch 'nameProven = nameCorrespondence && a.DevName.Proven && b.DevName.Proven') {
    throw 'Combined DevName proof must require both OEM sides and commit-chain correspondence.'
}
if ($analyzerText -notmatch 'cmpProven = cmpCorrespondence && a.DevCmpStr.Proven && b.DevCmpStr.Proven') {
    throw 'Combined DevCmpStr proof must require both OEM sides and commit-chain correspondence.'
}
if ($analyzerText -notmatch 'Stack/local values are only staging candidates when written on the field match CFG and read again after the recovered common join') {
    throw 'Object commit trace must require staging survival across the parser join.'
}

Write-Host 'OEM Identity Object Commit read-only safety smoke: PASS'

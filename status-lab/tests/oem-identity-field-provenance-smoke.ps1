$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$analyzer = Join-Path $root 'hid-research-lab\OemIdentityFieldProvenanceTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemIdentityGateTraceUi.cs'

foreach ($path in @($analyzer, $ui)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$analyzerText = Get-Content -LiteralPath $analyzer -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$all = $analyzerText + "`n" + $uiText

foreach ($required in @(
    'IDENTITY_FIELD_PROVENANCE_COMPLETE',
    'IDENTITY_FIELD_PROVENANCE_PARTIAL',
    'PARSER_MATCH_PATHS_TRACED',
    'IDENTITY_FIELD_PROVENANCE_UNRESOLVED',
    'DevNameToMember20Proven',
    'DevCmpStrToMember3EcProven',
    'WalkMatchPath',
    'TraceHelper',
    'DirectWriteToExpectedMember',
    'oem-identity-field-provenance.json',
    'oem-identity-field-provenance.txt',
    'Run field provenance trace'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Required field-provenance marker missing: $required" }
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
    if ($analyzerText -match [regex]::Escape($forbidden)) { throw "Forbidden device/process mutation surface found in field provenance analyzer: $forbidden" }
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

if ($analyzerText -notmatch 'A field is marked PROVEN only for an explicit branch-local persistent memory write') {
    throw 'Field provenance must explicitly reject helper/member proximity as proof.'
}
if ($analyzerText -notmatch 'var proven = directExpectedWrites.Count > 0') {
    throw 'PROVEN predicate must require explicit branch-local persistent writes.'
}
if ($analyzerText -match 'helperExpectedUses.Count > 0\s*;\s*$') {
    throw 'Helper member use must not independently produce PROVEN.'
}

Write-Host 'OEM Identity Field Provenance read-only safety smoke: PASS'

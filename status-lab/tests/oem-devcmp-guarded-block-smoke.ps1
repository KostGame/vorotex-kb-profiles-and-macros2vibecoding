$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$analyzer = Join-Path $root 'hid-research-lab\OemDevCmpGuardedBlockTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemIdentityGateTraceUi.cs'

foreach ($path in @($analyzer, $ui)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$analyzerText = Get-Content -LiteralPath $analyzer -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$all = $analyzerText + "`n" + $uiText

foreach ($required in @(
    'DEVNAME_PRODUCTSTRING_COMPARE_PROVEN',
    'GUARDED_MEMBER_COMPARE_LIKELY',
    'GUARDED_BLOCK_UNRESOLVED',
    'DevCmpMember = 0x3EC',
    'DevNameMember = 0x20',
    'RecordStride = 0x84',
    'FindDevCmpGuard',
    'FindFlagsProducer',
    'TraceField',
    'ProductDataFlowsIntoFlags',
    'DevNameDataFlowsIntoFlags',
    'oem-devcmp-guarded-block.json',
    'Run guarded block trace'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Required guarded-block marker missing: $required" }
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
    if ($analyzerText -match [regex]::Escape($forbidden)) { throw "Forbidden device/process mutation surface found in guarded-block analyzer: $forbidden" }
}

foreach ($requiredSafety in @(
    'firmwareModified\s*=\s*false',
    'deviceOpened\s*=\s*false',
    'processAttached\s*=\s*false',
    'hidWritesPerformed\s*=\s*false',
    'registryModified\s*=\s*false'
)) {
    if ($analyzerText -notmatch $requiredSafety) { throw "Missing explicit safety assertion: $requiredSafety" }
}

if ($analyzerText -notmatch 'private static bool IsProven') {
    throw 'PROVEN verdict must be protected by a dedicated conservative predicate.'
}
if ($analyzerText -notmatch '(?s)IsProven\(OemGuardedBlockSide side\).*DevCmpStrTrace\.MapsExpectedMember.*DevNameTrace\.MapsExpectedMember.*ProductDataFlowsIntoFlags.*DevNameDataFlowsIntoFlags.*FlagsProducerRva') {
    throw 'PROVEN predicate must require both parser/runtime mappings and ProductString/DevName provenance into flags.'
}

Write-Host 'OEM DevCmpStr Guarded Block read-only safety smoke: PASS'

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$analyzer = Join-Path $root 'hid-research-lab\OemIdentityGateTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemIdentityGateTraceUi.cs'
$program = Join-Path $root 'hid-research-lab\Program.cs'

foreach ($path in @($analyzer, $ui, $program)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$analyzerText = Get-Content -LiteralPath $analyzer -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$programText = Get-Content -LiteralPath $program -Raw
$all = $analyzerText + "`n" + $uiText

foreach ($required in @(
    'Ndevice.json',
    'DevCmpStr',
    'DevName',
    'UITextName',
    'HidD_GetProductString',
    'PRODUCT_STRING_GATE_LIKELY',
    'VID_PID_GATE_NOT_SUPPORTED_BY_MODEL_TABLE',
    'IDENTITY_GATE_UNRESOLVED',
    'oem-identity-gate-trace.json',
    'OemIdentityGateTraceUi.Attach'
)) {
    $haystack = if ($required -eq 'OemIdentityGateTraceUi.Attach') { $programText } else { $all }
    if ($haystack -notmatch [regex]::Escape($required)) { throw "Required identity-gate trace marker missing: $required" }
}

foreach ($forbidden in @(
    'HidD_SetFeature',
    'HidD_GetFeature',
    'DeviceIoControl',
    'DebugActiveProcess',
    'WriteProcessMemory',
    'VirtualAllocEx',
    'CreateRemoteThread',
    'RegistryKey',
    'SetDevice',
    'SelectActiveSlot'
)) {
    if ($all -match [regex]::Escape($forbidden)) { throw "Forbidden mutating/attach surface found in identity-gate trace: $forbidden" }
}

if ($analyzerText -match 'PRODUCT_STRING_GATE_PROVEN_STATICALLY"\s*;') {
    throw 'Analyzer must not emit PRODUCT_STRING_GATE_PROVEN_STATICALLY without concrete data-flow proof.'
}

Write-Host 'OEM Identity Gate Trace read-only safety smoke: PASS'

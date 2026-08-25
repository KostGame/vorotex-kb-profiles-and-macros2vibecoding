$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$analyzer = Join-Path $root 'hid-research-lab\OemProductCompareBranchTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemIdentityGateTraceUi.cs'
$project = Join-Path $root 'hid-research-lab\Vorotex.K15.HidResearchLab.csproj'

foreach ($path in @($analyzer, $ui, $project)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$analyzerText = Get-Content -LiteralPath $analyzer -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$projectText = Get-Content -LiteralPath $project -Raw
$all = $analyzerText + "`n" + $uiText + "`n" + $projectText

foreach ($required in @(
    'Iced',
    'PRODUCT_STRING_COMPARE_BRANCH_PROVEN',
    'PRODUCT_STRING_COMPARE_HELPER_LIKELY',
    'COMPARE_BRANCH_UNRESOLVED',
    'ProductBufferArgumentMatch',
    'DevNameArgumentMatch',
    'FlowControl.ConditionalBranch',
    'KnownCompareImports',
    'oem-product-compare-branch.json',
    'Run compare branch trace'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Required compare-branch marker missing: $required" }
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
    'SelectActiveSlot',
    'Firmware',
    'ResetDevice'
)) {
    if ($analyzerText -match [regex]::Escape($forbidden)) { throw "Forbidden device/process mutation surface found in compare-branch analyzer: $forbidden" }
}

if ($analyzerText -notmatch 'IsProvenCandidate') {
    throw 'PROVEN verdict must be guarded by a dedicated conservative predicate.'
}
if ($analyzerText -notmatch 'IsKnownCompareImport\(candidate\.ImportName\).*ProductBufferArgumentMatch.*DevNameArgumentMatch' -and
    $analyzerText -notmatch '(?s)IsProvenCandidate.*IsKnownCompareImport\(candidate\.ImportName\).*candidate\.ProductBufferArgumentMatch.*candidate\.DevNameArgumentMatch.*candidate\.BranchRva') {
    throw 'PROVEN predicate must require compare import + ProductString buffer + DevName argument + conditional branch.'
}

Write-Host 'OEM Product Compare Branch Trace read-only safety smoke: PASS'

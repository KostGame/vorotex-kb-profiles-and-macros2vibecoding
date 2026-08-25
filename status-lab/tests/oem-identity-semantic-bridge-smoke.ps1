$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$analyzer = Join-Path $root 'hid-research-lab\OemIdentitySemanticBridgeTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemIdentityGateTraceUi.cs'

foreach ($path in @($analyzer, $ui)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$analyzerText = Get-Content -LiteralPath $analyzer -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$all = $analyzerText + "`n" + $uiText

foreach ($required in @(
    'DEVNAME_PRODUCTSTRING_COMPARE_PROVEN',
    'COMPARE_HELPER_SEMANTICS_RESOLVED',
    'ALIGNED_XREFS_RECOVERED',
    'SEMANTIC_BRIDGE_UNRESOLVED',
    'FindAlignedTokenXrefs',
    'BuildRawXrefs',
    'OperandReferences',
    'ParseImports',
    'TraceHelper',
    'BooleanReturnFeedsFlags',
    'CompareSymbolLooksSemantic',
    'oem-identity-semantic-bridge.json',
    'oem-identity-semantic-bridge.txt',
    'Run semantic bridge trace'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Required semantic-bridge marker missing: $required" }
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
    if ($analyzerText -match [regex]::Escape($forbidden)) { throw "Forbidden device/process mutation surface found in semantic bridge analyzer: $forbidden" }
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

if ($analyzerText -notmatch 'Instruction-aligned xrefs are accepted only when a decoded operand equals the exact token VA/RVA') {
    throw 'Semantic bridge must explicitly reject raw-byte proximity as proof.'
}
if ($analyzerText -notmatch '(?s)var proven = structural && symbolCorrespondence &&.*DevCmpStrMapsTo3Ec.*DevNameMapsTo20.*BooleanReturnFeedsFlags.*CompareSymbolLooksSemantic') {
    throw 'PROVEN predicate must require aligned member mappings and explicit boolean compare semantics.'
}
if ($analyzerText -notmatch 'REJECTED: raw hit is inside a decoded instruction/data fragment') {
    throw 'Raw non-instruction-aligned xrefs must be explicitly rejected.'
}

Write-Host 'OEM Identity Semantic Bridge read-only safety smoke: PASS'

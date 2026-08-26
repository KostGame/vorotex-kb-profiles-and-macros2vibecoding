$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$trace = Join-Path $root 'hid-research-lab\OemKeyboardSleepReportPayloadHelperSemanticsTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemKeyboardSleepReportPayloadHelperSemanticsUi.cs'
foreach ($path in @($trace, $ui)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$traceText = Get-Content -LiteralPath $trace -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$all = $traceText + "`n" + $uiText

foreach ($required in @(
    'REPORT_PAYLOAD_HELPER_SEMANTICS_PROVEN',
    'REPORT_PAYLOAD_HELPERS_STRUCTURALLY_CORRESPONDING',
    'REPORT_PAYLOAD_HELPERS_POSITIONALLY_CORRESPONDING',
    'REPORT_PAYLOAD_HELPER_TRACE_UNRESOLVED',
    'RelativeToSetFeature',
    'MEMSET_LIKE',
    'MEMCPY_LIKE',
    'ZERO_FILL_CANDIDATE',
    'BOUNDED_COPY_CANDIDATE',
    'SemanticProven',
    'REP STOS',
    'REP MOVS',
    'AnalyzeKeyboardSleepPayloadHelperSemantics',
    'oem-keyboard-sleep-payload-helper-semantics.json',
    'oem-keyboard-sleep-payload-helper-semantics.txt',
    'Run payload helper semantics trace',
    'hidWritesPerformed = false',
    'deviceOpened = false',
    'reportReplayed = false'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Missing required payload-helper token: $required" }
}

foreach ($forbidden in @(
    'DllImport',
    'LibraryImport',
    'CreateFile(',
    'DeviceIoControl(',
    'WriteFile(',
    'ReadFile(',
    'OpenProcess(',
    'WriteProcessMemory(',
    'VirtualAllocEx(',
    'CreateRemoteThread(',
    'SetWindowsHookEx(',
    'SetupDiSet',
    'RegSetValue',
    'Process.Start(exeA',
    'Process.Start(exeB'
)) {
    if ($all -match [regex]::Escape($forbidden)) { throw "Forbidden mutation/process surface in payload-helper trace: $forbidden" }
}

if ($all -match 'extern\s+.*HidD_SetFeature') {
    throw 'Payload-helper analyzer must not declare/invoke HidD_SetFeature.'
}
if ($traceText -notmatch 'intentionally ignoring OEM-specific target RVA/delta') {
    throw 'Payload-helper pairing must explicitly ignore OEM direct-target relocation.'
}
if ($traceText -notmatch 'ABI shape alone never sets SemanticProven=true') {
    throw 'Caller ABI must not promote helper semantics by itself.'
}
if ($traceText -notmatch 'rep-stos/rep-movs primitive') {
    throw 'Helper semantic proof must be tied to decoded body primitives/imports.'
}

Write-Host 'OEM keyboard SleepTime payload-helper read-only safety smoke: PASS'

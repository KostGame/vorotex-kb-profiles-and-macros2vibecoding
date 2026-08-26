$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$trace = Join-Path $root 'hid-research-lab\OemKeyboardSleepReportTrace.cs'
$ui = Join-Path $root 'hid-research-lab\OemKeyboardSleepReportUi.cs'

foreach ($path in @($trace, $ui)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$traceText = Get-Content -LiteralPath $trace -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$all = $traceText + "`n" + $uiText

foreach ($required in @(
    'KEYBOARD_SLEEP_SETFEATURE_REPORT_PROVEN',
    'KEYBOARD_SLEEP_REPORT_PARTIAL',
    'SLEEPTIME_TO_TRANSPORT_TRACED',
    'KEYBOARD_SLEEP_REPORT_UNRESOLVED',
    'KBSpecialFuncSet.xml',
    'Slider_Sleep_Time',
    'Edit_Sleep_Time',
    'Value_Sleep_Time',
    'SleepTime',
    'HidD_SetFeature',
    'ReportLength41Proven',
    'SleepValueProven',
    'reportReplayed = false',
    'hidWritesPerformed = false',
    'deviceOpened = false',
    'oem-keyboard-sleep-report-trace.json',
    'oem-keyboard-sleep-report-trace.txt',
    'Run keyboard SleepTime report trace'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Missing required sleep-report token: $required" }
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
    if ($all -match [regex]::Escape($forbidden)) { throw "Forbidden mutation/process surface in sleep-report trace: $forbidden" }
}

if ($traceText -match 'extern\s+.*HidD_SetFeature') {
    throw 'Sleep-report analyzer must not declare/invoke HidD_SetFeature.'
}
if ($traceText -notmatch 'Only keyboard-specific KBSpecialFuncSet/SleepTime anchors') {
    throw 'Generic setting_more resources must not be accepted as keyboard SleepTime proof.'
}
if ($traceText -notmatch 'PROVEN requires an explicit SleepTime-derived value') {
    throw 'PROVEN criterion is not explicit enough.'
}

Write-Host 'OEM keyboard SleepTime report read-only safety smoke: PASS'

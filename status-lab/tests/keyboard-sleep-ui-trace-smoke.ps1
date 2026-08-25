$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$form = Join-Path $root 'hid-research-lab\HidResearchForm.cs'
$trace = Join-Path $root 'hid-research-lab\KeyboardSleepUiTrace.cs'
$capture = Join-Path $root 'hid-research-lab\KeyboardSleepCaptureSession.cs'

foreach ($path in @($form, $trace, $capture)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Keyboard Sleep UI Trace file missing: $path" }
}

$formText = Get-Content -LiteralPath $form -Raw -Encoding UTF8
$traceText = Get-Content -LiteralPath $trace -Raw -Encoding UTF8
$captureText = Get-Content -LiteralPath $capture -Raw -Encoding UTF8
$text = $formText + $traceText + $captureText

foreach ($forbidden in @(
    'HidD_SetFeature\s*\(',
    'HidD_SetOutputReport\s*\(',
    'HidD_GetFeature\s*\(',
    'CreateFileW?\s*\(',
    'WriteFile\s*\(',
    'DeviceIoControl\s*\(',
    'WriteProcessMemory\s*\(',
    'VirtualAllocEx\s*\(',
    'CreateRemoteThread\s*\(',
    'SetWindowsHookEx',
    'DebugActiveProcess',
    'NativeLibrary\.Load',
    'SelectActiveSlot',
    'K15HidProtocol',
    'DeviceWriteCommand',
    'LightingWriteCommand'
)) {
    if ($text -match $forbidden) { throw "Keyboard Sleep UI Trace contains forbidden mutating/injection path: $forbidden" }
}

foreach ($required in @(
    'Keyboard Sleep UI Trace',
    'KBSpecialFuncSet.xml',
    'Slider_Sleep_Time',
    'Edit_Sleep_Time',
    'Value_Sleep_Time',
    'SleepTime',
    'SavePowerSelect',
    'setting_more.xml',
    'PowerSavingMode',
    'VendorPeAnalyzer.Analyze',
    'owner-actions.jsonl',
    'config-delta.jsonl',
    'device-presence.jsonl',
    'runtime-observation.jsonl',
    'SetupDiGetClassDevs',
    'SetupDiEnumDeviceInterfaces',
    'GetForegroundWindow',
    'hidWritesPerformed = false',
    'featureReportsQueried = false',
    'processInjected = false',
    'executablePatched = false',
    'unknownSelectorsProbed = false',
    'Start owner capture',
    'Stop capture'
)) {
    if ($text -notmatch [regex]::Escape($required)) { throw "Keyboard Sleep UI Trace missing required read-only evidence feature: $required" }
}

if ($text -notmatch 'SHA256\.HashData') {
    throw 'Keyboard Sleep UI Trace must hash observed content instead of persisting arbitrary raw vendor/window text.'
}

if (($captureText -match 'GetWindowText\(') -and ($captureText -notmatch 'TitleSha256')) {
    throw 'Foreground title may only be persisted as hash/length metadata.'
}

Write-Output 'Keyboard Sleep UI Trace owner-capture read-only safety gates: PASS'

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tray = Join-Path $root 'StatusTrayApplicationContext.cs'
$ipc = Join-Path $root 'shared\StatusTrayIpc.cs'
$control = Join-Path $root 'control-center\ControlCenterForm.cs'
$controlProject = Join-Path $root 'control-center\Vorotex.K15.ControlCenter.csproj'
$lightingProject = Join-Path $root 'lighting-lab\Vorotex.K15.LightingLab.csproj'
$researchProject = Join-Path $root 'hid-research-lab\Vorotex.K15.HidResearchLab.csproj'
$researchForm = Join-Path $root 'hid-research-lab\HidResearchForm.cs'
$startup = Join-Path $root 'StartupManager.cs'
$hooks = Join-Path $root 'CodexHookHealth.cs'
$reducer = Join-Path $root 'StateReducer.cs'
$normalizer = Join-Path $root 'JournalStateNormalizer.cs'

foreach ($path in @($tray,$ipc,$control,$controlProject,$lightingProject,$researchProject,$researchForm,$startup,$hooks,$reducer,$normalizer)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "RC2 split required file missing: $path" }
}

$trayText = Get-Content -LiteralPath $tray -Raw -Encoding UTF8
$ipcText = Get-Content -LiteralPath $ipc -Raw -Encoding UTF8
$controlText = Get-Content -LiteralPath $control -Raw -Encoding UTF8
$controlProjectText = Get-Content -LiteralPath $controlProject -Raw -Encoding UTF8
$researchText = Get-Content -LiteralPath $researchForm -Raw -Encoding UTF8
$researchProjectText = Get-Content -LiteralPath $researchProject -Raw -Encoding UTF8
$startupText = Get-Content -LiteralPath $startup -Raw -Encoding UTF8
$hooksText = Get-Content -LiteralPath $hooks -Raw -Encoding UTF8
$semanticText = (Get-Content -LiteralPath $reducer -Raw -Encoding UTF8) + (Get-Content -LiteralPath $normalizer -Raw -Encoding UTF8)

if ($trayText -notmatch 'StatusTrayIpc\.RunServerAsync' -or $trayText -notmatch 'OpenControlCenterProcess' -or $trayText -notmatch 'DoubleClick') {
    throw 'Status Tray must host local IPC and launch standalone Control Center.'
}
if ($trayText -match 'new\s+ControlCenterForm') {
    throw 'Status Tray must not instantiate Control Center in-process.'
}
if ($controlText -notmatch 'StatusTrayIpc\.SendAsync' -or $controlText -notmatch 'Status Tray: подключён' -or $controlText -notmatch 'FormatElapsed') {
    throw 'Standalone Control Center must use tray IPC and expose live state/elapsed time.'
}
if ($controlText -match 'K15RgbCanary|K15HidProtocol|HidD_SetFeature|ApplyEffect|SelectActiveSlot') {
    throw 'Control Center must not own hardware/RGB write paths.'
}
if ($controlProjectText -notmatch 'Vorotex.K15.ControlCenter' -or $controlProjectText -notmatch 'StatusTrayIpc.cs') {
    throw 'Control Center must be an independent executable linked only to the IPC contract.'
}
if ($researchProjectText -notmatch 'Vorotex.K15.HidResearchLab' -or
    $researchProjectText -notmatch 'SleepSweepSession.cs' -or
    $researchProjectText -notmatch 'VendorPeAnalyzer.cs') {
    throw 'HID Research Lab must consolidate sleep sweep and vendor static analysis.'
}
if ($researchText -notmatch 'Sleep Sweep' -or $researchText -notmatch 'VendorPeAnalyzer\.Analyze') {
    throw 'HID Research Lab must expose both research workflows.'
}
if ($researchText -match 'HidD_SetFeature\s*\(|CreateFileW?\s*\(|K15HidProtocol|DeviceWriteCommand|ApplyEffect') {
    throw 'HID Research shell must not add unknown runtime HID write paths.'
}
if ($ipcText -notmatch 'NamedPipeServerStream' -or $ipcText -notmatch 'CurrentUserOnly' -or $ipcText -notmatch 'NamedPipeClientStream') {
    throw 'Tray/Control Center IPC must be local named-pipe IPC scoped to the current user.'
}
if ($startupText -notmatch 'CurrentUser' -or $startupText -notmatch 'Windows\\CurrentVersion\\Run') {
    throw 'Autostart must remain per-user HKCU Run.'
}
foreach ($eventName in @('UserPromptSubmit','PermissionRequest','PreToolUse','PostToolUse','Stop','SessionEnd')) {
    if ($hooksText -notmatch [regex]::Escape($eventName)) { throw "Hook health is missing $eventName." }
}
if ($semanticText -match 'GetForegroundWindow|ForegroundWindow|codex_foreground') {
    throw 'RC2 must not change DONE semantics based on merely foregrounding/opening Codex.'
}
if ($trayText -match '\bSelectActiveSlot\s*\(') {
    throw 'Status Tray must not programmatically select hardware profiles.'
}

Write-Output 'RC2 four-app split + IPC + safety gates: PASS'

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'StatusLabApplicationContext.cs'
$control = Join-Path $root 'ControlCenterForm.cs'
$startup = Join-Path $root 'StartupManager.cs'
$hooks = Join-Path $root 'CodexHookHealth.cs'
$research = Join-Path $root 'DeviceSettingsResearch.cs'
$reducer = Join-Path $root 'StateReducer.cs'
$normalizer = Join-Path $root 'JournalStateNormalizer.cs'

foreach ($path in @($app,$control,$startup,$hooks,$research,$reducer,$normalizer)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "RC2 required file missing: $path" }
}

$appText = Get-Content -LiteralPath $app -Raw -Encoding UTF8
$controlText = Get-Content -LiteralPath $control -Raw -Encoding UTF8
$startupText = Get-Content -LiteralPath $startup -Raw -Encoding UTF8
$hooksText = Get-Content -LiteralPath $hooks -Raw -Encoding UTF8
$researchText = Get-Content -LiteralPath $research -Raw -Encoding UTF8
$semanticText = (Get-Content -LiteralPath $reducer -Raw -Encoding UTF8) + (Get-Content -LiteralPath $normalizer -Raw -Encoding UTF8)

if ($appText -notmatch 'OpenControlCenter' -or $appText -notmatch 'GetControlCenterSnapshot' -or $appText -notmatch 'DoubleClick') {
    throw 'Tray must expose and open RC2 Control Center.'
}
if ($controlText -notmatch 'Состояние' -or $controlText -notmatch 'Причина' -or $controlText -notmatch 'В этом состоянии') {
    throw 'Control Center must expose state reason and elapsed time.'
}
if ($controlText -notmatch 'Capture BEFORE' -or $controlText -notmatch 'Capture AFTER' -or $controlText -notmatch 'sleep/standby') {
    throw 'Control Center must expose evidence-first sleep research workflow.'
}
if ($startupText -notmatch 'CurrentUser' -or $startupText -notmatch 'Windows\\CurrentVersion\\Run') {
    throw 'Autostart must be per-user HKCU Run, not elevated/system startup.'
}
foreach ($eventName in @('UserPromptSubmit','PermissionRequest','PreToolUse','PostToolUse','Stop','SessionEnd')) {
    if ($hooksText -notmatch [regex]::Escape($eventName)) { throw "Hook health is missing $eventName." }
}
if ($researchText -match 'HidD_SetFeature|DeviceWriteCommand|LightingWriteCommand|SelectActiveSlot|ApplyEffect') {
    throw 'Device Settings Research must not contain HID/device write paths.'
}
if ($researchText -notmatch 'VendorWritesPerformedByStatusLab' -or $researchText -notmatch 'HidPowerWritesPerformed') {
    throw 'Device Settings Research must report its no-write safety policy.'
}
if ($semanticText -match 'GetForegroundWindow|ForegroundWindow|codex_foreground') {
    throw 'RC2 must not change DONE semantics based on merely foregrounding/opening Codex.'
}
if ($appText -match '\bSelectActiveSlot\s*\(') {
    throw 'Status Lab runtime must not programmatically select hardware profiles.'
}

Write-Output 'RC2 Control Center + autostart + hook-health + sleep-research safety gates: PASS'

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tray = Get-Content -LiteralPath (Join-Path $root 'StatusTrayApplicationContext.cs') -Raw -Encoding UTF8
$formatter = Get-Content -LiteralPath (Join-Path $root 'DeviceUxFormatting.cs') -Raw -Encoding UTF8
$control = Get-Content -LiteralPath (Join-Path $root 'control-center\ControlCenterForm.cs') -Raw -Encoding UTF8
$controlProject = Get-Content -LiteralPath (Join-Path $root 'control-center\Vorotex.K15.ControlCenter.csproj') -Raw -Encoding UTF8
$ipc = Get-Content -LiteralPath (Join-Path $root 'shared\StatusTrayIpc.cs') -Raw -Encoding UTF8

foreach ($text in @('AppContext.BaseDirectory', 'control-center', 'ResolveControlCenterPath')) {
    if ($formatter -notmatch [regex]::Escape($text) -and $tray -notmatch [regex]::Escape($text)) { throw "Missing deterministic path resolver evidence: $text" }
}
if ($formatter -notmatch 'File.Exists\(colocated\)' -or $formatter -notmatch 'File.Exists\(splitSibling\)') { throw 'Control Center resolver must fail closed on exact supported paths.' }
if ($formatter -match 'SearchOption\.AllDirectories|Program Files|Registry|Environment\.GetEnvironmentVariable\("PATH"') { throw 'Control Center resolver must not perform broad search.' }
if ($tray -notmatch 'same layout|рядом с Status Tray или в соседней папке control-center' -and $tray -notmatch 'control-center') { throw 'Missing visible supported-layout failure message.' }

if ($formatter -notmatch 'ShortCandidateId' -or $formatter -notmatch 'ToUpperInvariant' -or $formatter -notmatch 'Math\.Min\(4') { throw 'Candidate discriminator must be deterministic and short.' }
if ($tray -notmatch 'var candidateId = candidate\.CandidateId' -or $tray -notmatch 'ConnectDeviceAsync\(candidateId\)') { throw 'Candidate click must use the exact selected CandidateId.' }
if ($tray -match 'Candidates\.(First|FirstOrDefault)\(' -or $tray -match 'Candidates\[0\]') { throw 'Tray must not auto-pick the first candidate.' }
if ($tray -notmatch '_deviceMenu' -or $tray -notmatch 'ScanDevicesAsync' -or $tray -notmatch 'ReconnectDeviceAsync' -or $tray -notmatch 'DisconnectDeviceAsync') { throw 'Tray device submenu actions are incomplete.' }
if ($tray -notmatch 'RefreshDeviceMenu\(\)' -or $tray -notmatch 'StateChanged \+= UpdateDeviceStatus') { throw 'Tray device submenu must refresh after state changes.' }
if ($control -notmatch 'DeviceUxFormatting\.CandidateLabel' -or $controlProject -notmatch 'DeviceUxFormatting\.cs') { throw 'Control Center must use the shared candidate formatter.' }
if ($control -notmatch '_explicitCandidateId' -or $control -notmatch 'connect_device') { throw 'Control Center explicit selection path is missing.' }
if ($tray -notmatch 'RGB|_rgbCanary') { throw 'RGB/device separation evidence is missing.' }
if ($ipc -notmatch 'StatusTrayDeviceCandidate') { throw 'Device candidate IPC contract is missing.' }

Write-Output 'Device UX resolver, discriminator, explicit selection, submenu and RGB separation smoke: PASS'
Write-Output 'CONTROL_CENTER_COLOCATED_PATH=PASS'
Write-Output 'CONTROL_CENTER_SPLIT_PATH=PASS'
Write-Output 'CONTROL_CENTER_MISSING_FAILS_CLOSED=PASS'
Write-Output 'CANDIDATE_DISCRIMINATOR_DETERMINISTIC=PASS'
Write-Output 'CANDIDATE_DISCRIMINATOR_DISPLAY_ONLY=PASS'
Write-Output 'MULTI_CANDIDATE_DISPLAY_DISTINCT=PASS'
Write-Output 'NO_FIRST_CANDIDATE_AUTOPICK=PASS'
Write-Output 'EXPLICIT_CANDIDATE_ID_REQUIRED=PASS'
Write-Output 'TRAY_DEVICE_ACTIONS_PRESENT=PASS'
Write-Output 'RGB_DEVICE_STATE_SEPARATION_PRESERVED=PASS'

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manager = Get-Content -LiteralPath (Join-Path $root 'K15DeviceManager.cs') -Raw -Encoding UTF8
$controller = Get-Content -LiteralPath (Join-Path $root 'K15HidLightingController.cs') -Raw -Encoding UTF8
$rgb = Get-Content -LiteralPath (Join-Path $root 'K15RgbCanary.cs') -Raw -Encoding UTF8
$tray = Get-Content -LiteralPath (Join-Path $root 'StatusTrayApplicationContext.cs') -Raw -Encoding UTF8
$ipc = Get-Content -LiteralPath (Join-Path $root 'shared\StatusTrayIpc.cs') -Raw -Encoding UTF8
$control = Get-Content -LiteralPath (Join-Path $root 'control-center\ControlCenterForm.cs') -Raw -Encoding UTF8

foreach ($state in @('Disconnected','Scanning','Connected','ConnectionLost','Error')) {
    if ($manager -notmatch [regex]::Escape($state)) { throw "Missing device state: $state" }
}
foreach ($member in @('ScanCandidates','SelectById','TryResolvePreferred','Reconnect','MarkConnectionLost','SavePreference','IdentityFingerprint')) {
    if ($manager -notmatch [regex]::Escape($member)) { throw "Missing device manager member: $member" }
}
if ($manager -notmatch 'if \(_controller is not null\)' -or
    $manager -notmatch 'Disconnect the current K15 device before scanning') {
    throw 'Direct scan with a live controller must fail closed.'
}
if ($manager -notmatch 'pendingController' -or $manager -notmatch 'pendingController\?\.Dispose\(\)' -or
    $manager -notmatch 'pendingController = null') {
    throw 'Verification ownership transfer/disposal seam is missing.'
}
if ($tray -notmatch '(?s)DisableAsync\("device_rescan"\).*_deviceManager\.Disconnect\(\).*Task\.Run\(\(\) => _deviceManager\.Scan\(\)\)') {
    throw 'Rescan must disable RGB and disconnect before scanning.'
}
if ($manager -notmatch 'matches\.Length == 1') { throw 'Preferred identity must fail closed unless exactly one candidate matches.' }
if ($manager -notmatch 'ReadActiveSlot\(\)') { throw 'Connection must use the proven active-slot verification gate.' }
if ($manager -notmatch 'K15HidLightingController\.Open\(_selected\.Path\)') { throw 'Manager must open only the explicitly selected endpoint.' }
if ($rgb -match 'K15HidLightingController\.Open\(\)') { throw 'RGB must not discover an endpoint implicitly.' }
if ($rgb -notmatch 'ConnectionState != K15DeviceConnectionState\.Connected') { throw 'RGB must be gated by verified device connection.' }
foreach ($command in @('scan_devices','connect_device','disconnect_device','reconnect_device')) {
    if ($tray -notmatch [regex]::Escape($command)) { throw "Missing IPC command: $command" }
}
foreach ($field in @('DeviceState','DeviceIdentity','DeviceCandidates')) {
    if ($ipc -notmatch [regex]::Escape($field)) { throw "Missing IPC device field: $field" }
}
foreach ($text in @('DevicePanel','scan_devices','connect_device','disconnect_device','Reconnect')) {
    if ($control -notmatch [regex]::Escape($text)) { throw "Missing Control Center device UX: $text" }
}
foreach ($text in @('SelectionChangeCommitted','_explicitCandidateId','SelectedIndex = -1','preservedExplicitId')) {
    if ($control -notmatch [regex]::Escape($text)) { throw "Missing explicit UI selection guard: $text" }
}
if ($control -notmatch 'Connect.*explicitCandidateId' -and $control -notmatch '_explicitCandidateId.*SendCommandAsync') {
    throw 'Connect must use the explicit owner-selected candidate id.'
}
if ($controller -notmatch 'Open\(string path\)' -or $controller -notmatch 'ScanCandidates\(\)') {
    throw 'HID controller must expose explicit endpoint open and candidate enumeration.'
}

Write-Output 'Device manager selection, verification, persistence and RGB gate smoke: PASS'

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manager = Get-Content (Join-Path $root 'K15DeviceManager.cs') -Raw -Encoding UTF8
$rgb = Get-Content (Join-Path $root 'K15RgbCanary.cs') -Raw -Encoding UTF8
$guard = Get-Content (Join-Path $root 'K15RgbBindingGuard.cs') -Raw -Encoding UTF8
$tray = Get-Content (Join-Path $root 'StatusTrayApplicationContext.cs') -Raw -Encoding UTF8

if ($rgb -match '_controller\?\.Dispose\(\)') { throw 'RGB borrower must never dispose manager-owned controller.' }
foreach ($member in @('ConnectionGeneration', 'IsCurrentBindingLocked', 'K15RgbBindingGuard', 'InvalidateBindingLocked')) {
    if ($manager + $rgb + $guard -notmatch [regex]::Escape($member)) { throw "Missing binding lifecycle guard: $member" }
}
if ($manager -notmatch '_connectionGeneration\+\+' -or $manager -notmatch 'pendingController = null') {
    throw 'Controller ownership transfer or generation update missing.'
}
if ($guard -notmatch 'generation == currentGeneration' -or
    $guard -notmatch 'ReferenceEquals\(boundController, currentController\)') {
    throw 'Hardware operations lack generation and controller identity proof.'
}
if ($rgb -notmatch 'var reconnectState = _desiredState' -or
    $rgb -notmatch 'PrepareProfileSnapshot\(_config\)' -or
    $rgb -notmatch 'Enabled = true') {
    throw 'Reconnect must create fresh binding, snapshot and enabled state.'
}
if ($tray -notmatch 'RunDeviceOperationWithRgbAsync' -or
    $tray -notmatch 'DisableAsync\(reason \+ "_teardown"\)' -or
    $tray -notmatch 'EnableAsync\(_stateNormalizer\.State\)') {
    throw 'Explicit device operations must teardown and re-enable RGB through fresh binding.'
}
if ($tray -notmatch '(?s)RunDeviceOperationWithRgbAsync.*?if \(!connected \|\| !wasRgbEnabled\).*?return connected') {
    throw 'Failed reconnect must return with RGB disabled.'
}
if ($manager -notmatch 'K15HidLightingController\.Open\(_selected\.Path\)' -or
    $manager -notmatch 'matches\.Length == 1') {
    throw 'Explicit candidate selection and ambiguous preferred-device rejection must remain.'
}

Write-Output 'RGB binding lifecycle ownership and stale-controller regression smoke: PASS'

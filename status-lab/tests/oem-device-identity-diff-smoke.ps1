$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$form = Join-Path $root 'hid-research-lab\HidResearchForm.cs'
$diff = Join-Path $root 'hid-research-lab\OemDeviceIdentityDiff.cs'

foreach ($path in @($form, $diff)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "OEM Device Identity Diff file missing: $path" }
}

$formText = Get-Content -LiteralPath $form -Raw -Encoding UTF8
$diffText = Get-Content -LiteralPath $diff -Raw -Encoding UTF8
$text = $formText + $diffText

foreach ($forbidden in @(
    'HidD_SetFeature\s*\(',
    'HidD_GetFeature\s*\(',
    'HidD_SetOutputReport\s*\(',
    'CreateFileW?\s*\(',
    'WriteFile\s*\(',
    'DeviceIoControl\s*\(',
    'WriteProcessMemory\s*\(',
    'VirtualAllocEx\s*\(',
    'CreateRemoteThread\s*\(',
    'SetWindowsHookEx',
    'DebugActiveProcess',
    'NativeLibrary\.Load',
    'SetupDiCallClassInstaller',
    'UpdateDriverForPlugAndPlayDevices',
    'RegSetValue',
    'SelectActiveSlot',
    'K15HidProtocol',
    'DeviceWriteCommand',
    'LightingWriteCommand'
)) {
    if ($text -match $forbidden) { throw "OEM Device Identity Diff contains forbidden mutating/injection path: $forbidden" }
}

foreach ($required in @(
    'OEM Device Identity Diff',
    'OemDeviceIdentityDiffAnalyzer',
    'HidD_GetAttributes',
    'HidD_GetProductString',
    'HidD_GetManufacturerString',
    'HidD_GetSerialNumberString',
    'HidD_GetPreparsedData',
    'HidP_GetCaps',
    'SetupDiGetClassDevs',
    'SetupDiEnumDeviceInterfaces',
    'VID_36A4',
    'VID_B6A4',
    'PID_4100',
    'PID_4101',
    'W909',
    'K15',
    'VOROTEX',
    'MKESPN',
    'SXS',
    'oem-device-identity-diff.json',
    'oem-device-identity-diff.txt',
    'executablePatched = false',
    'processInjected = false',
    'deviceOpened = false',
    'featureReportsQueried = false',
    'hidWritesPerformed = false',
    'vidPidSpoofed = false',
    'registryModified = false',
    'reportContainsOnlyStaticMetadataAndBoundedSnippets = true'
)) {
    if ($text -notmatch [regex]::Escape($required)) { throw "OEM Device Identity Diff missing required evidence/safety feature: $required" }
}

if ($diffText -notmatch 'BinaryPrimitives\.WriteUInt16LittleEndian') {
    throw 'OEM Device Identity Diff must search known VID/PID values as little-endian integer constants.'
}
if ($diffText -notmatch 'HexSimilarity') {
    throw 'OEM Device Identity Diff must compare aligned discovery code windows rather than only string counts.'
}
if ($diffText -match 'Profile\d+\.json') {
    throw 'OEM Device Identity Diff must not explicitly ingest profile JSON payloads.'
}

Write-Output 'OEM Device Identity Diff read-only safety/evidence gates: PASS'

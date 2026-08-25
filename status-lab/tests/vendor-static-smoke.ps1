$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'vendor-static-lab\Vorotex.K15.VendorStaticLab.csproj'
$program = Join-Path $root 'vendor-static-lab\Program.cs'
$analyzer = Join-Path $root 'vendor-static-lab\VendorPeAnalyzer.cs'

foreach ($path in @($project, $program, $analyzer)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Vendor Static Lab file missing: $path" }
}

$text = (Get-Content -LiteralPath $program -Raw -Encoding UTF8) + (Get-Content -LiteralPath $analyzer -Raw -Encoding UTF8)

foreach ($forbidden in @(
    '\[DllImport',
    'NativeLibrary\.Load',
    'HidD_SetFeature\s*\(',
    'HidD_SetOutputReport\s*\(',
    'CreateFileW?\s*\(',
    'K15HidProtocol',
    'DeviceWriteCommand',
    'LightingWriteCommand',
    'SelectActiveSlot',
    'ApplyEffect'
)) {
    if ($text -match $forbidden) { throw "Vendor Static Lab contains forbidden runtime/device path: $forbidden" }
}

foreach ($required in @(
    'executableModified = false',
    'processInjected = false',
    'deviceOpened = false',
    'hidReadsPerformed = false',
    'hidWritesPerformed = false',
    'driverInstalled = false',
    'HidD_SetFeature',
    'HidD_GetFeature',
    'IatRva',
    'direct_iat_call',
    'call_via_import_thunk',
    'KeywordMatches',
    'SleepTime',
    'SleepTimeout'
)) {
    if ($text -notmatch [regex]::Escape($required)) { throw "Vendor Static Lab missing required evidence feature: $required" }
}

if ($text -notmatch 'File\.ReadAllBytes' -or $text -notmatch 'SHA256\.HashData') {
    throw 'Vendor Static Lab must be a file-only read/static analyzer.'
}

Write-Output 'Vendor Static Lab PE/IAT read-only safety gates: PASS'

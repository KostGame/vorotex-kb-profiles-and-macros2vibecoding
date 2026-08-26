$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$main = Join-Path $root 'hid-research-lab\OemNdeviceAggregateCopyTrace.cs'
$decoder = Join-Path $root 'hid-research-lab\OemNdeviceAggregateCopyDecoder.cs'
$ui = Join-Path $root 'hid-research-lab\OemNdeviceAggregateCopyUi.cs'

foreach ($path in @($main, $decoder, $ui)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$mainText = Get-Content -LiteralPath $main -Raw
$decoderText = Get-Content -LiteralPath $decoder -Raw
$uiText = Get-Content -LiteralPath $ui -Raw
$all = $mainText + "`n" + $decoderText + "`n" + $uiText

foreach ($required in @(
    'NDEVICE_AGGREGATE_COPY_COMPLETE',
    'NDEVICE_AGGREGATE_COPY_PARTIAL',
    'AGGREGATE_COPY_HELPERS_TRACED',
    'NDEVICE_AGGREGATE_COPY_UNRESOLVED',
    'NdeviceSize = 0x434',
    'DevNameMember = 0x20',
    'DevCmpMember = 0x3EC',
    'LocalObjectBase',
    'RecoverExactFieldWrites',
    'FindAggregateCaller',
    'TraceAggregateHelper',
    'Member20Copied',
    'Member3EcCopied',
    'oem-ndevice-aggregate-copy.json',
    'oem-ndevice-aggregate-copy.txt',
    'Run Ndevice aggregate copy trace'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Required aggregate-copy marker missing: $required" }
}

foreach ($forbidden in @(
    'HidD_SetFeature',
    'HidD_GetFeature',
    'DeviceIoControl',
    'CreateFile(',
    'DebugActiveProcess',
    'OpenProcess(',
    'WriteProcessMemory',
    'VirtualAllocEx',
    'CreateRemoteThread',
    'SetWindowsHookEx',
    'RegistryKey',
    'SelectActiveSlot(',
    'ResetDevice('
)) {
    if (($mainText + "`n" + $decoderText) -match [regex]::Escape($forbidden)) {
        throw "Forbidden device/process mutation surface found in aggregate-copy analyzer: $forbidden"
    }
}

foreach ($requiredSafety in @(
    'firmwareModified\s*=\s*false',
    'deviceOpened\s*=\s*false',
    'processAttached\s*=\s*false',
    'processInjected\s*=\s*false',
    'hidWritesPerformed\s*=\s*false',
    'productStringSpoofed\s*=\s*false',
    'registryModified\s*=\s*false'
)) {
    if ($mainText -notmatch $requiredSafety) { throw "Missing explicit safety assertion: $requiredSafety" }
}

if ($mainText -notmatch 'A field is promoted to PROVEN only when the helper trace explicitly transfers the same source/destination member offset') {
    throw 'Aggregate proof must explicitly require same-offset helper transfer.'
}
if ($mainText -notmatch 'Caller shape, helper proximity, object size and equal offsets are evidence only') {
    throw 'Aggregate proof must explicitly reject structural/proximity evidence as proof.'
}
if ($mainText -notmatch 'localCorrespondence && callerCorrespondence && helperCorrespondence') {
    throw 'Final proof predicate must require cross-OEM local/caller/helper correspondence.'
}

Write-Host 'OEM Ndevice Aggregate Copy read-only safety smoke: PASS'

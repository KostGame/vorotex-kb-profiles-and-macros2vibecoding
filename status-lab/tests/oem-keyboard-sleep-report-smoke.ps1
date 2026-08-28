$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$trace = Join-Path $root 'hid-research-lab\OemKeyboardSleepReportTrace.cs'
$recovery = Join-Path $root 'hid-research-lab\OemKeyboardSleepTransportRecovery.cs'
$construction = Join-Path $root 'hid-research-lab\OemKeyboardSleepReportConstructionTrace.cs'
$payloadSeed = Join-Path $root 'hid-research-lab\OemKeyboardSleepReportPayloadSeedTrace.cs'
$facade = Join-Path $root 'hid-research-lab\HidResearchHeadless.cs'

foreach ($path in @($trace, $recovery, $construction, $payloadSeed, $facade)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$traceText = Get-Content -LiteralPath $trace -Raw
$recoveryText = Get-Content -LiteralPath $recovery -Raw
$constructionText = Get-Content -LiteralPath $construction -Raw
$payloadSeedText = Get-Content -LiteralPath $payloadSeed -Raw
$facadeText = Get-Content -LiteralPath $facade -Raw
$all = $traceText + "`n" + $recoveryText + "`n" + $constructionText + "`n" + $payloadSeedText + "`n" + $facadeText

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
    'oem-keyboard-sleep-report-trace',
    'oem-keyboard-sleep-report-trace',
    'AnalyzeKeyboardSleepReportRecovered',
    'AnalyzeKeyboardSleepReportRecovered',
    'RecoverExactPe32SetFeatureRvas',
    'textSection.RawPointer',
    'textSection.RawSize',
    'SequenceEqual(needle)',
    'BinaryPrimitives.WriteUInt32LittleEndian',
    'TraceRawPe32SetFeatureCall',
    'push 0x29',
    'exact PE32 direct-IAT',
    'REPORT_BUFFER_WRITES_CORRESPONDING',
    'REPORT_CONSTRUCTION_HELPER_CORRESPONDING',
    'REPORT_CONSTRUCTION_REFERENCES_CORRESPONDING',
    'REPORT_CONSTRUCTION_SLICE_UNRESOLVED',
    'AnalyzeKeyboardSleepReportConstruction',
    '0x2400',
    'only retains EBP-relative references inside the proven 41-byte report range',
    'oem-keyboard-sleep-report-construction',
    'oem-keyboard-sleep-report-construction',
    'sleep-report-construction',
    'REPORT_PLUS1_PAYLOAD_CALLS_CORRESPONDING',
    'REPORT_PLUS1_WINDOWS_CORRESPONDING',
    'REPORT_PLUS1_PAYLOAD_UNRESOLVED',
    'AnalyzeKeyboardSleepReportPayloadSeed',
    'report+1 address anchors are construction evidence only',
    'oem-keyboard-sleep-report-payload-seed',
    'oem-keyboard-sleep-report-payload-seed',
    'sleep-payload-seed'
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

if ($all -match 'extern\s+.*HidD_SetFeature') {
    throw 'Sleep-report analyzer must not declare/invoke HidD_SetFeature.'
}
if ($traceText -notmatch 'Only keyboard-specific KBSpecialFuncSet/SleepTime anchors') {
    throw 'Generic setting_more resources must not be accepted as keyboard SleepTime proof.'
}
if ($traceText -notmatch 'PROVEN requires an explicit SleepTime-derived value') {
    throw 'PROVEN criterion is not explicit enough.'
}
if ($recoveryText -notmatch 'independently of linear decoding|independently of linear instruction decoding') {
    throw 'Raw SetFeature recovery must not depend on the linear Iced instruction list.'
}
if ($recoveryText -notmatch 'PE32 OEM binaries') {
    throw 'Raw transport recovery must remain explicitly bounded to the proven PE32 OEM shape.'
}
if ($constructionText -notmatch 'matching write or helper is not SleepTime provenance') {
    throw 'Construction-slice correspondence must not be promoted to SleepTime provenance.'
}
if ($constructionText -notmatch 'DecodeOneRawInstruction') {
    throw 'Construction slice must decode from each candidate raw RVA rather than depend on the global linear sweep.'
}
if ($payloadSeedText -notmatch 'helper receiving report\+1 is not automatically a SleepTime helper') {
    throw 'Report+1 helper correspondence must not be promoted to SleepTime provenance.'
}
if ($payloadSeedText -notmatch 'Register aliasing is bounded locally') {
    throw 'Report+1 payload trace must keep alias tracking explicitly bounded.'
}

# Public bounded regression fixtures copied from previously proven Vendor Static
# call-site windows. These are code bytes only, not private device data.
function Convert-HexBytes([string]$Hex) {
    if (($Hex.Length % 2) -ne 0) { throw 'Odd hex fixture length.' }
    $bytes = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }
    return $bytes
}

function Assert-SetFeatureFixture([string]$Hex, [uint32]$IatVa) {
    $bytes = Convert-HexBytes $Hex
    $iat = [BitConverter]::GetBytes($IatVa)
    $found = $false
    for ($i = 0; $i -le $bytes.Length - 6; $i++) {
        if ($bytes[$i] -ne 0xFF -or $bytes[$i + 1] -ne 0x15) { continue }
        if ($bytes[$i + 2] -eq $iat[0] -and $bytes[$i + 3] -eq $iat[1] -and $bytes[$i + 4] -eq $iat[2] -and $bytes[$i + 5] -eq $iat[3]) {
            if ($i -lt 11) { throw 'Fixture direct-IAT call lacks the bounded 11-byte ABI prefix.' }
            $p = $i - 11
            if ($bytes[$p] -ne 0x6A -or $bytes[$p + 1] -ne 0x29 -or
                $bytes[$p + 2] -ne 0x8D -or $bytes[$p + 3] -ne 0x85 -or
                $bytes[$p + 8] -ne 0x50 -or $bytes[$p + 9] -ne 0xFF -or $bytes[$p + 10] -ne 0x36) {
                throw 'Fixture direct-IAT call does not match the proven 41-byte SetFeature ABI prefix.'
            }
            $found = $true
            break
        }
    }
    if (-not $found) { throw ('Fixture did not recover FF 15 [{0:X8}]' -f $IatVa) }
}

Assert-SetFeatureFixture '6A298D85D8FDFFFF50FF36FF15B0364F00' 0x004F36B0
Assert-SetFeatureFixture '6A298D85D8FDFFFF50FF36FF15ACE64E00' 0x004EE6AC

Write-Host 'OEM keyboard SleepTime report read-only safety smoke: PASS'

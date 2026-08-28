$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$trace = Join-Path $root 'hid-research-lab\OemKeyboardSleepPayloadSourceTrace.cs'
$provenance = Join-Path $root 'hid-research-lab\OemKeyboardSleepPayloadSourceProvenanceTrace.cs'
$facade = Join-Path $root 'hid-research-lab\HidResearchHeadless.cs'
$program = Join-Path $root 'hid-research-cli\Program.cs'
$readme = Join-Path $root 'hid-research-cli\README.md'
$workspaceContract = Join-Path $root 'hid-research-cli\LOCAL_WORKSPACE.md'
foreach ($path in @($trace, $provenance, $facade, $program, $readme, $workspaceContract)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
}

$traceText = Get-Content -LiteralPath $trace -Raw
$provenanceText = Get-Content -LiteralPath $provenance -Raw
$facadeText = Get-Content -LiteralPath $facade -Raw
$programText = Get-Content -LiteralPath $program -Raw
$readmeText = Get-Content -LiteralPath $readme -Raw
$workspaceContractText = Get-Content -LiteralPath $workspaceContract -Raw
$all = $traceText + "`n" + $provenanceText + "`n" + $facadeText + "`n" + $programText

function Assert-Pattern([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) { throw $Message }
}

foreach ($required in @(
    'sleep-payload-source',
    'RunSleepPayloadSource',
    'AnalyzeKeyboardSleepPayloadSource',
    'KeyboardSleepPayloadSourceToText',
    'oem-keyboard-sleep-payload-source',
    'PAYLOAD_SOURCE_STRUCTURE_PROVEN',
    'SLEEPTIME_PAYLOAD_FIELD_PROVEN',
    'SLEEPTIME_PAYLOAD_SOURCE_PARTIAL',
    'KEYBOARD_SLEEP_REPORT_UNRESOLVED',
    'MEMCPY_LIKE',
    '[ESP+0x10]', '[ESP+0x14]', '[ESP+0x0C]',
    '[EBP-0x22C]', 'report[1..40]',
    'AnalyzePayloadSourceProvenance',
    'OemPayloadOffsetToReportOffset', 'SourceOffsetToReportOffset', 'DestinationReportOffset',
    'ReportPayloadCopyProven', 'CopyCountUpperBoundProven', 'SignedUpperBound', 'NonNegativeCountProven',
    'IsProvenSourceStructure', 'IsExactSleepTimeProof',
    'deviceOpened = false', 'hidWritesPerformed = false', 'processStarted = false'
)) {
    if ($all -notmatch [regex]::Escape($required)) { throw "Missing required payload-source token: $required" }
}

Assert-Pattern $facadeText '(?m)^\s*"sleep-payload-source"\s*=>\s*RunSleepPayloadSource' 'sleep-payload-source must dispatch to the implemented headless analyzer.'
if ($facadeText -match '\bReservedNextMode\b' -or $programText -match '\bExitReservedMode\b') {
    throw 'Obsolete reserved-mode plumbing must not remain after source analyzer implementation.'
}
if (($readmeText + "`n" + $workspaceContractText) -match '(?i)reserved\s+(next\s+)?mode|reserved.*sleep-payload-source') {
    throw 'CLI documentation must not describe implemented sleep-payload-source as reserved.'
}

# Semantic gate: SleepTime promotion requires an explicit proven candidate and
# an exact source-byte -> report-byte -> SetFeature chain. A nearby write or a
# string/resource anchor alone must not promote the verdict.
Assert-Pattern $traceText 'var candidate = traces\.FirstOrDefault\(x => x\.Stage == "sleep_value" && x\.Status == "PROVEN"\)' 'SleepTime promotion must require an explicit PROVEN sleep_value trace.'
Assert-Pattern $traceText 'candidate\.SourceOffset is not int sourceOffset' 'SleepTime proof must carry a structured source offset.'
Assert-Pattern $traceText 'candidate\.ReportOffset is not int reportOffset' 'SleepTime proof must carry a structured report offset.'
Assert-Pattern $traceText 'candidate\.CopyCallRva != provenCopy\.CallRva' 'SleepTime proof must identify the exact memcpy call.'
Assert-Pattern $traceText 'candidate\.SetFeatureCallRva != setFeatureCallRva' 'SleepTime proof must identify the exact SetFeature call.'
Assert-Pattern $traceText 'reportOffset != sourceOffset \+ 1' 'SleepTime proof must require the report+1 relation.'
Assert-Pattern $traceText '!provenCopy\.ReportPayloadCopyProven.*!provenCopy\.CopyCountUpperBoundProven.*!provenCopy\.NonNegativeCountProven' 'SleepTime proof must require proven copy semantics and a safe count bound.'
Assert-Pattern $traceText 'writes\.Any\(x => x\.Status == "PROVEN" && x\.SourceOffset == sourceOffset' 'SleepTime proof must require a matching concrete proven source write.'
Assert-Pattern $traceText 'offsetMap\.Any\(x => x\.Status == "PROVEN" && x\.SourceOffset == sourceOffset && x\.ReportOffset == reportOffset' 'SleepTime proof must require the matching normalized source/report map entry.'
Assert-Pattern $traceText 'var sleepTimePathMatch = IsExactSleepTimeProof\(a\) && IsExactSleepTimeProof\(b\)' 'Cross-OEM SleepTime correlation must use the exact proof gate.'
Assert-Pattern $traceText 'UNRESOLVED SleepTime:.*capped below field proof' 'Unresolved SleepTime must produce an explicitly capped verdict.'
Assert-Pattern $provenanceText 'CONDITIONAL ONLY: when copy count is nonnegative' 'Signed-only copy bounds must remain conditional in the report mapping.'
Assert-Pattern $traceText 'strict deterministic offline' 'The payload-source report must declare deterministic output semantics.'
Assert-Pattern $provenanceText 'setupRecovered \? "PROVEN" : "INFERRED"' 'Unique caller setup must be PROVEN and incomplete setup INFERRED.'
if ($provenanceText -match 'setupRecovered \? "INFERRED" : "PROVEN"') {
    throw 'Caller setup/object status must not invert INFERRED and PROVEN.'
}
if ($provenanceText -match 'directDefinitionIndices\.Count') {
    throw 'Direct source-slot status must not depend on a current partial definition count.'
}
Assert-Pattern $provenanceText 'directDefinitionCandidates = new List' 'Direct source-slot candidates must be finalized after the complete CFG scan.'
Assert-Pattern $provenanceText 'var directDefinitionCount = directDefinitionCandidates\.Count' 'Direct source-slot uniqueness must use the final CFG candidate count.'
Assert-Pattern $provenanceText 'var proven = directDefinitionCount == 1 && candidate\.DominatesCopy && !candidate\.PhiMerge' 'Direct source-slot PROVEN status must require final uniqueness, dominance, and no phi merge.'
Assert-Pattern $provenanceText 'Final CFG reaching-definition count' 'Ambiguous final CFG reaching definitions must remain visibly unresolved.'
Assert-Pattern $provenanceText 'definitions\[candidate\.DefinitionIndex\] = current with' 'Direct source-slot statuses must be rewritten after all definitions are known.'
if ($traceText -match '\bfullSleepTimeTransportProven\b') {
    throw 'Unreachable full SleepTime transport verdict state must not remain.'
}
Assert-Pattern $traceText 'var verdict = sleepTimePathMatch \? "SLEEPTIME_PAYLOAD_FIELD_PROVEN"' 'SleepTime field promotion must use the single reachable exact-proof branch.'
if ($traceText -match 'KEYBOARD_SLEEP_SETFEATURE_REPORT_PROVEN') {
    throw 'Stronger unreachable SleepTime verdict must not remain in the source analyzer.'
}
Assert-Pattern $readmeText '(?m)^- \x60sleep-payload-source\x60\r?$' 'README Implemented modes must list sleep-payload-source.'
if ($traceText -match 'DateTimeOffset\.UtcNow|CreatedUtc') {
    throw 'Payload-source output must not contain a volatile timestamp.'
}
if ($traceText -match [regex]::Escape('SleepTime remains intentionally unpromoted')) {
    throw 'The old impossible literal smoke assertion must not be used as the source-proof contract.'
}

foreach ($forbidden in @(
    'DllImport', 'LibraryImport',
    'CreateFile(', 'DeviceIoControl(', 'HidD_SetFeature(', 'HidD_GetFeature(', 'WriteFile(', 'ReadFile(',
    'OpenProcess(', 'WriteProcessMemory(', 'VirtualAllocEx(', 'CreateRemoteThread(', 'DebugActiveProcess(',
    'RegSetValue', 'SetupDiSet', 'Process.Start(exeA', 'Process.Start(exeB'
)) {
    if ($all -match [regex]::Escape($forbidden)) { throw "Forbidden mutation/process surface in payload-source trace: $forbidden" }
}

Write-Host 'OEM keyboard SleepTime payload-source read-only safety smoke: PASS'

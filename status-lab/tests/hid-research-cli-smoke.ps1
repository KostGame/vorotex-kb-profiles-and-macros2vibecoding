$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$cliDir = Join-Path $root 'hid-research-cli'
$labDir = Join-Path $root 'hid-research-lab'
$cliProgram = Join-Path $cliDir 'Program.cs'
$cliProject = Join-Path $cliDir 'Vorotex.K15.HidResearch.Cli.csproj'
$facade = Join-Path $labDir 'HidResearchHeadless.cs'
$fullWorkflow = Join-Path (Split-Path -Parent $root) '.github\workflows\status-lab-build.yml'

foreach ($path in @($cliProgram, $cliProject, $facade, $fullWorkflow)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing required file: $path" }
}

$programText = Get-Content -LiteralPath $cliProgram -Raw
$projectText = Get-Content -LiteralPath $cliProject -Raw
$facadeText = Get-Content -LiteralPath $facade -Raw
$workflowText = Get-Content -LiteralPath $fullWorkflow -Raw
$combined = $programText + "`n" + $facadeText

$required = @(
    '--mode', '--a', '--b', '--out', '--list-modes',
    'sleep-report', 'sleep-report-construction', 'sleep-payload-seed',
    'sleep-payload-helper-semantics', 'sleep-payload-source',
    'HidResearchHeadless.Run', 'ProjectReference'
)
foreach ($token in $required) {
    if (($combined + "`n" + $projectText) -notmatch [regex]::Escape($token)) {
        throw "Required CLI token missing: $token"
    }
}

$banned = @(
    'HidD_SetFeature(', 'HidD_GetFeature(', 'CreateFile(', 'DeviceIoControl(',
    'Process.Start(', 'OpenProcess(', 'WriteProcessMemory(', 'CreateRemoteThread(',
    'DebugActiveProcess(', 'RegSetValue', 'SetupDiCallClassInstaller',
    'Firmware', 'SetFeatureReport', 'featureReportsQueried = true',
    'hidWritesPerformed = true', 'processStarted = true', 'processAttached = true'
)
foreach ($token in $banned) {
    if ($combined -match [regex]::Escape($token)) {
        throw "Forbidden mutation/execution surface in headless CLI: $token"
    }
}

if ($projectText -notmatch [regex]::Escape('..\hid-research-lab\Vorotex.K15.HidResearchLab.csproj')) {
    throw 'CLI must reuse HID Research Lab analyzers through a project reference.'
}

if ($workflowText -match 'Upload HID Research CLI' -or $workflowText -match 'vorotex-k15-hid-research-cli') {
    throw 'Headless CLI must not become a fifth product artifact in the four-app Status Lab pipeline.'
}

Write-Host 'PASS: HID Research CLI safety/architecture smoke'

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$logger = Join-Path $projectRoot 'codex-hook-logger.ps1'
$installer = Join-Path $projectRoot 'install-codex-hooks.ps1'
$configExample = Join-Path $projectRoot 'status-lab-config.example.toml'
$configurator = Join-Path $projectRoot 'configurator\index.html'
$rgbCanary = Join-Path $projectRoot 'K15RgbCanary.cs'
$lightingLabProject = Join-Path $projectRoot 'lighting-lab\Vorotex.K15.LightingLab.csproj'
$lightingLabForm = Join-Path $projectRoot 'lighting-lab\LightingLabForm.cs'
$lightingLabSession = Join-Path $projectRoot 'lighting-lab\LightingLabSession.cs'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('vorotex-k15-status-lab-' + [Guid]::NewGuid().ToString('N'))

foreach ($required in @($configExample, $configurator, $rgbCanary, $lightingLabProject, $lightingLabForm, $lightingLabSession)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required Status Lab file missing: $required" }
}

$html = Get-Content -LiteralPath $configurator -Raw -Encoding UTF8
if ($html -match 'https?://') { throw 'HTML configurator must not depend on network URLs.' }
if ($html -notmatch 'fileInput' -or $html -notmatch 'downloadBtn') {
    throw 'HTML configurator must support local File API load and TOML download.'
}
if ($html -match "\['mono_water'" -or $html -match "\['tetris_blocks'" -or $html -match "\['neon'") {
    throw 'Main notifier configurator must not offer research-only native modes.'
}

$toml = Get-Content -LiteralPath $configExample -Raw -Encoding UTF8
if ($toml -notmatch '\[profiles\.A\]' -or $toml -notmatch '\[states\.running\]' -or $toml -notmatch '#') {
    throw 'TOML example must be annotated and include profile/state sections.'
}
if ($toml -match '\[states\.running\][\s\S]*?\bcolor\s*=') {
    throw 'State sections must not own semantic colors.'
}
if ($toml -match 'effect\s*=\s*"mono_water"') {
    throw 'mono_water/Horse race must not be a notifier default.'
}

$rgbSource = Get-Content -LiteralPath $rgbCanary -Raw -Encoding UTF8
if ($rgbSource -match '\bSelectActiveSlot\s*\(') {
    throw 'K15RgbCanary must never programmatically select hardware profiles.'
}
if ($rgbSource -notmatch 'hardwareProfileSelectionPolicy = "observe_only"') {
    throw 'K15RgbCanary must log observe-only hardware-profile policy.'
}

$labSource = (Get-Content -LiteralPath $lightingLabForm -Raw -Encoding UTF8) + (Get-Content -LiteralPath $lightingLabSession -Raw -Encoding UTF8)
if ($labSource -notmatch 'PaletteMask' -or $labSource -notmatch 'lighting-lab\.jsonl' -or $labSource -notmatch 'Restore exact baseline') {
    throw 'Lighting Lab must expose palette-mask research, JSONL logging and exact restore.'
}
if ($labSource -match '\bSelectActiveSlot\s*\(') {
    throw 'Lighting Lab must never programmatically select hardware profiles.'
}

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$oldLocalAppData = $env:LOCALAPPDATA
$oldUserProfile = $env:USERPROFILE

try {
    $localAppData = Join-Path $tempRoot 'localappdata'
    $userProfile = Join-Path $tempRoot 'user'
    New-Item -ItemType Directory -Path $localAppData, $userProfile -Force | Out-Null
    $env:LOCALAPPDATA = $localAppData
    $env:USERPROFILE = $userProfile

    $synthetic = @{
        hook_event_name = 'UserPromptSubmit'
        session_id = 'session-1'
        turn_id = 'turn-1'
        model = 'gpt-test'
        cwd = 'C:\work'
        permission_mode = 'default'
        prompt = 'THIS MUST NOT BE LOGGED'
    } | ConvertTo-Json -Compress

    $synthetic | powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $logger

    $journal = Join-Path $localAppData 'VOROTEX\K15 Status Lab\events.jsonl'
    if (-not (Test-Path -LiteralPath $journal)) { throw 'Hook logger did not create journal.' }
    $record = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.source -ne 'codex_hook' -or $record.event -ne 'UserPromptSubmit') { throw 'Hook logger emitted unexpected event.' }
    if ($null -ne $record.PSObject.Properties['prompt']) { throw 'Hook logger persisted prompt content.' }

    $codexHome = Join-Path $userProfile '.codex'
    $agentLoopHome = Join-Path $userProfile '.codex-agentloop'
    New-Item -ItemType Directory -Path $codexHome, $agentLoopHome -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $agentLoopHome 'config.toml') -Value 'model = "test"' -Encoding UTF8
    $hooksPath = Join-Path $codexHome 'hooks.json'
    @'
{
  "description": "pre-existing",
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "^Bash$",
        "hooks": [
          { "type": "command", "command": "echo keep-me" }
        ]
      }
    ]
  }
}
'@ | Set-Content -LiteralPath $hooksPath -Encoding UTF8

    $firstInstall = (& $installer | Out-String | ConvertFrom-Json)
    $secondInstall = (& $installer | Out-String | ConvertFrom-Json)
    if ($firstInstall.count -ne 2 -or $secondInstall.count -ne 2) {
        throw 'Expected installer to target both .codex and .codex-agentloop.'
    }

    $installed = Get-Content -LiteralPath $hooksPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($installed.description -ne 'pre-existing') { throw 'Installer did not preserve existing root fields.' }
    if (@($installed.hooks.PreToolUse).Count -ne 1) { throw 'Installer did not preserve existing hook groups.' }

    foreach ($eventName in @('UserPromptSubmit', 'PermissionRequest', 'PostToolUse', 'Stop', 'SessionEnd')) {
        $groups = @($installed.hooks.$eventName)
        $statusLabHandlers = @(
            foreach ($group in $groups) {
                foreach ($handler in @($group.hooks)) {
                    if ([string]$handler.commandWindows -like '*codex-hook-logger.ps1*') { $handler }
                }
            }
        )
        if ($statusLabHandlers.Count -ne 1) {
            throw "Expected exactly one Status Lab handler for $eventName, found $($statusLabHandlers.Count)."
        }
    }

    $sessionEndHandler = @(
        foreach ($group in @($installed.hooks.SessionEnd)) {
            foreach ($handler in @($group.hooks)) {
                if ([string]$handler.commandWindows -like '*codex-hook-logger.ps1*') { $handler }
            }
        }
    )[0]
    if ([int]$sessionEndHandler.timeout -ne 3) { throw 'SessionEnd timeout must be 3 seconds.' }

    $backup = $hooksPath + '.vorotex-k15-status-lab.bak'
    if (-not (Test-Path -LiteralPath $backup)) { throw 'Installer did not create one-time backup.' }

    $agentLoopHooksPath = Join-Path $agentLoopHome 'hooks.json'
    if (-not (Test-Path -LiteralPath $agentLoopHooksPath)) { throw 'Installer did not install hooks into .codex-agentloop.' }
    $agentLoopInstalled = Get-Content -LiteralPath $agentLoopHooksPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($eventName in @('UserPromptSubmit', 'PermissionRequest', 'PostToolUse', 'Stop', 'SessionEnd')) {
        if (@($agentLoopInstalled.hooks.$eventName).Count -lt 1) { throw "Missing $eventName in .codex-agentloop hooks.json." }
    }

    Write-Output 'Status Lab + Lighting Lab smoke tests: PASS'
}
finally {
    $env:LOCALAPPDATA = $oldLocalAppData
    $env:USERPROFILE = $oldUserProfile
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

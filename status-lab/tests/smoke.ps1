$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$logger = Join-Path $projectRoot 'codex-hook-logger.ps1'
$installer = Join-Path $projectRoot 'install-codex-hooks.ps1'
$configExample = Join-Path $projectRoot 'status-lab-config.example.toml'
$configurator = Join-Path $projectRoot 'configurator\index.html'
$rgbCanary = Join-Path $projectRoot 'K15RgbCanary.cs'
$trayIconFactory = Join-Path $projectRoot 'TrayIconFactory.cs'
$stateReducer = Join-Path $projectRoot 'StateReducer.cs'
$normalizer = Join-Path $projectRoot 'JournalStateNormalizer.cs'
$eventJournal = Join-Path $projectRoot 'EventJournal.cs'
$appContext = Join-Path $projectRoot 'StatusTrayApplicationContext.cs'
$lightingLabProject = Join-Path $projectRoot 'lighting-lab\Vorotex.K15.LightingLab.csproj'
$lightingLabForm = Join-Path $projectRoot 'lighting-lab\LightingLabForm.cs'
$lightingLabSession = Join-Path $projectRoot 'lighting-lab\LightingLabSession.cs'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('vorotex-k15-status-lab-' + [Guid]::NewGuid().ToString('N'))

foreach ($required in @($configExample, $configurator, $rgbCanary, $trayIconFactory, $stateReducer, $normalizer, $eventJournal, $appContext, $lightingLabProject, $lightingLabForm, $lightingLabSession)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required Status Lab file missing: $required" }
}

$html = Get-Content -LiteralPath $configurator -Raw -Encoding UTF8
if ($html -match 'https?://') { throw 'HTML configurator must not depend on network URLs.' }
if ($html -notmatch 'fileInput' -or $html -notmatch 'downloadBtn') {
    throw 'HTML configurator must support local File API load and TOML download.'
}
if ($html -notmatch 'backupBtn' -or $html -notmatch 'restoreBtn' -or $html -notmatch 'configPath' -or $html -notmatch 'configData') {
    throw 'Configurator must auto-load current config and expose backup/restore workflow.'
}
if ($html -match "\['mono_water'" -or $html -match "\['tetris_blocks'" -or $html -match "\['neon'") {
    throw 'Main notifier configurator must not offer research-only native modes.'
}
if ($html -notmatch 'cycle_breathing' -or $html -notmatch 'profile_pair' -or $html -notmatch 'stop_signal') {
    throw 'Configurator must expose accepted Cycle breathing and profile-pair STOP/activation signals.'
}
if ($html -notmatch 'staleTimeout:18000' -or $html -notmatch 'schema_version = 5' -or $html -notmatch 'profile_switch:\{enabled:false') {
    throw 'Configurator must expose schema v5, five-hour stale attention reset, and profile switch OFF.'
}
if ($html -notmatch "activation:\{enabled:true,effect:'cycle_breathing'" -or $html -notmatch 'migrateLoadedModel') {
    throw 'Configurator must use Cycle breathing activation and migrate beta defaults in memory.'
}

$toml = Get-Content -LiteralPath $configExample -Raw -Encoding UTF8
if ($toml -notmatch 'schema_version\s*=\s*5' -or $toml -notmatch '\[stop_signal\]' -or $toml -notmatch 'profile_pair') {
    throw 'TOML example must use schema v5 and include profile-pair STOP signal.'
}
if ($toml -notmatch '\[behavior\]' -or $toml -notmatch 'stale_attention_timeout_seconds\s*=\s*18000') {
    throw 'TOML example must expose five-hour stale attention reset.'
}
if ($toml -notmatch '\[profile_switch\][\s\S]*?enabled\s*=\s*false' -or
    $toml -notmatch '\[activation\][\s\S]*?effect\s*=\s*"cycle_breathing"') {
    throw 'RC1 TOML must disable profile-switch overlay and use Cycle breathing activation.'
}
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
if ($rgbSource -notmatch 'rgb_stop_signal_started' -or $rgbSource -notmatch 'StopSignal') {
    throw 'K15RgbCanary must implement explicit STOP overlay before DONE state.'
}
if ($rgbSource -notmatch 'hardwareProfileSelectionPolicy = "observe_only"') {
    throw 'K15RgbCanary must log observe-only hardware-profile policy.'
}
if ($rgbSource -notmatch '_pendingRestores' -or $rgbSource -notmatch 'RestorePendingForSlotLocked' -or
    $rgbSource -notmatch 'manual_restore_while_disabled') {
    throw 'RGB OFF must retain deferred exact baselines and allow manual restore on the physical active profile.'
}

$traySource = Get-Content -LiteralPath $trayIconFactory -Raw -Encoding UTF8
if ($traySource -notmatch 'trackingEnabled' -or $traySource -notmatch 'DestroyIcon') {
    throw 'Tray icon factory must expose distinct ON/OFF icons without leaking native handles.'
}

$stateSource = Get-Content -LiteralPath $stateReducer -Raw -Encoding UTF8
$normalizerSource = Get-Content -LiteralPath $normalizer -Raw -Encoding UTF8
if ($stateSource -notmatch 'FocusedSessionId' -or $stateSource -notmatch 'IsInternalCwd' -or $stateSource -notmatch 'Rehydrate') {
    throw 'State reducer must be session-aware and support restart rehydration.'
}
if ($stateSource -notmatch 'case "PreToolUse"' -or $stateSource -notmatch 'codex_pre_tool_use') {
    throw 'State reducer must resume WAITING from the PreToolUse approval signal.'
}
if ($stateSource -notmatch 'CodexAttentionSnapshot' -or $stateSource -notmatch 'stale_attention_timeout' -or
    $stateSource -match 'done_notification_resolved') {
    throw 'State reducer must aggregate attention and never treat toast removal as DONE acknowledgement.'
}
if ($normalizerSource -notmatch 'StartupReplayWindow' -or $normalizerSource -notmatch 'state_rehydrated') {
    throw 'Journal normalizer must replay recent Codex hooks and log rehydrated state.'
}
if ($normalizerSource -notmatch '_readOffset' -or $normalizerSource -match 'File\.ReadAllLines\(EventJournal\.FilePath\)') {
    throw 'Runtime normalizer must tail appended journal bytes instead of re-reading the whole file.'
}

$journalSource = Get-Content -LiteralPath $eventJournal -Raw -Encoding UTF8
$hookLoggerSource = Get-Content -LiteralPath $logger -Raw -Encoding UTF8
$appSource = Get-Content -LiteralPath $appContext -Raw -Encoding UTF8
if ($journalSource -notmatch 'MaxFileBytes' -or $journalSource -notmatch 'MaxArchives' -or $journalSource -notmatch 'DetailedLoggingEnabled') {
    throw 'Event journal must be bounded and expose detailed logging toggle.'
}
if ($hookLoggerSource -notmatch 'Rotate-JournalIfNeeded') {
    throw 'Codex hook transport must rotate the shared journal independently of the tray process.'
}
if ($appSource -notmatch '_loggingItem' -or $appSource -notmatch 'ToggleDetailedLogging' -or $appSource -notmatch 'RefreshTrackingIndicator') {
    throw 'Tray must expose detailed-log switch and tracking indicator wiring.'
}
if ($appSource -notmatch 'ManualResetAttention' -or $appSource -notmatch 'RestoreNativeLightingAsync' -or
    $appSource -notmatch 'manual_attention_reset') {
    throw 'Tray must expose manual WAITING/DONE reset and exact baseline recovery.'
}
if ($appSource -notmatch 'StatusTrayIpc' -or $appSource -notmatch 'OpenControlCenterProcess') {
    throw 'Split tray must expose local IPC and launch the standalone Control Center.'
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
$oldCodexHome = $env:CODEX_HOME

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
    if ($record.sessionId -ne 'session-1' -or $record.cwd -ne 'C:\work') { throw 'Hook logger must preserve sessionId/cwd metadata for focus tracking.' }
    if ($null -ne $record.PSObject.Properties['prompt']) { throw 'Hook logger persisted prompt content.' }

    $codexHome = Join-Path $userProfile '.codex'
    $agentLoopHome = Join-Path $userProfile '.codex-agentloop'
    New-Item -ItemType Directory -Path $codexHome, $agentLoopHome -Force | Out-Null
    # install-codex-hooks.ps1 intentionally honors CODEX_HOME before probing
    # USERPROFILE. Keep that lookup fully inside the synthetic fixture.
    $env:CODEX_HOME = $agentLoopHome
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
    if (@($installed.hooks.PreToolUse).Count -ne 2) { throw 'Installer must preserve the existing PreToolUse group and add its own group.' }

    foreach ($eventName in @('UserPromptSubmit', 'PermissionRequest', 'PreToolUse', 'PostToolUse', 'Stop', 'SessionEnd')) {
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
    foreach ($eventName in @('UserPromptSubmit', 'PermissionRequest', 'PreToolUse', 'PostToolUse', 'Stop', 'SessionEnd')) {
        if (@($agentLoopInstalled.hooks.$eventName).Count -lt 1) { throw "Missing $eventName in .codex-agentloop hooks.json." }
    }

    Write-Output 'Status Tray approval + restore + config + Lighting Lab smoke tests: PASS'
}
finally {
    $env:LOCALAPPDATA = $oldLocalAppData
    $env:USERPROFILE = $oldUserProfile
    $env:CODEX_HOME = $oldCodexHome
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

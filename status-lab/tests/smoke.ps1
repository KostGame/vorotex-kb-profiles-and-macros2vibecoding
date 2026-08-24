$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$logger = Join-Path $projectRoot 'codex-hook-logger.ps1'
$installer = Join-Path $projectRoot 'install-codex-hooks.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('vorotex-k15-status-lab-' + [Guid]::NewGuid().ToString('N'))

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
    if (-not (Test-Path -LiteralPath $journal)) {
        throw 'Hook logger did not create journal.'
    }

    $record = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.source -ne 'codex_hook' -or $record.event -ne 'UserPromptSubmit') {
        throw 'Hook logger emitted unexpected event.'
    }
    if ($null -ne $record.PSObject.Properties['prompt']) {
        throw 'Hook logger persisted prompt content.'
    }

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
          {
            "type": "command",
            "command": "echo keep-me"
          }
        ]
      }
    ]
  }
}
'@ | Set-Content -LiteralPath $hooksPath -Encoding UTF8

    $firstInstall = (& $installer | Out-String | ConvertFrom-Json)
    $secondInstall = (& $installer | Out-String | ConvertFrom-Json)
    if ($firstInstall.count -ne 2 -or $secondInstall.count -ne 2) {
        throw "Expected installer to target both .codex and .codex-agentloop."
    }

    $installed = Get-Content -LiteralPath $hooksPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($installed.description -ne 'pre-existing') {
        throw 'Installer did not preserve existing root fields.'
    }
    if (@($installed.hooks.PreToolUse).Count -ne 1) {
        throw 'Installer did not preserve existing hook groups.'
    }

    foreach ($eventName in @('UserPromptSubmit', 'PermissionRequest', 'PostToolUse', 'Stop', 'SessionEnd')) {
        $groups = @($installed.hooks.$eventName)
        $statusLabHandlers = @(
            foreach ($group in $groups) {
                foreach ($handler in @($group.hooks)) {
                    if ([string]$handler.commandWindows -like '*codex-hook-logger.ps1*') {
                        $handler
                    }
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
                if ([string]$handler.commandWindows -like '*codex-hook-logger.ps1*') {
                    $handler
                }
            }
        }
    )[0]
    if ([int]$sessionEndHandler.timeout -ne 3) {
        throw "SessionEnd timeout must be 3 seconds to match Codex loader limits."
    }

    $backup = $hooksPath + '.vorotex-k15-status-lab.bak'
    if (-not (Test-Path -LiteralPath $backup)) {
        throw 'Installer did not create one-time backup.'
    }

    $agentLoopHooksPath = Join-Path $agentLoopHome 'hooks.json'
    if (-not (Test-Path -LiteralPath $agentLoopHooksPath)) {
        throw 'Installer did not install hooks into .codex-agentloop.'
    }
    $agentLoopInstalled = Get-Content -LiteralPath $agentLoopHooksPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($eventName in @('UserPromptSubmit', 'PermissionRequest', 'PostToolUse', 'Stop', 'SessionEnd')) {
        if (@($agentLoopInstalled.hooks.$eventName).Count -lt 1) {
            throw "Missing $eventName in .codex-agentloop hooks.json."
        }
    }

    Write-Output 'Status Lab smoke tests: PASS'
}
finally {
    $env:LOCALAPPDATA = $oldLocalAppData
    $env:USERPROFILE = $oldUserProfile
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

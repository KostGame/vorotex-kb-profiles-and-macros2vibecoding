$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $root 'install-codex-hooks.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('vorotex-hook-home-sync-' + [Guid]::NewGuid().ToString('N'))
$oldLocalAppData = $env:LOCALAPPDATA
$oldUserProfile = $env:USERPROFILE
$oldCodexHome = $env:CODEX_HOME

try {
    $userProfile = Join-Path $tempRoot 'user'
    $agentLoopHome = Join-Path $userProfile '.codex-agentloop'
    $defaultHome = Join-Path $userProfile '.codex'
    New-Item -ItemType Directory -Path $agentLoopHome, $defaultHome -Force | Out-Null

    $env:USERPROFILE = $userProfile
    $env:LOCALAPPDATA = Join-Path $tempRoot 'localappdata'
    $env:CODEX_HOME = $agentLoopHome

    @'
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "commandWindows": "powershell.exe -File C:\\stale\\codex-hook-logger.ps1" } ] }
    ],
    "PreToolUse": [
      { "matcher": "^Bash$", "hooks": [ { "type": "command", "command": "echo foreign-keep" } ] },
      { "hooks": [ { "type": "command", "commandWindows": "powershell.exe -File C:\\old\\codex-hook-logger.ps1" } ] },
      { "hooks": [ { "type": "command", "commandWindows": "powershell.exe -File C:\\old\\codex-hook-logger.ps1" } ] }
    ]
  }
}
'@ | Set-Content -LiteralPath (Join-Path $agentLoopHome 'hooks.json') -Encoding UTF8
    $originalHooks = Get-Content -LiteralPath (Join-Path $agentLoopHome 'hooks.json') -Raw -Encoding UTF8

    $result = (& $installer | Out-String | ConvertFrom-Json)
    if ($result.count -ne 2) {
        throw "Expected CODEX_HOME plus default .codex to be synchronized; installer count=$($result.count)."
    }
    $firstBackup = @($result.installed | Where-Object { $_.home -eq $agentLoopHome })[0].backupPath
    if ([string]::IsNullOrWhiteSpace($firstBackup) -or -not (Test-Path -LiteralPath $firstBackup -PathType Leaf)) {
        throw 'First repair must create a unique hooks.json backup.'
    }
    if ((Get-Content -LiteralPath $firstBackup -Raw -Encoding UTF8) -ne $originalHooks) {
        throw 'hooks.json backup does not preserve the exact pre-repair contents.'
    }

    foreach ($codexHomePath in @($agentLoopHome, $defaultHome)) {
        $hooksPath = Join-Path $codexHomePath 'hooks.json'
        if (-not (Test-Path -LiteralPath $hooksPath)) {
            throw "hooks.json missing after sync: $hooksPath"
        }

        $hooks = Get-Content -LiteralPath $hooksPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($eventName in @('UserPromptSubmit','PermissionRequest','PreToolUse','PostToolUse','Stop','SessionEnd')) {
            $groups = @($hooks.hooks.$eventName)
            $matches = @(
                foreach ($group in $groups) {
                    foreach ($handler in @($group.hooks)) {
                        if ([string]$handler.commandWindows -like '*codex-hook-logger.ps1*') { $handler }
                    }
                }
            )
            if ($matches.Count -ne 1) {
                throw "Expected exactly one Status Lab handler for $eventName in $codexHomePath; found $($matches.Count)."
            }
        }
        if ($null -ne $hooks.hooks.PSObject.Properties['SessionStart']) { throw "Stale SessionStart Status Lab hook survived: $codexHomePath" }
    }

    $preTool = Get-Content -LiteralPath (Join-Path $agentLoopHome 'hooks.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $foreign = @($preTool.hooks.PreToolUse | ForEach-Object { $_.hooks } | ForEach-Object { $_ } |
        Where-Object { $_.command -eq 'echo foreign-keep' })
    if ($foreign.Count -ne 1) { throw 'Foreign PreToolUse handler was not preserved.' }
    $stableLogger = Join-Path $env:LOCALAPPDATA 'VorotexK15\app\hooks\codex-hook-logger.ps1'
    if (-not (Test-Path -LiteralPath $stableLogger -PathType Leaf)) { throw 'Stable deployed logger is missing.' }
    $firstSemantic = $preTool | ConvertTo-Json -Depth 20 -Compress
    & $installer | Out-Null
    $backupCount = @(Get-ChildItem -LiteralPath $agentLoopHome -Filter 'hooks.json.vorotex-k15-status-lab.*.bak' -File).Count
    if ($backupCount -ne 1) { throw "Idempotent repair created an unnecessary backup chain: $backupCount" }
    $secondSemantic = (Get-Content -LiteralPath (Join-Path $agentLoopHome 'hooks.json') -Raw -Encoding UTF8 | ConvertFrom-Json | ConvertTo-Json -Depth 20 -Compress)
    if ($firstSemantic -ne $secondSemantic) { throw 'Second repair introduced semantic drift.' }

    Write-Output 'Codex hook home sync with CODEX_HOME + .codex: PASS'
}
finally {
    $env:USERPROFILE = $oldUserProfile
    $env:LOCALAPPDATA = $oldLocalAppData
    $env:CODEX_HOME = $oldCodexHome
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

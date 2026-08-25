$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $root 'install-codex-hooks.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('vorotex-hook-home-sync-' + [Guid]::NewGuid().ToString('N'))
$oldUserProfile = $env:USERPROFILE
$oldCodexHome = $env:CODEX_HOME

try {
    $userProfile = Join-Path $tempRoot 'user'
    $agentLoopHome = Join-Path $userProfile '.codex-agentloop'
    $defaultHome = Join-Path $userProfile '.codex'
    New-Item -ItemType Directory -Path $agentLoopHome, $defaultHome -Force | Out-Null

    $env:USERPROFILE = $userProfile
    $env:CODEX_HOME = $agentLoopHome

    $result = (& $installer | Out-String | ConvertFrom-Json)
    if ($result.count -ne 2) {
        throw "Expected CODEX_HOME plus default .codex to be synchronized; installer count=$($result.count)."
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
    }

    Write-Output 'Codex hook home sync with CODEX_HOME + .codex: PASS'
}
finally {
    $env:USERPROFILE = $oldUserProfile
    $env:CODEX_HOME = $oldCodexHome
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

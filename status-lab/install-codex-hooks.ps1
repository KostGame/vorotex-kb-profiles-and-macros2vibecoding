param(
    [string[]]$CodexHome
)

$ErrorActionPreference = 'Stop'

function Get-DetectedCodexHomes {
    param([string[]]$ExplicitHomes)

    $candidates = New-Object System.Collections.Generic.List[string]
    $hasExplicitHomes = $false

    foreach ($homePath in @($ExplicitHomes)) {
        if (-not [string]::IsNullOrWhiteSpace($homePath)) {
            $candidates.Add($homePath)
            $hasExplicitHomes = $true
        }
    }

    # An explicit -CodexHome remains a precise/manual override. Without it, CODEX_HOME is
    # one detected environment, not an exclusive environment: Status Lab health checks all
    # local Codex homes, so the installer must keep that same set in sync.
    if (-not $hasExplicitHomes) {
        if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
            $candidates.Add($env:CODEX_HOME)
        }

        foreach ($name in @('.codex-agentloop', '.codex')) {
            $candidate = Join-Path $env:USERPROFILE $name
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $candidates.Add($candidate)
            }
        }

        foreach ($dir in @(Get-ChildItem -LiteralPath $env:USERPROFILE -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '.codex-*' })) {
            $candidates.Add($dir.FullName)
        }
    }

    if ($candidates.Count -eq 0) {
        $candidates.Add((Join-Path $env:USERPROFILE '.codex'))
    }

    $seen = @{}
    $result = @()
    foreach ($candidate in $candidates) {
        $full = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($candidate)).TrimEnd('\')
        $key = $full.ToLowerInvariant()
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            $result += $full
        }
    }

    return @($result)
}

function Test-IsStatusLabHandler {
    param($Handler)

    if ($null -eq $Handler) {
        return $false
    }

    foreach ($name in @('command', 'commandWindows', 'command_windows')) {
        $property = $Handler.PSObject.Properties[$name]
        if ($null -ne $property -and $null -ne $property.Value) {
            $value = [string]$property.Value
            if ($value -like '*codex-hook-logger.ps1*') {
                return $true
            }
        }
    }

    return $false
}

function Remove-OldStatusLabHandlers {
    param([object[]]$Groups)

    $result = @()
    foreach ($group in @($Groups)) {
        if ($null -eq $group) {
            continue
        }

        if ($group -is [Array]) {
            $result += Remove-OldStatusLabHandlers -Groups $group
            continue
        }

        $hooksProperty = $group.PSObject.Properties['hooks']
        if ($null -eq $hooksProperty) {
            $result += $group
            continue
        }

        $keptHandlers = @()
        foreach ($handler in @($hooksProperty.Value)) {
            if (-not (Test-IsStatusLabHandler -Handler $handler)) {
                $keptHandlers += $handler
            }
        }

        if ($keptHandlers.Count -gt 0) {
            $group.hooks = @($keptHandlers)
            $result += $group
        }
    }

    return @($result)
}

function Remove-StatusLabHandlersFromAllEvents {
    param([Parameter(Mandatory)]$Hooks)

    # Snapshot event names first so stale events such as SessionStart are cleaned
    # while every non-Status-Lab group remains unchanged.
    foreach ($property in @($Hooks.PSObject.Properties)) {
        $eventName = $property.Name
        $groups = Remove-OldStatusLabHandlers -Groups @($property.Value)
        if ($groups.Count -eq 0) {
            $Hooks.PSObject.Properties.Remove($eventName)
        } else {
            $property.Value = @($groups)
        }
    }
}

function Get-StableLoggerPath {
    $localAppData = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($localAppData)) { $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData) }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'LOCALAPPDATA is unavailable; stable Status Lab logger path cannot be established.'
    }
    return (Join-Path $localAppData 'VorotexK15\app\hooks\codex-hook-logger.ps1')
}

function Deploy-StableLogger {
    param([string]$SourcePath)

    $destination = Get-StableLoggerPath
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    if (-not [string]::Equals([IO.Path]::GetFullPath($SourcePath), [IO.Path]::GetFullPath($destination), [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $SourcePath -Destination $destination -Force -ErrorAction Stop
    }
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        throw "Stable logger deployment failed: $destination"
    }
    return [IO.Path]::GetFullPath($destination)
}

function New-HooksBackupPath {
    param([string]$HooksPath)

    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    do {
        $candidate = "$HooksPath.vorotex-k15-status-lab.$stamp.$([Guid]::NewGuid().ToString('N')).bak"
    } while (Test-Path -LiteralPath $candidate)
    return $candidate
}

function Install-StatusLabHooks {
    param(
        [Parameter(Mandatory)][string]$CodexHomePath,
        [Parameter(Mandatory)][string]$Logger
    )

    New-Item -ItemType Directory -Path $CodexHomePath -Force | Out-Null

    $target = Join-Path $CodexHomePath 'hooks.json'
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        try {
            $root = Get-Content -LiteralPath $target -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        } catch {
            throw "Existing hooks.json is malformed; no changes were written: $target"
        }
    } else {
        $root = New-Object PSObject
    }

    if ($null -eq $root.PSObject.Properties['hooks']) {
        $root | Add-Member -MemberType NoteProperty -Name 'hooks' -Value (New-Object PSObject)
    }
    if ($null -eq $root.hooks) {
        $root.hooks = New-Object PSObject
    }

    Remove-StatusLabHandlersFromAllEvents -Hooks $root.hooks

    $quotedLogger = '"' + $Logger + '"'
    $commandLine = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $quotedLogger"

    $events = @(
        @{ Name = 'UserPromptSubmit'; Async = $true },
        @{ Name = 'PermissionRequest'; Async = $true },
        @{ Name = 'PreToolUse'; Async = $true },
        @{ Name = 'PostToolUse'; Async = $true },
        @{ Name = 'Stop'; Async = $true },
        @{ Name = 'SessionEnd'; Async = $false }
    )

    foreach ($event in $events) {
        $name = [string]$event.Name
        $property = $root.hooks.PSObject.Properties[$name]
        $groups = if ($null -eq $property) { @() } else { @($property.Value) }
        $groups = Remove-OldStatusLabHandlers -Groups $groups

        $handler = [ordered]@{
            type = 'command'
            command = $commandLine
            commandWindows = $commandLine
            timeout = if ($name -eq 'SessionEnd') { 3 } else { 5 }
            async = [bool]$event.Async
            statusMessage = 'K15 Status Lab'
        }

        $newGroup = [ordered]@{
            hooks = @($handler)
        }

        $newValue = @($groups) + @($newGroup)
        if ($null -eq $property) {
            $root.hooks | Add-Member -MemberType NoteProperty -Name $name -Value $newValue
        } else {
            $root.hooks.$name = $newValue
        }
    }

    $json = $root | ConvertTo-Json -Depth 20
    $existingSemantic = if (Test-Path -LiteralPath $target -PathType Leaf) {
        try { (Get-Content -LiteralPath $target -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop | ConvertTo-Json -Depth 20 -Compress) }
        catch { throw "Existing hooks.json is malformed; no changes were written: $target" }
    } else { $null }
    $desiredSemantic = ($json | ConvertFrom-Json -ErrorAction Stop | ConvertTo-Json -Depth 20 -Compress)
    if ($null -ne $existingSemantic -and $existingSemantic -eq $desiredSemantic) {
        return [pscustomobject]@{
            home = $CodexHomePath
            hooksPath = $target
            backupPath = $null
            changed = $false
            loggerPath = $Logger
        }
    }

    # The backup is created only after a real semantic change is known, and
    # immediately before the first write to hooks.json. Never overwrite one.
    $backup = $null
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $backup = New-HooksBackupPath -HooksPath $target
        [IO.File]::Copy($target, $backup, $false)
    }
    $tmp = $target + '.tmp.' + [Guid]::NewGuid().ToString('N')

    try {
        [IO.File]::WriteAllText($tmp, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tmp -Destination $target -Force
    } finally {
        if (Test-Path -LiteralPath $tmp) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }

    # Verify the exact Status Lab handlers survived serialization.
    $verify = Get-Content -LiteralPath $target -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    foreach ($name in @('UserPromptSubmit', 'PermissionRequest', 'PreToolUse', 'PostToolUse', 'Stop', 'SessionEnd')) {
        $matches = @(
            foreach ($group in @($verify.hooks.$name)) {
                foreach ($handler in @($group.hooks)) {
                    if (Test-IsStatusLabHandler -Handler $handler) {
                        $handler
                    }
                }
            }
        )
        if ($matches.Count -ne 1) {
            throw "Hook verification failed for $name in $target"
        }
        foreach ($handler in $matches) {
            if ([string]$handler.commandWindows -notlike "*$Logger*") {
                throw "Hook path verification failed for $name in $target"
            }
        }
    }
    $canonicalNames = @('UserPromptSubmit', 'PermissionRequest', 'PreToolUse', 'PostToolUse', 'Stop', 'SessionEnd')
    foreach ($property in @($verify.hooks.PSObject.Properties)) {
        if ($canonicalNames -notcontains $property.Name) {
            $stale = @(
                foreach ($group in @($property.Value)) {
                    foreach ($handler in @($group.hooks)) {
                        if (Test-IsStatusLabHandler -Handler $handler) { $handler }
                    }
                }
            )
            if ($stale.Count -ne 0) { throw "Stale Status Lab handler survived for $($property.Name) in $target" }
        }
    }

    return [pscustomobject]@{
        home = $CodexHomePath
        hooksPath = $target
        backupPath = $backup
        changed = $true
        loggerPath = $Logger
    }
}

$sourceLogger = Join-Path $PSScriptRoot 'codex-hook-logger.ps1'
if (-not (Test-Path -LiteralPath $sourceLogger -PathType Leaf)) {
    throw "Logger script not found: $sourceLogger"
}
$logger = Deploy-StableLogger -SourcePath $sourceLogger

$homes = Get-DetectedCodexHomes -ExplicitHomes $CodexHome
$installed = @()
foreach ($codexHomePath in $homes) {
    $installed += Install-StatusLabHooks -CodexHomePath $codexHomePath -Logger $logger
}

[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
[pscustomobject]@{
    status = 'OK'
    count = $installed.Count
    installed = @($installed)
} | ConvertTo-Json -Depth 8 -Compress

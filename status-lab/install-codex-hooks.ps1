param(
    [string[]]$CodexHome
)

$ErrorActionPreference = 'Stop'

function Get-DetectedCodexHomes {
    param([string[]]$ExplicitHomes)

    $candidates = New-Object System.Collections.Generic.List[string]

    foreach ($home in @($ExplicitHomes)) {
        if (-not [string]::IsNullOrWhiteSpace($home)) {
            $candidates.Add($home)
        }
    }

    if ($candidates.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $candidates.Add($env:CODEX_HOME)
    }

    if ($candidates.Count -eq 0) {
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

function Install-StatusLabHooks {
    param(
        [Parameter(Mandatory)][string]$CodexHomePath,
        [Parameter(Mandatory)][string]$Logger
    )

    New-Item -ItemType Directory -Path $CodexHomePath -Force | Out-Null

    $target = Join-Path $CodexHomePath 'hooks.json'
    $backup = $target + '.vorotex-k15-status-lab.bak'

    if (Test-Path -LiteralPath $target -PathType Leaf) {
        if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
            Copy-Item -LiteralPath $target -Destination $backup -ErrorAction Stop
        }

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

    $quotedLogger = '"' + $Logger + '"'
    $commandLine = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $quotedLogger"

    $events = @(
        @{ Name = 'UserPromptSubmit'; Async = $true },
        @{ Name = 'PermissionRequest'; Async = $true },
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
            timeout = 5
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
    foreach ($name in @('UserPromptSubmit', 'PermissionRequest', 'Stop', 'SessionEnd')) {
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
    }

    return [pscustomobject]@{
        home = $CodexHomePath
        hooksPath = $target
        backupPath = if (Test-Path -LiteralPath $backup -PathType Leaf) { $backup } else { $null }
    }
}

$logger = Join-Path $PSScriptRoot 'codex-hook-logger.ps1'
if (-not (Test-Path -LiteralPath $logger -PathType Leaf)) {
    throw "Logger script not found: $logger"
}

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

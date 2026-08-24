$ErrorActionPreference = 'Stop'

$codexHome = Join-Path $env:USERPROFILE '.codex'
$target = Join-Path $codexHome 'hooks.json'
$backup = $target + '.vorotex-k15-status-lab.bak'
$logger = Join-Path $PSScriptRoot 'codex-hook-logger.ps1'

if (-not (Test-Path -LiteralPath $logger -PathType Leaf)) {
    throw "Logger script not found: $logger"
}

New-Item -ItemType Directory -Path $codexHome -Force | Out-Null

if (Test-Path -LiteralPath $target -PathType Leaf) {
    if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
        Copy-Item -LiteralPath $target -Destination $backup -ErrorAction Stop
    }

    try {
        $root = Get-Content -LiteralPath $target -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Existing Codex hooks.json is malformed; no changes were written. File: $target"
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

$quotedLogger = '"' + $logger + '"'
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

Write-Output "Codex hooks установлены: $target. Перезапусти Codex; если он попросит доверие к hooks, подтверди их."

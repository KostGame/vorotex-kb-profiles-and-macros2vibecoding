param(
    [string]$SourceInstanceId
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceInstanceId) -or
    $SourceInstanceId -cnotmatch '^local:[0-9a-f]{32}$' -or
    [Text.Encoding]::UTF8.GetByteCount($SourceInstanceId) -gt 64) {
    exit 0
}

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $null
    }

    return [string]$property.Value
}

function Get-CanonicalPath {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    try {
        $full = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Value))
        $root = [IO.Path]::GetPathRoot($full)
        $normalized = if ($null -ne $root -and [string]::Equals($full, $root, [StringComparison]::OrdinalIgnoreCase)) {
            $root
        } else {
            $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        }
        return $normalized.Replace('/', '\').ToUpperInvariant()
    } catch {
        return $null
    }
}

function Get-DetectedCodexHomePaths {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) { $candidates += $env:CODEX_HOME }
    $candidates += (Join-Path $env:USERPROFILE '.codex-agentloop')
    $candidates += (Join-Path $env:USERPROFILE '.codex')
    foreach ($dir in @(Get-ChildItem -LiteralPath $env:USERPROFILE -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '.codex-*' })) { $candidates += $dir.FullName }
    return @($candidates | ForEach-Object { Get-CanonicalPath -Value $_ } | Where-Object { $_ })
}

function Get-SafeCwd {
    param([string]$Value)

    $canonical = Get-CanonicalPath -Value $Value
    if ($null -ne $canonical) {
        $leaf = [IO.Path]::GetFileName($canonical.TrimEnd('\'))
        if ($leaf -eq '.CODEX' -or $leaf.StartsWith('.CODEX-', [StringComparison]::Ordinal)) { return $null }
        if ((Get-DetectedCodexHomePaths) -contains $canonical) { return $null }
    }
    return $Value
}

function Rotate-JournalIfNeeded {
    param(
        [Parameter(Mandatory)][string]$Journal,
        [long]$MaxBytes = 5MB,
        [int]$MaxArchives = 2
    )

    if (-not (Test-Path -LiteralPath $Journal)) { return }
    if ((Get-Item -LiteralPath $Journal).Length -lt $MaxBytes) { return }

    for ($i = $MaxArchives; $i -ge 1; $i--) {
        $destination = $Journal + '.' + $i
        if (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Force
        }

        $source = if ($i -eq 1) { $Journal } else { $Journal + '.' + ($i - 1) }
        if (Test-Path -LiteralPath $source) {
            Move-Item -LiteralPath $source -Destination $destination -Force
        }
    }

    [IO.File]::WriteAllText($Journal, '', (New-Object Text.UTF8Encoding($false)))
}

$raw = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw)) {
    exit 0
}

try {
    $payload = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

$eventName = Get-OptionalProperty -Object $payload -Name 'hook_event_name'
if ([string]::IsNullOrWhiteSpace($eventName)) {
    exit 0
}

$record = [ordered]@{
    timestampUtc  = [DateTime]::UtcNow.ToString('o')
    source        = 'codex_hook'
    sourceInstanceId = $SourceInstanceId
    event         = $eventName
    sessionId     = Get-OptionalProperty -Object $payload -Name 'session_id'
    turnId        = Get-OptionalProperty -Object $payload -Name 'turn_id'
    model         = Get-OptionalProperty -Object $payload -Name 'model'
    cwd           = Get-SafeCwd -Value (Get-OptionalProperty -Object $payload -Name 'cwd')
    toolName      = Get-OptionalProperty -Object $payload -Name 'tool_name'
    permissionMode = Get-OptionalProperty -Object $payload -Name 'permission_mode'
}

# Intentionally do not persist prompt, tool_input, transcript content, or assistant text.
$json = $record | ConvertTo-Json -Compress -Depth 4

$root = Join-Path $env:LOCALAPPDATA 'VOROTEX\K15 Status Lab'
$journal = Join-Path $root 'events.jsonl'
New-Item -ItemType Directory -Path $root -Force | Out-Null

$mutex = New-Object Threading.Mutex($false, 'Local\VorotexK15StatusLabJournal')
$locked = $false
try {
    $locked = $mutex.WaitOne([TimeSpan]::FromSeconds(5))
    if (-not $locked) {
        exit 0
    }

    Rotate-JournalIfNeeded -Journal $journal
    [IO.File]::AppendAllText(
        $journal,
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false))
    )
} finally {
    if ($locked) {
        try { $mutex.ReleaseMutex() } catch {}
    }
    $mutex.Dispose()
}

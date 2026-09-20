$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) ('vorotex-source-instance-' + [Guid]::NewGuid().ToString('N'))
$oldLocalAppData = $env:LOCALAPPDATA
$logger = Join-Path $PSScriptRoot '..\codex-hook-logger.ps1'
$homePath = Join-Path $root '.codex-agentloop'
$valid = 'local:0123456789abcdef0123456789abcdef'

try {
    $env:LOCALAPPDATA = Join-Path $root 'localappdata'
    New-Item -ItemType Directory -Path $homePath -Force | Out-Null
    $payload = [ordered]@{
        hook_event_name = 'Stop'
        session_id = 'session-source'
        turn_id = 'turn-source'
        model = 'gpt-test'
        cwd = $homePath
        tool_name = 'tool-name'
        permission_mode = 'default'
        prompt = 'PRIVATE_PROMPT_SENTINEL'
        tool_input = @{ command = 'PRIVATE_TOOL_SENTINEL' }
        transcript = 'PRIVATE_TRANSCRIPT_SENTINEL'
    } | ConvertTo-Json -Depth 5

    $payload | & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $logger -SourceInstanceId $valid
    $journal = Join-Path $env:LOCALAPPDATA 'VOROTEX\K15 Status Lab\events.jsonl'
    if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) { throw 'Logger did not create journal.' }
    $text = Get-Content -LiteralPath $journal -Raw -Encoding UTF8
    $event = $text.Trim() | ConvertFrom-Json
    if ($event.sourceInstanceId -ne $valid) { throw 'Valid sourceInstanceId was not persisted.' }
    if (-not [string]::IsNullOrWhiteSpace([string]$event.cwd) -or
        $text.Contains($homePath.Replace('\', '\\')) -or $text.Contains('PRIVATE_PROMPT_SENTINEL') -or
        $text.Contains('PRIVATE_TOOL_SENTINEL') -or $text.Contains('PRIVATE_TRANSCRIPT_SENTINEL')) {
        throw 'Logger persisted forbidden content or raw home path.'
    }

    $before = $text
    $payload | & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $logger -SourceInstanceId 'LOCAL:0123456789abcdef0123456789abcdef'
    $payload | & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $logger
    if ((Get-Content -LiteralPath $journal -Raw -Encoding UTF8) -ne $before) {
        throw 'Malformed or missing sourceInstanceId was not fail-closed.'
    }

    Write-Output 'Codex hook logger source identity and privacy tests: PASS'
}
finally {
    $env:LOCALAPPDATA = $oldLocalAppData
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

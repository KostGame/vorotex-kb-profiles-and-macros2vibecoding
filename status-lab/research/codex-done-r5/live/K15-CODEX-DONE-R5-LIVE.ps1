[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('PREPARE','ARM','VERIFY_DISABLE','ROLLBACK')][string]$Mode,
    [string]$StateRoot = (Join-Path ($env:LOCALAPPDATA ?? [Environment]::GetFolderPath('LocalApplicationData')) 'VorotexK15\app\codex-done-r5-live'),
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'r5-live-runner.psm1'
Import-Module $module -Force
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$provider = New-R5RealProvider $repoRoot $StateRoot $TimeoutSeconds
New-Item -Path $provider.StateRoot -ItemType Directory -Force | Out-Null

try {
    switch ($Mode) {
        'PREPARE'       { Invoke-R5Prepare $provider }
        'ARM'           { Invoke-R5Arm $provider }
        'VERIFY_DISABLE' { Invoke-R5VerifyDisable $provider }
        'ROLLBACK'      { Invoke-R5Rollback $provider }
    }
} catch {
    $errorReport = New-R5ErrorReport $provider $Mode $_
    "STATUS=BLOCKED"
    "NEXT_ACTION=ROLLBACK"
    "ERROR_CLASS=$($errorReport.ERROR_CLASS)"
    "ERROR_STAGE=$($errorReport.ERROR_STAGE)"
    $safeMessage = switch -Regex ([string]$_.Exception.Message) {
        'clean HEAD==origin.main' { 'repository preflight blocked: clean HEAD==origin/main required'; break }
        'hook health' { 'hook health validation blocked'; break }
        'permanent StatusTray' { 'permanent StatusTray identity was ambiguous'; break }
        'child' { 'Codex child identity validation blocked'; break }
        default { "$Mode operation blocked" }
    }
    "ERROR_MESSAGE=$safeMessage"
    exit 2
}

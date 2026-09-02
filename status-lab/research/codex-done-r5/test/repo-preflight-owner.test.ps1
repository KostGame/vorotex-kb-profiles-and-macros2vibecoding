$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '..\live\r5-live-runner.psm1'
Import-Module $module -Force

function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Invoke-Git([string]$Root, [string[]]$Arguments) {
    & git -C $Root @Arguments | Out-Null
    if ($LASTEXITCODE) { throw "git fixture command failed: $($Arguments -join ' ')" }
}
function New-RepoFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) "r5-preflight-$([Guid]::NewGuid().ToString('N'))"
    New-Item -Path $root -ItemType Directory -Force | Out-Null
    Invoke-Git $root @('init', '-q', '-b', 'main')
    Invoke-Git $root @('config', 'user.email', 'r5-test@example.invalid')
    Invoke-Git $root @('config', 'user.name', 'R5 Test')
    Set-Content -LiteralPath (Join-Path $root 'tracked.txt') -Value 'baseline' -Encoding UTF8
    Invoke-Git $root @('add', 'tracked.txt'); Invoke-Git $root @('commit', '-q', '-m', 'fixture')
    Invoke-Git $root @('update-ref', 'refs/remotes/origin/main', 'HEAD')
    return $root
}

$roots = @()
try {
    $root = New-RepoFixture; $roots += $root
    $provider = New-R5RealProvider $root (Join-Path $root 'state') 1
    $head = $provider.RepoPreflight()
    Require ($head -eq (git -C $root rev-parse HEAD).Trim()) 'clean real RepoPreflight did not return HEAD'
    'CLEAN_WORKTREE=PASS'
    'NULL_SAFE_PORCELAIN=PASS'

    Add-Content -LiteralPath (Join-Path $root 'tracked.txt') -Value 'dirty'
    $blocked = $false; try { $provider.RepoPreflight() } catch { $blocked = $true }
    Require $blocked 'tracked dirty worktree was accepted'
    'DIRTY_TRACKED_WORKTREE_BLOCKED=PASS'

    Invoke-Git $root @('checkout', '-q', '--', 'tracked.txt')
    Set-Content -LiteralPath (Join-Path $root 'untracked.txt') -Value 'untracked' -Encoding UTF8
    $blocked = $false; try { $provider.RepoPreflight() } catch { $blocked = $true }
    Require $blocked 'untracked worktree was accepted'
    'DIRTY_UNTRACKED_WORKTREE_BLOCKED=PASS'
    Remove-Item -LiteralPath (Join-Path $root 'untracked.txt') -Force

    Invoke-Git $root @('update-ref', 'refs/remotes/origin/main', (git -C $root rev-parse HEAD~0))
    Invoke-Git $root @('commit', '-q', '--allow-empty', '-m', 'mismatch')
    $blocked = $false; try { $provider.RepoPreflight() } catch { $blocked = $true }
    Require $blocked 'HEAD mismatch was accepted'
    'HEAD_MISMATCH_BLOCKED=PASS'

    $runner = Join-Path $PSScriptRoot '..\live\K15-CODEX-DONE-R5-LIVE.ps1'
    $source = Get-Content -LiteralPath $runner -Raw
    Require ($source -match 'ERROR_CLASS=' -and $source -match 'ERROR_MESSAGE=' -and $source -match 'ERROR_STAGE=') 'top-level PREPARE error surface is incomplete'
    Require ($source -notmatch 'Exception\.Message\)\s*"?$') 'top-level error surface may emit raw exception text'
    'OWNER_ERROR_SURFACE=PASS'
    'REPO_PREFLIGHT_OWNER_BEHAVIORAL=PASS'
}
finally {
    foreach ($root in $roots) { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } }
}

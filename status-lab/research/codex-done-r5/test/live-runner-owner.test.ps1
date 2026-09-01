$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot '..\live\K15-CODEX-DONE-R5-LIVE.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "r5-owner-test-$PID"
try {
    $prepare = & pwsh.exe -NoProfile -File $runner -Mode PREPARE -Simulation -StateRoot $root
    if (($prepare -join "`n") -notmatch 'CANARY_PREPARED=YES') { throw 'simulation PREPARE did not arm the owner entrypoint' }
    $arm = & pwsh.exe -NoProfile -File $runner -Mode ARM -Simulation -StateRoot $root
    if (($arm -join "`n") -notmatch 'CANARY_ARMED=YES|APP_SERVER_ROUTE_OBSERVED=YES') { throw 'simulation ARM did not prove route evidence' }
    $verify = & pwsh.exe -NoProfile -File $runner -Mode VERIFY_DISABLE -Simulation -StateRoot $root
    if (($verify -join "`n") -notmatch 'PRODUCTION_DISABLE=PASS') { throw 'simulation VERIFY_DISABLE did not restore state' }
    $rollback = & pwsh.exe -NoProfile -File $runner -Mode ROLLBACK -Simulation -StateRoot $root
    if (($rollback -join "`n") -notmatch 'ROLLBACK=PASS') { throw 'simulation ROLLBACK did not complete' }
    'LIVE_RUNNER_OWNER_ENTRYPOINT=PASS'
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

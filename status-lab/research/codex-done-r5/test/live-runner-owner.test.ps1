$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '..\live\r5-live-runner.psm1'
Import-Module $module -Force

function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function New-TestProvider([string]$Scenario = '') {
    $root = Join-Path ([IO.Path]::GetTempPath()) "r5-owner-test-$PID-$([Guid]::NewGuid().ToString('N'))"
    New-Item $root -ItemType Directory -Force | Out-Null
    return [pscustomobject]@{ Root = $root; Provider = New-R5FakeProvider $root $Scenario }
}

$cases = @()
try {
    $case = New-TestProvider; $cases += $case
    $prepare = Invoke-R5Prepare $case.Provider | Out-String
    Require ($prepare -match 'CANARY_PREPARED=YES') 'full PREPARE did not complete'
    Require (Test-Path (Join-Path $case.Root 'production-manifest.json')) 'PREPARE manifest was not persisted'
    Require (Test-Path (Join-Path $case.Root 'artifacts\adapter\K15.CodexBridge.WindowsAdapter.exe')) 'adapter artifact was not published'
    Require (Test-Path (Join-Path $case.Root 'artifacts\tray\Vorotex.K15.StatusTray.exe')) 'tray artifact was not published'

    foreach ($scenario in 'hook','route-timeout') {
        $case = New-TestProvider $scenario; $cases += $case
        $failed = $false; try { Invoke-R5Prepare $case.Provider | Out-Null } catch { $failed = $true }
        if ($scenario -eq 'hook') { Require $failed 'PREPARE hook-health failure was accepted' } else {
            $armFailed = $false; try { Invoke-R5Arm $case.Provider | Out-Null } catch { $armFailed = $true }
            Require (!$armFailed) 'route timeout should return rollback-capable BLOCKED result'
            Require ((Get-Content $case.Provider.StatePath -Raw) -match 'DESKTOP_STARTED|BRIDGE_ENABLED') 'route timeout did not persist partial ARM state'
        }
    }

    $case = New-TestProvider; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; $arm = Invoke-R5Arm $case.Provider | Out-String
    Require ($arm -match 'CANARY_ARMED=YES') 'ARM did not prove adapter and child route'
    $verify = Invoke-R5VerifyDisable $case.Provider | Out-String
    Require ($verify -match 'STOCK_ROUTE_RESTORED=PASS') 'stock route was not restored'
    Require ($verify -match 'PERMANENT_TRAY_RESTORED=PASS') 'permanent tray was not restored'
    Require ($verify -match 'DETAILED_LOGGING_RESTORED=PASS') 'detailed logging was not restored'

    foreach ($presence in 'missing','empty','value') {
        $case = New-TestProvider; $cases += $case
        if ($presence -eq 'missing') { $case.Provider.Env.CODEX_CLI_PATH = [ordered]@{ present = $false; value = $null } }
        elseif ($presence -eq 'empty') { $case.Provider.Env.CODEX_CLI_PATH = [ordered]@{ present = $true; value = '' } }
        Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null; $result = Invoke-R5VerifyDisable $case.Provider | Out-String
        Require ($result -match 'USER_ENV_EXACT_RESTORE=PASS') "$presence User environment value was not restored exactly"
    }

    foreach ($scenario in 'tray-stopped','logging-fail') {
        $case = New-TestProvider $scenario; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null
        $result = Invoke-R5VerifyDisable $case.Provider | Out-String
        if ($scenario -eq 'tray-stopped') { Require ($result -match 'PERMANENT_TRAY_RESTORED=PASS') 'stopped permanent tray was not kept stopped' }
        else { Require ($result -match 'DETAILED_LOGGING_RESTORED=FAIL' -and $result -match 'STATUS=BLOCKED') 'logging restore failure was not fail-closed' }
    }

    foreach ($scenario in 'stock-fail','tray-fail','logging-fail','env-fail') {
        $case = New-TestProvider $scenario; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null
        $result = Invoke-R5VerifyDisable $case.Provider | Out-String
        Require ($result -match 'STATUS=BLOCKED') "$scenario was not fail-closed"
    }

    $case = New-TestProvider; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null; $case.Provider.Live = $true
    $blocked = Invoke-R5VerifyDisable $case.Provider | Out-String; Require ($blocked -match 'STATUS=BLOCKED') 'VERIFY accepted a live Codex process'

    $case = New-TestProvider; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null
    $rollback = Invoke-R5Rollback $case.Provider | Out-String; $again = Invoke-R5Rollback $case.Provider | Out-String
    Require ($rollback -match 'ROLLBACK=PASS' -and $again -match 'ROLLBACK=PASS') 'rollback was not idempotent'

    $case = New-TestProvider; $cases += $case
    $identity = [pscustomobject]@{ pid = 9999; path = 'other'; sha256 = 'other' }; $failed = $false
    try { $case.Provider.StopExact($identity) | Out-Null } catch { $failed = $true }
    Require $failed 'PID identity mismatch was not rejected'
    'LIVE_RUNNER_OWNER_BEHAVIORAL=PASS'
} finally {
    foreach ($case in $cases) { if (Test-Path $case.Root) { Remove-Item $case.Root -Recurse -Force -ErrorAction SilentlyContinue } }
}

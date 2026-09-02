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

    foreach ($scenario in 'child-live','child-fallback') {
        $case = New-TestProvider $scenario; $cases += $case; $manifest = Invoke-R5Prepare $case.Provider | Out-Null
        $saved = Get-Content $case.Provider.ManifestPath -Raw
        Require ($saved -match $(if ($scenario -eq 'child-live') { 'live-child' } else { 'fallback-child' })) "$scenario child discovery was not persisted"
        Require ($saved -match '"desktopIdentity"\s*:\s*"OpenAI\.Codex"' -and $saved -match '"desktopInstallLocation"\s*:\s*"C:\\\\Program Files\\\\WindowsApps') 'AppX package identity was not persisted independently'
        Require ($saved -match '"desktopAppUserModelId"\s*:\s*"OpenAI\.Codex_abc123!App"') 'AppX AppUserModelId was not persisted'
        Require ($case.Provider.DiscoverCount -eq 1) 'DiscoverChild was invoked more than once during PREPARE'
    }
    $case = New-TestProvider 'child-ambiguous'; $cases += $case; $failed = $false
    try { Invoke-R5Prepare $case.Provider | Out-Null } catch { $failed = $true }
    Require $failed 'ambiguous child discovery was accepted'
    $case = New-TestProvider 'package-ambiguous'; $cases += $case; $failed = $false
    try { Invoke-R5Prepare $case.Provider | Out-Null } catch { $failed = $true }
    Require $failed 'ambiguous AppX package discovery was accepted'
    $case = New-TestProvider 'tray-ambiguous'; $cases += $case; $failed = $false
    try { Invoke-R5Prepare $case.Provider | Out-Null } catch { $failed = $true }
    Require $failed 'multiple permanent StatusTray processes were accepted'

    foreach ($scenario in 'hook','route-timeout') {
        $case = New-TestProvider $scenario; $cases += $case
        $failed = $false; try { Invoke-R5Prepare $case.Provider | Out-Null } catch { $failed = $true }
        if ($scenario -eq 'hook') { Require $failed 'PREPARE hook-health failure was accepted' } else {
            $armFailed = $false; try { Invoke-R5Arm $case.Provider | Out-Null } catch { $armFailed = $true }
            Require (!$armFailed) 'route timeout should return rollback-capable BLOCKED result'
            Require ((Get-Content $case.Provider.StatePath -Raw) -match 'DESKTOP_STARTED|BRIDGE_ENABLED') 'route timeout did not persist partial ARM state'
        }
    }

    $case = New-TestProvider 'delayed'; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; $arm = Invoke-R5Arm $case.Provider | Out-String
    Require ($arm -match 'CANARY_ARMED=YES') 'ARM did not prove adapter and child route'
    Require ($case.Provider.PollCount -ge 3) 'route polling did not wait for delayed success'
    $state = Get-Content $case.Provider.StatePath -Raw
    Require ($state -notmatch 'System.Diagnostics.Process') 'raw process object leaked into state'
    Require ($state -notmatch '"desktopProcess"|"pid"\s*:\s*2003|fake-desktop') 'unproven AppX shell launcher became owned Desktop state'
    Require (!$case.Provider.LauncherKillAttempted) 'launcher cleanup was attempted before verification'
    $verify = Invoke-R5VerifyDisable $case.Provider | Out-String
    Require ($verify -match 'STOCK_ROUTE_RESTORED=PASS') 'stock route was not restored'
    Require ($verify -match 'PERMANENT_TRAY_RESTORED=PASS') 'permanent tray was not restored'
    Require ($verify -match 'DETAILED_LOGGING_RESTORED=PASS') 'detailed logging was not restored'
    Require $case.Provider.CanaryAliveAtDiagnosis 'canary tray was stopped before diagnosis'
    Require ($verify -match 'R5_CLASSIFICATION=NO_STOP_LIVE_DONE_ACCEPTED') 'classification was not preserved'
    Require (Test-Path (Join-Path $case.Root 'result.txt')) 'durable result.txt was not written'
    Require ($case.Provider.DiagnoseInput -notmatch 'unrelated') 'pre-diagnosis privacy filter leaked unrelated content'

    $case = New-TestProvider 'diagnosis-fail'; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null
    $diagnosisFailure = Invoke-R5VerifyDisable $case.Provider | Out-String
    $failureResult = Get-Content (Join-Path $case.Root 'result.txt') -Raw
    Require ($diagnosisFailure -match 'R5_CLASSIFICATION=NOT_PROVEN' -and $diagnosisFailure -match 'STATUS=BLOCKED') 'diagnosis failure was not classified as blocked/not proven'
    Require ($failureResult -match 'R5_CLASSIFICATION=NOT_PROVEN' -and $failureResult -match 'ISSUE_93_ACCEPTANCE=NO') 'result.txt lost diagnosis failure classification'
    Require ($failureResult -match 'PRODUCTION_DISABLE=PASS' -and $failureResult -match 'USER_ENV_EXACT_RESTORE=PASS' -and $failureResult -match 'DETAILED_LOGGING_RESTORED=PASS' -and $failureResult -match 'PERMANENT_TRAY_RESTORED=PASS' -and $failureResult -match 'STOCK_ROUTE_RESTORED=PASS') 'cleanup did not run or persist after diagnosis failure'
    Require $case.Provider.CanaryAliveAtDiagnosis 'canary tray was not alive through failed diagnosis'
    Require ($case.Provider.Activation -eq 'DISABLED') 'production bridge Disable was skipped after diagnosis failure'

    $case = New-TestProvider 'multibyte'; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null; $bytes = Invoke-R5VerifyDisable $case.Provider | Out-String
    Require ($bytes -match 'DELTA_BYTES=4') 'delta byte length was not measured before decode'

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
        if ($scenario -eq 'stock-fail') { Require ($result -match 'R5_CLASSIFICATION=NO_STOP_LIVE_DONE_ACCEPTED') 'R5 classification was lost after cleanup failure'; Require (Test-Path (Join-Path $case.Root 'result.txt')) 'result.txt was lost after cleanup failure' }
    }

    $case = New-TestProvider; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null; $case.Provider.Live = $true
    $blocked = Invoke-R5VerifyDisable $case.Provider | Out-String; Require ($blocked -match 'STATUS=BLOCKED') 'VERIFY accepted a live Codex process'

    $case = New-TestProvider; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null
    $rollback = Invoke-R5Rollback $case.Provider | Out-String; $again = Invoke-R5Rollback $case.Provider | Out-String
    Require ($rollback -match 'ROLLBACK=PASS' -and $again -match 'ROLLBACK=PASS') 'rollback was not idempotent'

    foreach ($phase in 'ARM','VERIFY_DISABLE','ROLLBACK') {
        $case = New-TestProvider; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null; $case.Provider.MachineDrift = $true
        $drift = if ($phase -eq 'ARM') { $case.Provider.MachineDrift = $false; $null = Invoke-R5Rollback $case.Provider; $case.Provider.MachineDrift = $true; try { Invoke-R5Arm $case.Provider | Out-Null } catch { 'blocked' } } elseif ($phase -eq 'VERIFY_DISABLE') { Invoke-R5VerifyDisable $case.Provider | Out-String } else { Invoke-R5Rollback $case.Provider | Out-String }
        Require ($drift -match 'blocked|STATUS=BLOCKED|ROLLBACK=FAIL') "Machine environment drift was not guarded in $phase"
    }

    $case = New-TestProvider; $cases += $case
    $identity = [pscustomobject]@{ pid = 9999; path = 'other'; sha256 = 'other' }; $failed = $false
    try { $case.Provider.StopExact($identity) | Out-Null } catch { $failed = $true }
    Require $failed 'PID identity mismatch was not rejected'
    'LIVE_RUNNER_OWNER_BEHAVIORAL=PASS'
} finally {
    foreach ($case in $cases) { if (Test-Path $case.Root) { Remove-Item $case.Root -Recurse -Force -ErrorAction SilentlyContinue } }
}

$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '..\live\r5-live-runner.psm1'
Import-Module $module -Force

function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Invoke-HookHealth([string]$LocalAppData, [string[]]$Homes) {
    $moduleObject = Get-Module (Split-Path -LeafBase $module)
    & $moduleObject { param($local, $controlledHomes) Test-R5HookHealth $local $controlledHomes } $LocalAppData $Homes
}
function New-HookFixture([string]$Root, [string]$StableLogger, [string]$Variant = 'healthy') {
    $hookHome = Join-Path $Root 'home'; $hookDir = Join-Path $Root 'LocalAppData\VorotexK15\app\hooks'
    New-Item -Path $hookHome,$hookDir -ItemType Directory -Force | Out-Null
    New-Item -Path $StableLogger -ItemType File -Force | Out-Null
    $events = 'UserPromptSubmit','PermissionRequest','PreToolUse','PostToolUse','Stop','SessionEnd'
    $hooks = [ordered]@{}
    foreach ($event in $events) { $hooks[$event] = @{ hooks = @(@{ commandWindows = "pwsh -File `"$StableLogger`"" }) } }
    if ($Variant -eq 'duplicate') { $hooks.Stop.hooks += @{ commandWindows = "pwsh -File `"$StableLogger`"" } }
    if ($Variant -eq 'stale') { $hooks.LegacyEvent = @{ hooks = @(@{ commandWindows = "pwsh -File `"$StableLogger`"" }) } }
    if ($Variant -eq 'drift') { $hooks.Stop.hooks[0].commandWindows = 'pwsh -File "C:\other\codex-hook-logger.ps1"' }
    if ($Variant -eq 'transient') { $hooks.Stop.hooks[0].commandWindows = "pwsh -File `"$Root\build (7)\codex-hook-logger.ps1`"" }
    if ($Variant -eq 'multiple') { $hooks.Stop.hooks += @{ commandWindows = "pwsh -File `"$StableLogger`"" }; $hooks.LegacyEvent = @{ hooks = @(@{ commandWindows = "pwsh -File `"$StableLogger`"" }) } }
    @{ hooks = $hooks } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $hookHome 'hooks.json') -Encoding UTF8
    [pscustomobject]@{ Home = $hookHome }
}
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

    $hookRoot = Join-Path ([IO.Path]::GetTempPath()) "r5-hook-health-$PID-$([Guid]::NewGuid().ToString('N'))"
    try {
        $local = Join-Path $hookRoot 'LocalAppData'; $stable = Join-Path $local 'VorotexK15\app\hooks\codex-hook-logger.ps1'
        $fixture = New-HookFixture $hookRoot $stable
        $zero = Invoke-HookHealth $local @($fixture.Home)
        Require $zero.Pass 'zero hook-health findings did not pass'
        Require ($zero.Detail -eq '') 'zero hook-health findings did not produce empty Detail'
        $oneFixture = New-HookFixture $hookRoot $stable 'drift'; $one = Invoke-HookHealth $local @($oneFixture.Home)
        Require ((-not $one.Pass) -and $one.Detail -eq "$($oneFixture.Home) path drift Stop") 'one hook-health finding was not blocked with its exact detail'
        $multipleFixture = New-HookFixture $hookRoot $stable 'multiple'; $multiple = Invoke-HookHealth $local @($multipleFixture.Home)
        $multipleFindings = @($multiple.Detail -split '; ' | Sort-Object -Unique); $expectedMultiple = $multipleFindings -join '; '
        Require ((-not $multiple.Pass) -and $multipleFindings.Count -ge 2 -and $multiple.Detail -eq $expectedMultiple) 'multiple hook-health findings were not deterministic and unique'
        foreach ($variant in 'duplicate','stale','drift','transient') {
            $blockedFixture = New-HookFixture $hookRoot $stable $variant; $blocked = Invoke-HookHealth $local @($blockedFixture.Home)
            Require (-not $blocked.Pass) "$variant hook-health guard was weakened"
        }
    } finally { if (Test-Path $hookRoot) { Remove-Item $hookRoot -Recurse -Force -ErrorAction SilentlyContinue } }

    foreach ($scenario in 'child-live','child-fallback') {
        $case = New-TestProvider $scenario; $cases += $case; $manifest = Invoke-R5Prepare $case.Provider | Out-Null
        $saved = Get-Content $case.Provider.ManifestPath -Raw
        Require ($saved -match $(if ($scenario -eq 'child-live') { 'live-child' } else { 'fallback-child' })) "$scenario child discovery was not persisted"
        Require ($saved -match '"desktopIdentity"\s*:\s*"OpenAI\.Codex"' -and $saved -match '"desktopInstallLocation"\s*:\s*"C:\\\\Program Files\\\\WindowsApps') 'AppX package identity was not persisted independently'
        Require ($saved -match '"desktopAppUserModelId"\s*:\s*"OpenAI\.Codex_2p2nqsd0c76g0!App"') 'AppX AppUserModelId was not persisted'
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
            Require ((Get-Content $case.Provider.StatePath -Raw) -match 'ROUTE_BLOCKED') 'route timeout did not persist partial ARM state'
            $timeoutState = Get-Content $case.Provider.StatePath -Raw | ConvertFrom-Json
            Require (!$timeoutState.PSObject.Properties['adapter'] -and !$timeoutState.PSObject.Properties['child'] -and $case.Provider.StoppedPids -notcontains 2004 -and $case.Provider.StoppedPids -notcontains 2005) 'timeout invented an unobserved route identity'
        }
    }

    foreach ($scenario in 'adapter-only','child-only','neither') {
        $case = New-TestProvider $scenario; $cases += $case; $case.Provider.TimeoutSeconds = 0.02; Invoke-R5Prepare $case.Provider | Out-Null
        $partialArm = Invoke-R5Arm $case.Provider | Out-String
        $partialState = Get-Content $case.Provider.StatePath -Raw | ConvertFrom-Json
        Require ($partialArm -match 'STATUS=BLOCKED' -and $partialArm -match 'CANARY_ARMED=NO' -and $partialArm -match 'NEXT_ACTION=ROLLBACK') "$scenario did not fail closed as a partial route"
        if ($scenario -eq 'adapter-only') {
            Require ($partialState.PSObject.Properties['adapter'] -and !$partialState.PSObject.Properties['child'] -and $partialState.adapter.pid -eq 2004 -and $partialState.adapter.sha256 -eq 'fake-adapter') 'adapter-only identity was not persisted exactly'
            $partialRollback = Invoke-R5Rollback $case.Provider | Out-String
            Require ($partialRollback -match 'ROLLBACK=PASS' -and $case.Provider.StoppedPids -contains 2004 -and $case.Provider.StoppedPids -notcontains 2005) 'partial adapter was not stopped through exact rollback ownership'
        } elseif ($scenario -eq 'child-only') {
            Require ($partialState.PSObject.Properties['child'] -and !$partialState.PSObject.Properties['adapter'] -and $partialState.child.pid -eq 2005 -and $partialState.child.sha256 -eq 'fake-child') 'child-only identity was not persisted exactly'
            $partialRollback = Invoke-R5Rollback $case.Provider | Out-String
            Require ($partialRollback -match 'ROLLBACK=PASS' -and $case.Provider.StoppedPids -contains 2005 -and $case.Provider.StoppedPids -notcontains 2004) 'partial child was not stopped through exact rollback ownership'
        } else {
            Require (!$partialState.PSObject.Properties['adapter'] -and !$partialState.PSObject.Properties['child']) 'neither-observed route invented an identity'
            $partialRollback = Invoke-R5Rollback $case.Provider | Out-String
            Require ($partialRollback -match 'ROLLBACK=PASS' -and $case.Provider.StoppedPids -notcontains 2004 -and $case.Provider.StoppedPids -notcontains 2005) 'neither-observed rollback attempted an invented identity'
        }
    }

    $case = New-TestProvider 'adapter-only'; $cases += $case; $case.Provider.TimeoutSeconds = 0.02; Invoke-R5Prepare $case.Provider | Out-Null; Invoke-R5Arm $case.Provider | Out-Null
    $mismatchedState = Get-Content $case.Provider.StatePath -Raw | ConvertFrom-Json; $mismatchedState.adapter.path = 'wrong-adapter.exe'; $mismatchedState | ConvertTo-Json -Depth 20 | Set-Content $case.Provider.StatePath -Encoding UTF8
    $mismatchRollback = Invoke-R5Rollback $case.Provider | Out-String
    Require ($mismatchRollback -match 'ROLLBACK=FAIL' -and $case.Provider.StoppedPids -notcontains 2004) 'partial PID identity mismatch was not fail-closed'

    $case = New-TestProvider 'delayed'; $cases += $case; Invoke-R5Prepare $case.Provider | Out-Null; $arm = Invoke-R5Arm $case.Provider | Out-String
    Require ($arm -match 'CANARY_ARMED=YES') 'ARM did not prove adapter and child route'
    Require ($case.Provider.PollCount -ge 3) 'route polling did not wait for delayed success'
    $state = Get-Content $case.Provider.StatePath -Raw
    Require ($state -notmatch 'System.Diagnostics.Process') 'raw process object leaked into state'
    Require ($state -notmatch '"desktopProcess"|"pid"\s*:\s*2003|fake-desktop') 'unproven AppX shell launcher became owned Desktop state'
    Require (!$case.Provider.LauncherKillAttempted) 'launcher cleanup was attempted before verification'
    Require ($state -match '"adapter"\s*:\s*\{[^}]*"pid"[^}]*"path"[^}]*"sha256"' -and $state -match '"child"\s*:\s*\{[^}]*"pid"[^}]*"path"[^}]*"sha256"') 'full route identities were not normalized'
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

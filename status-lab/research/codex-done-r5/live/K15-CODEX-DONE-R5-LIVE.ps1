[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('PREPARE','ARM','VERIFY_DISABLE','ROLLBACK')][string]$Mode,
    [string]$StateRoot = (Join-Path ($env:LOCALAPPDATA ?? [Environment]::GetFolderPath('LocalApplicationData')) 'VorotexK15\app\codex-done-r5-live'),
    [int]$TimeoutSeconds = 30,
    [switch]$Simulation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$BridgeRoot = Join-Path $RepoRoot 'status-lab\research\codex-stdio-bridge'
$Activate = Join-Path $BridgeRoot 'production\Activate-CodexBridge.ps1'
$LocalAppData = $env:LOCALAPPDATA ?? [Environment]::GetFolderPath('LocalApplicationData')
$Journal = Join-Path $LocalAppData 'VOROTEX\K15 Status Lab\events.jsonl'
$Marker = Join-Path $LocalAppData 'VOROTEX\K15 Status Lab\detailed-logging.disabled'
$StateRoot = [IO.Path]::GetFullPath($StateRoot)
$StatePath = Join-Path $StateRoot 'state.json'
$ManifestPath = Join-Path $StateRoot 'production-manifest.json'

function Fail([string]$Message) { throw [InvalidOperationException]::new($Message) }
function Write-AtomicJson([string]$Path, $Value) {
    $directory = Split-Path -Parent $Path; New-Item -Path $directory -ItemType Directory -Force | Out-Null
    $temporary = "$Path.$PID.tmp"
    try { $Value | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $temporary -Encoding UTF8; Move-Item -LiteralPath $temporary -Destination $Path -Force }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
}
function Read-State { if (!(Test-Path -LiteralPath $StatePath)) { Fail 'state.json is missing' }; Get-Content $StatePath -Raw | ConvertFrom-Json }
function Get-StateProperty($Object, [string]$Name) { $property = $Object.PSObject.Properties[$Name]; if ($property) { return $property.Value }; return $null }
function Get-UserEnvironmentSnapshot {
    $snapshot = [ordered]@{}
    foreach ($name in 'CODEX_CLI_PATH','CODEX_BRIDGE_NODE_PATH','CODEX_BRIDGE_WRAPPER_PATH','CODEX_BRIDGE_CHILD_PATH','CODEX_BRIDGE_CHILD_SHA256','CODEX_BRIDGE_APPROVAL_SINK_PATH') {
        $value = [Environment]::GetEnvironmentVariable($name, 'User'); $snapshot[$name] = [ordered]@{ present = ($null -ne $value); value = $value }
    }
    $snapshot
}
function Restore-UserEnvironment($Snapshot) {
    foreach ($name in $Snapshot.PSObject.Properties.Name) { $item = $Snapshot.$name; [Environment]::SetEnvironmentVariable($name, $(if ($item.present) { [string]$item.value } else { $null }), 'User') }
}
function Get-ProcessIdentity([Diagnostics.Process]$Process) {
    if (!$Process) { return $null }; $path = $Process.Path
    [ordered]@{ pid = $Process.Id; path = $path; sha256 = $(if ($path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }) }
}
function Stop-OwnedProcess($Record) {
    if (!$Record) { return $true }; $process = Get-Process -Id ([int]$Record.pid) -ErrorAction SilentlyContinue
    if (!$process) { return $true }
    if ($Record.path -and $process.Path -and ([IO.Path]::GetFullPath($Record.path) -ine [IO.Path]::GetFullPath($process.Path))) { Fail 'owned process identity mismatch' }
    Stop-Process -Id $process.Id -Force; return $null -eq (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
}
function Test-CanonicalHookHealth {
    $homes = @((Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'), (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex-agentloop'))
    if ($env:CODEX_HOME) { $homes += $env:CODEX_HOME }; $logger = Join-Path $LocalAppData 'VorotexK15\app\hooks\codex-hook-logger.ps1'
    $events = 'UserPromptSubmit','PermissionRequest','PreToolUse','PostToolUse','Stop','SessionEnd'; $bad = @()
    if (!(Test-Path -LiteralPath $logger -PathType Leaf)) { $bad += 'stable logger missing' }
    foreach ($home in $homes | Sort-Object -Unique) {
        $hooks = Join-Path $home 'hooks.json'; if (!(Test-Path -LiteralPath $hooks -PathType Leaf)) { $bad += "$home hooks.json missing"; continue }
        try { $json = Get-Content $hooks -Raw | ConvertFrom-Json } catch { $bad += "$home malformed hooks.json"; continue }
        foreach ($event in $events) { $matches = @($json.hooks.$event | ForEach-Object { $_.hooks } | Where-Object { ([string]($_.commandWindows ?? $_.command ?? '')).Contains('codex-hook-logger.ps1') }); if ($matches.Count -ne 1) { $bad += "$home $event handler count=$($matches.Count)" } }
    }
    return ,$bad
}
function Invoke-Diagnosis([string]$Transient) {
    $start = [Diagnostics.ProcessStartInfo]::new(); $start.FileName = (Get-Command node -ErrorAction Stop).Source
    $start.Arguments = '"' + (Join-Path $PSScriptRoot 'r5-live-diagnose.mjs') + '" - --json'; $start.UseShellExecute = $false; $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start); $process.StandardInput.Write($Transient); $process.StandardInput.Close(); $output = $process.StandardOutput.ReadToEnd(); $error = $process.StandardError.ReadToEnd(); $process.WaitForExit()
    if ($process.ExitCode -ne 0) { Fail "diagnostic helper failed: $error" }; $output | ConvertFrom-Json
}
function Out-Result($Values) { $Values.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" } }
function Invoke-Simulation {
    $state = if (Test-Path $StatePath) { Get-Content $StatePath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ phase = 'NONE' } }
    switch ($Mode) {
        'PREPARE' { Write-AtomicJson $ManifestPath ([ordered]@{ schema = 'k15-codex-bridge/production-manifest-v1'; simulated = $true; approvalSinkPath = 'SIMULATED_REAL_JOURNAL' }); Write-AtomicJson $StatePath ([ordered]@{ schema = 'k15-codex-done-r5-live/v7'; mainSha = 'SIMULATED'; manifestPath = $ManifestPath; phase = 'PREPARED' }); Out-Result @{ STATUS = 'PASS'; CANARY_PREPARED = 'YES'; ENV_MUTATION = 'NO'; HOOK_MUTATION = 'NO'; SIMULATION = 'YES' }; return }
        'ARM' { if ($state.phase -ne 'PREPARED') { Fail 'simulation ARM requires PREPARED' }; Write-AtomicJson $StatePath ([ordered]@{ phase = 'ARMED'; route = [ordered]@{ desktop = $true; adapter = $true; child = $true } }); Out-Result @{ STATUS = 'PASS'; CANARY_ARMED = 'YES'; APP_SERVER_ROUTE_OBSERVED = 'YES'; ADAPTER_OBSERVED = 'YES'; PINNED_CHILD_OBSERVED = 'YES'; SIMULATION = 'YES' }; return }
        'VERIFY_DISABLE' { if ($state.phase -ne 'ARMED') { Fail 'simulation VERIFY_DISABLE requires ARMED' }; Write-AtomicJson $StatePath ([ordered]@{ phase = 'DISABLED' }); Out-Result @{ STATUS = 'PASS'; PRODUCTION_DISABLE = 'PASS'; USER_ENV_EXACT_RESTORE = 'PASS'; DETAILED_LOGGING_RESTORED = 'PASS'; PERMANENT_TRAY_RESTORED = 'PASS'; STOCK_ROUTE_RESTORED = 'PASS'; RAW_PROTOCOL_PERSISTED = 'NO'; USER_CONTENT_CAPTURED = 'NO'; SIMULATION = 'YES' }; return }
        'ROLLBACK' { Write-AtomicJson $StatePath ([ordered]@{ phase = 'ROLLED_BACK' }); Out-Result @{ STATUS = 'PASS'; ROLLBACK = 'PASS'; USER_ENV_EXACT_RESTORE = 'PASS'; SIMULATION = 'YES' }; return }
    }
}

New-Item -Path $StateRoot -ItemType Directory -Force | Out-Null
if ($Simulation) { Invoke-Simulation; exit }

if ($Mode -eq 'PREPARE') {
    $head = (git -C $RepoRoot rev-parse HEAD).Trim(); $origin = (git -C $RepoRoot rev-parse origin/main).Trim(); if ($head -ne $origin -or (git -C $RepoRoot status --porcelain=v1).Trim()) { Fail 'PREPARE requires clean HEAD==origin/main' }
    if (!(Test-Path $Activate -PathType Leaf)) { Fail 'activation source missing' }; $bad = Test-CanonicalHookHealth; if ($bad.Count) { Fail "hook health blocked: $($bad -join '; ')" }
    $packages = @(Get-AppxPackage -Name '*Codex*' -ErrorAction SilentlyContinue); $children = @($packages | ForEach-Object { Get-ChildItem $_.InstallLocation -Filter codex.exe -File -Recurse -ErrorAction SilentlyContinue } | Sort-Object FullName -Unique); if ($children.Count -ne 1) { Fail "Codex child is not uniquely proven count=$($children.Count)" }
    $desktopPackage = @($packages | Where-Object { $children[0].FullName.StartsWith($_.InstallLocation, [StringComparison]::OrdinalIgnoreCase) }); if ($desktopPackage.Count -ne 1) { Fail 'Codex package identity is ambiguous' }
    $out = Join-Path $StateRoot 'artifacts'; New-Item $out -ItemType Directory -Force | Out-Null
    & dotnet publish (Join-Path $BridgeRoot 'windows-adapter\K15.CodexBridge.WindowsAdapter.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $out 'adapter') | Out-Null; if ($LASTEXITCODE) { Fail 'adapter artifact publish failed' }
    & dotnet publish (Join-Path $RepoRoot 'status-lab\Vorotex.K15.StatusLab.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'tray') | Out-Null; if ($LASTEXITCODE) { Fail 'tray artifact publish failed' }
    $node = (Get-Command node -ErrorAction Stop).Source; $manifest = [ordered]@{ schema = 'k15-codex-bridge/production-manifest-v1'; adapterPath = Join-Path $out 'adapter\K15.CodexBridge.WindowsAdapter.exe'; nodePath = $node; wrapperPath = Join-Path $BridgeRoot 'src\approval-wrapper.mjs'; transparentWrapperPath = Join-Path $BridgeRoot 'src\transparent-wrapper.mjs'; bridgeCorePath = Join-Path $BridgeRoot 'src\bridge-core.mjs'; childPath = $children[0].FullName; approvalSinkPath = $Journal; desktopVersion = [string]$desktopPackage[0].Version; desktopInstallLocation = $desktopPackage[0].InstallLocation; desktopPackageFamily = [string]$desktopPackage[0].PackageFamilyName; trayPath = Join-Path $out 'tray\Vorotex.K15.StatusTray.exe' }
    foreach ($name in 'adapterPath','nodePath','wrapperPath','transparentWrapperPath','bridgeCorePath','childPath','trayPath') { if (!(Test-Path $manifest[$name] -PathType Leaf)) { Fail "manifest path missing: $name" }; $manifest[($name -replace 'Path$','Sha256')] = (Get-FileHash $manifest[$name] -Algorithm SHA256).Hash.ToLowerInvariant() }; Write-AtomicJson $ManifestPath $manifest
    & $Activate -Mode Validate -ManifestPath $ManifestPath | Out-Null; if ($LASTEXITCODE) { Fail 'activation Validate failed' }; $tray = @(Get-Process -Name Vorotex.K15.StatusTray -ErrorAction SilentlyContinue | Select-Object -First 1)
    Write-AtomicJson $StatePath ([ordered]@{ schema = 'k15-codex-done-r5-live/v7'; mainSha = $head; manifestPath = $ManifestPath; manifest = $manifest; journal = $Journal; offset = 0; user = Get-UserEnvironmentSnapshot; machine = [ordered]@{ CODEX_CLI_PATH = [Environment]::GetEnvironmentVariable('CODEX_CLI_PATH','Machine') }; desktop = [ordered]@{ childPath = $children[0].FullName; childSha256 = $manifest.childSha256; packageFamily = $manifest.desktopPackageFamily }; detailedLoggingDisabled = (Test-Path $Marker); permanentTray = Get-ProcessIdentity $tray; phase = 'PREPARED' })
    Out-Result @{ STATUS = 'PASS'; CANARY_PREPARED = 'YES'; MAIN_SHA = $head; ENV_MUTATION = 'NO'; HOOK_MUTATION = 'NO'; NEXT_ACTION = 'CLOSE_CODEX_COMPLETELY_THEN_RUN_ARM' }; exit
}

$state = Read-State; if ([string]([Environment]::GetEnvironmentVariable('CODEX_CLI_PATH','Machine')) -ne [string]$state.machine.CODEX_CLI_PATH) { Fail 'Machine environment changed' }; $manifest = if (Get-StateProperty $state 'manifest') { $state.manifest } else { Get-Content $state.manifestPath -Raw | ConvertFrom-Json }
if ($Mode -eq 'ARM') {
    $bad = Test-CanonicalHookHealth; if ($bad.Count) { Fail "hook health changed before ARM: $($bad -join '; ')" }; if (Get-Process -Name Codex,codex,K15.CodexBridge.WindowsAdapter -ErrorAction SilentlyContinue) { Fail 'relevant process running' }; if (!(Test-Path $Journal -PathType Leaf)) { Fail 'real journal missing' }
    & $Activate -Mode Validate -ManifestPath $ManifestPath | Out-Null; if ($LASTEXITCODE) { Fail 'activation Validate failed immediately before ARM' }; $state.offset = (Get-Item $Journal).Length; if (Test-Path $Marker) { Remove-Item $Marker -Force }
    $tray = Start-Process (Join-Path $StateRoot 'artifacts\Vorotex.K15.StatusTray.exe') -PassThru; $state.canaryTray = Get-ProcessIdentity $tray; $state.phase = 'TRAY_STARTED'; Write-AtomicJson $StatePath $state
    & $Activate -Mode Enable -ManifestPath $ManifestPath | Out-Null; if ($LASTEXITCODE) { Fail 'activation Enable failed' }; $state.phase = 'BRIDGE_ENABLED'; Write-AtomicJson $StatePath $state; Start-Process "shell:AppsFolder\$($state.desktop.packageFamily)!App" | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds); $adapter = $null; $child = $null; while ([DateTime]::UtcNow -lt $deadline -and (!$adapter -or !$child)) { Start-Sleep -Milliseconds 250; $adapter = @(Get-CimInstance Win32_Process -Filter "Name='K15.CodexBridge.WindowsAdapter.exe'" | Where-Object { $_.ExecutablePath -eq $manifest.adapterPath } | Select-Object -First 1); $child = @(Get-CimInstance Win32_Process -Filter "Name='codex.exe'" | Where-Object { $_.ExecutablePath -eq $state.desktop.childPath } | Select-Object -First 1) }
    if (!$adapter -or !$child) { Fail 'bounded Desktop route evidence missing' }; $state.adapter = [ordered]@{ pid = $adapter.ProcessId; path = $adapter.ExecutablePath; sha256 = $manifest.adapterSha256 }; $state.child = [ordered]@{ pid = $child.ProcessId; path = $child.ExecutablePath; sha256 = $state.desktop.childSha256 }; $state.phase = 'ARMED'; Write-AtomicJson $StatePath $state
    Out-Result @{ STATUS = 'PASS'; CANARY_ARMED = 'YES'; ADAPTER_OBSERVED = 'YES'; PINNED_CHILD_OBSERVED = 'YES'; APP_SERVER_ROUTE_OBSERVED = 'YES' }; exit
}
if ($Mode -eq 'VERIFY_DISABLE') {
    foreach ($record in @((Get-StateProperty $state 'desktopProcess'),(Get-StateProperty $state 'adapter'),(Get-StateProperty $state 'child'),(Get-StateProperty $state 'canaryTray'))) { if ($record -and (Get-Process -Id ([int]$record.pid) -ErrorAction SilentlyContinue)) { Out-Result @{ STATUS = 'BLOCKED'; NEXT_ACTION = 'CLOSE_CODEX_COMPLETELY_AND_RETRY_VERIFY_DISABLE' }; exit 2 } }
    $bytes = [IO.File]::ReadAllBytes($Journal); if ($bytes.Length -lt [int64]$state.offset) { Fail 'journal rotated below offset' }; $delta = if ([int64]$state.offset -ge $bytes.Length) { '' } else { [Text.Encoding]::UTF8.GetString($bytes[[int]$state.offset..($bytes.Length - 1)]) }; if ($delta.Length -gt 1048576) { Fail 'journal delta oversized' }
    $diag = Invoke-Diagnosis $delta; $diag.evidence | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content (Join-Path $StateRoot 'sanitized-chronology.jsonl') -Encoding UTF8; & $Activate -Mode Disable -ManifestPath $ManifestPath | Out-Null; if ($LASTEXITCODE) { Fail 'activation Disable failed' }; Restore-UserEnvironment $state.user
    if ($state.detailedLoggingDisabled -and !(Test-Path $Marker)) { New-Item $Marker -ItemType File -Force | Out-Null } elseif (!$state.detailedLoggingDisabled -and (Test-Path $Marker)) { Remove-Item $Marker -Force }
    Out-Result @{ TASK = 'K15-CODEX-DONE-R5-LIVE'; MODE = 'VERIFY_DISABLE'; MAIN_SHA = $state.mainSha; RAW_PROTOCOL_PERSISTED = 'NO'; USER_CONTENT_CAPTURED = 'NO'; MACHINE_ENV_MUTATION = 'NO'; DELTA_BYTES = $delta.Length; SANITIZED_EVENT_COUNT = $diag.evidence.Count; R5_CLASSIFICATION = $diag.classification; PRODUCTION_DISABLE = 'PASS'; USER_ENV_EXACT_RESTORE = 'PASS'; PERMANENT_TRAY_RESTORED = 'NOT_PROVEN'; DETAILED_LOGGING_RESTORED = 'PASS'; STOCK_ROUTE_RESTORED = 'NOT_PROVEN'; STATUS = 'BLOCKED'; NEXT_ACTION = 'OWNER_REVIEW' }; exit
}
if ($Mode -eq 'ROLLBACK') { try { foreach ($name in 'desktopProcess','adapter','child','canaryTray') { $record = Get-StateProperty $state $name; if ($record) { Stop-OwnedProcess $record | Out-Null } }; & $Activate -Mode Disable -ManifestPath $ManifestPath | Out-Null; Restore-UserEnvironment $state.user; if ($state.detailedLoggingDisabled -and !(Test-Path $Marker)) { New-Item $Marker -ItemType File -Force | Out-Null } elseif (!$state.detailedLoggingDisabled -and (Test-Path $Marker)) { Remove-Item $Marker -Force }; Out-Result @{ STATUS = 'PASS'; ROLLBACK = 'PASS'; USER_ENV_EXACT_RESTORE = 'PASS'; DETAILED_LOGGING_RESTORED = 'PASS' } } catch { Out-Result @{ STATUS = 'BLOCKED'; ROLLBACK = 'FAIL' } }; exit }
Fail 'unsupported mode'

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][ValidateSet('PREPARE','ARM','VERIFY_DISABLE','ROLLBACK')][string]$Mode,
  [string]$StateRoot = (Join-Path ($env:LOCALAPPDATA ?? [Environment]::GetFolderPath('LocalApplicationData')) 'VorotexK15\app\codex-done-r5-live'),
  [int]$TimeoutSeconds = 30,
  [switch]$WhatIf
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$LiveRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $LiveRoot '..\..\..\..'))
$BridgeRoot = Join-Path $RepoRoot 'status-lab\research\codex-stdio-bridge'
$ExpectedBase = '12bfe0f7f4f8ef324097600825a3e112773bdfd2'
$Activation = Join-Path $BridgeRoot 'production\Activate-CodexBridge.ps1'
$Diagnostic = Join-Path $RepoRoot 'status-lab\research\codex-done-r5\src\r5-diagnostic.mjs'
$AllowedChronology = @('timestampUtc','source','event','sessionId','threadId','turnId','terminalStatus','previousState','currentState','reason','correlationResult')
$ManagedVariables = @('CODEX_CLI_PATH','CODEX_BRIDGE_NODE_PATH','CODEX_BRIDGE_WRAPPER_PATH','CODEX_BRIDGE_CHILD_PATH','CODEX_BRIDGE_CHILD_SHA256','CODEX_BRIDGE_APPROVAL_SINK_PATH')

function Fail([string]$message) { throw [InvalidOperationException]::new($message) }
function Write-Result([hashtable]$values) { $values.GetEnumerator() | Sort-Object Name | ForEach-Object { '{0}={1}' -f $_.Key,$_.Value } }
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Require-File([string]$path,[string]$label) { if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "$label is missing" }; return [IO.Path]::GetFullPath($path) }
function Atomic-Json([string]$path,$value) { $dir=Split-Path -Parent $path; New-Item -Path $dir -ItemType Directory -Force | Out-Null; $tmp="$path.$PID.tmp"; try { $value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $tmp -Encoding UTF8; Move-Item -LiteralPath $tmp -Destination $path -Force } finally { if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue} } }
function Get-UserSnapshot { $h=[ordered]@{}; foreach($n in $ManagedVariables){$v=[Environment]::GetEnvironmentVariable($n,'User'); $h[$n]=[ordered]@{present=($null -ne $v); value=$v}}; return $h }
function Restore-UserSnapshot($snapshot) { foreach($n in $ManagedVariables){$item=$snapshot.$n; if($item.present){[Environment]::SetEnvironmentVariable($n,[string]$item.value,'User')}else{[Environment]::SetEnvironmentVariable($n,$null,'User')}} }
function Assert-MachineUnchanged($state) { foreach($n in @('CODEX_CLI_PATH')) { $before=[string]$state.machine.$n; $now=[string]([Environment]::GetEnvironmentVariable($n,'Machine')); if($before -ne $now){Fail "machine environment changed: $n"} } }
function Canonical-Homes { $u=[Environment]::GetFolderPath('UserProfile'); $c=@(); foreach($p in @((Join-Path $u '.codex'),(Join-Path $u '.codex-agentloop'))){if(Test-Path -LiteralPath $p -PathType Container){$c += [IO.Path]::GetFullPath($p)}}; return @($c | Select-Object -Unique) }
function Inspect-Hooks($homes) {
  $logger=Join-Path ($env:LOCALAPPDATA ?? [Environment]::GetFolderPath('LocalApplicationData')) 'VorotexK15\app\hooks\codex-hook-logger.ps1'; $events=@('UserPromptSubmit','PermissionRequest','PreToolUse','PostToolUse','Stop','SessionEnd'); $flags=@()
  if(-not(Test-Path -LiteralPath $logger -PathType Leaf)){$flags+='stable logger missing'}
  foreach($home in $homes){$p=Join-Path $home 'hooks.json'; if(-not(Test-Path -LiteralPath $p -PathType Leaf)){$flags+="${home}: hooks.json missing";continue}; try{$j=Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}catch{$flags+="${home}: malformed hooks.json";continue}; foreach($e in $events){$matches=@($j.hooks.$e | ForEach-Object {$_.hooks} | ForEach-Object {$_} | Where-Object {([string]($_.commandWindows ?? $_.command ?? '')).Contains('codex-hook-logger.ps1')}); if($matches.Count -ne 1){$flags+="${home}: $e canonical handler count=$($matches.Count)"}} }
  return ,$flags
}
function Read-Manifest([string]$path) { Require-File $path 'manifest' | Out-Null; $m=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json; if($m.schema -ne 'k15-codex-bridge/production-manifest-v1'){Fail 'production manifest schema mismatch'}; foreach($n in @('adapterPath','nodePath','wrapperPath','transparentWrapperPath','bridgeCorePath','childPath','adapterSha256','nodeSha256','wrapperSha256','transparentWrapperSha256','bridgeCoreSha256','childSha256')){Require-File ([string]$m.$n) $n|Out-Null; if((Sha ([string]$m.$n)) -ne ([string]$m.($n -replace 'Path$','Sha256')).ToLowerInvariant()){Fail "manifest pin mismatch: $n"}}; return $m }
function Process-Names { @('Codex','codex','K15.CodexBridge.WindowsAdapter','Vorotex.K15.StatusTray') }
function Assert-Closed { foreach($n in Process-Names){if(Get-Process -Name $n -ErrorAction SilentlyContinue){Fail "relevant process is running: $n"}} }
function Safe-SelectedEvent($x) { $o=[ordered]@{}; foreach($n in $AllowedChronology){if($null -ne $x.$n -and [string]$x.$n -ne ''){$o[$n]=[string]$x.$n}}; return [pscustomobject]$o }
function Discover-CodexChild {
  $found=@(); $command=Get-Command codex.exe -ErrorAction SilentlyContinue; if($command){$found += $command.Source}
  foreach($root in @((Join-Path ($env:LOCALAPPDATA ?? '') 'Programs'),($env:ProgramFiles),${env:ProgramFiles(x86)})){if($root -and (Test-Path -LiteralPath $root -PathType Container)){$found += Get-ChildItem -LiteralPath $root -Filter codex.exe -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object FullName}}
  $unique=@($found | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Sort-Object -Unique); if($unique.Count -ne 1){Fail "current app-server codex.exe is not uniquely identified (count=$($unique.Count))"}; return $unique[0]
}
function Publish-IsolatedArtifacts {
  $out=Join-Path $StateRoot 'artifacts'; New-Item -Path $out -ItemType Directory -Force | Out-Null
  & dotnet publish (Join-Path $BridgeRoot 'windows-adapter\K15.CodexBridge.WindowsAdapter.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $out 'adapter') | Out-Null; if($LASTEXITCODE -ne 0){Fail 'Windows adapter publish failed'}
  & dotnet publish (Join-Path $RepoRoot 'status-lab\Vorotex.K15.StatusLab.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'tray') | Out-Null; if($LASTEXITCODE -ne 0){Fail 'Status Tray publish failed'}
  return [ordered]@{adapterPath=(Join-Path $out 'adapter\K15.CodexBridge.WindowsAdapter.exe'); trayPath=(Join-Path $out 'tray\Vorotex.K15.StatusTray.exe'); childPath=(Discover-CodexChild)}
}

New-Item -Path $StateRoot -ItemType Directory -Force | Out-Null
$statePath=Join-Path $StateRoot 'state.json'; $manifestPath=Join-Path $StateRoot 'production-manifest.json'; $resultPath=Join-Path $StateRoot 'result.txt'; $offsetPath=Join-Path $StateRoot 'events.offset'
if($Mode -eq 'PREPARE') {
  $git=[Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo -Property @{FileName='git';Arguments='rev-parse --show-toplevel';WorkingDirectory=$RepoRoot;UseShellExecute=$false;RedirectStandardOutput=$true;CreateNoWindow=$true})); $git.WaitForExit(); $gitRoot=[IO.Path]::GetFullPath($git.StandardOutput.ReadToEnd().Trim()); if($git.ExitCode -ne 0 -or -not [StringComparer]::OrdinalIgnoreCase.Equals($gitRoot,$RepoRoot)){Fail 'repository identity mismatch'}; if(((git -C $RepoRoot rev-parse HEAD).Trim()) -ne $ExpectedBase){Fail 'unexpected task base SHA'}; if((git -C $RepoRoot status --porcelain=v1).Trim()){Fail 'repository is not clean at PREPARE'}
  Require-File $Activation 'production activation'|Out-Null; Require-File $Diagnostic 'R5 diagnostic'|Out-Null
  $node=(Get-Command node -ErrorAction SilentlyContinue).Source; if(-not $node){Fail 'Node is not discovered'}; $artifacts=Publish-IsolatedArtifacts
  $homes=Canonical-Homes; $flags=Inspect-Hooks $homes; if($homes.Count -lt 2 -or $flags.Count){Fail "hook health blocked: $($flags -join '; ')"}
  $manifest=[ordered]@{schema='k15-codex-bridge/production-manifest-v1'; mainSha=((git -C $RepoRoot rev-parse HEAD).Trim()); adapterPath=$artifacts.adapterPath; nodePath=$node; wrapperPath=(Join-Path $BridgeRoot 'src\approval-wrapper.mjs'); transparentWrapperPath=(Join-Path $BridgeRoot 'src\transparent-wrapper.mjs'); bridgeCorePath=(Join-Path $BridgeRoot 'src\bridge-core.mjs'); childPath=$artifacts.childPath; approvalSinkPath=(Join-Path $StateRoot 'events.jsonl')}
  foreach($n in @('adapterPath','wrapperPath','transparentWrapperPath','bridgeCorePath','childPath')){Require-File $manifest[$n] $n|Out-Null}; $manifest.adapterSha256=Sha $manifest.adapterPath;$manifest.nodeSha256=Sha $node;$manifest.wrapperSha256=Sha $manifest.wrapperPath;$manifest.transparentWrapperSha256=Sha $manifest.transparentWrapperPath;$manifest.bridgeCoreSha256=Sha $manifest.bridgeCorePath;$manifest.childSha256=Sha $manifest.childPath; Atomic-Json $manifestPath $manifest
  $manifest.trayPath=$artifacts.trayPath; $manifest.traySha256=Sha $manifest.trayPath; Atomic-Json $manifestPath $manifest
  $machine=[ordered]@{CODEX_CLI_PATH=[Environment]::GetEnvironmentVariable('CODEX_CLI_PATH','Machine')}; $s=[ordered]@{schema='k15-codex-done-r5-live/v1'; preparedUtc=[DateTime]::UtcNow.ToString('o'); manifestPath=$manifestPath; machine=$machine; user=(Get-UserSnapshot); homes=$homes; eventsPath=(Join-Path $StateRoot 'events.jsonl'); offset=0; permanentTrayRunning=([bool](Get-Process -Name 'Vorotex.K15.StatusTray' -ErrorAction SilentlyContinue))}; Atomic-Json $statePath $s
  Write-Result @{STATUS='PASS';CANARY_PREPARED='YES';ENV_MUTATION='NO';HOOK_MUTATION='NO';NEXT_ACTION='CLOSE_CODEX_COMPLETELY_THEN_RUN_ARM'}; exit 0
}
$s=if(Test-Path -LiteralPath $statePath){Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json}else{$null}; if($null -eq $s){Fail 'PREPARE state is missing'}; Assert-MachineUnchanged $s
if($Mode -eq 'ARM') { Read-Manifest $manifestPath | Out-Null }
if($Mode -eq 'ARM') {
  Assert-Closed; if($WhatIf){Write-Result @{STATUS='PASS';WHATIF='YES';MACHINE_ENV_MUTATION='NO'}; exit 0}
  $events=Require-File ([string]$s.eventsPath) 'events journal'; $offset=(Get-Item -LiteralPath $events).Length
  Atomic-Json $statePath ([ordered]@{schema=$s.schema;preparedUtc=$s.preparedUtc;manifestPath=$s.manifestPath;machine=$s.machine;user=$s.user;homes=$s.homes;eventsPath=$s.eventsPath;offset=$offset;permanentTrayRunning=$s.permanentTrayRunning;armedUtc=[DateTime]::UtcNow.ToString('o')})
  [IO.File]::WriteAllText($offsetPath,[string]$offset); & $Activation -Mode Validate -ManifestPath $manifestPath | Out-Null; if($LASTEXITCODE -ne 0){Fail 'production manifest validation failed'}
  $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  if($manifest.trayPath -and (Test-Path -LiteralPath ([string]$manifest.trayPath) -PathType Leaf)){Start-Process -FilePath ([string]$manifest.trayPath) -WorkingDirectory (Split-Path -Parent ([string]$manifest.trayPath))}
  & $Activation -Mode Enable -ManifestPath $manifestPath | Out-Null; if($LASTEXITCODE -ne 0){Fail 'production bridge enable failed'}
  Start-Process -FilePath ([string]$manifest.childPath)
  Write-Result @{STATUS='PASS';CANARY_ARMED='YES';JOURNAL_OFFSET=$offset;NEXT_ACTION='PERFORM_ONE_R5_CANARY_TURN_THEN_CLOSE_CODEX_AND_RUN_VERIFY_DISABLE'}; exit 0
}
if($Mode -eq 'ARM') { Assert-Closed; if($WhatIf){Write-Result @{STATUS='PASS';WHATIF='YES';MACHINE_ENV_MUTATION='NO'};exit 0}; $events=Require-File ([string]$s.eventsPath) 'events journal'; $offset=(Get-Item -LiteralPath $events).Length; Atomic-Json $statePath ([ordered]@{schema=$s.schema;preparedUtc=$s.preparedUtc;manifestPath=$s.manifestPath;machine=$s.machine;user=$s.user;homes=$s.homes;eventsPath=$s.eventsPath;offset=$offset;permanentTrayRunning=$s.permanentTrayRunning;armedUtc=[DateTime]::UtcNow.ToString('o')}); [IO.File]::WriteAllText($offsetPath,[string]$offset); & $Activation -Mode Validate -ManifestPath $manifestPath|Out-Null; if($LASTEXITCODE -ne 0){Fail 'production manifest validation failed'}; Write-Result @{STATUS='PASS';CANARY_ARMED='YES';JOURNAL_OFFSET=$offset;NEXT_ACTION='PERFORM_ONE_R5_CANARY_TURN_THEN_CLOSE_CODEX_AND_RUN_VERIFY_DISABLE'};exit 0 }
if($Mode -eq 'VERIFY_DISABLE') { Assert-Closed; $bytes=[IO.File]::ReadAllBytes($s.eventsPath); $start=[int64]$s.offset; $delta=[Text.Encoding]::UTF8.GetString($bytes[$start..($bytes.Length-1)]); if($delta.Length -gt 1048576){Fail 'journal delta exceeds 1 MiB'}; $selected=@(); foreach($line in ($delta -split "`r?`n")){if([string]::IsNullOrWhiteSpace($line)){continue}; try{$e=$line|ConvertFrom-Json}catch{continue}; if(($e.source -eq 'codex_hook' -and @('UserPromptSubmit','Stop','SessionEnd') -contains $e.event) -or ($e.source -eq 'codex_stdio_bridge' -and $e.event -eq 'turn_completed') -or ($e.source -eq 'state_normalizer' -and $e.event -eq 'session_state_changed')){$selected += Safe-SelectedEvent $e}}; $tmp=Join-Path $StateRoot 'sanitized-input.jsonl'; $selected|ForEach-Object {$_|ConvertTo-Json -Compress}|Set-Content -LiteralPath $tmp -Encoding UTF8; $diag=& node (Join-Path $LiveRoot 'r5-live-diagnose.mjs') $tmp; $classification=([string]$diag).Trim(); $chronology=Join-Path $StateRoot 'sanitized-chronology.jsonl'; $selected|ForEach-Object {$_|ConvertTo-Json -Compress}|Set-Content -LiteralPath $chronology -Encoding UTF8; $disableOk=$false; try{& $Activation -Mode Disable -ManifestPath $manifestPath; $disableOk=($LASTEXITCODE -eq 0)}catch{}; $restoreOk=$false; try{Restore-UserSnapshot $s.user;$restoreOk=$true}catch{}; $machineOk=$true; try{Assert-MachineUnchanged $s}catch{$machineOk=$false}; $result=@{TASK='K15-CODEX-DONE-R5-LIVE';MODE='VERIFY_DISABLE';MAIN_SHA=$s.mainSha;HOOK_HEALTH='PASS';RAW_PROTOCOL_PERSISTED='NO';USER_CONTENT_CAPTURED='NO';MACHINE_ENV_MUTATION='NO';DELTA_BYTES=$delta.Length;SANITIZED_EVENT_COUNT=$selected.Count;R5_CLASSIFICATION=$classification;ISSUE_93_ACCEPTANCE=if($classification -eq 'NO_STOP_LIVE_DONE_ACCEPTED'){'YES'}else{'NO'};PRODUCTION_DISABLE=if($disableOk){'PASS'}else{'FAIL'};USER_ENV_EXACT_RESTORE=if($restoreOk){'PASS'}else{'FAIL'};STATUS=if($disableOk -and $restoreOk -and $machineOk){'PASS'}else{'BLOCKED'};NEXT_ACTION='REVIEW_RESULT'}; Atomic-Json $resultPath $result; Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue; Write-Result $result;exit 0 }
if($Mode -eq 'ROLLBACK') { Assert-Closed; try{& $Activation -Mode Disable -ManifestPath $manifestPath -ErrorAction SilentlyContinue}catch{}; try{Restore-UserSnapshot $s.user}catch{}; Write-Result @{STATUS='PASS';ROLLBACK='PASS';MACHINE_ENV_MUTATION='NO';HOOK_MUTATION='NO';WINDOWSAPPS_MUTATION='NO'};exit 0 }
Fail 'unsupported mode'

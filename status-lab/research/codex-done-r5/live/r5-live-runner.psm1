Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-R5Json($Path, $Value) {
    $dir = Split-Path -Parent $Path; New-Item -Path $dir -ItemType Directory -Force | Out-Null
    $tmp = "$Path.$PID.tmp"
    try { $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8; Move-Item -LiteralPath $tmp -Destination $Path -Force }
    finally { if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue } }
}
function Get-R5State($Provider) { if (!(Test-Path $Provider.StatePath)) { throw 'state.json is missing' }; Get-Content $Provider.StatePath -Raw | ConvertFrom-Json }
function Save-R5State($Provider, $State) { Write-R5Json $Provider.StatePath $State }
function Get-R5Property($Object, [string]$Name) { if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($Name)) { return $Object[$Name] }; $p = $Object.PSObject.Properties[$Name]; if ($p) { return $p.Value }; return $null }
function Set-R5Property($Object, [string]$Name, $Value) { $p = $Object.PSObject.Properties[$Name]; if ($p) { $p.Value = $Value } else { $Object | Add-Member NoteProperty $Name $Value } }
function Set-R5Stage($Provider, [string]$Stage) { Set-R5Property $Provider 'LastStage' $Stage }
function New-R5Result($Map) { $Map.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" } }
function Write-R5Result($Provider, $Map) { $lines = @(New-R5Result $Map); $lines | Set-Content -LiteralPath (Join-Path $Provider.StateRoot 'result.txt') -Encoding UTF8; $lines }
function Get-R5Transient([string]$Payload) {
    $allowed = @('UserPromptSubmit','Stop','SessionEnd'); $out = foreach ($line in ($Payload -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }; try { $event = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        $source = Get-R5Property $event 'source'; $name = Get-R5Property $event 'event'
        if ($source -eq 'codex_hook' -and $allowed -contains $name) {
            [ordered]@{ timestampUtc = Get-R5Property $event 'timestampUtc'; source = $source; event = $name; sessionId = Get-R5Property $event 'sessionId'; threadId = Get-R5Property $event 'threadId'; turnId = Get-R5Property $event 'turnId'; reason = Get-R5Property $event 'reason' }
        } elseif ($source -eq 'codex_stdio_bridge' -and $name -eq 'turn_completed') {
            [ordered]@{ schemaVersion = Get-R5Property $event 'schemaVersion'; timestampUtc = Get-R5Property $event 'timestampUtc'; source = $source; event = $name; threadId = Get-R5Property $event 'threadId'; turnId = Get-R5Property $event 'turnId'; status = Get-R5Property $event 'status' }
        } elseif ($source -eq 'state_normalizer' -and $name -eq 'session_state_changed') {
            $correlation = Get-R5Property $event 'correlation'; if (!$correlation) { continue }
            [ordered]@{ source = $source; event = $name; plane = Get-R5Property $event 'plane'; sessionId = Get-R5Property $event 'sessionId'; previous = Get-R5Property $event 'previous'; current = Get-R5Property $event 'current'; reason = Get-R5Property $event 'reason'; isRehydrated = Get-R5Property $event 'isRehydrated'; sourceTimestampUtc = Get-R5Property $event 'sourceTimestampUtc'; correlation = [ordered]@{ threadId = Get-R5Property $correlation 'threadId'; turnId = Get-R5Property $correlation 'turnId'; rpcIdType = Get-R5Property $correlation 'rpcIdType'; rpcId = Get-R5Property $correlation 'rpcId' } }
        }
    }
    @($out | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join "`n"
}
function Test-R5HookHealth([string]$LocalAppData, [string[]]$Homes) {
    if (!$PSBoundParameters.ContainsKey('Homes')) {
        $user = [Environment]::GetFolderPath('UserProfile'); $Homes = @((Join-Path $user '.codex'), (Join-Path $user '.codex-agentloop')); if ($env:CODEX_HOME) { $Homes += $env:CODEX_HOME }; try { $Homes += @(Get-ChildItem $user -Directory -Filter '.codex-*' -ErrorAction Stop | % FullName) } catch { }
    }
    $stable = [IO.Path]::GetFullPath((Join-Path $LocalAppData 'VorotexK15\app\hooks\codex-hook-logger.ps1')); $required = 'UserPromptSubmit','PermissionRequest','PreToolUse','PostToolUse','Stop','SessionEnd'; $bad = @(); if (!(Test-Path $stable -PathType Leaf)) { $bad += 'stable logger missing' }
    foreach ($home in $homes | Sort-Object -Unique) { if (!(Test-Path $home -PathType Container)) { continue }; $path = Join-Path $home 'hooks.json'; if (!(Test-Path $path -PathType Leaf)) { $bad += "$home hooks.json missing"; continue }; try { $json = Get-Content $path -Raw | ConvertFrom-Json } catch { $bad += "$home malformed hooks.json"; continue }; foreach ($property in @($json.hooks.PSObject.Properties)) { if ($required -notcontains $property.Name) { $stale = @($property.Value | % hooks | ? { ([string]($_.commandWindows ?? $_.command_windows ?? $_.command ?? '')).Contains('codex-hook-logger.ps1') }); if ($stale.Count) { $bad += "$home stale Status Lab event $($property.Name)" } } }; foreach ($event in $required) { $matches = @($json.hooks.$event | % hooks | ? { ([string]($_.commandWindows ?? $_.command_windows ?? $_.command ?? '')).Contains('codex-hook-logger.ps1') }); if ($matches.Count -ne 1) { $bad += "$home $event handler count=$($matches.Count)" }; foreach ($handler in $matches) { $command = [string]($handler.commandWindows ?? $handler.command_windows ?? $handler.command ?? ''); $m = [regex]::Match($command, '(?i)-File\s+(?:"([^"]+)"|(\S+))'); $target = if ($m.Success) { if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value } } else { '' }; if (!$target -or [IO.Path]::GetFullPath($target) -ine $stable) { $bad += "$home path drift $event" }; if ($target -match '\([^)]*\d[^)]*\)') { $bad += "$home transient numbered build path $event" } } } }
    $findings = @($bad | Sort-Object -Unique)
    return [pscustomobject]@{ Pass = ($findings.Count -eq 0); Detail = ($findings -join '; ') }
}

function Get-R5WindowsPowerShellPath {
    $systemRoot = [Environment]::GetEnvironmentVariable('SystemRoot')
    if ([string]::IsNullOrWhiteSpace($systemRoot)) { throw 'SystemRoot is unavailable' }
    $path = Join-Path $systemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Windows PowerShell 5.1 executable is missing' }
    return [IO.Path]::GetFullPath($path)
}

function Invoke-R5WindowsPowerShellAppxDiscovery {
    param(
        [string]$ExecutablePath = (Get-R5WindowsPowerShellPath),
        [string]$ScriptText
    )
    if ([string]::IsNullOrWhiteSpace($ScriptText)) {
        $ScriptText = @'
$ErrorActionPreference = 'Stop'
try {
    $packages = @(Get-AppxPackage -Name '*Codex*' | Where-Object {
        $_.Name -match '(?i)codex' -and $_.Publisher -match '(?i)OpenAI' -and $_.InstallLocation -and $_.PackageFamilyName -and $_.Version
    } | ForEach-Object {
        $manifest = Get-AppxPackageManifest -Package $_
        $applications = @($manifest.Package.Applications.Application | Where-Object { $_.Id } | ForEach-Object { [string]$_.Id })
        [ordered]@{
            identity = [string]$_.Name
            version = [string]$_.Version
            installLocation = [string]$_.InstallLocation
            packageFamily = [string]$_.PackageFamilyName
            applications = $applications
        }
    })
    ConvertTo-Json -InputObject ([object[]]$packages) -Compress -Depth 4
    exit 0
} catch {
    [Console]::Error.WriteLine('AppX discovery child failed')
    exit 1
}
'@
    }
    if (!(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Windows PowerShell 5.1 executable is missing' }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ScriptText))
    $si = [Diagnostics.ProcessStartInfo]::new()
    $si.FileName = $ExecutablePath
    $si.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encoded
    $si.UseShellExecute = $false
    $si.CreateNoWindow = $true
    $si.RedirectStandardOutput = $true
    $si.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($si)
    $stdout = $process.StandardOutput.ReadToEnd()
    $null = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "AppX discovery child exited with code $($process.ExitCode)" }
    if ([string]::IsNullOrWhiteSpace($stdout)) { throw 'AppX discovery child returned empty stdout' }
    return $stdout
}

function Resolve-R5AppxIdentity {
    param([object]$Payload)
    if ($Payload -is [string]) {
        if ([string]::IsNullOrWhiteSpace($Payload)) { throw 'AppX discovery payload is empty' }
        try { $Payload = $Payload | ConvertFrom-Json -ErrorAction Stop } catch { throw 'AppX discovery payload is invalid JSON' }
    }
    if ($null -eq $Payload) { throw 'AppX discovery payload is null' }
    # Windows PowerShell unwraps a one-element JSON array during pipeline output;
    # normalize that transport quirk while validating the package object strictly.
    $packages = @($Payload)
    if ($packages.Count -eq 0) { throw 'Codex AppX package discovery found zero matching packages' }
    if ($packages.Count -ne 1) { throw "Codex AppX package discovery found multiple matching packages count=$($packages.Count)" }
    $package = $packages[0]
    $allowed = @('identity','version','installLocation','packageFamily','applications')
    $properties = @($package.PSObject.Properties.Name)
    if (@($properties | Where-Object { $allowed -notcontains $_ }).Count -ne 0 -or @($allowed | Where-Object { $properties -notcontains $_ }).Count -ne 0) { throw 'AppX discovery payload has unexpected or missing package fields' }
    foreach ($name in 'identity','version','installLocation','packageFamily') {
        $value = $package.$name
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) { throw "AppX discovery payload field is missing or malformed: $name" }
    }
    if ($package.identity -notmatch '(?i)OpenAI.*Codex|Codex.*OpenAI' -or $package.packageFamily -notmatch '(?i)Codex') { throw 'AppX discovery payload is not an OpenAI Codex package' }
    try { $null = [Version]$package.version } catch { throw 'AppX discovery payload version is malformed' }
    try { $installLocation = [IO.Path]::GetFullPath($package.installLocation) } catch { throw 'AppX discovery payload installLocation is malformed' }
    if ($package.applications -isnot [array]) { throw 'AppX discovery applications field is malformed' }
    $applications = @($package.applications)
    if ($applications.Count -ne 1 -or $applications[0] -isnot [string] -or [string]::IsNullOrWhiteSpace($applications[0]) -or $applications[0] -match '[!\\/]') { throw "Codex AppX application identity is ambiguous count=$($applications.Count)" }
    return [ordered]@{ identity = $package.identity; version = $package.version; installLocation = $installLocation; packageFamily = $package.packageFamily; appUserModelId = "$($package.packageFamily)!$($applications[0])" }
}

function New-R5RealProvider([string]$RepoRoot, [string]$StateRoot, [int]$TimeoutSeconds) {
    $local = $env:LOCALAPPDATA ?? [Environment]::GetFolderPath('LocalApplicationData')
    $p = [pscustomobject]@{ Kind = 'Real'; RepoRoot = $RepoRoot; LocalAppData = $local; ChildBin = Join-Path $local 'OpenAI\Codex\bin'; StateRoot = [IO.Path]::GetFullPath($StateRoot); StatePath = Join-Path $StateRoot 'state.json'; ManifestPath = Join-Path $StateRoot 'production-manifest.json'; TimeoutSeconds = $TimeoutSeconds; Journal = Join-Path $local 'VOROTEX\K15 Status Lab\events.jsonl'; Marker = Join-Path $local 'VOROTEX\K15 Status Lab\detailed-logging.disabled'; BridgeRoot = Join-Path $RepoRoot 'status-lab\research\codex-stdio-bridge'; Activate = Join-Path $RepoRoot 'status-lab\research\codex-stdio-bridge\production\Activate-CodexBridge.ps1'; AppxDiscoveryExecutor = $null }
    $p | Add-Member ScriptMethod RepoPreflight { $head = ([string](git -C $this.RepoRoot rev-parse HEAD)).Trim(); $origin = ([string](git -C $this.RepoRoot rev-parse origin/main)).Trim(); $status = [string](git -C $this.RepoRoot status --porcelain=v1); if ($head -ne $origin -or -not [string]::IsNullOrWhiteSpace($status)) { throw 'PREPARE requires clean HEAD==origin/main' }; return $head }
    $p | Add-Member ScriptMethod HookHealth { return Test-R5HookHealth $this.LocalAppData }
    $p | Add-Member ScriptMethod DiscoverChild {
        $live = @(Get-CimInstance Win32_Process -Filter "Name='codex.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($this.ChildBin, [StringComparison]::OrdinalIgnoreCase) -and ([regex]::IsMatch([string]$_.CommandLine, '(?i)(^|[\s"/\\])app-server([\s"/\\]|$)')) })
        if ($live.Count -gt 1) { throw "live app-server child ambiguity count=$($live.Count)" }
        if ($live.Count -eq 1) { return [ordered]@{ path = $live[0].ExecutablePath; sha256 = (Get-FileHash $live[0].ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant(); commandLineToken = 'app-server' } }
        $candidates = @(Get-ChildItem -LiteralPath $this.ChildBin -Filter codex.exe -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName -Unique)
        if ($candidates.Count -ne 1) { throw "filesystem child ambiguity count=$($candidates.Count)" }
        return [ordered]@{ path = $candidates[0].FullName; sha256 = (Get-FileHash $candidates[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant(); commandLineToken = 'NOT_OBSERVED' }
    }
    $p | Add-Member ScriptMethod DiscoverAppxPackage {
        $payload = if ($this.AppxDiscoveryExecutor) { & $this.AppxDiscoveryExecutor } else { Invoke-R5WindowsPowerShellAppxDiscovery }
        return Resolve-R5AppxIdentity $payload
    }
    $p | Add-Member ScriptMethod Publish {
        param($child)
        if (!$child -or !$child.path -or !$child.sha256) { throw 'pinned child snapshot is required' }
        $out = Join-Path $this.StateRoot 'artifacts'; New-Item $out -ItemType Directory -Force | Out-Null
        & dotnet publish (Join-Path $this.BridgeRoot 'windows-adapter\K15.CodexBridge.WindowsAdapter.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $out 'adapter') | Out-Null; if ($LASTEXITCODE) { throw 'adapter artifact publish failed' }
        & dotnet publish (Join-Path $this.RepoRoot 'status-lab\Vorotex.K15.StatusLab.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'tray') | Out-Null; if ($LASTEXITCODE) { throw 'tray artifact publish failed' }
        $package = $this.DiscoverAppxPackage()
        $m = [ordered]@{ schema = 'k15-codex-bridge/production-manifest-v1'; adapterPath = Join-Path $out 'adapter\K15.CodexBridge.WindowsAdapter.exe'; nodePath = (Get-Command node -ErrorAction Stop).Source; wrapperPath = Join-Path $this.BridgeRoot 'src\approval-wrapper.mjs'; transparentWrapperPath = Join-Path $this.BridgeRoot 'src\transparent-wrapper.mjs'; bridgeCorePath = Join-Path $this.BridgeRoot 'src\bridge-core.mjs'; childPath = $child.path; childSha256 = $child.sha256; childCommandLineToken = $child.commandLineToken; approvalSinkPath = $this.Journal; desktopIdentity = $package.identity; desktopVersion = $package.version; desktopInstallLocation = $package.installLocation; desktopPackageFamily = $package.packageFamily; desktopAppUserModelId = $package.appUserModelId; trayPath = Join-Path $out 'tray\Vorotex.K15.StatusTray.exe' }
        foreach ($n in 'adapterPath','nodePath','wrapperPath','transparentWrapperPath','bridgeCorePath','childPath','trayPath') { if (!(Test-Path $m[$n] -PathType Leaf)) { throw "manifest path missing: $n" }; $m[($n -replace 'Path$','Sha256')] = (Get-FileHash $m[$n] -Algorithm SHA256).Hash.ToLowerInvariant() }; return $m
    }
    $p | Add-Member ScriptMethod Validate { & $this.Activate -Mode Validate -ManifestPath $this.ManifestPath | Out-Null; if ($LASTEXITCODE) { throw 'activation Validate failed' } }
    $p | Add-Member ScriptMethod Enable { & $this.Activate -Mode Enable -ManifestPath $this.ManifestPath | Out-Null; if ($LASTEXITCODE) { throw 'activation Enable failed' } }
    $p | Add-Member ScriptMethod Disable { & $this.Activate -Mode Disable -ManifestPath $this.ManifestPath | Out-Null; if ($LASTEXITCODE) { throw 'activation Disable failed' } }
    $p | Add-Member ScriptMethod GetPermanentTrays { $q = @(Get-Process -Name Vorotex.K15.StatusTray -ErrorAction SilentlyContinue); return @($q | ForEach-Object { [ordered]@{ running = $true; pid = $_.Id; path = $_.Path; sha256 = (Get-FileHash $_.Path -Algorithm SHA256).Hash.ToLowerInvariant() } }) }
    $p | Add-Member ScriptMethod EnvSnapshot { $r = [ordered]@{}; foreach ($n in 'CODEX_CLI_PATH','CODEX_BRIDGE_NODE_PATH','CODEX_BRIDGE_WRAPPER_PATH','CODEX_BRIDGE_CHILD_PATH','CODEX_BRIDGE_CHILD_SHA256','CODEX_BRIDGE_APPROVAL_SINK_PATH') { $v = [Environment]::GetEnvironmentVariable($n,'User'); $r[$n] = [ordered]@{ present = ($null -ne $v); value = $v } }; return $r }
    $p | Add-Member ScriptMethod RestoreEnv { param($s); foreach ($n in $s.PSObject.Properties.Name) { $i = $s.$n; [Environment]::SetEnvironmentVariable($n, $(if ($i.present) { [string]$i.value } else { $null }), 'User') } }
    $p | Add-Member ScriptMethod EnvMatches { param($s); foreach ($n in $s.PSObject.Properties.Name) { $actual = [Environment]::GetEnvironmentVariable($n,'User'); if ([bool]$s.$n.present -ne ($null -ne $actual) -or ([bool]$s.$n.present -and [string]$s.$n.value -cne [string]$actual)) { return $false } }; return $true }
    $p | Add-Member ScriptMethod MachineValue { [Environment]::GetEnvironmentVariable('CODEX_CLI_PATH','Machine') }
    $p | Add-Member ScriptMethod JournalOffset { if (!(Test-Path $this.Journal -PathType Leaf)) { throw 'real journal missing' }; (Get-Item $this.Journal).Length }
    $p | Add-Member ScriptMethod JournalDelta { param($offset); $b = [IO.File]::ReadAllBytes($this.Journal); if ($b.Length -lt $offset) { throw 'journal rotated below offset' }; $count = $b.Length - [int64]$offset; if ($count -gt 1048576) { throw 'journal delta oversized' }; if ($count -eq 0) { return [pscustomobject]@{ Bytes = 0; Text = '' } }; return [pscustomobject]@{ Bytes = $count; Text = [Text.Encoding]::UTF8.GetString($b[[int]$offset..($b.Length-1)]) } }
    $p | Add-Member ScriptMethod StartTray { param($path); return Get-ProcessIdentity (Start-Process $path -PassThru) }
    $p | Add-Member ScriptMethod StartDesktop { param($appUserModelId); Start-Process "shell:AppsFolder\$appUserModelId" | Out-Null; return [ordered]@{ launched = $true; appUserModelId = $appUserModelId } }
    $p | Add-Member ScriptMethod Route { param($manifest); $a = @(Get-CimInstance Win32_Process -Filter "Name='K15.CodexBridge.WindowsAdapter.exe'" | Where-Object ExecutablePath -eq $manifest.adapterPath | Select-Object -First 1); $c = @(Get-CimInstance Win32_Process -Filter "Name='codex.exe'" | Where-Object ExecutablePath -eq $manifest.childPath | Where-Object { ([regex]::IsMatch([string]$_.CommandLine, '(?i)(^|[\s"/\\])app-server([\s"/\\]|$)')) } | Select-Object -First 1); return [pscustomobject]@{ adapter = $a; child = $c } }
    $p | Add-Member ScriptMethod WaitForRoute {
        param($manifest)
        $latestAdapter = $null; $latestChild = $null; $deadline = [DateTime]::UtcNow.AddSeconds($this.TimeoutSeconds)
        do {
            $r = $this.Route($manifest)
            if (@($r.adapter).Count -eq 1 -and $r.adapter[0].ProcessId -and $r.adapter[0].ExecutablePath -eq $manifest.adapterPath) {
                try { $adapterHash = (Get-FileHash $r.adapter[0].ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant(); if ($adapterHash -ceq $manifest.adapterSha256) { $latestAdapter = [ordered]@{ pid = [int]$r.adapter[0].ProcessId; path = $r.adapter[0].ExecutablePath; sha256 = $adapterHash } } } catch { }
            }
            if (@($r.child).Count -eq 1 -and $r.child[0].ProcessId -and $r.child[0].ExecutablePath -eq $manifest.childPath -and [regex]::IsMatch([string]$r.child[0].CommandLine, '(?i)(^|[\s"/\\])app-server([\s"/\\]|$)')) {
                try { $childHash = (Get-FileHash $r.child[0].ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant(); if ($childHash -ceq $manifest.childSha256) { $latestChild = [ordered]@{ pid = [int]$r.child[0].ProcessId; path = $r.child[0].ExecutablePath; sha256 = $childHash } } } catch { }
            }
            if ($latestAdapter -and $latestChild) { return [pscustomobject]@{ adapter = @($latestAdapter); child = @($latestChild) } }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        return [pscustomobject]@{ adapter = $(if ($latestAdapter) { @($latestAdapter) } else { @() }); child = $(if ($latestChild) { @($latestChild) } else { @() }) }
    }
    $p | Add-Member ScriptMethod LiveProcesses { @((Get-Process -Name Codex,codex,K15.CodexBridge.WindowsAdapter -ErrorAction SilentlyContinue)) }
    $p | Add-Member ScriptMethod StopExact { param($r); if (!$r) { return $true }; $q = Get-Process -Id ([int]$r.pid) -ErrorAction SilentlyContinue; if (!$q) { return $true }; if ($r.path -and $q.Path -and [IO.Path]::GetFullPath($r.path) -ine [IO.Path]::GetFullPath($q.Path)) { throw 'owned process identity mismatch' }; if ($r.sha256 -and $q.Path -and (Get-FileHash $q.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$r.sha256) { throw 'owned process hash mismatch' }; Stop-Process -Id $q.Id -Force; return $null -eq (Get-Process -Id $q.Id -ErrorAction SilentlyContinue) }
    $p | Add-Member ScriptMethod MarkerExists { Test-Path $this.Marker }
    $p | Add-Member ScriptMethod SetMarker { param($exists); if ($exists -and !(Test-Path $this.Marker)) { New-Item $this.Marker -ItemType File -Force | Out-Null }; if (!$exists -and (Test-Path $this.Marker)) { Remove-Item $this.Marker -Force } }
    $p | Add-Member ScriptMethod RestoreTray { param($original); if (!$original.running) { if (@(Get-Process -Name Vorotex.K15.StatusTray -ErrorAction SilentlyContinue).Count -ne 0) { throw 'permanent tray should remain stopped' }; return $true }; if (!(Test-Path $original.path -PathType Leaf)) { throw 'original permanent tray path missing' }; $q = Start-Process $original.path -PassThru; if (!$q.Path -or [IO.Path]::GetFullPath($q.Path) -ine [IO.Path]::GetFullPath($original.path) -or (Get-FileHash $q.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$original.sha256) { throw 'permanent tray identity not restored' }; return $true }
    $p | Add-Member ScriptMethod RestoreLogging { param($original); $this.SetMarker($original) ; return ($this.MarkerExists() -eq [bool]$original) }
    $p | Add-Member ScriptMethod RestoreStock { param($s); $null = $this.StartDesktop($s.desktop.appUserModelId); $deadline = [DateTime]::UtcNow.AddSeconds($this.TimeoutSeconds); do { Start-Sleep -Milliseconds 250; $r = $this.Route($s.manifest); if (@($r.adapter).Count -eq 0 -and @($r.child).Count -eq 1 -and $r.child[0].ExecutablePath -eq $s.desktop.childPath -and (Get-FileHash $r.child[0].ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $s.desktop.childSha256) { return $true } } while ([DateTime]::UtcNow -lt $deadline); throw 'stock route proof failed' }
    $p | Add-Member ScriptMethod Diagnose { param($delta); $helper = Join-Path $this.RepoRoot 'status-lab\research\codex-done-r5\live\r5-live-diagnose.mjs'; $si = [Diagnostics.ProcessStartInfo]::new(); $si.FileName = (Get-Command node).Source; $si.Arguments = '"' + $helper + '" - --json'; $si.UseShellExecute = $false; $si.RedirectStandardInput = $true; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true; $q = [Diagnostics.Process]::Start($si); $q.StandardInput.Write($delta); $q.StandardInput.Close(); $o = $q.StandardOutput.ReadToEnd(); $e = $q.StandardError.ReadToEnd(); $q.WaitForExit(); if ($q.ExitCode) { throw $e }; return $o | ConvertFrom-Json }
    $p
}

function New-R5FakeProvider([string]$StateRoot, [string]$Scenario = '') {
    $p = [pscustomobject]@{ Kind = 'Fake'; StateRoot = [IO.Path]::GetFullPath($StateRoot); StatePath = Join-Path $StateRoot 'state.json'; ManifestPath = Join-Path $StateRoot 'production-manifest.json'; Journal = 'fake-journal'; TimeoutSeconds = 1; Scenario = $Scenario; Machine = 'machine-baseline'; Env = [ordered]@{}; Marker = $true; PermanentRunning = $true; Canaries = @{}; CanaryAliveAtDiagnosis = $false; DiagnoseInput = ''; PollCount = 0; DiscoverCount = 0; StoppedPids = @(); LauncherPid = 2003; LauncherKillAttempted = $false; Activation = 'DISABLED'; HookPass = $true; RoutePass = $true; StockPass = $true; RestoreTrayPass = $true; RestoreLoggingPass = $true; RestoreEnvPass = $true; MachineDrift = $false; Live = $false }
    foreach ($n in 'CODEX_CLI_PATH','CODEX_BRIDGE_NODE_PATH','CODEX_BRIDGE_WRAPPER_PATH','CODEX_BRIDGE_CHILD_PATH','CODEX_BRIDGE_CHILD_SHA256','CODEX_BRIDGE_APPROVAL_SINK_PATH') { $p.Env[$n] = [ordered]@{ present = $true; value = "value-$n" } }
    if ($Scenario -match 'hook') { $p.HookPass = $false }; if ($Scenario -match 'route-timeout') { $p.RoutePass = $false }; if ($Scenario -match 'stock-fail') { $p.StockPass = $false }; if ($Scenario -match 'tray-fail') { $p.RestoreTrayPass = $false }; if ($Scenario -match 'logging-fail') { $p.RestoreLoggingPass = $false }; if ($Scenario -match 'env-fail') { $p.RestoreEnvPass = $false }; if ($Scenario -match 'tray-stopped') { $p.PermanentRunning = $false }; if ($Scenario -match 'machine-drift') { $p.MachineDrift = $true }; if ($Scenario -match 'live') { $p.Live = $true }
    $p | Add-Member ScriptMethod DiscoverChild { $this.DiscoverCount++; if ($this.Scenario -match 'child-ambiguous') { throw 'filesystem child ambiguity count=2' }; return [ordered]@{ path = $(if ($this.Scenario -match 'child-live') { 'live-child' } else { 'fallback-child' }); sha256 = 'fake-child'; commandLineToken = $(if ($this.Scenario -match 'child-live') { 'app-server' } else { 'NOT_OBSERVED' }) } }
    $p | Add-Member ScriptMethod DiscoverAppxPackage { if ($this.Scenario -match 'package-ambiguous') { throw 'Codex AppX package identity is ambiguous count=2' }; return [ordered]@{ identity = 'OpenAI.Codex'; version = '1.2.3.4'; installLocation = 'C:\Program Files\WindowsApps\OpenAI.Codex_1.2.3.4_x64'; packageFamily = 'OpenAI.Codex_abc123'; appUserModelId = 'OpenAI.Codex_abc123!App' } }
    $p | Add-Member ScriptMethod RepoPreflight { return 'FAKE-MAIN' }
    $p | Add-Member ScriptMethod HookHealth { return [pscustomobject]@{ Pass = $this.HookPass; Detail = $(if ($this.HookPass) { 'canonical Status Lab hook health' } else { 'hook health mismatch' }) } }
    $p | Add-Member ScriptMethod Publish { param($child); if (!$child) { throw 'pinned child snapshot is required' }; $package = $this.DiscoverAppxPackage(); $out = Join-Path $this.StateRoot 'artifacts'; New-Item $out -ItemType Directory -Force | Out-Null; foreach ($f in 'adapter\K15.CodexBridge.WindowsAdapter.exe','tray\Vorotex.K15.StatusTray.exe') { $path = Join-Path $out $f; New-Item (Split-Path $path) -ItemType Directory -Force | Out-Null; Set-Content $path 'fake artifact' }; return [ordered]@{ schema = 'k15-codex-bridge/production-manifest-v1'; adapterPath = Join-Path $out 'adapter\K15.CodexBridge.WindowsAdapter.exe'; adapterSha256 = 'fake-adapter'; nodePath = 'fake-node'; wrapperPath = 'fake-wrapper'; transparentWrapperPath = 'fake-transparent'; bridgeCorePath = 'fake-core'; childPath = $child.path; childSha256 = $child.sha256; childCommandLineToken = $child.commandLineToken; approvalSinkPath = 'fake-journal'; desktopIdentity = $package.identity; desktopVersion = $package.version; desktopInstallLocation = $package.installLocation; desktopPackageFamily = $package.packageFamily; desktopAppUserModelId = $package.appUserModelId; trayPath = Join-Path $out 'tray\Vorotex.K15.StatusTray.exe'; traySha256 = 'fake-tray' } }
    $p | Add-Member ScriptMethod Validate { if ($this.Scenario -match 'validate-fail') { throw 'activation Validate failed' } }
    $p | Add-Member ScriptMethod Enable { $this.Activation = 'ENABLED' }
    $p | Add-Member ScriptMethod Disable { $this.Activation = 'DISABLED' }
    $p | Add-Member ScriptMethod GetPermanentTray { return [ordered]@{ running = $this.PermanentRunning; pid = $(if ($this.PermanentRunning) { 2001 } else { 0 }); path = 'fake-permanent-tray'; sha256 = 'fake-permanent' } }
    $p | Add-Member ScriptMethod EnvSnapshot { return $this.Env }
    $p | Add-Member ScriptMethod RestoreEnv { param($s); if (!$this.RestoreEnvPass) { throw 'fake env restore failed' }; $this.Env = $s }
    $p | Add-Member ScriptMethod EnvMatches { param($s); return $this.RestoreEnvPass }
    $p | Add-Member ScriptMethod MachineValue { return $(if ($this.MachineDrift) { 'machine-drift' } else { $this.Machine }) }
    $p | Add-Member ScriptMethod JournalOffset { return 0 }
    $p | Add-Member ScriptMethod JournalDelta { param($offset); if ($this.Scenario -match 'multibyte') { return [pscustomobject]@{ Bytes = 4; Text = "é" } }; return [pscustomobject]@{ Bytes = 3; Text = "{`"unrelated`":true}`n{`"source`":`"codex_hook`",`"event`":`"UserPromptSubmit`"}`n" } }
    $p | Add-Member ScriptMethod StartTray { param($path); $r = [ordered]@{ pid = 2002; path = $path; sha256 = 'fake-tray' }; $this.Canaries[$r.pid] = $true; return $r }
    $p | Add-Member ScriptMethod StartDesktop { param($appUserModelId); return [ordered]@{ launched = $true; appUserModelId = $appUserModelId } }
    $p | Add-Member ScriptMethod Route { param($manifest); $this.PollCount++; if (!$this.RoutePass -or $this.Scenario -match 'neither' -or ($this.Scenario -match 'delayed' -and $this.PollCount -lt 3)) { return [pscustomobject]@{ adapter = @(); child = @() } }; $adapter = @([pscustomobject]@{ ProcessId = 2004; ExecutablePath = $manifest.adapterPath; CommandLine = 'adapter' }); $child = @([pscustomobject]@{ ProcessId = 2005; ExecutablePath = $manifest.childPath; CommandLine = $(if ($this.Scenario -match 'no-token') { 'codex --other' } else { 'codex app-server' }) }); if ($this.Scenario -match 'adapter-only') { $child = @() }; if ($this.Scenario -match 'child-only') { $adapter = @() }; return [pscustomobject]@{ adapter = $adapter; child = $child } }
    $p | Add-Member ScriptMethod WaitForRoute { param($manifest); $latestAdapter = $null; $latestChild = $null; $deadline = [DateTime]::UtcNow.AddSeconds($this.TimeoutSeconds); do { $r = $this.Route($manifest); if (@($r.adapter).Count -eq 1 -and $r.adapter[0].ProcessId -and $r.adapter[0].ExecutablePath -eq $manifest.adapterPath -and $manifest.adapterSha256 -eq 'fake-adapter') { $latestAdapter = [ordered]@{ pid = [int]$r.adapter[0].ProcessId; path = $r.adapter[0].ExecutablePath; sha256 = 'fake-adapter' } }; if (@($r.child).Count -eq 1 -and $r.child[0].ProcessId -and $r.child[0].ExecutablePath -eq $manifest.childPath -and $r.child[0].CommandLine -match '(?i)(^|[\s"/\\])app-server([\s"/\\]|$)' -and $manifest.childSha256 -eq 'fake-child') { $latestChild = [ordered]@{ pid = [int]$r.child[0].ProcessId; path = $r.child[0].ExecutablePath; sha256 = 'fake-child' } }; if ($latestAdapter -and $latestChild) { return [pscustomobject]@{ adapter = @($latestAdapter); child = @($latestChild) } }; Start-Sleep -Milliseconds 1 } while ([DateTime]::UtcNow -lt $deadline); return [pscustomobject]@{ adapter = $(if ($latestAdapter) { @($latestAdapter) } else { @() }); child = $(if ($latestChild) { @($latestChild) } else { @() }) } }
    $p | Add-Member ScriptMethod LiveProcesses { if ($this.Live) { return @([pscustomobject]@{ Id = 2010 }) }; return @() }
    $p | Add-Member ScriptMethod StopExact { param($r); if (!$r) { return $true }; if ($r.pid -eq $this.LauncherPid) { $this.LauncherKillAttempted = $true; throw 'unproven AppX shell launcher cannot be stopped' }; if ($r.pid -eq 9999) { throw 'owned process identity mismatch' }; if ($r.pid -eq 2004 -and ([string]$r.sha256 -cne 'fake-adapter' -or [string]$r.path -notmatch 'K15\.CodexBridge\.WindowsAdapter\.exe$')) { throw 'owned adapter identity mismatch' }; if ($r.pid -eq 2005 -and ([string]$r.sha256 -cne 'fake-child' -or [string]$r.path -notmatch '^(live-child|fallback-child)$')) { throw 'owned child identity mismatch' }; $this.StoppedPids += [int]$r.pid; if ($this.Canaries.ContainsKey($r.pid)) { $this.Canaries[$r.pid] = $false }; return $true }
    $p | Add-Member ScriptMethod MarkerExists { return $this.Marker }
    $p | Add-Member ScriptMethod SetMarker { param($exists); $this.Marker = [bool]$exists }
    $p | Add-Member ScriptMethod RestoreLogging { param($original); if (!$this.RestoreLoggingPass) { throw 'fake logging restore failed' }; $this.Marker = [bool]$original; return $true }
    $p | Add-Member ScriptMethod Diagnose { param($delta); $this.DiagnoseInput = $delta; $this.CanaryAliveAtDiagnosis = [bool]$this.Canaries[2002]; if ($this.Scenario -match 'diagnosis-fail') { throw 'diagnosis failed' }; $e = @([ordered]@{ timestampUtc = '2026-01-01T00:00:00Z'; source = 'codex_hook'; event = 'UserPromptSubmit'; sessionId = 'S'; turnId = 'T' }, [ordered]@{ timestampUtc = '2026-01-01T00:00:01Z'; source = 'codex_stdio_bridge'; event = 'turn_completed'; threadId = 'S'; turnId = 'T'; terminalStatus = 'completed' }); return [pscustomobject]@{ classification = 'NO_STOP_LIVE_DONE_ACCEPTED'; evidence = $e; cases = @() } }
    $p | Add-Member ScriptMethod RestoreTray { if (!$this.RestoreTrayPass) { throw 'fake tray restore failed' }; return $true }
    $p | Add-Member ScriptMethod RestoreStock { if (!$this.StockPass) { throw 'fake stock route failed' }; return $true }
    $p | Add-Member ScriptMethod GetPermanentTrays { if ($this.Scenario -match 'tray-ambiguous') { return @([ordered]@{ running = $true; pid = 2001; path = 'fake-permanent-tray'; sha256 = 'fake-permanent' }, [ordered]@{ running = $true; pid = 2002; path = 'fake-permanent-tray'; sha256 = 'fake-permanent' }) }; if (!$this.PermanentRunning) { return @() }; return @([ordered]@{ running = $true; pid = 2001; path = 'fake-permanent-tray'; sha256 = 'fake-permanent' }) }
    $p
}

function Get-ProcessIdentity([Diagnostics.Process]$Process) { if (!$Process) { return $null }; [ordered]@{ pid = $Process.Id; path = $Process.Path; sha256 = $(if ($Process.Path) { (Get-FileHash $Process.Path -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }) } }
function Invoke-R5Prepare($Provider) {
    Set-R5Stage $Provider 'REPO_PREFLIGHT'; $main = $Provider.RepoPreflight(); Set-R5Stage $Provider 'HOOK_HEALTH'; $health = $Provider.HookHealth(); if (!$health.Pass) { throw "hook health blocked: $($health.Detail)" }
    Set-R5Stage $Provider 'TRAY_DISCOVERY'; $trays = @($Provider.GetPermanentTrays()); if ($trays.Count -gt 1) { throw "multiple permanent StatusTray processes count=$($trays.Count)" }
    Set-R5Stage $Provider 'CHILD_DISCOVERY'; $child = $Provider.DiscoverChild(); Set-R5Stage $Provider 'ARTIFACT_PUBLISH'; $manifest = $Provider.Publish($child); Write-R5Json $Provider.ManifestPath $manifest; Set-R5Stage $Provider 'ACTIVATION_VALIDATE'; $Provider.Validate()
    $tray = if ($trays.Count -eq 1) { $trays[0] } else { [ordered]@{ running = $false; path = ''; sha256 = '' } }
    $s = [ordered]@{ schema = 'k15-codex-done-r5-live/v9'; mainSha = $main; manifestPath = $Provider.ManifestPath; manifest = $manifest; journal = $Provider.Journal; offset = 0; user = $Provider.EnvSnapshot(); machine = [ordered]@{ CODEX_CLI_PATH = $Provider.MachineValue() }; desktop = [ordered]@{ identity = $manifest.desktopIdentity; installLocation = $manifest.desktopInstallLocation; packageFamily = $manifest.desktopPackageFamily; appUserModelId = $manifest.desktopAppUserModelId; childPath = $manifest.childPath; childSha256 = $manifest.childSha256; version = $manifest.desktopVersion }; detailedLoggingDisabled = $Provider.MarkerExists(); permanentTray = $tray; phase = 'PREPARED' }
    Save-R5State $Provider $s; New-R5Result @{ STATUS = 'PASS'; CANARY_PREPARED = 'YES'; MAIN_SHA = $main; ENV_MUTATION = 'NO'; HOOK_MUTATION = 'NO'; PROVEN_CHILD_DISCOVERY = 'PASS' }
}
function Invoke-R5Arm($Provider) {
    $s = Get-R5State $Provider; if ($Provider.MachineValue() -cne [string]$s.machine.CODEX_CLI_PATH) { throw 'Machine environment changed before ARM' }; $health = $Provider.HookHealth(); if (!$health.Pass) { throw "hook health changed before ARM: $($health.Detail)" }; if (@($Provider.LiveProcesses()).Count) { throw 'relevant process running' }; $Provider.Validate(); $s.offset = $Provider.JournalOffset(); $s.phase = 'VALIDATED'; Save-R5State $Provider $s
    if ($s.permanentTray.running) { if (!$Provider.StopExact($s.permanentTray)) { throw 'permanent tray stop not proven' } }
    $canary = $Provider.StartTray($s.manifest.trayPath); Set-R5Property $s 'canaryTray' $canary; $s.phase = 'TRAY_STARTED'; Save-R5State $Provider $s; $Provider.SetMarker($false); $Provider.Enable(); $s.phase = 'BRIDGE_ENABLED'; Save-R5State $Provider $s; $null = $Provider.StartDesktop($s.desktop.appUserModelId); $s.phase = 'DESKTOP_STARTED'; Save-R5State $Provider $s
    $route = $Provider.WaitForRoute($s.manifest)
    if (@($route.adapter).Count -eq 1) { $a = if ($route.adapter -is [System.Collections.IDictionary]) { $route.adapter } else { @($route.adapter)[0] }; Set-R5Property $s 'adapter' ([ordered]@{ pid = Get-R5Property $a 'pid'; path = Get-R5Property $a 'path'; sha256 = Get-R5Property $a 'sha256' }) }
    if (@($route.child).Count -eq 1) { $c = if ($route.child -is [System.Collections.IDictionary]) { $route.child } else { @($route.child)[0] }; Set-R5Property $s 'child' ([ordered]@{ pid = Get-R5Property $c 'pid'; path = Get-R5Property $c 'path'; sha256 = Get-R5Property $c 'sha256' }) }
    if (@($route.adapter).Count -ne 1 -or @($route.child).Count -ne 1) { $s.phase = 'ROUTE_BLOCKED'; Save-R5State $Provider $s; New-R5Result @{ STATUS = 'BLOCKED'; CANARY_ARMED = 'NO'; NEXT_ACTION = 'ROLLBACK'; ADAPTER_OBSERVED = $(if (Get-R5Property $s 'adapter') { 'YES' } else { 'NO' }); PINNED_CHILD_OBSERVED = $(if (Get-R5Property $s 'child') { 'YES' } else { 'NO' }); APP_SERVER_TOKEN_PROOF = $(if (Get-R5Property $s 'child') { 'YES' } else { 'NO' }) }; return }
    $s.phase = 'ARMED'; Save-R5State $Provider $s; New-R5Result @{ STATUS = 'PASS'; CANARY_ARMED = 'YES'; ADAPTER_OBSERVED = 'YES'; PINNED_CHILD_OBSERVED = 'YES'; APP_SERVER_ROUTE_OBSERVED = 'YES'; APP_SERVER_TOKEN_PROOF = 'YES'; BOUNDED_ROUTE_POLL = 'PASS' }
}
function Invoke-R5VerifyDisable($Provider) {
    $s = Get-R5State $Provider; if ($Provider.MachineValue() -cne [string]$s.machine.CODEX_CLI_PATH) { $result = @{ STATUS = 'BLOCKED'; NEXT_ACTION = 'ROLLBACK'; MACHINE_ENV_MUTATION = 'NO'; MACHINE_ENV_DRIFT = 'YES' }; Write-R5Result $Provider $result; return }
    if (@($Provider.LiveProcesses()).Count) { $result = @{ STATUS = 'BLOCKED'; NEXT_ACTION = 'CLOSE_CODEX_COMPLETELY_AND_RETRY_VERIFY_DISABLE'; MACHINE_ENV_MUTATION = 'NO' }; Write-R5Result $Provider $result; return }
    $diag = $null; $e = @(); $deltaBytes = 0; $diagnosisOk = $false
    try {
        $delta = $Provider.JournalDelta($s.offset); $deltaBytes = [int64]$delta.Bytes; $filtered = Get-R5Transient $delta.Text; $diag = $Provider.Diagnose($filtered); $e = @($diag.evidence); $diagnosisOk = $true
    } catch { $diag = [pscustomobject]@{ classification = 'NOT_PROVEN'; evidence = @() } }
    $chronology = Join-Path $Provider.StateRoot 'sanitized-chronology.jsonl'; $e | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content $chronology -Encoding UTF8
    $sessions = @($e | ForEach-Object { Get-R5Property $_ 'sessionId' } | Where-Object { $_ }); $turns = @($e | ForEach-Object { Get-R5Property $_ 'turnId' } | Where-Object { $_ }); $statuses = @($e | ForEach-Object { Get-R5Property $_ 'terminalStatus' } | Where-Object { $_ }); $counts = @{ USER_PROMPT_COUNT = @($e | Where-Object { (Get-R5Property $_ 'event') -eq 'UserPromptSubmit' }).Count; STOP_COUNT = @($e | Where-Object { (Get-R5Property $_ 'event') -eq 'Stop' }).Count; SESSION_END_COUNT = @($e | Where-Object { (Get-R5Property $_ 'event') -eq 'SessionEnd' }).Count; TURN_COMPLETED_COUNT = @($e | Where-Object { (Get-R5Property $_ 'event') -eq 'turn_completed' }).Count; SESSION_STATE_CHANGED_COUNT = @($e | Where-Object { (Get-R5Property $_ 'event') -eq 'session_state_changed' }).Count }
    $result = @{ TASK = 'K15-CODEX-DONE-R5-LIVE'; MODE = 'VERIFY_DISABLE'; MAIN_SHA = $s.mainSha; DESKTOP_VERSION = $s.desktop.version; HOOK_HEALTH = 'PASS'; RAW_PROTOCOL_PERSISTED = 'NO'; USER_CONTENT_CAPTURED = 'NO'; MACHINE_ENV_MUTATION = 'NO'; DELTA_BYTES = $deltaBytes; SANITIZED_EVENT_COUNT = $e.Count; VERIFIED_SESSION_ID_LENGTH = $(if ($sessions) { ($sessions | Measure-Object Length -Maximum).Maximum } else { 0 }); VERIFIED_TURN_ID_LENGTH = $(if ($turns) { ($turns | Measure-Object Length -Maximum).Maximum } else { 0 }); TURN_COMPLETION_STATUS = $(if ($statuses) { $statuses -join ',' } else { 'NOT_EMITTED' }); CHRONOLOGY = 'SANITIZED_EVIDENCE'; R5_CLASSIFICATION = $diag.classification; NO_STOP_LIVE_DONE_ACCEPTED = $(if ($diag.classification -eq 'NO_STOP_LIVE_DONE_ACCEPTED') {'YES'} else {'NO'}); STOP_AUTHORED_DONE = $(if ($diag.classification -eq 'STOP_AUTHORED_DONE') {'YES'} else {'NO'}); CORRELATION_FIX_CANDIDATE = $(if ($diag.classification -eq 'CORRELATION_FIX_CANDIDATE') {'PROVEN'} else {'NOT_PROVEN'}); ISSUE_93_ACCEPTANCE = $(if ($diag.classification -eq 'NO_STOP_LIVE_DONE_ACCEPTED') {'YES'} else {'NO'}); PRODUCTION_DISABLE = 'NOT_RUN'; USER_ENV_EXACT_RESTORE = 'NOT_RUN'; PERMANENT_TRAY_RESTORED = 'NOT_RUN'; DETAILED_LOGGING_RESTORED = 'NOT_RUN'; STOCK_ROUTE_RESTORED = 'NOT_RUN'; STOCK_CHILD_AFTER_DISABLE = 'NOT_RUN'; ADAPTER_AFTER_DISABLE = 'NOT_RUN'; STATUS = 'BLOCKED'; NEXT_ACTION = 'ROLLBACK' }
    foreach ($key in $counts.Keys) { $result[$key] = $counts[$key] }; Write-R5Result $Provider $result | Out-Null
    $cleanup = @{ disable = $false; env = $false; logging = $false; tray = $false; stock = $false }
    try { $Provider.Disable(); $cleanup.disable = $true } catch { }
    try { $Provider.RestoreEnv($s.user); $cleanup.env = $Provider.EnvMatches($s.user) } catch { }
    try { $cleanup.logging = $Provider.RestoreLogging($s.detailedLoggingDisabled) } catch { }
    try { if (Get-R5Property $s 'canaryTray') { $Provider.StopExact($s.canaryTray) | Out-Null }; $cleanup.tray = $Provider.RestoreTray($s.permanentTray) } catch { }
    try { $cleanup.stock = $Provider.RestoreStock($s) } catch { }
    $result.PRODUCTION_DISABLE = if ($cleanup.disable) { 'PASS' } else { 'FAIL' }; $result.USER_ENV_EXACT_RESTORE = if ($cleanup.env) { 'PASS' } else { 'FAIL' }; $result.PERMANENT_TRAY_RESTORED = if ($cleanup.tray) { 'PASS' } else { 'FAIL' }; $result.DETAILED_LOGGING_RESTORED = if ($cleanup.logging) { 'PASS' } else { 'FAIL' }; $result.STOCK_ROUTE_RESTORED = if ($cleanup.stock) { 'PASS' } else { 'FAIL' }; $result.STOCK_CHILD_AFTER_DISABLE = $result.STOCK_ROUTE_RESTORED; $result.ADAPTER_AFTER_DISABLE = if ($cleanup.stock) { 'NOT_OBSERVED' } else { 'NOT_PROVEN' }; $ok = $diagnosisOk -and $cleanup.disable -and $cleanup.env -and $cleanup.logging -and $cleanup.tray -and $cleanup.stock; $result.STATUS = if ($ok) { 'PASS' } else { 'BLOCKED' }; $result.NEXT_ACTION = if ($ok) { 'OWNER_REVIEW' } else { 'ROLLBACK' }; Write-R5Result $Provider $result; New-R5Result $result
}
function Invoke-R5Rollback($Provider) {
    $s = Get-R5State $Provider; $fail = $false; if ($Provider.MachineValue() -cne [string]$s.machine.CODEX_CLI_PATH) { $fail = $true }
    foreach ($n in 'adapter','child','canaryTray') { $r = Get-R5Property $s $n; if ($r) { try { if (!$Provider.StopExact($r)) { $fail = $true } } catch { $fail = $true } } }
    try { $Provider.Disable() } catch { $fail = $true }
    try { $Provider.RestoreEnv($s.user); if (!$Provider.EnvMatches($s.user)) { $fail = $true } } catch { $fail = $true }
    try { if (!$Provider.RestoreLogging($s.detailedLoggingDisabled)) { $fail = $true } } catch { $fail = $true }
    try { $Provider.RestoreTray($s.permanentTray) | Out-Null; $Provider.RestoreStock($s) | Out-Null } catch { $fail = $true }
    New-R5Result @{ STATUS = $(if ($fail) {'BLOCKED'} else {'PASS'}); ROLLBACK = $(if ($fail) {'FAIL'} else {'PASS'}); USER_ENV_EXACT_RESTORE = $(if ($fail) {'FAIL'} else {'PASS'}); MACHINE_ENV_MUTATION = 'NO'; MACHINE_ENV_DRIFT = $(if ($Provider.MachineValue() -ceq [string]$s.machine.CODEX_CLI_PATH) {'NO'} else {'YES'}) }
}
Export-ModuleMember -Function New-R5RealProvider,New-R5FakeProvider,Invoke-R5Prepare,Invoke-R5Arm,Invoke-R5VerifyDisable,Invoke-R5Rollback,Invoke-R5WindowsPowerShellAppxDiscovery,Resolve-R5AppxIdentity

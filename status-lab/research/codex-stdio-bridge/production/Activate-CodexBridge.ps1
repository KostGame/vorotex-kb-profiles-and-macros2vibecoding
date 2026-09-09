# Production activation is opt-in. It changes only the current user's environment.
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Validate', 'Enable', 'Disable', 'Status')]
    [string] $Mode = 'Validate',
    [Parameter(Mandatory)] [string] $ManifestPath,
    [string] $StatePath,
    [string] $EnvironmentStorePath,
    [string] $EnvironmentStoreFailOnSet,
    [string] $EnvironmentStorePostcheckMismatch,
    [string] $ProcessInventoryPath,
    [string] $UserEnvironmentRegistrySubKey = 'Environment',
    [ValidateSet('Real', 'FakeSuccess', 'FakeFailure')]
    [string] $BroadcastMode = 'Real'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UserEnvironmentBroadcast.ps1')

$ManagedVariables = @(
    'CODEX_CLI_PATH',
    'CODEX_BRIDGE_NODE_PATH',
    'CODEX_BRIDGE_WRAPPER_PATH',
    'CODEX_BRIDGE_CHILD_PATH',
    'CODEX_BRIDGE_CHILD_SHA256',
    'CODEX_BRIDGE_APPROVAL_SINK_PATH',
    'CODEX_BRIDGE_DIAGNOSTICS_SINK_PATH'
)
$PreDiagnosticsManagedVariables = @($ManagedVariables | Where-Object { $_ -ne 'CODEX_BRIDGE_DIAGNOSTICS_SINK_PATH' })
$ManifestSchema = 'k15-codex-bridge/production-manifest-v2'
$ActivationStateSchema = 'k15-codex-bridge/activation-state-v3'
$LegacyActivationStateSchema = 'k15-codex-bridge/activation-state-v2'
$MaxRuntimeInventoryEntries = 256
$MaxRuntimeGenerationNameLength = 255
$script:FailureInjected = $false
$script:PostcheckMismatchInjected = $false
$script:UserEnvironmentMutated = $false

function Fail([string] $Message) { throw [InvalidOperationException]::new($Message) }

function New-EnvironmentEntry([bool] $Present, [string] $Value = '', [string] $RegistryKind = 'String') {
    return [pscustomobject]@{
        presence = if ($Present) { 'PRESENT' } else { 'ABSENT' }
        value = if ($Present) { [string] $Value } else { '' }
        registryKind = if ($Present) { $RegistryKind } else { 'None' }
    }
}

function Assert-ExactPropertyNames($Object, [string[]] $ExpectedNames, [string] $Context) {
    if ($null -eq $Object) { Fail "$Context is required" }
    $actualNames = @($Object.PSObject.Properties.Name)
    if ($actualNames.Count -ne $ExpectedNames.Count) { Fail "$Context has an unexpected property set" }
    foreach ($name in $ExpectedNames) {
        if ($null -eq $Object.PSObject.Properties[$name]) { Fail "$Context is missing $name" }
    }
}

function Assert-EnvironmentEntry($Entry, [string] $Name, [string] $Context) {
    Assert-ExactPropertyNames $Entry @('presence', 'value', 'registryKind') "$Context.$Name"
    $presence = $Entry.presence
    $value = $Entry.value
    $registryKind = $Entry.registryKind
    if ($presence -isnot [string] -or $presence -notin @('PRESENT', 'ABSENT')) { Fail "$Context.$Name has an invalid presence" }
    if ($value -isnot [string]) { Fail "$Context.$Name has a non-string value" }
    if ($registryKind -isnot [string]) { Fail "$Context.$Name has an invalid registryKind" }
    if ($presence -eq 'ABSENT') {
        if ($value -ne '' -or $registryKind -ne 'None') { Fail "$Context.$Name has an invalid ABSENT representation" }
        return New-EnvironmentEntry $false
    }
    if ($registryKind -notin @('String', 'ExpandString')) { Fail "$Context.$Name has an unsupported registryKind" }
    return New-EnvironmentEntry $true $value $registryKind
}

function Require-CanonicalAbsolutePath([string] $Value, [string] $Name) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+\\)') {
        Fail "$Name must be an absolute path"
    }
    $fullPath = [IO.Path]::GetFullPath($Value)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($fullPath, $Value)) { Fail "$Name must be a canonical absolute path" }
    return $fullPath
}

function Assert-NoReparsePath([string] $Path, [string] $Name) {
    $root = [IO.Path]::GetPathRoot($Path)
    $relativePath = $Path.Substring($root.Length)
    $current = $root.TrimEnd('\')
    foreach ($segment in $relativePath.Split('\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = if ($current -match '^[A-Za-z]:$') { "$current\$segment" } else { Join-Path $current $segment }
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) { Fail "$Name path component does not exist" }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail "$Name must not traverse a reparse point" }
    }
}

function Require-AbsoluteFile([string] $Value, [string] $Name) {
    $fullPath = Require-CanonicalAbsolutePath $Value $Name
    Assert-NoReparsePath $fullPath $Name
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or $item.PSIsContainer) { Fail "$Name must name an existing regular file" }
    return $item.FullName
}

function Require-OptionalOutputPath([string] $Value, [string] $Name) {
    if ([string]::IsNullOrEmpty($Value)) { return '' }
    return Require-CanonicalAbsolutePath $Value $Name
}

function Get-Sha256([string] $Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($Path))) -replace '-', '').ToLowerInvariant()) } finally { $sha.Dispose() }
}

function Require-PinnedFile([string] $Value, [string] $PathName, [string] $Pin, [string] $PinName) {
    if ($Pin -notmatch '^[0-9a-fA-F]{64}$') { Fail "$PinName must be a SHA-256 hex pin" }
    $path = Require-AbsoluteFile $Value $PathName
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals((Get-Sha256 $path), $Pin)) { Fail "$PinName does not match" }
    return $path
}

function Require-Directory([string] $Value, [string] $Name) {
    $fullPath = Require-CanonicalAbsolutePath $Value $Name
    Assert-NoReparsePath $fullPath $Name
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or -not $item.PSIsContainer) { Fail "$Name must name an existing directory" }
    return $item.FullName
}

function Get-RuntimeInventory([string] $RuntimeRoot) {
    $root = Require-Directory $RuntimeRoot 'runtime root'
    $inventory = [Collections.Generic.List[object]]::new()
    $directories = @(Get-ChildItem -LiteralPath $root -Force -Directory -ErrorAction Stop | Sort-Object -Property Name)
    if ($directories.Count -gt $MaxRuntimeInventoryEntries) { Fail 'runtime inventory exceeds the bounded entry limit' }
    foreach ($directory in $directories) {
        if ($directory.Name.Length -gt $MaxRuntimeGenerationNameLength) { Fail 'runtime generation name exceeds the bounded length' }
        $isReparse = (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        $childPresent = $false
        $hostPresent = $false
        if (-not $isReparse) {
            $childItem = Get-Item -LiteralPath (Join-Path $directory.FullName 'codex.exe') -Force -ErrorAction SilentlyContinue
            $hostItem = Get-Item -LiteralPath (Join-Path $directory.FullName 'codex-code-mode-host.exe') -Force -ErrorAction SilentlyContinue
            $childPresent = $null -ne $childItem -and -not $childItem.PSIsContainer
            $hostPresent = $null -ne $hostItem -and -not $hostItem.PSIsContainer
        }
        $inventory.Add([ordered]@{
            generation = [string] $directory.Name
            codexExePresent = [bool] $childPresent
            codeModeHostPresent = [bool] $hostPresent
        })
    }
    return ,$inventory.ToArray()
}

function Assert-RuntimeInventory($Inventory, [string] $Context) {
    if ($null -eq $Inventory) { Fail "$Context is required" }
    $entries = @($Inventory)
    if ($entries.Count -gt $MaxRuntimeInventoryEntries) { Fail "$Context exceeds the bounded entry limit" }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $previous = $null
    $normalized = [Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        Assert-ExactPropertyNames $entry @('generation', 'codexExePresent', 'codeModeHostPresent') $Context
        if ($entry.generation -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.generation)) { Fail "$Context has an invalid generation name" }
        if ($entry.generation.Length -gt $MaxRuntimeGenerationNameLength) { Fail "$Context has an overlong generation name" }
        if ($entry.codexExePresent -isnot [bool] -or $entry.codeModeHostPresent -isnot [bool]) { Fail "$Context has invalid presence metadata" }
        if (-not $seen.Add($entry.generation)) { Fail "$Context contains duplicate generation names" }
        if ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $entry.generation) -gt 0) { Fail "$Context is not deterministically sorted" }
        $previous = $entry.generation
        $normalized.Add([ordered]@{
            generation = $entry.generation
            codexExePresent = [bool] $entry.codexExePresent
            codeModeHostPresent = [bool] $entry.codeModeHostPresent
        })
    }
    return ,$normalized.ToArray()
}

function Test-RuntimeInventoryEqual($Expected, $Actual) {
    $left = @($Expected)
    $right = @($Actual)
    if ($left.Count -ne $right.Count) { return $false }
    for ($index = 0; $index -lt $left.Count; $index++) {
        if (-not [StringComparer]::Ordinal.Equals($left[$index].generation, $right[$index].generation)) { return $false }
        if ([bool] $left[$index].codexExePresent -ne [bool] $right[$index].codexExePresent) { return $false }
        if ([bool] $left[$index].codeModeHostPresent -ne [bool] $right[$index].codeModeHostPresent) { return $false }
    }
    return $true
}

function Read-Manifest {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $required = @(
        'schema',
        'adapterPath', 'nodePath', 'wrapperPath', 'transparentWrapperPath', 'bridgeCorePath', 'runtimeAuthorityPath', 'childPath', 'codeModeHostPath',
        'adapterSha256', 'nodeSha256', 'wrapperSha256', 'transparentWrapperSha256', 'bridgeCoreSha256', 'runtimeAuthoritySha256', 'childSha256', 'codeModeHostSha256',
        'approvalSinkPath'
    )
    $manifestNames = @($manifest.PSObject.Properties.Name)
    $allowedManifestNames = @($required + 'diagnosticsSinkPath')
    if ($manifestNames.Count -notin @($required.Count, $allowedManifestNames.Count) -or
        @($manifestNames | Where-Object { $_ -notin $allowedManifestNames }).Count -ne 0) {
        Fail 'production manifest has an unexpected property set'
    }
    foreach ($name in $required) { if ($null -eq $manifest.PSObject.Properties[$name]) { Fail "production manifest is missing $name" } }
    if ($manifest.schema -ne $ManifestSchema) { Fail 'unsupported production manifest schema' }
    foreach ($name in $manifestNames) {
        if ($manifest.$name -isnot [string]) { Fail "manifest field $name must be a string" }
    }
    foreach ($name in @('adapterPath', 'nodePath', 'wrapperPath', 'transparentWrapperPath', 'bridgeCorePath', 'runtimeAuthorityPath', 'childPath', 'codeModeHostPath', 'adapterSha256', 'nodeSha256', 'wrapperSha256', 'transparentWrapperSha256', 'bridgeCoreSha256', 'runtimeAuthoritySha256', 'childSha256', 'codeModeHostSha256')) {
        if ([string]::IsNullOrWhiteSpace([string] $manifest.$name)) { Fail "manifest field $name is required" }
    }
    $paths = [ordered]@{
        adapterPath = Require-PinnedFile ([string] $manifest.adapterPath) 'adapterPath' ([string] $manifest.adapterSha256) 'adapterSha256'
        nodePath = Require-PinnedFile ([string] $manifest.nodePath) 'nodePath' ([string] $manifest.nodeSha256) 'nodeSha256'
        wrapperPath = Require-PinnedFile ([string] $manifest.wrapperPath) 'wrapperPath' ([string] $manifest.wrapperSha256) 'wrapperSha256'
        transparentWrapperPath = Require-PinnedFile ([string] $manifest.transparentWrapperPath) 'transparentWrapperPath' ([string] $manifest.transparentWrapperSha256) 'transparentWrapperSha256'
        bridgeCorePath = Require-PinnedFile ([string] $manifest.bridgeCorePath) 'bridgeCorePath' ([string] $manifest.bridgeCoreSha256) 'bridgeCoreSha256'
        runtimeAuthorityPath = Require-PinnedFile ([string] $manifest.runtimeAuthorityPath) 'runtimeAuthorityPath' ([string] $manifest.runtimeAuthoritySha256) 'runtimeAuthoritySha256'
        childPath = Require-PinnedFile ([string] $manifest.childPath) 'childPath' ([string] $manifest.childSha256) 'childSha256'
        codeModeHostPath = Require-PinnedFile ([string] $manifest.codeModeHostPath) 'codeModeHostPath' ([string] $manifest.codeModeHostSha256) 'codeModeHostSha256'
    }
    $wrapperDirectory = [IO.Path]::GetDirectoryName($paths.wrapperPath)
    foreach ($sibling in @(
        @{ Name = 'transparentWrapperPath'; File = 'transparent-wrapper.mjs' },
        @{ Name = 'bridgeCorePath'; File = 'bridge-core.mjs' },
        @{ Name = 'runtimeAuthorityPath'; File = 'runtime-process-authority.mjs' }
    )) {
        $expectedSibling = [IO.Path]::Combine($wrapperDirectory, $sibling.File)
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($paths[$sibling.Name]), $expectedSibling)) {
            Fail "$($sibling.Name) must be the canonical wrapper sibling $($sibling.File)"
        }
    }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFileName($paths.childPath), 'codex.exe')) { Fail 'childPath must name codex.exe' }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFileName($paths.codeModeHostPath), 'codex-code-mode-host.exe')) { Fail 'codeModeHostPath must name codex-code-mode-host.exe' }
    $childGenerationDirectory = [IO.Path]::GetDirectoryName($paths.childPath)
    $hostGenerationDirectory = [IO.Path]::GetDirectoryName($paths.codeModeHostPath)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($childGenerationDirectory, $hostGenerationDirectory)) { Fail 'childPath and codeModeHostPath must share one runtime generation directory' }
    $runtimeRoot = Require-Directory ([IO.Path]::GetDirectoryName($childGenerationDirectory)) 'runtime root'
    $uniquePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $paths.Keys) {
        if (-not $uniquePaths.Add([IO.Path]::GetFullPath($paths[$name]))) { Fail "manifest path $name duplicates another executable path" }
    }
    $approvalSinkPath = Require-OptionalOutputPath ([string] $manifest.approvalSinkPath) 'approvalSinkPath'
    $diagnosticsSinkPath = if ($null -eq $manifest.PSObject.Properties['diagnosticsSinkPath']) { '' } else { Require-OptionalOutputPath ([string] $manifest.diagnosticsSinkPath) 'diagnosticsSinkPath' }
    return [pscustomobject]@{
        Manifest = $manifest
        Paths = $paths
        ApprovalSinkPath = $approvalSinkPath
        DiagnosticsSinkPath = $diagnosticsSinkPath
        GenerationName = [IO.Path]::GetFileName($childGenerationDirectory)
        RuntimeRoot = $runtimeRoot
    }
}

function Get-StatePath {
    if (-not [string]::IsNullOrWhiteSpace($StatePath)) { return [IO.Path]::GetFullPath($StatePath) }
    return [IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ManifestPath)), 'activation-state.json')
}

function Write-AtomicJson([string] $Path, $Value) {
    $directory = [IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { New-Item -Path $directory -ItemType Directory -Force | Out-Null }
    $temporary = "$Path.$PID.tmp"
    try {
        $Value | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Assert-UserEnvironmentRegistrySubKey {
    if ($UserEnvironmentRegistrySubKey -eq 'Environment') { return }
    if ($UserEnvironmentRegistrySubKey -notmatch '^Software\\KostGame\\K15CodexBridgeTests\\[a-f0-9]{32}$') {
        Fail 'UserEnvironmentRegistrySubKey is restricted to an isolated K15 test key'
    }
}

function Test-IsolatedEnvironmentTarget {
    return -not [string]::IsNullOrWhiteSpace($EnvironmentStorePath) -or $UserEnvironmentRegistrySubKey -ne 'Environment'
}

function Get-CodexProcessInventory {
    if (-not [string]::IsNullOrWhiteSpace($ProcessInventoryPath)) {
        if (-not (Test-IsolatedEnvironmentTarget)) { Fail 'process inventory injection requires an isolated environment target' }
        if (-not (Test-Path -LiteralPath $ProcessInventoryPath -PathType Leaf)) { Fail 'process inventory injection file is missing' }
        $injected = Get-Content -LiteralPath $ProcessInventoryPath -Raw | ConvertFrom-Json
        if ($injected -isnot [array]) { Fail 'process inventory injection must be an array' }
        foreach ($entry in $injected) {
            Assert-ExactPropertyNames $entry @('name', 'path') 'process inventory entry'
            if ($entry.name -isnot [string] -or $entry.path -isnot [string]) { Fail 'process inventory entry has invalid fields' }
        }
        return @($injected)
    }
    if (Test-IsolatedEnvironmentTarget) { return @() }
    $inventory = @()
    foreach ($name in @('codex', 'ChatGPT')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            $path = ''
            try { $path = [string] $process.MainModule.FileName } catch { $path = '' }
            $inventory += [pscustomobject]@{ name = [string] $process.ProcessName; path = $path }
        }
    }
    return $inventory
}

function Open-UserEnvironmentRegistryKey([bool] $Writable, [bool] $CreateIfMissing) {
    Assert-UserEnvironmentRegistrySubKey
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($UserEnvironmentRegistrySubKey, $Writable)
    if ($null -eq $key -and $CreateIfMissing) { $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($UserEnvironmentRegistrySubKey, $Writable) }
    return $key
}

function Find-RegistryValueName($Key, [string] $Name) {
    foreach ($candidate in @($Key.GetValueNames())) {
        if ([StringComparer]::OrdinalIgnoreCase.Equals($candidate, $Name)) { return $candidate }
    }
    return $null
}

function Get-RegistryUserEnvironmentEntry([string] $Name) {
    $key = Open-UserEnvironmentRegistryKey $false $false
    if ($null -eq $key) { return New-EnvironmentEntry $false }
    try {
        $actualName = Find-RegistryValueName $key $Name
        if ($null -eq $actualName) { return New-EnvironmentEntry $false }
        $value = $key.GetValue($actualName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($value -isnot [string]) { Fail "User environment value $Name is not a string" }
        $kind = $key.GetValueKind($actualName).ToString()
        if ($kind -notin @('String', 'ExpandString')) { Fail "User environment value $Name has unsupported registry kind" }
        return New-EnvironmentEntry $true ([string] $value) $kind
    } finally {
        $key.Dispose()
    }
}

function Get-EnvironmentStoreEntry([string] $Name) {
    if (-not (Test-Path -LiteralPath $EnvironmentStorePath -PathType Leaf)) { return New-EnvironmentEntry $false }
    $store = Get-Content -LiteralPath $EnvironmentStorePath -Raw | ConvertFrom-Json
    $property = $store.PSObject.Properties[$Name]
    if ($null -eq $property) { return New-EnvironmentEntry $false }
    if ($property.Value -isnot [string]) { Fail "isolated environment-store value $Name is not a string" }
    return New-EnvironmentEntry $true ([string] $property.Value) 'String'
}

function Get-UserEnvironmentEntry([string] $Name) {
    if ([string]::IsNullOrWhiteSpace($EnvironmentStorePath)) { return Get-RegistryUserEnvironmentEntry $Name }
    return Get-EnvironmentStoreEntry $Name
}

function Set-EnvironmentStoreEntry([string] $Name, $Entry) {
    $store = if (Test-Path -LiteralPath $EnvironmentStorePath -PathType Leaf) {
        Get-Content -LiteralPath $EnvironmentStorePath -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{} }
    $property = $store.PSObject.Properties[$Name]
    if ($Entry.presence -eq 'ABSENT') {
        if ($null -eq $property) { return }
        $store.PSObject.Properties.Remove($Name)
    } elseif ($null -ne $property) {
        $property.Value = $Entry.value
    } else {
        $store | Add-Member -MemberType NoteProperty -Name $Name -Value $Entry.value
    }
    Write-AtomicJson $EnvironmentStorePath $store
    $script:UserEnvironmentMutated = $true
}

function Set-RegistryUserEnvironmentEntry([string] $Name, $Entry) {
    $key = Open-UserEnvironmentRegistryKey $true ($Entry.presence -eq 'PRESENT')
    if ($null -eq $key) { return }
    try {
        $actualName = Find-RegistryValueName $key $Name
        if ($Entry.presence -eq 'ABSENT') {
            if ($null -eq $actualName) { return }
            $key.DeleteValue($actualName, $false)
        } else {
            $kind = [Microsoft.Win32.RegistryValueKind]::$($Entry.registryKind)
            $key.SetValue($(if ($null -eq $actualName) { $Name } else { $actualName }), $Entry.value, $kind)
        }
        $script:UserEnvironmentMutated = $true
    } finally {
        $key.Dispose()
    }
}

function Set-UserEnvironmentEntry([string] $Name, $Entry) {
    $validatedEntry = Assert-EnvironmentEntry $Entry $Name 'environment entry'
    if (-not [string]::IsNullOrWhiteSpace($EnvironmentStoreFailOnSet) -and -not $script:FailureInjected -and $Name -eq $EnvironmentStoreFailOnSet) {
        $script:FailureInjected = $true
        Fail 'injected environment-store failure'
    }
    if ([string]::IsNullOrWhiteSpace($EnvironmentStorePath)) {
        Set-RegistryUserEnvironmentEntry $Name $validatedEntry
    } else {
        Set-EnvironmentStoreEntry $Name $validatedEntry
    }
}

function Get-ActiveEnvironment($Resolved) {
    $active = [ordered]@{
        CODEX_CLI_PATH = New-EnvironmentEntry $true $Resolved.Paths.adapterPath
        CODEX_BRIDGE_NODE_PATH = New-EnvironmentEntry $true $Resolved.Paths.nodePath
        CODEX_BRIDGE_WRAPPER_PATH = New-EnvironmentEntry $true $Resolved.Paths.wrapperPath
        CODEX_BRIDGE_CHILD_PATH = New-EnvironmentEntry $true $Resolved.Paths.childPath
        CODEX_BRIDGE_CHILD_SHA256 = New-EnvironmentEntry $true ([string] $Resolved.Manifest.childSha256).ToLowerInvariant()
        CODEX_BRIDGE_APPROVAL_SINK_PATH = New-EnvironmentEntry (-not [string]::IsNullOrEmpty($Resolved.ApprovalSinkPath)) $Resolved.ApprovalSinkPath
        CODEX_BRIDGE_DIAGNOSTICS_SINK_PATH = New-EnvironmentEntry (-not [string]::IsNullOrEmpty($Resolved.DiagnosticsSinkPath)) $Resolved.DiagnosticsSinkPath
    }
    return $active
}

function Get-PostcheckEntry($Actual, [string] $Phase, [string] $Name) {
    if (
        -not [string]::IsNullOrWhiteSpace($EnvironmentStorePath) -and
        -not $script:PostcheckMismatchInjected -and
        $EnvironmentStorePostcheckMismatch -eq ($Phase + ':' + $Name)
    ) {
        $script:PostcheckMismatchInjected = $true
        if ($Actual.presence -eq 'PRESENT') { return New-EnvironmentEntry $false }
        return New-EnvironmentEntry $true '__isolated_postcheck_mismatch__'
    }
    return $Actual
}

function Assert-UserEnvironmentMatches($Expected, [string] $Phase, [string[]] $Names = $ManagedVariables) {
    $mismatches = @()
    foreach ($name in $Names) {
        $expectedEntry = Assert-EnvironmentEntry $Expected[$name] $name "$Phase expected"
        $actualEntry = Get-PostcheckEntry (Get-UserEnvironmentEntry $name) $Phase $name
        $actualEntry = Assert-EnvironmentEntry $actualEntry $name "$Phase actual"
        $presenceMatches = $expectedEntry.presence -eq $actualEntry.presence
        $valueMatches = [StringComparer]::Ordinal.Equals($expectedEntry.value, $actualEntry.value)
        if (-not $presenceMatches -or -not $valueMatches) {
            $mismatches += [pscustomobject]@{ Name = $name; Expected = $expectedEntry.presence; Current = $actualEntry.presence; ValueMatch = if ($valueMatches) { 'YES' } else { 'NO' } }
        }
    }
    if ($mismatches.Count -gt 0) {
        foreach ($mismatch in $mismatches) {
            [Console]::Error.WriteLine("VARIABLE=$($mismatch.Name)")
            [Console]::Error.WriteLine("EXPECTED=$($mismatch.Expected)")
            [Console]::Error.WriteLine("CURRENT=$($mismatch.Current)")
            [Console]::Error.WriteLine("VALUE_MATCH=$($mismatch.ValueMatch)")
        }
        Fail "User environment $Phase postcheck failed"
    }
}

function Read-ActivationState([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail 'activation state is missing' }
    $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($state.schema -notin @($ActivationStateSchema, $LegacyActivationStateSchema)) { Fail 'unsupported activation state schema' }
    if ($state.schema -eq $LegacyActivationStateSchema) {
        Assert-ExactPropertyNames $state @('schema', 'manifestPath', 'original') 'legacy activation state'
    } else {
        Assert-ExactPropertyNames $state @('schema', 'manifestPath', 'manifestSha256', 'original', 'runtimeBaseline') 'activation state'
        if ($state.manifestSha256 -isnot [string] -or $state.manifestSha256 -notmatch '^[0-9a-fA-F]{64}$') {
            Fail 'activation state manifestSha256 is invalid'
        }
        Assert-ExactPropertyNames $state.runtimeBaseline @('approvedGeneration', 'runtimeInventory') 'activation state runtimeBaseline'
        if ($state.runtimeBaseline.approvedGeneration -isnot [string] -or [string]::IsNullOrWhiteSpace($state.runtimeBaseline.approvedGeneration)) {
            Fail 'activation state approvedGeneration is invalid'
        }
        $runtimeInventory = Assert-RuntimeInventory $state.runtimeBaseline.runtimeInventory 'activation state runtimeInventory'
    }
    if ($state.manifestPath -isnot [string] -or [string]::IsNullOrWhiteSpace($state.manifestPath)) { Fail 'activation state manifestPath is invalid' }
    $originalNames = @($state.original.PSObject.Properties.Name)
    if ($originalNames.Count -notin @($ManagedVariables.Count, $PreDiagnosticsManagedVariables.Count) -or
        @($originalNames | Where-Object { $_ -notin $ManagedVariables }).Count -ne 0) { Fail 'activation state original has an unexpected property set' }
    if ($state.schema -eq $LegacyActivationStateSchema -and $originalNames.Count -ne $PreDiagnosticsManagedVariables.Count) { Fail 'legacy activation state original has an unexpected property set' }
    $original = [ordered]@{}
    foreach ($name in $originalNames) { $original[$name] = Assert-EnvironmentEntry $state.original.$name $name 'activation state original' }
    return [pscustomobject]@{
        ManifestPath = $state.manifestPath
        ManifestSha256 = if ($state.schema -eq $ActivationStateSchema) { ([string] $state.manifestSha256).ToLowerInvariant() } else { $null }
        Original = $original
        IsLegacy = $state.schema -eq $LegacyActivationStateSchema
        ManagedVariables = $originalNames
        RuntimeBaseline = if ($state.schema -eq $ActivationStateSchema) {
            [pscustomobject]@{ ApprovedGeneration = $state.runtimeBaseline.approvedGeneration; RuntimeInventory = $runtimeInventory }
        } else { $null }
    }
}

function Write-RuntimeStatus([string] $Active, [string] $Health, [string] $Reason = '') {
    "ACTIVE=$Active"
    "RUNTIME_HEALTH=$Health"
    if (-not [string]::IsNullOrEmpty($Reason)) { "REASON=$Reason" }
}

function Assert-CodexDesktopClosed {
    $inventory = Get-CodexProcessInventory
    $backendAlive = @($inventory | Where-Object { [StringComparer]::OrdinalIgnoreCase.Equals(($_.name -replace '\.exe$', ''), 'codex') })
    if ($backendAlive.Count -gt 0) {
        [Console]::Error.WriteLine('CODEX_PROCESS_GUARD=BACKEND_ALIVE')
        Fail 'Codex backend (codex.exe) must be closed before User environment mutation'
    }
    $uiAlive = @($inventory | Where-Object { [StringComparer]::OrdinalIgnoreCase.Equals(($_.name -replace '\.exe$', ''), 'ChatGPT') })
    if ($uiAlive.Count -gt 0) {
        [Console]::Error.WriteLine('CODEX_PROCESS_GUARD=CHATGPT_UI_ALIVE')
        Fail 'Codex Desktop UI (ChatGPT.exe) must be closed before User environment mutation'
    }
}

function Restore-OriginalEnvironment($Original, [string[]] $Names = $ManagedVariables) {
    foreach ($name in $Names) { Set-UserEnvironmentEntry $name $Original[$name] }
    Assert-UserEnvironmentMatches $Original 'EnableRollback' $Names
}

try {
    $stateFile = Get-StatePath
    if ([string]::IsNullOrWhiteSpace($EnvironmentStorePath) -and (-not [string]::IsNullOrWhiteSpace($EnvironmentStoreFailOnSet) -or -not [string]::IsNullOrWhiteSpace($EnvironmentStorePostcheckMismatch))) {
        Fail 'environment fault injection requires an isolated EnvironmentStorePath'
    }
    if (-not [string]::IsNullOrWhiteSpace($ProcessInventoryPath) -and -not (Test-IsolatedEnvironmentTarget)) {
        Fail 'process inventory injection requires an isolated environment target'
    }
    if ($BroadcastMode -ne 'Real' -and -not (Test-IsolatedEnvironmentTarget)) {
        Fail 'fake broadcast mode requires an isolated environment target'
    }
    if ($Mode -eq 'Validate') {
        $resolved = Read-Manifest
        'VALID=YES'
        'PIN=EXACT'
        'CODE_MODE_HOST=PINNED'
        'SAME_GENERATION=YES'
        'MACHINE_ENV=UNCHANGED'
        'PACKAGE_FILES=UNCHANGED'
        exit 0
    }
    if ($Mode -eq 'Status') {
        if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
            Write-RuntimeStatus 'NO' 'INACTIVE'
            exit 0
        }
        $state = Read-ActivationState $stateFile
        if ($state.IsLegacy) {
            Write-RuntimeStatus 'YES' 'UPDATE_REVALIDATION_REQUIRED' 'LEGACY_STATE_NO_RUNTIME_BASELINE'
            exit 0
        }
        try {
            $currentManifestPath = [IO.Path]::GetFullPath($ManifestPath)
            if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($state.ManifestPath), $currentManifestPath)) {
                Write-RuntimeStatus 'YES' 'UPDATE_REVALIDATION_REQUIRED' 'MANIFEST_PATH_CHANGED_SINCE_ENABLE'
                exit 0
            }
            if (-not [StringComparer]::OrdinalIgnoreCase.Equals((Get-Sha256 $currentManifestPath), $state.ManifestSha256)) {
                Write-RuntimeStatus 'YES' 'UPDATE_REVALIDATION_REQUIRED' 'MANIFEST_CHANGED_SINCE_ENABLE'
                exit 0
            }
            $resolved = Read-Manifest
            if (-not [StringComparer]::OrdinalIgnoreCase.Equals($resolved.GenerationName, $state.RuntimeBaseline.ApprovedGeneration)) {
                Write-RuntimeStatus 'YES' 'UPDATE_REVALIDATION_REQUIRED' 'MANIFEST_GENERATION_DIFFERS_FROM_BASELINE'
                exit 0
            }
            $active = Get-ActiveEnvironment $resolved
            Assert-UserEnvironmentMatches $active 'StatusActive' $state.ManagedVariables
            $currentInventory = Get-RuntimeInventory $resolved.RuntimeRoot
            if (-not (Test-RuntimeInventoryEqual $state.RuntimeBaseline.RuntimeInventory $currentInventory)) {
                Write-RuntimeStatus 'YES' 'UPDATE_REVALIDATION_REQUIRED' 'RUNTIME_INVENTORY_CHANGED'
                exit 0
            }
            Write-RuntimeStatus 'YES' 'HEALTHY'
        } catch {
            Write-RuntimeStatus 'YES' 'CHILD_RUNTIME_STALE' 'APPROVED_RUNTIME_OR_ACTIVATION_INVALID'
        }
        exit 0
    }
    if ($Mode -eq 'Enable') {
        $resolved = Read-Manifest
        if (-not $PSCmdlet.ShouldProcess('current user environment', 'enable Codex bridge')) { 'WHATIF=YES'; exit 0 }
        if (Test-Path -LiteralPath $stateFile -PathType Leaf) { Fail 'activation already exists; Disable must complete before Enable can retry' }
        Assert-CodexDesktopClosed
        $original = [ordered]@{}
        foreach ($name in $ManagedVariables) { $original[$name] = Get-UserEnvironmentEntry $name }
        $runtimeInventory = Get-RuntimeInventory $resolved.RuntimeRoot
        $state = [ordered]@{
            schema = $ActivationStateSchema
            manifestPath = [IO.Path]::GetFullPath($ManifestPath)
            manifestSha256 = (Get-Sha256 ([IO.Path]::GetFullPath($ManifestPath))).ToLowerInvariant()
            original = $original
            runtimeBaseline = [ordered]@{
                approvedGeneration = $resolved.GenerationName
                runtimeInventory = $runtimeInventory
            }
        }
        Write-AtomicJson $stateFile $state
        try {
            $active = Get-ActiveEnvironment $resolved
            foreach ($name in $ManagedVariables) { Set-UserEnvironmentEntry $name $active[$name] }
            Assert-UserEnvironmentMatches $active 'EnableActive'
            Assert-UserEnvironmentBroadcast -Mode $BroadcastMode | Out-Null
            'ACTIVE=YES'
            "USER_ENV_MUTATED=$(if ($script:UserEnvironmentMutated) { 'YES' } else { 'NO' })"
        } catch {
            $operationError = $_.Exception
            $rollbackError = $null
            try {
                Restore-OriginalEnvironment $original
                Assert-UserEnvironmentBroadcast -Mode $BroadcastMode | Out-Null
            } catch {
                $rollbackError = $_.Exception
            }
            if ($null -eq $rollbackError) { Remove-Item -LiteralPath $stateFile -Force }
            if ($null -ne $rollbackError) { throw "Enable failed: $($operationError.Message); rollback failed: $($rollbackError.Message)" }
            throw $operationError
        }
        exit 0
    }
    if ($Mode -eq 'Disable') {
        if (-not $PSCmdlet.ShouldProcess('current user environment', 'disable Codex bridge')) { 'WHATIF=YES'; exit 0 }
        if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
            'ACTIVE=NO'
            'USER_ENV_MUTATED=NO'
            exit 0
        }
        $state = Read-ActivationState $stateFile
        Assert-CodexDesktopClosed
        foreach ($name in $state.ManagedVariables) { Set-UserEnvironmentEntry $name $state.Original[$name] }
        Assert-UserEnvironmentMatches $state.Original 'DisableBaseline' $state.ManagedVariables
        Assert-UserEnvironmentBroadcast -Mode $BroadcastMode | Out-Null
        Remove-Item -LiteralPath $stateFile -Force
        'ACTIVE=NO'
        "USER_ENV_MUTATED=$(if ($script:UserEnvironmentMutated) { 'YES' } else { 'NO' })"
        exit 0
    }
} catch {
    [Console]::Error.WriteLine('codex bridge activation: operation failed')
    [Console]::Error.WriteLine("USER_ENV_MUTATED=$(if ($script:UserEnvironmentMutated) { 'YES' } else { 'NO' })")
    exit 2
}

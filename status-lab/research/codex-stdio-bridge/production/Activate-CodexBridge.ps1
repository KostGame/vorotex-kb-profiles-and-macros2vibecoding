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
    'CODEX_BRIDGE_APPROVAL_SINK_PATH'
)
$ActivationStateSchema = 'k15-codex-bridge/activation-state-v2'
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

function Read-Manifest {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $required = @(
        'schema',
        'adapterPath', 'nodePath', 'wrapperPath', 'transparentWrapperPath', 'bridgeCorePath', 'childPath',
        'adapterSha256', 'nodeSha256', 'wrapperSha256', 'transparentWrapperSha256', 'bridgeCoreSha256', 'childSha256',
        'approvalSinkPath'
    )
    Assert-ExactPropertyNames $manifest $required 'production manifest'
    if ($manifest.schema -ne 'k15-codex-bridge/production-manifest-v1') { Fail 'unsupported production manifest schema' }
    foreach ($name in $required) {
        if ($manifest.$name -isnot [string]) { Fail "manifest field $name must be a string" }
    }
    foreach ($name in @('adapterPath', 'nodePath', 'wrapperPath', 'transparentWrapperPath', 'bridgeCorePath', 'childPath', 'adapterSha256', 'nodeSha256', 'wrapperSha256', 'transparentWrapperSha256', 'bridgeCoreSha256', 'childSha256')) {
        if ([string]::IsNullOrWhiteSpace([string] $manifest.$name)) { Fail "manifest field $name is required" }
    }
    $paths = [ordered]@{
        adapterPath = Require-PinnedFile ([string] $manifest.adapterPath) 'adapterPath' ([string] $manifest.adapterSha256) 'adapterSha256'
        nodePath = Require-PinnedFile ([string] $manifest.nodePath) 'nodePath' ([string] $manifest.nodeSha256) 'nodeSha256'
        wrapperPath = Require-PinnedFile ([string] $manifest.wrapperPath) 'wrapperPath' ([string] $manifest.wrapperSha256) 'wrapperSha256'
        transparentWrapperPath = Require-PinnedFile ([string] $manifest.transparentWrapperPath) 'transparentWrapperPath' ([string] $manifest.transparentWrapperSha256) 'transparentWrapperSha256'
        bridgeCorePath = Require-PinnedFile ([string] $manifest.bridgeCorePath) 'bridgeCorePath' ([string] $manifest.bridgeCoreSha256) 'bridgeCoreSha256'
        childPath = Require-PinnedFile ([string] $manifest.childPath) 'childPath' ([string] $manifest.childSha256) 'childSha256'
    }
    $uniquePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $paths.Keys) {
        if (-not $uniquePaths.Add([IO.Path]::GetFullPath($paths[$name]))) { Fail "manifest path $name duplicates another executable path" }
    }
    $approvalSinkPath = Require-OptionalOutputPath ([string] $manifest.approvalSinkPath) 'approvalSinkPath'
    return [pscustomobject]@{ Manifest = $manifest; Paths = $paths; ApprovalSinkPath = $approvalSinkPath }
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

function Assert-UserEnvironmentMatches($Expected, [string] $Phase) {
    $mismatches = @()
    foreach ($name in $ManagedVariables) {
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
    Assert-ExactPropertyNames $state @('schema', 'manifestPath', 'original') 'activation state'
    if ($state.schema -ne $ActivationStateSchema) { Fail 'unsupported activation state schema' }
    if ($state.manifestPath -isnot [string] -or [string]::IsNullOrWhiteSpace($state.manifestPath)) { Fail 'activation state manifestPath is invalid' }
    Assert-ExactPropertyNames $state.original $ManagedVariables 'activation state original'
    $original = [ordered]@{}
    foreach ($name in $ManagedVariables) { $original[$name] = Assert-EnvironmentEntry $state.original.$name $name 'activation state original' }
    return [pscustomobject]@{ ManifestPath = $state.manifestPath; Original = $original }
}

function Assert-CodexDesktopClosed {
    if (Test-IsolatedEnvironmentTarget) { return }
    if (@(Get-Process -Name 'Codex' -ErrorAction SilentlyContinue).Count -gt 0) {
        Fail 'Codex Desktop must be closed before User environment mutation'
    }
}

function Restore-OriginalEnvironment($Original) {
    foreach ($name in $ManagedVariables) { Set-UserEnvironmentEntry $name $Original[$name] }
    Assert-UserEnvironmentMatches $Original 'EnableRollback'
}

try {
    $stateFile = Get-StatePath
    if ([string]::IsNullOrWhiteSpace($EnvironmentStorePath) -and (-not [string]::IsNullOrWhiteSpace($EnvironmentStoreFailOnSet) -or -not [string]::IsNullOrWhiteSpace($EnvironmentStorePostcheckMismatch))) {
        Fail 'environment fault injection requires an isolated EnvironmentStorePath'
    }
    if ($BroadcastMode -ne 'Real' -and -not (Test-IsolatedEnvironmentTarget)) {
        Fail 'fake broadcast mode requires an isolated environment target'
    }
    if ($Mode -eq 'Validate') {
        Read-Manifest | Out-Null
        'VALID=YES'
        'PIN=EXACT'
        'MACHINE_ENV=UNCHANGED'
        'PACKAGE_FILES=UNCHANGED'
        exit 0
    }
    if ($Mode -eq 'Status') {
        if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) { 'ACTIVE=NO'; exit 0 }
        Read-ActivationState $stateFile | Out-Null
        'ACTIVE=YES'
        exit 0
    }
    if ($Mode -eq 'Enable') {
        $resolved = Read-Manifest
        if (-not $PSCmdlet.ShouldProcess('current user environment', 'enable Codex bridge')) { 'WHATIF=YES'; exit 0 }
        if (Test-Path -LiteralPath $stateFile -PathType Leaf) { Fail 'activation already exists; Disable must complete before Enable can retry' }
        Assert-CodexDesktopClosed
        $original = [ordered]@{}
        foreach ($name in $ManagedVariables) { $original[$name] = Get-UserEnvironmentEntry $name }
        $state = [ordered]@{ schema = $ActivationStateSchema; manifestPath = [IO.Path]::GetFullPath($ManifestPath); original = $original }
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
        foreach ($name in $ManagedVariables) { Set-UserEnvironmentEntry $name $state.Original[$name] }
        Assert-UserEnvironmentMatches $state.Original 'DisableBaseline'
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

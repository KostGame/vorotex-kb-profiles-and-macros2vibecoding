# Production activation is opt-in. It changes only the current user's environment.
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Validate', 'Enable', 'Disable', 'Status')]
    [string] $Mode = 'Validate',
    [Parameter(Mandatory)] [string] $ManifestPath,
    [string] $StatePath,
    [string] $EnvironmentStorePath,
    [string] $EnvironmentStoreFailOnSet,
    [ValidateSet('Real', 'FakeSuccess', 'FakeFailure')]
    [string] $BroadcastMode = 'Real',
    [switch] $Force
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

function Fail([string] $Message) { throw [InvalidOperationException]::new($Message) }

function Require-AbsoluteFile([string] $Value, [string] $Name) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^(?:[A-Za-z]:\\|\\\\)') {
        Fail "$Name must be an absolute file path"
    }
    $item = Get-Item -LiteralPath $Value -ErrorAction SilentlyContinue
    if ($null -eq $item -or -not $item.PSIsContainer -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        Fail "$Name must be a non-reparse regular file"
    }
    if ($null -eq $item -or $item.PSIsContainer) { Fail "$Name must name an existing file" }
    return $item.FullName
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
    if ($manifest.schema -ne 'k15-codex-bridge/production-manifest-v1') { Fail 'unsupported production manifest schema' }
    foreach ($name in @('adapterPath', 'nodePath', 'wrapperPath', 'transparentWrapperPath', 'bridgeCorePath', 'childPath', 'adapterSha256', 'nodeSha256', 'wrapperSha256', 'transparentWrapperSha256', 'bridgeCoreSha256', 'childSha256')) {
        if ($null -eq $manifest.$name -or [string]::IsNullOrWhiteSpace([string]$manifest.$name)) { Fail "manifest field $name is required" }
    }
    $paths = @{
        adapterPath = Require-PinnedFile ([string]$manifest.adapterPath) 'adapterPath' ([string]$manifest.adapterSha256) 'adapterSha256'
        nodePath = Require-PinnedFile ([string]$manifest.nodePath) 'nodePath' ([string]$manifest.nodeSha256) 'nodeSha256'
        wrapperPath = Require-PinnedFile ([string]$manifest.wrapperPath) 'wrapperPath' ([string]$manifest.wrapperSha256) 'wrapperSha256'
        transparentWrapperPath = Require-PinnedFile ([string]$manifest.transparentWrapperPath) 'transparentWrapperPath' ([string]$manifest.transparentWrapperSha256) 'transparentWrapperSha256'
        bridgeCorePath = Require-PinnedFile ([string]$manifest.bridgeCorePath) 'bridgeCorePath' ([string]$manifest.bridgeCoreSha256) 'bridgeCoreSha256'
        childPath = Require-PinnedFile ([string]$manifest.childPath) 'childPath' ([string]$manifest.childSha256) 'childSha256'
    }
    $adapter = [IO.Path]::GetFullPath($paths.adapterPath)
    foreach ($name in @('nodePath', 'wrapperPath', 'transparentWrapperPath', 'bridgeCorePath', 'childPath')) {
        if ([StringComparer]::OrdinalIgnoreCase.Equals($adapter, [IO.Path]::GetFullPath($paths[$name]))) {
            Fail "$name resolves to the adapter"
        }
    }
    return [pscustomobject]@{ Manifest = $manifest; Paths = $paths }
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
        $Value | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

$script:FailureInjected = $false
function Get-UserValue([string] $Name) {
    if ([string]::IsNullOrWhiteSpace($EnvironmentStorePath)) { return [Environment]::GetEnvironmentVariable($Name, 'User') }
    if (-not (Test-Path -LiteralPath $EnvironmentStorePath -PathType Leaf)) { return $null }
    $store = Get-Content -LiteralPath $EnvironmentStorePath -Raw | ConvertFrom-Json
    $property = $store.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}
function Set-UserValue([string] $Name, $Value) {
    if (-not [string]::IsNullOrWhiteSpace($EnvironmentStoreFailOnSet) -and -not $script:FailureInjected -and $Name -eq $EnvironmentStoreFailOnSet) {
        $script:FailureInjected = $true
        Fail 'injected environment-store failure'
    }
    if ([string]::IsNullOrWhiteSpace($EnvironmentStorePath)) {
        [Environment]::SetEnvironmentVariable($Name, $Value, 'User')
        return
    }
    $store = if (Test-Path -LiteralPath $EnvironmentStorePath -PathType Leaf) {
        Get-Content -LiteralPath $EnvironmentStorePath -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{} }
    $property = $store.PSObject.Properties[$Name]
    if ($null -eq $Value) {
        if ($null -ne $property) { $store.PSObject.Properties.Remove($Name) }
    } elseif ($null -ne $property) {
        $property.Value = [string]$Value
    } else {
        $store | Add-Member -MemberType NoteProperty -Name $Name -Value ([string]$Value)
    }
    Write-AtomicJson $EnvironmentStorePath $store
}

try {
    $resolved = Read-Manifest
    $stateFile = Get-StatePath
    if ($BroadcastMode -ne 'Real' -and [string]::IsNullOrWhiteSpace($EnvironmentStorePath)) {
        Fail 'fake broadcast mode requires an isolated EnvironmentStorePath'
    }
    if ($Mode -eq 'Validate') {
        'VALID=YES'
        'PIN=EXACT'
        'MACHINE_ENV=UNCHANGED'
        'PACKAGE_FILES=UNCHANGED'
        exit 0
    }
    if ($Mode -eq 'Status') {
        if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) { 'ACTIVE=NO'; exit 0 }
        $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        if ($state.schema -ne 'k15-codex-bridge/activation-state-v1') { Fail 'unsupported activation state schema' }
        'ACTIVE=YES'
        exit 0
    }
    if ($Mode -eq 'Enable') {
        if (-not $PSCmdlet.ShouldProcess('current user environment', 'enable Codex bridge')) { 'WHATIF=YES'; exit 0 }
        if ((Test-Path -LiteralPath $stateFile -PathType Leaf) -and -not $Force) { Fail 'activation already exists; use Disable first or -Force' }
        $original = [ordered]@{}
        foreach ($name in $ManagedVariables) { $original[$name] = Get-UserValue $name }
        $state = [ordered]@{ schema = 'k15-codex-bridge/activation-state-v1'; manifestPath = [IO.Path]::GetFullPath($ManifestPath); original = $original }
        Write-AtomicJson $stateFile $state
        try {
            Set-UserValue 'CODEX_CLI_PATH' $resolved.Paths.adapterPath
            Set-UserValue 'CODEX_BRIDGE_NODE_PATH' $resolved.Paths.nodePath
            Set-UserValue 'CODEX_BRIDGE_WRAPPER_PATH' $resolved.Paths.wrapperPath
            Set-UserValue 'CODEX_BRIDGE_CHILD_PATH' $resolved.Paths.childPath
            Set-UserValue 'CODEX_BRIDGE_CHILD_SHA256' ([string]$resolved.Manifest.childSha256).ToLowerInvariant()
            if ($null -ne $resolved.Manifest.approvalSinkPath -and [string]$resolved.Manifest.approvalSinkPath -ne '') {
                Set-UserValue 'CODEX_BRIDGE_APPROVAL_SINK_PATH' ([string]$resolved.Manifest.approvalSinkPath)
            } else {
                Set-UserValue 'CODEX_BRIDGE_APPROVAL_SINK_PATH' $null
            }
            Assert-UserEnvironmentBroadcast -Mode $BroadcastMode | Out-Null
            'ACTIVE=YES'
        } catch {
            $operationError = $_.Exception
            $rollbackError = $null
            try { foreach ($name in $ManagedVariables) { Set-UserValue $name $original[$name] }; Assert-UserEnvironmentBroadcast -Mode $BroadcastMode | Out-Null }
            catch { $rollbackError = $_.Exception }
            if ($null -eq $rollbackError) { Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue }
            if ($null -ne $rollbackError) { throw "Enable failed: $($operationError.Message); rollback failed: $($rollbackError.Message)" }
            throw $operationError
        }
        exit 0
    }
    if ($Mode -eq 'Disable') {
        if (-not $PSCmdlet.ShouldProcess('current user environment', 'disable Codex bridge')) { 'WHATIF=YES'; exit 0 }
        if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) { 'ACTIVE=NO'; exit 0 }
        $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        if ($state.schema -ne 'k15-codex-bridge/activation-state-v1') { Fail 'unsupported activation state schema' }
        foreach ($name in $ManagedVariables) { Set-UserValue $name $state.original.$name }
        Assert-UserEnvironmentBroadcast -Mode $BroadcastMode | Out-Null
        Remove-Item -LiteralPath $stateFile -Force
        'ACTIVE=NO'
        exit 0
    }
} catch {
    [Console]::Error.WriteLine('codex bridge activation: operation failed')
    exit 2
}

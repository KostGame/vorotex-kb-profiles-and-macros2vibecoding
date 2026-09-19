[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ArtifactPath,
  [Parameter(Mandatory=$true)][string]$ExpectedProject,
  [string]$SourceArtifactPath,
  [string]$PackagedArtifactPath,
  [string]$ExpectedBuildCommit
)
$ErrorActionPreference = 'Stop'

function Assert-StatusTrayArtifact {
  param(
    [Parameter(Mandatory=$true)][string]$Artifact,
    [Parameter(Mandatory=$true)][string]$Project,
    [string]$SourceArtifact,
    [string]$PackagedArtifact,
    [string]$BuildCommit
  )
  if (-not (Test-Path -LiteralPath $Artifact -PathType Leaf)) { throw "Status Tray artifact missing: $Artifact" }
  $artifactFull = (Resolve-Path -LiteralPath $Artifact).Path
  if ([IO.Path]::GetFileName($artifactFull) -cne 'Vorotex.K15.StatusTray.exe') { throw 'Status Tray artifact name mismatch' }
  if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) { throw "Expected Status Tray project missing: $Project" }
  $projectFull = (Resolve-Path -LiteralPath $Project).Path
  if ([IO.Path]::GetFileName($projectFull) -cne 'Vorotex.K15.StatusLab.csproj' -or
      $projectFull -notmatch '[\\/]status-lab[\\/]Vorotex\.K15\.StatusLab\.csproj$') {
    throw "Status Tray project is not the current explicit status-lab project: $projectFull"
  }
  $sourceFile = Join-Path (Split-Path -Parent $projectFull) 'StatusTrayApplicationContext.cs'
  $source = [IO.File]::ReadAllText($sourceFile, [Text.Encoding]::UTF8)
  if ($source -notmatch 'Codex Pet:\s') { throw 'Required current Codex Pet marker is missing from source' }
  if ($source -notmatch 'K15 Control Center') { throw 'Required current Control Center marker is missing from source' }
  if ($source -notmatch 'RGB-[^\r\n"]+') { throw 'Required current RGB status marker is missing from source' }
  if ($source.Contains('RGB on/off')) { throw 'Legacy-only RGB on/off marker is present in the expected source' }
  $version = (Get-Item -LiteralPath $artifactFull).VersionInfo
  if ($version.ProductName -cne 'Vorotex.K15.StatusTray') { throw "ProductName mismatch: $($version.ProductName)" }
  if ($version.FileDescription -cne 'Vorotex.K15.StatusTray') { throw "FileDescription mismatch: $($version.FileDescription)" }
  if ($BuildCommit -and $version.ProductVersion -notmatch [regex]::Escape($BuildCommit)) {
    throw "Embedded build identity missing from ProductVersion: expected $BuildCommit, actual $($version.ProductVersion)"
  }
  $artifactSha = Get-VNextFileSha256 -LiteralPath $artifactFull
  $artifactSize = (Get-Item -LiteralPath $artifactFull).Length
  if ($SourceArtifact) {
    if (-not (Test-Path -LiteralPath $SourceArtifact -PathType Leaf)) { throw "Source artifact missing: $SourceArtifact" }
    $sourceSha = Get-VNextFileSha256 -LiteralPath $SourceArtifact
    $sourceSize = (Get-Item -LiteralPath $SourceArtifact).Length
    if ($artifactSha -ne $sourceSha) { throw "Source/destination SHA mismatch: $sourceSha != $artifactSha" }
    if ($artifactSize -ne $sourceSize) { throw "Source/destination size mismatch: $sourceSize != $artifactSize" }
  }
  if ($PackagedArtifact) {
    if (-not (Test-Path -LiteralPath $PackagedArtifact -PathType Leaf)) { throw "Packaged artifact missing: $PackagedArtifact" }
    $packagedSha = Get-VNextFileSha256 -LiteralPath $PackagedArtifact
    $packagedSize = (Get-Item -LiteralPath $PackagedArtifact).Length
    if ($artifactSha -ne $packagedSha -or $artifactSize -ne $packagedSize) { throw 'Packaged artifact provenance mismatch' }
  }
  [pscustomobject]@{ Sha256 = $artifactSha; Size = $artifactSize; ProductName = $version.ProductName; FileDescription = $version.FileDescription; ProductVersion = $version.ProductVersion; Project = $projectFull }
}

$hashHelper = Join-Path $PSScriptRoot 'VNextPackageHash.ps1'
. $hashHelper
Assert-StatusTrayArtifact -Artifact $ArtifactPath -Project $ExpectedProject -SourceArtifact $SourceArtifactPath -PackagedArtifact $PackagedArtifactPath -BuildCommit $ExpectedBuildCommit | ConvertTo-Json -Compress

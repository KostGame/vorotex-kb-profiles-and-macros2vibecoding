[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [string]$Version,
  [string]$SourceCommit,
  [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$BaseMainSha,
  [string]$SourceRef
)
$ErrorActionPreference = 'Stop'
$hashHelper = Join-Path $PSScriptRoot 'VNextPackageHash.ps1'; . $hashHelper
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checkedOutCommit = (& git -C $repo rev-parse HEAD).Trim()
if (-not $SourceCommit) { $SourceCommit = $checkedOutCommit }
if ($SourceCommit -ne $checkedOutCommit) { throw 'supplied SourceCommit does not match checked-out source' }
if (-not $Version) { $Version = $SourceCommit }
if (-not $SourceRef) { $SourceRef = if ($env:GITHUB_REF) { $env:GITHUB_REF } else { (& git -C $repo branch --show-current).Trim() } }
if ($SourceRef -notmatch '^refs/') { $SourceRef = "refs/heads/$SourceRef" }
& git -C $repo cat-file -e "$BaseMainSha^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) { throw 'BaseMainSha is not present in checked-out Git source' }
& git -C $repo merge-base --is-ancestor $BaseMainSha $checkedOutCommit 2>$null
if ($LASTEXITCODE -ne 0) { throw 'BaseMainSha is not an ancestor of the build commit' }
if ($Version -notmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'Version must be a safe immutable directory name' }
# This is staging only. Apply/update/rollback is exclusively Apply-VNextPackage.ps1.
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
$payload = Join-Path $out "versions\$Version\payload"
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $out 'integration'), (Join-Path $out 'data') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $out 'current-version.txt') -Value $Version -NoNewline
Set-Content -LiteralPath (Join-Path $out 'integration\README.txt') -Value 'Stable integration boundary. Do not place this directory under versions.'
Set-Content -LiteralPath (Join-Path $out 'data\README.txt') -Value 'Persistent runtime data boundary. Updates must not copy it backwards.'
$versionManifest = @{
  schema = 'vorotex-k15-vnext-package/v1'
  source = @{ ref = $SourceRef; baseMainSha = $BaseMainSha; buildCommit = $SourceCommit }
  version = $Version
  executables = @('Vorotex.K15.Runtime.exe','Vorotex.K15.StatusTray.exe','Vorotex.K15.ControlCenter.exe','Vorotex.K15.LiveDashboard.exe')
  defaults = @{ bridgeEnabled = $false; physicalHidEnabled = $false; autostart = $false }
}
$versionManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out "versions\$Version\manifest.json")
@{ bridgeEnabled = $false; physicalHidEnabled = $false; autostart = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'integration\default-config.json')
$verifyStatusTray = Join-Path $PSScriptRoot 'Verify-StatusTrayArtifact.ps1'
$staging = Join-Path $out 'staging'
$projectSpecs = @(
  @{ Name = 'Vorotex.K15.Runtime.exe'; Project = 'vnext\Vorotex.K15.Runtime\Vorotex.K15.Runtime.csproj'; Identity = $false },
  @{ Name = 'Vorotex.K15.StatusTray.exe'; Project = 'status-lab\Vorotex.K15.StatusLab.csproj'; Identity = $true },
  @{ Name = 'Vorotex.K15.ControlCenter.exe'; Project = 'status-lab\control-center\Vorotex.K15.ControlCenter.csproj'; Identity = $false },
  @{ Name = 'Vorotex.K15.LiveDashboard.exe'; Project = 'status-lab\live-dashboard\Vorotex.K15.LiveDashboard.csproj'; Identity = $false }
)
foreach ($spec in $projectSpecs) {
  $projectPath = Join-Path $repo $spec.Project
  $projectStage = Join-Path $staging ([IO.Path]::GetFileNameWithoutExtension($spec.Project))
  New-Item -ItemType Directory -Path $projectStage -Force | Out-Null
  $publishArgs = @($projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-o', $projectStage)
  if ($spec.Identity) { $publishArgs += "-p:InformationalVersion=$SourceCommit" }
  & dotnet publish @publishArgs
  if ($LASTEXITCODE) { throw "publish failed: $($spec.Project)" }
  $sourceExe = Join-Path $projectStage $spec.Name
  if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "explicit publish missing $($spec.Name) from $($spec.Project)" }
  if ($spec.Identity) {
    $verification = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifyStatusTray -ArtifactPath $sourceExe -ExpectedProject (Join-Path $repo $spec.Project) -ExpectedBuildCommit $SourceCommit | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Status Tray artifact identity gate failed' }
    Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $payload $spec.Name)
    $packagedExe = Join-Path $payload $spec.Name
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifyStatusTray -ArtifactPath $packagedExe -ExpectedProject (Join-Path $repo $spec.Project) -SourceArtifactPath $sourceExe -ExpectedBuildCommit $SourceCommit | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Status Tray final-copy provenance gate failed' }
  } else {
    Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $payload $spec.Name)
    if ($spec.Name -eq 'Vorotex.K15.LiveDashboard.exe') {
      $wwwroot = Join-Path $projectStage 'wwwroot'
      if (Test-Path -LiteralPath $wwwroot) { Copy-Item -LiteralPath (Join-Path $wwwroot '*') -Destination (Join-Path $payload 'wwwroot') -Recurse -Force }
    }
  }
}
$expected = $projectSpecs.Name
foreach ($name in $expected) { if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "package missing explicit $name" } }
$provenance = [ordered]@{ schema = 'vorotex-k15-status-tray-provenance/v1'; sourceProject = 'status-lab/Vorotex.K15.StatusLab.csproj'; sourceCommit = $SourceCommit; publishConfiguration = 'Release'; runtimeIdentifier = 'win-x64'; selfContained = $true; singleFile = $true; sourceSha256 = $verification.Sha256; sourceSize = $verification.Size; packagedSha256 = Get-VNextFileSha256 -LiteralPath (Join-Path $payload 'Vorotex.K15.StatusTray.exe'); packagedSize = (Get-Item (Join-Path $payload 'Vorotex.K15.StatusTray.exe')).Length }
$provenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'Vorotex.K15.StatusTray.provenance.json')
$hashes = [ordered]@{}
Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
  $relative = $_.FullName.Substring($payload.Length + 1).Replace('\','/')
  $hashes[$relative] = Get-VNextFileSha256 -LiteralPath $_.FullName
}
$versionManifest | Add-Member -NotePropertyName contentSha256 -NotePropertyValue $hashes
$versionManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out "versions\$Version\manifest.json")
Write-Output "PACKAGE=$out"
Write-Output "VERSION=$Version"

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [string]$Version,
  [string]$SourceCommit,
  [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$BaseMainSha,
  [string]$SourceRef
)
$ErrorActionPreference = 'Stop'
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
$projects = @(
  'vnext\Vorotex.K15.Runtime\Vorotex.K15.Runtime.csproj',
  'vnext\Vorotex.K15.StatusTray\Vorotex.K15.StatusTray.csproj',
  'vnext\Vorotex.K15.ControlCenter\Vorotex.K15.ControlCenter.csproj',
  'vnext\Vorotex.K15.LiveDashboard\Vorotex.K15.LiveDashboard.csproj'
)
foreach ($project in $projects) {
  & dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $payload
  if ($LASTEXITCODE) { throw "publish failed: $project" }
}
$expected = @('Vorotex.K15.Runtime.exe','Vorotex.K15.StatusTray.exe','Vorotex.K15.ControlCenter.exe','Vorotex.K15.LiveDashboard.exe')
foreach ($name in $expected) { if (-not (Test-Path (Join-Path $payload $name))) { throw "package missing $name" } }
$hashes = [ordered]@{}
Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
  $relative = $_.FullName.Substring($payload.Length + 1).Replace('\','/')
  $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$versionManifest | Add-Member -NotePropertyName contentSha256 -NotePropertyValue $hashes
$versionManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out "versions\$Version\manifest.json")
Write-Output "PACKAGE=$out"
Write-Output "VERSION=$Version"

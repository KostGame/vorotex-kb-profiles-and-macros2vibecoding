[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [string]$Version,
  [string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $SourceCommit) { $SourceCommit = (& git -C $repo rev-parse HEAD).Trim() }
if (-not $Version) { $Version = $SourceCommit }
if ($Version -notmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'Version must be a safe immutable directory name' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
$payload = Join-Path $out "versions\$Version\payload"
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $out 'integration'), (Join-Path $out 'data') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $out 'current-version.txt') -Value $Version -NoNewline
Set-Content -LiteralPath (Join-Path $out 'integration\README.txt') -Value 'Stable integration boundary. Do not place this directory under versions.'
Set-Content -LiteralPath (Join-Path $out 'data\README.txt') -Value 'Persistent runtime data boundary. Updates must not copy it backwards.'
@{
  schema = 'vorotex-k15-vnext-package/v1'
  source = @{ main = 'main'; commit = $SourceCommit }
  version = $Version
  legacyRoot = '%LOCALAPPDATA%\VorotexK15\app'
  stableBoundaries = @('integration','data')
  executables = @('Vorotex.K15.Runtime.exe','Vorotex.K15.StatusTray.exe','Vorotex.K15.ControlCenter.exe','Vorotex.K15.LiveDashboard.exe')
  defaults = @{ bridgeEnabled = $false; physicalHidEnabled = $false; autostart = $false }
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'integration\default-config.json')
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
Get-ChildItem -LiteralPath $out -File -Recurse | Where-Object { $_.Name -ne 'manifest.json' } | Sort-Object FullName | ForEach-Object {
  $relative = $_.FullName.Substring($out.Length + 1).Replace('\','/')
  $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = Get-Content (Join-Path $out 'integration\default-config.json') -Raw | ConvertFrom-Json
$manifest | Add-Member -NotePropertyName contentSha256 -NotePropertyValue $hashes
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'manifest.json')
Write-Output "PACKAGE=$out"
Write-Output "VERSION=$Version"

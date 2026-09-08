[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$SourcePackage,
  [Parameter(Mandatory=$true)][string]$InstallRoot,
  [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedBaseMainSha,
  [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedBuildCommit,
  [string]$SelectVersion,
  [string]$RollbackVersion,
  [switch]$SimulateSelectorFailure
)
$ErrorActionPreference = 'Stop'
$integrity = Join-Path $PSScriptRoot 'VNextPackageIntegrity.ps1'; . $integrity
$source = (Resolve-Path $SourcePackage).Path
$root = [IO.Path]::GetFullPath($InstallRoot)
$allow = @('Vorotex.K15.Runtime.exe','Vorotex.K15.StatusTray.exe','Vorotex.K15.ControlCenter.exe','Vorotex.K15.LiveDashboard.exe')
function Switch-Selector([string]$rootPath, [string]$versionName, [switch]$fail) {
  $selectorPath = Join-Path $rootPath 'current-version.txt'; $tempPath = Join-Path $rootPath ".current-version.$([guid]::NewGuid().ToString('N')).tmp"; $backupPath = Join-Path $rootPath ".current-version.$([guid]::NewGuid().ToString('N')).bak"
  try { Set-Content -LiteralPath $tempPath -Value $versionName -NoNewline; if ($fail) { throw 'simulated selector switch failure' }; if (Test-Path $selectorPath) { [IO.File]::Replace($tempPath, $selectorPath, $backupPath, $true); Remove-Item $backupPath -Force -ErrorAction SilentlyContinue } else { [IO.File]::Move($tempPath, $selectorPath) } } finally { if (Test-Path $tempPath) { Remove-Item $tempPath -Force }; if (Test-Path $backupPath) { Remove-Item $backupPath -Force -ErrorAction SilentlyContinue } }
}
if ($RollbackVersion) {
  if ($RollbackVersion -notmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'invalid rollback version' }
  $rollbackDir = Join-Path $root "versions\$RollbackVersion"; $rollbackManifestPath = Join-Path $rollbackDir 'manifest.json'; $rollbackManifest = Get-Content $rollbackManifestPath -Raw | ConvertFrom-Json
  if ($rollbackManifest.source.baseMainSha -ne $ExpectedBaseMainSha -or $rollbackManifest.source.buildCommit -ne $ExpectedBuildCommit) { throw 'rollback provenance contract mismatch' }
  Test-VNextVersionIntegrity $rollbackDir | Out-Null
  Switch-Selector -rootPath $root -versionName $RollbackVersion -fail:$SimulateSelectorFailure; Write-Output "ROLLED_BACK_VERSION=$RollbackVersion"; exit 0
}
$sourceSelector = (Get-Content (Join-Path $source 'current-version.txt') -Raw).Trim()
$version = if ($SelectVersion) { $SelectVersion } else { $sourceSelector }
if ($version -notmatch '^[A-Za-z0-9._-]{1,128}$' -or $version -ne $sourceSelector) { throw 'invalid source version selector' }
$sourceVersion = Join-Path $source "versions\$version"; $sourcePayload = Join-Path $sourceVersion 'payload'; $sourceManifestPath = Join-Path $sourceVersion 'manifest.json'
if (-not (Test-Path $sourceManifestPath) -or -not (Test-Path $sourcePayload)) { throw 'source version package is incomplete' }
$manifest = Test-VNextVersionIntegrity $sourceVersion
if ($manifest.version -ne $version -or $manifest.source.baseMainSha -ne $ExpectedBaseMainSha -or $manifest.source.buildCommit -ne $ExpectedBuildCommit) { throw 'source provenance contract mismatch' }
if ($manifest.defaults.bridgeEnabled -or $manifest.defaults.physicalHidEnabled -or $manifest.defaults.autostart) { throw 'unsafe default enabled' }
if (@($manifest.executables | Where-Object { $_ -notin $allow }).Count -or @($manifest.executables).Count -ne 4) { throw 'executable allowlist mismatch' }
foreach ($entry in $manifest.contentSha256.psobject.Properties) {
  $file = Join-Path $sourcePayload ($entry.Name -replace '/','\')
  if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "source payload hash mismatch: $($entry.Name)" }
}
New-Item -ItemType Directory -Path $root, (Join-Path $root 'versions'), (Join-Path $root 'integration'), (Join-Path $root 'data') -Force | Out-Null
$targetVersion = Join-Path $root "versions\$version"; $targetManifest = Join-Path $targetVersion 'manifest.json'
if (Test-Path $targetVersion) {
  $installedManifest = Test-VNextVersionIntegrity $targetVersion
  if (-not (Test-Path $targetManifest) -or (Get-FileHash $targetManifest -Algorithm SHA256).Hash -ne (Get-FileHash $sourceManifestPath -Algorithm SHA256).Hash) { throw 'existing version differs; refusing overwrite' }
} else {
  $tempVersion = Join-Path $root "versions\.$version.$([guid]::NewGuid().ToString('N')).tmp"
  try { Copy-Item $sourceVersion $tempVersion -Recurse; Move-Item $tempVersion $targetVersion } finally { if (Test-Path $tempVersion) { Remove-Item $tempVersion -Recurse -Force } }
}
$defaultConfig = Join-Path $source 'integration\default-config.json'
if (-not (Test-Path (Join-Path $root 'integration\default-config.json'))) { Copy-Item $defaultConfig (Join-Path $root 'integration\default-config.json') }
Switch-Selector -rootPath $root -versionName $version -fail:$SimulateSelectorFailure
Write-Output "APPLIED_VERSION=$version"

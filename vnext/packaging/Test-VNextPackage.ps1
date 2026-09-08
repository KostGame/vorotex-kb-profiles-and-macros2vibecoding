[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$manifest = Get-Content (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'vorotex-k15-vnext-package/v1') { throw 'manifest schema mismatch' }
if ($manifest.source.main -ne 'main' -or $manifest.source.commit -notmatch '^[0-9a-f]{40}$') { throw 'source provenance missing' }
foreach ($name in $manifest.executables) {
  $relative = "versions\$($manifest.version)\payload\$name"
  if (-not (Test-Path (Join-Path $package $relative))) { throw "manifest executable missing: $name" }
}
if ((Get-Content (Join-Path $package 'current-version.txt') -Raw).Trim() -ne $manifest.version) { throw 'current version selection mismatch' }
if ($manifest.defaults.bridgeEnabled -or $manifest.defaults.physicalHidEnabled -or $manifest.defaults.autostart) { throw 'unsafe default enabled' }
foreach ($entry in $manifest.contentSha256.psobject.Properties) {
  $actual = (Get-FileHash (Join-Path $package ($entry.Name -replace '/','\') ) -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $entry.Value) { throw "content hash mismatch: $($entry.Name)" }
}
$simulation = Join-Path ([IO.Path]::GetTempPath()) ('k15-update-' + [guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path (Join-Path $simulation 'versions\B\payload'), (Join-Path $simulation 'integration'), (Join-Path $simulation 'data'), (Join-Path $simulation 'legacy') -Force | Out-Null
  Copy-Item -LiteralPath (Join-Path $package 'integration\default-config.json') -Destination (Join-Path $simulation 'integration\default-config.json')
  Set-Content (Join-Path $simulation 'integration\state.txt') 'stable'; Set-Content (Join-Path $simulation 'data\state.txt') 'persistent'; Set-Content (Join-Path $simulation 'legacy\legacy.txt') 'untouched'
  $integrationHash = (Get-FileHash (Join-Path $simulation 'integration\state.txt')).Hash
  $dataHash = (Get-FileHash (Join-Path $simulation 'data\state.txt')).Hash
  Copy-Item -LiteralPath (Join-Path $package "versions\$($manifest.version)\payload\*") -Destination (Join-Path $simulation 'versions\B\payload') -Recurse
  Set-Content (Join-Path $simulation 'current-version.txt') 'B' -NoNewline
  if ((Get-FileHash (Join-Path $simulation 'integration\state.txt')).Hash -ne $integrationHash -or (Get-FileHash (Join-Path $simulation 'data\state.txt')).Hash -ne $dataHash) { throw 'stable boundary changed during update' }
  Set-Content (Join-Path $simulation 'current-version.txt') $manifest.version -NoNewline
  if ((Get-Content (Join-Path $simulation 'legacy\legacy.txt') -Raw).TrimEnd() -ne 'untouched') { throw 'legacy file changed during update' }
} finally { if (Test-Path $simulation) { Remove-Item $simulation -Recurse -Force } }
Write-Output 'PACKAGE_MANIFEST=PASS'
Write-Output 'PACKAGE_EXECUTABLES=PASS'
Write-Output 'PACKAGE_HASHES=PASS'
Write-Output 'PACKAGE_DEFAULTS_FAIL_CLOSED=PASS'
Write-Output 'PACKAGE_UPDATE_ROLLBACK=PASS'

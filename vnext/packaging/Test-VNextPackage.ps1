[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$selected = (Get-Content (Join-Path $package 'current-version.txt') -Raw).Trim()
$manifestPath = Join-Path $package "versions\$selected\manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$payload = Join-Path $package "versions\$selected\payload"
if ($manifest.schema -ne 'vorotex-k15-vnext-package/v1' -or $manifest.source.ref -eq 'main') { throw 'manifest schema/provenance mismatch' }
if ($manifest.source.baseMainSha -notmatch '^[0-9a-f]{40}$' -or $manifest.source.buildCommit -notmatch '^[0-9a-f]{40}$') { throw 'source provenance missing' }
$allow = @('Vorotex.K15.Runtime.exe','Vorotex.K15.StatusTray.exe','Vorotex.K15.ControlCenter.exe','Vorotex.K15.LiveDashboard.exe')
if (@($manifest.executables | Where-Object { $_ -notin $allow }).Count -or @($manifest.executables).Count -ne 4) { throw 'executable allowlist mismatch' }
if ($manifest.defaults.bridgeEnabled -or $manifest.defaults.physicalHidEnabled -or $manifest.defaults.autostart) { throw 'unsafe default enabled' }
foreach ($entry in $manifest.contentSha256.psobject.Properties) {
  if ($entry.Name -match '(^|/)(current-version\.txt|integration|data)(/|$)' -or $entry.Name -match '\.\.') { throw 'mutable content entered immutable manifest' }
  $file = Join-Path $payload ($entry.Name -replace '/','\')
  if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "content hash mismatch: $($entry.Name)" }
}
Write-Output 'PACKAGE_MANIFEST=PASS'; Write-Output 'PACKAGE_EXECUTABLES=PASS'; Write-Output 'PACKAGE_HASHES=PASS'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('k15-apply-' + [guid]::NewGuid().ToString('N')); $apply = Join-Path $PSScriptRoot 'Apply-VNextPackage.ps1'
function Invoke-Apply([string]$sourcePath, [string]$rootPath, [string]$buildCommit, [string]$rollback, [switch]$fail) {
  $childArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$apply,'-SourcePackage',$sourcePath,'-InstallRoot',$rootPath,'-ExpectedBaseMainSha',$manifest.source.baseMainSha,'-ExpectedBuildCommit',$buildCommit)
  if ($fail) { $childArgs += '-SimulateSelectorFailure' }
  if ($rollback) { $childArgs += @('-RollbackVersion',$rollback) }
  & powershell.exe @childArgs 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "apply failed with exit code $LASTEXITCODE" }
}
try {
  $sourceB = Join-Path $temp 'source-b'; $install = Join-Path $temp 'install'; New-Item -ItemType Directory -Path $sourceB -Force | Out-Null
  Copy-Item (Join-Path $package '*') $sourceB -Recurse
  Move-Item (Join-Path $sourceB "versions\$selected") (Join-Path $sourceB 'versions\B'); Set-Content (Join-Path $sourceB 'current-version.txt') 'B' -NoNewline
  $bManifestPath = Join-Path $sourceB 'versions\B\manifest.json'; $bManifest = Get-Content $bManifestPath -Raw | ConvertFrom-Json; $bManifest.version = 'B'
  $mutated = Join-Path $sourceB 'versions\B\payload\Vorotex.K15.Runtime.exe'; Add-Content $mutated 'B' -NoNewline
  $hashes = [ordered]@{}; $bPayload = Join-Path $sourceB 'versions\B\payload'; Get-ChildItem $bPayload -File -Recurse | ForEach-Object { $hashes[$_.FullName.Substring($bPayload.Length + 1).Replace('\','/')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }; $bManifest.contentSha256 = $hashes; $bManifest | ConvertTo-Json -Depth 8 | Set-Content $bManifestPath
  $legacy = Join-Path $temp 'legacy-sentinel.txt'; Set-Content $legacy 'legacy'
  Invoke-Apply $package $install $manifest.source.buildCommit
  $aHash = (Get-FileHash (Join-Path $install "versions\$selected\payload\Vorotex.K15.Runtime.exe")).Hash; $integrationHash = (Get-FileHash (Join-Path $install 'integration\default-config.json')).Hash; $data = Join-Path $install 'data\state.txt'; Set-Content $data 'persistent'; $dataHash = (Get-FileHash $data).Hash
  Invoke-Apply $sourceB $install $manifest.source.buildCommit
  $sourceBHash = (Get-FileHash (Join-Path $sourceB 'versions\B\manifest.json') -Algorithm SHA256).Hash; $installedBHash = (Get-FileHash (Join-Path $install 'versions\B\manifest.json') -Algorithm SHA256).Hash; if ($sourceBHash -ne $installedBHash) { throw "B manifest changed during install: source=$sourceBHash installed=$installedBHash" }
  $conflict = Join-Path $temp 'source-conflict'; New-Item -ItemType Directory -Path $conflict -Force | Out-Null; Copy-Item (Join-Path $package '*') $conflict -Recurse
  $conflictExe = Join-Path $conflict "versions\$selected\payload\Vorotex.K15.Runtime.exe"; Add-Content $conflictExe 'CONFLICT' -NoNewline
  $conflictManifestPath = Join-Path $conflict "versions\$selected\manifest.json"; $conflictManifest = Get-Content $conflictManifestPath -Raw | ConvertFrom-Json; $conflictName = Split-Path $conflictExe -Leaf; $conflictManifest.contentSha256 | Add-Member -Force -NotePropertyName $conflictName -NotePropertyValue (Get-FileHash $conflictExe -Algorithm SHA256).Hash.ToLowerInvariant(); $conflictManifest | ConvertTo-Json -Depth 8 | Set-Content $conflictManifestPath
  try { Invoke-Apply $conflict $install $manifest.source.buildCommit; throw 'different existing version accepted' } catch { if ($_.Exception.Message -eq 'different existing version accepted') { throw } }
  if ((Get-Content (Join-Path $install 'current-version.txt') -Raw).Trim() -ne 'B' -or (Get-FileHash (Join-Path $install 'integration\default-config.json')).Hash -ne $integrationHash -or (Get-FileHash $data).Hash -ne $dataHash -or (Get-FileHash (Join-Path $install "versions\$selected\payload\Vorotex.K15.Runtime.exe")).Hash -ne $aHash -or (Get-Content $legacy -Raw).Trim() -ne 'legacy') { throw 'A to B changed stable, A payload or legacy state' }
  Invoke-Apply $sourceB $install $manifest.source.buildCommit
  try { Invoke-Apply $sourceB $install ('0' * 40); throw 'provenance mismatch accepted' } catch { if ($_.Exception.Message -eq 'provenance mismatch accepted') { throw } }
  try { Invoke-Apply $sourceB $install $manifest.source.buildCommit -fail; throw 'selector failure accepted' } catch { if ($_.Exception.Message -eq 'selector failure accepted') { throw } }
  if ((Get-Content (Join-Path $install 'current-version.txt') -Raw).Trim() -ne 'B') { throw 'failed selector switch damaged selector' }
  Invoke-Apply $sourceB $install $manifest.source.buildCommit $selected
  if ((Get-Content (Join-Path $install 'current-version.txt') -Raw).Trim() -ne $selected) { throw 'rollback failed' }
  foreach ($checkVersion in @($selected,'B')) { $checkPath = Join-Path -Path $install -ChildPath "versions\$checkVersion"; $check = Get-Content (Join-Path -Path $checkPath -ChildPath 'manifest.json') -Raw | ConvertFrom-Json; $checkPayload = Join-Path -Path $checkPath -ChildPath 'payload'; foreach ($entry in $check.contentSha256.psobject.Properties) { $checkFile = Join-Path -Path $checkPayload -ChildPath $entry.Name.Replace('/','\'); if ((Get-FileHash -LiteralPath $checkFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "manifest invalid after rollback: $checkVersion" } } }
  Write-Output 'PACKAGE_APPLY_FIRST_INSTALL=PASS'; Write-Output 'PACKAGE_APPLY_A_TO_B=PASS'; Write-Output 'PACKAGE_APPLY_IDEMPOTENT=PASS'; Write-Output 'PACKAGE_APPLY_CONFLICT_FAIL_CLOSED=PASS'; Write-Output 'PACKAGE_APPLY_ROLLBACK=PASS'; Write-Output 'PACKAGE_APPLY_ATOMIC_FAILURE=PASS'; Write-Output 'PACKAGE_PROVENANCE_FAIL_CLOSED=PASS'; Write-Output 'PACKAGE_DEFAULTS_FAIL_CLOSED=PASS'
} finally { if (Test-Path $temp) { Remove-Item $temp -Recurse -Force } }

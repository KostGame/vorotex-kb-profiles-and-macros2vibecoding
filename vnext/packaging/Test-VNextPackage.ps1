[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$integrity = Join-Path $PSScriptRoot 'VNextPackageIntegrity.ps1'; . $integrity
$selected = (Get-Content (Join-Path $package 'current-version.txt') -Raw).Trim()
$manifestPath = Join-Path $package "versions\$selected\manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$payload = Join-Path $package "versions\$selected\payload"
if ($manifest.schema -ne 'vorotex-k15-vnext-package/v1' -or $manifest.source.ref -eq 'main') { throw 'manifest schema/provenance mismatch' }
if ($manifest.source.baseMainSha -notmatch '^[0-9a-f]{40}$' -or $manifest.source.buildCommit -notmatch '^[0-9a-f]{40}$') { throw 'source provenance missing' }
$allow = @('Vorotex.K15.Runtime.exe','Vorotex.K15.StatusTray.exe','Vorotex.K15.ControlCenter.exe','Vorotex.K15.LiveDashboard.exe')
if (@($manifest.executables | Where-Object { $_ -notin $allow }).Count -or @($manifest.executables).Count -ne 4) { throw 'executable allowlist mismatch' }
if ($manifest.defaults.bridgeEnabled -or $manifest.defaults.physicalHidEnabled -or $manifest.defaults.autostart) { throw 'unsafe default enabled' }
Test-VNextVersionIntegrity (Join-Path $package "versions\$selected") | Out-Null
Write-Output 'PACKAGE_MANIFEST=PASS'; Write-Output 'PACKAGE_EXECUTABLES=PASS'; Write-Output 'PACKAGE_HASHES=PASS'
$negative = Join-Path ([IO.Path]::GetTempPath()) ('k15-integrity-negative-' + [guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $negative -Force | Out-Null; Copy-Item (Join-Path $package '*') $negative -Recurse
  $omittedPath = Join-Path $negative "versions\$selected\manifest.json"; $originalManifestRaw = Get-Content $omittedPath -Raw; $omitted = $originalManifestRaw | ConvertFrom-Json; $omitted.contentSha256.psobject.Properties.Remove('Vorotex.K15.Runtime.exe'); $omitted | ConvertTo-Json -Depth 8 | Set-Content $omittedPath
  try { Test-VNextVersionIntegrity (Join-Path $negative "versions\$selected") | Out-Null; throw 'omitted executable hash accepted' } catch { if ($_.Exception.Message -eq 'omitted executable hash accepted') { throw } }
  Set-Content $omittedPath $originalManifestRaw
  $traversal = $originalManifestRaw | ConvertFrom-Json; $traversal.contentSha256 | Add-Member -NotePropertyName '../escape.exe' -NotePropertyValue ('0' * 64); $traversal | ConvertTo-Json -Depth 8 | Set-Content $omittedPath
  try { Test-VNextVersionIntegrity (Join-Path $negative "versions\$selected") | Out-Null; throw 'traversal manifest key accepted' } catch { if ($_.Exception.Message -eq 'traversal manifest key accepted') { throw } }
  Set-Content $omittedPath $originalManifestRaw
  $runtimeHash = ($manifest.contentSha256.psobject.Properties | Where-Object Name -eq 'Vorotex.K15.Runtime.exe').Value
  $duplicateRaw = [regex]::Replace($originalManifestRaw, '("Vorotex\.K15\.Runtime\.exe"\s*:\s*"[0-9a-f]+")', { param($match) $match.Value + ',"vorotex.k15.runtime.exe":"' + $runtimeHash + '"' }, 1)
  Set-Content $omittedPath $duplicateRaw
  try { Test-VNextVersionIntegrity (Join-Path $negative "versions\$selected") | Out-Null; throw 'case-colliding manifest key accepted' } catch { if ($_.Exception.Message -eq 'case-colliding manifest key accepted') { throw } }
  Set-Content $omittedPath $originalManifestRaw
  Copy-Item (Join-Path $payload 'Vorotex.K15.Runtime.exe') (Join-Path $negative "versions\$selected\payload\extra.bin"); Test-VNextVersionIntegrity (Join-Path $package "versions\$selected") | Out-Null
  try { Test-VNextVersionIntegrity (Join-Path $negative "versions\$selected") | Out-Null; throw 'extra payload file accepted' } catch { if ($_.Exception.Message -eq 'extra payload file accepted') { throw } }
  $builder = Join-Path $PSScriptRoot 'New-VNextPackage.ps1'; $previousPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $builder -OutputDirectory (Join-Path $negative 'fabricated') -Version 'invalid-base-test' -SourceCommit $manifest.source.buildCommit -BaseMainSha ('0' * 40) -SourceRef $manifest.source.ref 2>&1 | Out-Null; $fabricatedExit = $LASTEXITCODE; $ErrorActionPreference = $previousPreference; if ($fabricatedExit -eq 0) { throw 'fabricated BaseMainSha accepted' }
} finally { if (Test-Path $negative) { Remove-Item $negative -Recurse -Force } }
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
  Test-VNextVersionIntegrity (Join-Path $sourceB 'versions\B') | Out-Null
  $legacy = Join-Path $temp 'legacy-sentinel.txt'; Set-Content $legacy 'legacy'
  Invoke-Apply $package $install $manifest.source.buildCommit
  Test-VNextVersionIntegrity (Join-Path $install "versions\$selected") | Out-Null
  $installedA = Join-Path $install "versions\$selected\payload\Vorotex.K15.Runtime.exe"; Add-Content $installedA 'CORRUPTED' -NoNewline
  try { Invoke-Apply $package $install $manifest.source.buildCommit; throw 'corrupted installed payload accepted' } catch { if ($_.Exception.Message -eq 'corrupted installed payload accepted') { throw } }
  if ((Get-Content (Join-Path $install 'current-version.txt') -Raw).Trim() -ne $selected) { throw 'selector changed after corrupted reapply' }
  Copy-Item (Join-Path $payload 'Vorotex.K15.Runtime.exe') $installedA -Force
  $aHash = (Get-FileHash (Join-Path $install "versions\$selected\payload\Vorotex.K15.Runtime.exe")).Hash; $integrationHash = (Get-FileHash (Join-Path $install 'integration\default-config.json')).Hash; $data = Join-Path $install 'data\state.txt'; Set-Content $data 'persistent'; $dataHash = (Get-FileHash $data).Hash
  Invoke-Apply $sourceB $install $manifest.source.buildCommit
  Test-VNextVersionIntegrity (Join-Path $install "versions\$selected") | Out-Null; Test-VNextVersionIntegrity (Join-Path $install 'versions\B') | Out-Null
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
  Test-VNextVersionIntegrity (Join-Path $install "versions\$selected") | Out-Null; Test-VNextVersionIntegrity (Join-Path $install 'versions\B') | Out-Null
  Write-Output 'PACKAGE_APPLY_FIRST_INSTALL=PASS'; Write-Output 'PACKAGE_APPLY_A_TO_B=PASS'; Write-Output 'PACKAGE_APPLY_IDEMPOTENT=PASS'; Write-Output 'PACKAGE_APPLY_CONFLICT_FAIL_CLOSED=PASS'; Write-Output 'PACKAGE_APPLY_ROLLBACK=PASS'; Write-Output 'PACKAGE_APPLY_ATOMIC_FAILURE=PASS'; Write-Output 'PACKAGE_PROVENANCE_FAIL_CLOSED=PASS'; Write-Output 'PACKAGE_DEFAULTS_FAIL_CLOSED=PASS'
} finally { if (Test-Path $temp) { Remove-Item $temp -Recurse -Force } }

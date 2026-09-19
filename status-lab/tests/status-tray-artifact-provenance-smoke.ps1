$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verify = Join-Path $root 'vnext\packaging\Verify-StatusTrayArtifact.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('k15-status-tray-provenance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
  $current = Join-Path $tmp 'current\Vorotex.K15.StatusTray.exe'; $legacy = Join-Path $tmp 'legacy\Vorotex.K15.StatusTray.exe'
  New-Item -ItemType Directory -Path (Split-Path $current), (Split-Path $legacy) -Force | Out-Null
  $currentProject = Join-Path $root 'status-lab\Vorotex.K15.StatusLab.csproj'; $legacyProject = Join-Path $root 'vnext\Vorotex.K15.StatusTray\Vorotex.K15.StatusTray.csproj'
  $commit = (& git -C $root rev-parse HEAD).Trim()
  & dotnet publish $currentProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Split-Path $current) | Out-Null
  & dotnet publish $legacyProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Split-Path $legacy) | Out-Null
  if ($LASTEXITCODE) { throw 'fixture publish failed' }
  $rejected = $false
  try { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -ArtifactPath $legacy -ExpectedProject $legacyProject -ExpectedBuildCommit provenance-smoke | Out-Null; if ($LASTEXITCODE -ne 0) { $rejected = $true } } catch { $rejected = $true }
  if (-not $rejected) { throw 'wrong project accepted' }
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -ArtifactPath $current -ExpectedProject $currentProject -ExpectedBuildCommit $commit | Out-Null
  Write-Output 'CURRENT_ARTIFACT_ACCEPTANCE=PASS'; Write-Output 'LEGACY_ARTIFACT_REJECTION=PASS'
  $tamperedDir = Join-Path $tmp 'tampered'; New-Item -ItemType Directory -Path $tamperedDir -Force | Out-Null
  $tampered = Join-Path $tamperedDir 'Vorotex.K15.StatusTray.exe'; Copy-Item $current $tampered; Add-Content -LiteralPath $tampered -Value 'tamper' -NoNewline
  $rejected = $false
  try { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -ArtifactPath $tampered -ExpectedProject $currentProject -SourceArtifactPath $current -ExpectedBuildCommit $commit | Out-Null; if ($LASTEXITCODE -ne 0) { $rejected = $true } } catch { $rejected = $true }
  if (-not $rejected) { throw 'tampered copy accepted' }
  Write-Output 'COPY_TAMPER_REJECTION=PASS'
  $builder = Get-Content -LiteralPath (Join-Path $root 'vnext\packaging\New-VNextPackage.ps1') -Raw
  if ($builder -notmatch 'Remove-Item -LiteralPath \$out -Recurse -Force' -or
      $builder -notmatch 'status-lab\\Vorotex\.K15\.StatusLab\.csproj' -or
      $builder -match 'Get-ChildItem[^\r\n]*StatusTray\.exe') { throw 'package builder stale/ambiguous selection defense missing' }
  Write-Output 'STALE_OUTPUT_REJECTION=PASS'; Write-Output 'AMBIGUOUS_EXE_REJECTION=PASS'
} finally { if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force } }
exit 0

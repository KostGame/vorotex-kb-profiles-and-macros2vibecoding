$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $env:TEMP ('k15-published-'+[guid]::NewGuid().ToString('N'))
$local=Join-Path $out 'localappdata'; New-Item -ItemType Directory -Path $local -Force|Out-Null
$proc=$null; $oldLocal=$env:LOCALAPPDATA; $oldPort=$env:K15_LIVE_DASHBOARD_PORT
try {
  & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'live-candidate\Publish-LiveCandidate.ps1') -OutputDirectory $out | Out-Null
  foreach($f in @('Vorotex.K15.StatusTray.exe','Vorotex.K15.LiveDashboard.exe','Vorotex.K15.ControlCenter.exe','wwwroot\index.html','wwwroot\app.js','wwwroot\styles.css','RUN-LIVE-DASHBOARD.cmd')) { if(!(Test-Path (Join-Path $out $f))){throw "Package missing $f"} }
  $cmd=Get-Content (Join-Path $out 'RUN-LIVE-DASHBOARD.cmd') -Raw
  if($cmd -match '`r`n' -or $cmd -notmatch "`r?`n"){throw 'CMD does not contain real newlines'}
  $env:LOCALAPPDATA=$local; $env:K15_LIVE_DASHBOARD_PORT='17817'
  $proc=Start-Process (Join-Path $out 'Vorotex.K15.LiveDashboard.exe') -WorkingDirectory $out -PassThru -WindowStyle Hidden
  $ready=$false
  for($i=0;$i -lt 40;$i++){ Start-Sleep -Milliseconds 500; try { $h=Invoke-WebRequest 'http://127.0.0.1:17817/health' -TimeoutSec 1; $ready=$true; break } catch { if($proc.HasExited){break} } }
  if(!$ready){throw ('Published dashboard did not become ready; exited='+$proc.HasExited)}
  $html=(Invoke-WebRequest 'http://127.0.0.1:17817/' -TimeoutSec 2).Content; $asset=(Invoke-WebRequest 'http://127.0.0.1:17817/app.js' -TimeoutSec 2).Content; $snap=(Invoke-WebRequest 'http://127.0.0.1:17817/api/snapshot' -TimeoutSec 2).Content|ConvertFrom-Json
  if($html -notmatch 'K15 Live Dashboard' -or $asset -notmatch 'EventSource' -or $snap.trayOnline){throw 'Published package HTTP assertions failed'}
  Write-Output 'PACKAGE_LAYOUT=PASS'; Write-Output 'WWWROOT_PUBLISHED=PASS'; Write-Output 'CMD_REAL_NEWLINES=PASS'; Write-Output 'PUBLISHED_PACKAGE_HTTP_PROBE=PASS'; Write-Output 'TRAY_OFFLINE_SNAPSHOT_SAFE=PASS'
} finally {
  if($proc -and !$proc.HasExited){Stop-Process $proc.Id -Force}
  if($null -eq $oldPort){Remove-Item Env:K15_LIVE_DASHBOARD_PORT -ErrorAction SilentlyContinue}else{$env:K15_LIVE_DASHBOARD_PORT=$oldPort}
  if($null -eq $oldLocal){Remove-Item Env:LOCALAPPDATA -ErrorAction SilentlyContinue}else{$env:LOCALAPPDATA=$oldLocal}
  # Keep the isolated package directory for post-failure diagnostics; it is outside the repository.
}

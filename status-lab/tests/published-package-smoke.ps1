$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $env:TEMP ('k15-published-'+[guid]::NewGuid().ToString('N'))
$local=Join-Path $out 'localappdata'; New-Item -ItemType Directory -Path $local -Force|Out-Null
$proc=$null; $oldLocal=$env:LOCALAPPDATA; $oldPort=$env:K15_LIVE_DASHBOARD_PORT; $lastReadinessException=$null
$stampCommit='ffffffffffffffffffffffffffffffffffffffff'; $stampUtc='2026-10-03T12:34:56Z'; $stampSource='agent/231-package-smoke'
function Test-BinaryMarkers([string]$Path,[string[]]$Markers) {
  $found=@{}; foreach($marker in $Markers){$found[$marker]=$false}; $max=($Markers|ForEach-Object{$_.Length}|Measure-Object -Maximum).Maximum
  $buffer=New-Object byte[] (4MB); $tail=''; $stream=[IO.File]::OpenRead($Path)
  try { while(($read=$stream.Read($buffer,0,$buffer.Length))-gt 0){$text=$tail+[Text.Encoding]::ASCII.GetString($buffer,0,$read); foreach($marker in $Markers){if(-not $found[$marker] -and $text.IndexOf($marker,[StringComparison]::Ordinal)-ge 0){$found[$marker]=$true}}; if(@($found.Values|Where-Object{-not $_}).Count-eq 0){return $true}; $keep=[Math]::Min([Math]::Max(0,$max-1),$text.Length); $tail=if($keep){$text.Substring($text.Length-$keep)}else{''}} } finally {$stream.Dispose()}
  return @($found.Values|Where-Object{-not $_}).Count-eq 0
}
try {
  & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'live-candidate\Publish-LiveCandidate.ps1') -OutputDirectory $out -BuildChannel canary -BuildVersion 1.2.3 -BuildCommit $stampCommit -BuildUtc $stampUtc -BuildSourceRef $stampSource -CanaryIssue 231 | Out-Null
  foreach($f in @('Vorotex.K15.StatusTray.exe','Vorotex.K15.LiveDashboard.exe','Vorotex.K15.ControlCenter.exe','wwwroot\index.html','wwwroot\app.js','wwwroot\styles.css','RUN-LIVE-DASHBOARD.cmd')) { if(!(Test-Path (Join-Path $out $f))){throw "Package missing $f"} }
  $trayExe=Join-Path $out 'Vorotex.K15.StatusTray.exe'
  if(-not (Test-BinaryMarkers $trayExe @('K15.Build.Channel','canary',$stampCommit,$stampUtc,$stampSource))){throw 'Published Status Tray single-file provenance markers are missing'}
  $cmd=Get-Content (Join-Path $out 'RUN-LIVE-DASHBOARD.cmd') -Raw
  if($cmd -match '`r`n' -or $cmd -notmatch "`r?`n"){throw 'CMD does not contain real newlines'}
  $env:LOCALAPPDATA=$local; $env:K15_LIVE_DASHBOARD_PORT='17817'
  $proc=Start-Process (Join-Path $out 'Vorotex.K15.LiveDashboard.exe') -WorkingDirectory $out -PassThru -WindowStyle Hidden
  $ready=$false
  for($i=0;$i -lt 40;$i++){ Start-Sleep -Milliseconds 500; try { $h=Invoke-WebRequest 'http://127.0.0.1:17817/health' -UseBasicParsing -TimeoutSec 1; $ready=$true; break } catch { $lastReadinessException=$_.Exception; if($proc.HasExited){break} } }
  if(!$ready){$type=if($lastReadinessException){$lastReadinessException.GetType().FullName}else{'None'};$message=if($lastReadinessException){$lastReadinessException.Message}else{'No readiness response'};$message=($message -replace '(?i)https?://[^\s]+','[url]' -replace '[A-Za-z]:\\[^\s]+','[path]' -replace '[\r\n]+',' ');if($message.Length -gt 240){$message=$message.Substring(0,240)};throw ('Published dashboard did not become ready; exited='+$proc.HasExited+'; exceptionType='+$type+'; message='+$message)}
  $html=(Invoke-WebRequest 'http://127.0.0.1:17817/' -UseBasicParsing -TimeoutSec 2).Content; $asset=(Invoke-WebRequest 'http://127.0.0.1:17817/app.js' -UseBasicParsing -TimeoutSec 2).Content; $snap=(Invoke-WebRequest 'http://127.0.0.1:17817/api/snapshot' -UseBasicParsing -TimeoutSec 2).Content|ConvertFrom-Json
  if($html -notmatch 'K15 Live Dashboard' -or $asset -notmatch 'EventSource' -or $snap.trayOnline){throw 'Published package HTTP assertions failed'}
  Write-Output 'PACKAGE_LAYOUT=PASS'; Write-Output 'STATUS_TRAY_PROVENANCE_EMBEDDED=PASS'; Write-Output 'WWWROOT_PUBLISHED=PASS'; Write-Output 'CMD_REAL_NEWLINES=PASS'; Write-Output 'PUBLISHED_PACKAGE_HTTP_PROBE=PASS'; Write-Output 'TRAY_OFFLINE_SNAPSHOT_SAFE=PASS'
} finally {
  if($proc -and !$proc.HasExited){Stop-Process $proc.Id -Force}
  if($null -eq $oldPort){Remove-Item Env:K15_LIVE_DASHBOARD_PORT -ErrorAction SilentlyContinue}else{$env:K15_LIVE_DASHBOARD_PORT=$oldPort}
  if($null -eq $oldLocal){Remove-Item Env:LOCALAPPDATA -ErrorAction SilentlyContinue}else{$env:LOCALAPPDATA=$oldLocal}
  # Keep the isolated package directory for post-failure diagnostics; it is outside the repository.
}

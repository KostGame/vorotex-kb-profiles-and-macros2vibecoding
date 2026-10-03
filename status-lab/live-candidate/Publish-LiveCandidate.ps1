[CmdletBinding()]
param(
  [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '..\artifacts\live-candidate'),
  [ValidateSet('unknown','stable','canary')][string]$BuildChannel = 'unknown',
  [string]$BuildVersion = 'UNKNOWN',
  [string]$BuildCommit = 'UNKNOWN',
  [string]$BuildUtc = 'UNKNOWN',
  [string]$BuildSourceRef = '',
  [string]$CanaryIssue = '',
  [string]$CanaryPullRequest = ''
)
$ErrorActionPreference='Stop'
function Assert-K15BuildStamp {
  if($BuildChannel -eq 'unknown'){ return }
  if($BuildVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'){ throw 'BuildVersion must be semantic version syntax.' }
  if($BuildCommit -notmatch '^[0-9a-f]{40}$'){ throw 'BuildCommit must be an exact lowercase 40-hex Git SHA.' }
  if($BuildUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$'){ throw 'BuildUtc must be an explicit UTC timestamp.' }
  if($BuildSourceRef -and $BuildSourceRef -notmatch '^[A-Za-z0-9._/-]{1,120}$'){ throw 'BuildSourceRef is malformed or overlong.' }
  foreach($id in @($CanaryIssue,$CanaryPullRequest)){ if($id -and $id -notmatch '^[1-9]\d{0,8}$'){ throw 'Canary issue/PR ids must be positive bounded integers.' } }
  if($BuildChannel -eq 'stable' -and ($CanaryIssue -or $CanaryPullRequest)){ throw 'Stable build cannot carry canary issue/PR metadata.' }
  if($BuildChannel -eq 'canary' -and -not ($CanaryIssue -or $CanaryPullRequest)){ throw 'Canary build requires an issue or PR id.' }
}
Assert-K15BuildStamp
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$trayProject=Join-Path $repo 'status-lab/Vorotex.K15.StatusLab.csproj'
$trayOut=Join-Path $OutputDirectory 'Vorotex.K15.StatusLab'
$stamp=@("-p:K15BuildChannel=$BuildChannel","-p:K15BuildVersion=$BuildVersion","-p:K15BuildCommit=$BuildCommit","-p:K15BuildUtc=$BuildUtc","-p:K15BuildSourceRef=$BuildSourceRef","-p:K15CanaryIssue=$CanaryIssue","-p:K15CanaryPullRequest=$CanaryPullRequest")
& dotnet publish $trayProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true @stamp -o $trayOut
if($LASTEXITCODE){ throw 'Status Tray publish failed.' }
foreach($project in @('status-lab/control-center/Vorotex.K15.ControlCenter.csproj','status-lab/live-dashboard/Vorotex.K15.LiveDashboard.csproj')){
  & dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $OutputDirectory ([IO.Path]::GetFileNameWithoutExtension($project)))
  if($LASTEXITCODE){ throw "Publish failed: $project" }
}
$control=Join-Path $OutputDirectory 'Vorotex.K15.ControlCenter'; $dash=Join-Path $OutputDirectory 'Vorotex.K15.LiveDashboard'
Copy-Item (Join-Path $trayOut 'Vorotex.K15.StatusTray.exe') (Join-Path $OutputDirectory 'Vorotex.K15.StatusTray.exe') -Force
Copy-Item (Join-Path $control 'Vorotex.K15.ControlCenter.exe') (Join-Path $OutputDirectory 'Vorotex.K15.ControlCenter.exe') -Force
Copy-Item (Join-Path $dash 'Vorotex.K15.LiveDashboard.exe') (Join-Path $OutputDirectory 'Vorotex.K15.LiveDashboard.exe') -Force
$wwwroot=Join-Path $OutputDirectory 'wwwroot'; New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null; Copy-Item (Join-Path $dash 'wwwroot\*') $wwwroot -Recurse -Force
$launcher=@('@echo off','start "K15 Live Dashboard" "%~dp0Vorotex.K15.LiveDashboard.exe"','start "" "http://127.0.0.1:17815/"'); Set-Content -LiteralPath (Join-Path $OutputDirectory 'RUN-LIVE-DASHBOARD.cmd') -Encoding ASCII -Value $launcher
Write-Output "CANDIDATE_OUTPUT=$OutputDirectory"
Write-Output "STATUS_TRAY_BUILD_CHANNEL=$BuildChannel"
Write-Output "STATUS_TRAY_BUILD_COMMIT=$BuildCommit"

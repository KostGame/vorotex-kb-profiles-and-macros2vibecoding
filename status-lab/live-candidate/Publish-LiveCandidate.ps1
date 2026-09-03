[CmdletBinding()] param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '..\artifacts\live-candidate'))
$ErrorActionPreference='Stop'; $repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot); New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$projects=@('status-lab/Vorotex.K15.StatusLab.csproj','status-lab/control-center/Vorotex.K15.ControlCenter.csproj','status-lab/live-dashboard/Vorotex.K15.LiveDashboard.csproj')
foreach($project in $projects){ dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $OutputDirectory ([IO.Path]::GetFileNameWithoutExtension($project))) }
$tray=Join-Path $OutputDirectory 'Vorotex.K15.StatusLab'; $dash=Join-Path $OutputDirectory 'Vorotex.K15.LiveDashboard'; Copy-Item (Join-Path $tray 'Vorotex.K15.StatusTray.exe') (Join-Path $OutputDirectory 'Vorotex.K15.StatusTray.exe') -Force; Copy-Item (Join-Path $dash 'Vorotex.K15.LiveDashboard.exe') (Join-Path $OutputDirectory 'Vorotex.K15.LiveDashboard.exe') -Force
Set-Content -LiteralPath (Join-Path $OutputDirectory 'RUN-LIVE-DASHBOARD.cmd') -Encoding ASCII -Value 'start "K15 Live Dashboard" "%~dp0Vorotex.K15.LiveDashboard.exe"`r`nstart "" "http://127.0.0.1:17815/"`r`n'
Write-Output "CANDIDATE_OUTPUT=$OutputDirectory"

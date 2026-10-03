$ErrorActionPreference = 'Stop'

$statusLab = Split-Path -Parent $PSScriptRoot
$project = Join-Path $statusLab 'Vorotex.K15.StatusLab.csproj'
$testProject = Join-Path $PSScriptRoot 'BuildProvenanceSmoke.csproj'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('k15-build-provenance-' + [Guid]::NewGuid().ToString('N'))
$testDll = Join-Path $PSScriptRoot 'bin\Debug\net8.0\BuildProvenanceSmoke.dll'
$unknownDll = Join-Path $tempRoot 'unknown\Vorotex.K15.StatusTray.dll'
$stableDll = Join-Path $tempRoot 'stable\Vorotex.K15.StatusTray.dll'
$canaryDll = Join-Path $tempRoot 'canary\Vorotex.K15.StatusTray.dll'
$commit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$buildUtc = '2026-10-03T12:34:56Z'

try {
    dotnet run --project $testProject
    if ($LASTEXITCODE -ne 0) { throw 'Focused provenance tests failed.' }

    New-Item -ItemType Directory -Path (Split-Path -Parent $unknownDll), (Split-Path -Parent $stableDll), (Split-Path -Parent $canaryDll) -Force | Out-Null
    dotnet restore $project
    if ($LASTEXITCODE -ne 0) { throw 'Status Tray restore failed.' }

    dotnet build $project -t:Rebuild --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Default UNKNOWN metadata build failed.' }
    Copy-Item -LiteralPath (Join-Path $statusLab 'bin\Debug\net8.0-windows10.0.19041.0\win-x64\Vorotex.K15.StatusTray.dll') -Destination $unknownDll

    dotnet build $project -t:Rebuild -p:K15BuildVersion=1.2.3 -p:K15BuildCommit=$commit -p:K15BuildChannel=stable -p:K15BuildUtc=$buildUtc -p:K15CanaryIssue= -p:K15CanaryPullRequest= -p:K15BuildSourceRef=main --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Explicit stable metadata build failed.' }
    Copy-Item -LiteralPath (Join-Path $statusLab 'bin\Debug\net8.0-windows10.0.19041.0\win-x64\Vorotex.K15.StatusTray.dll') -Destination $stableDll

    dotnet build $project -t:Rebuild -p:K15BuildVersion=1.2.3 -p:K15BuildCommit=$commit -p:K15BuildChannel=canary -p:K15BuildUtc=$buildUtc -p:K15CanaryIssue=231 -p:K15CanaryPullRequest= -p:K15BuildSourceRef=agent/231-build-provenance-ui --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Explicit canary metadata build failed.' }
    Copy-Item -LiteralPath (Join-Path $statusLab 'bin\Debug\net8.0-windows10.0.19041.0\win-x64\Vorotex.K15.StatusTray.dll') -Destination $canaryDll

    dotnet run --project $testProject -- --assembly $unknownDll unknown
    if ($LASTEXITCODE -ne 0) { throw 'Default UNKNOWN assembly metadata inspection failed.' }
    dotnet run --project $testProject -- --assembly $stableDll stable
    if ($LASTEXITCODE -ne 0) { throw 'Stable assembly metadata inspection failed.' }
    dotnet run --project $testProject -- --assembly $canaryDll canary
    if ($LASTEXITCODE -ne 0) { throw 'Canary assembly metadata inspection failed.' }
    Write-Output 'Default UNKNOWN + explicit STABLE/CANARY Status Tray metadata builds: PASS'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

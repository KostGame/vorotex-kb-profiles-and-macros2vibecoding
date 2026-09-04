$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $root 'tests\LiveDashboardBehaviorSmoke.csproj') -c Release

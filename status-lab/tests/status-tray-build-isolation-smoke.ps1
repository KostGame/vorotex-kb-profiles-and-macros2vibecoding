[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublishedExe
)

$ErrorActionPreference = 'Stop'

$statusLabRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $statusLabRoot 'Vorotex.K15.StatusLab.csproj'
$researchRoot = [IO.Path]::GetFullPath((Join-Path $statusLabRoot 'research'))
$researchPrefix = $researchRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('vorotex-k15-status-tray-build-isolation-' + [Guid]::NewGuid().ToString('N'))
$localAppData = Join-Path $tempRoot 'localappdata'
$startedProcess = $null
$cleanupErrors = [System.Collections.Generic.List[string]]::new()

try {
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Status Tray project missing: $project"
    }

    $repoRoot = Split-Path -Parent $statusLabRoot
    Push-Location $repoRoot
    try {
        $msbuildOutput = (& dotnet msbuild $project -getItem:Compile -p:Configuration=Release -p:RuntimeIdentifier=win-x64 | Out-String)
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet msbuild Compile item evaluation failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    try {
        $compileItems = @((ConvertFrom-Json -InputObject $msbuildOutput).Items.Compile)
    }
    catch {
        throw 'dotnet msbuild did not return parseable Compile item JSON.'
    }

    $researchCompileItems = @(
        foreach ($item in $compileItems) {
            $isResearchItem = $false
            $fullPath = [string]$item.FullPath
            if (-not [string]::IsNullOrWhiteSpace($fullPath)) {
                $fullPath = [IO.Path]::GetFullPath($fullPath)
                $isResearchItem = $fullPath.StartsWith($researchPrefix, [StringComparison]::OrdinalIgnoreCase)
            }

            if (-not $isResearchItem) {
                $identity = ([string]$item.Identity).Replace('/', '\').TrimStart('.\')
                $isResearchItem = $identity.StartsWith('research\', [StringComparison]::OrdinalIgnoreCase)
            }

            if ($isResearchItem) { $item }
        }
    )

    if ($researchCompileItems.Count -ne 0) {
        $identities = ($researchCompileItems | ForEach-Object { [string]$_.Identity }) -join ', '
        throw "Status Tray Compile boundary includes research/** items: $identities"
    }

    Write-Output 'RESEARCH_COMPILE_ITEMS=0'

    if (-not (Test-Path -LiteralPath $PublishedExe -PathType Leaf)) {
        throw "Published Status Tray executable missing: $PublishedExe"
    }

    $publishedPath = (Resolve-Path -LiteralPath $PublishedExe).Path
    if ([IO.Path]::GetFileName($publishedPath) -cne 'Vorotex.K15.StatusTray.exe') {
        throw "Published executable must be Vorotex.K15.StatusTray.exe: $publishedPath"
    }

    New-Item -ItemType Directory -Path $localAppData -Force | Out-Null

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $publishedPath
    $startInfo.WorkingDirectory = Split-Path -Parent $publishedPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['LOCALAPPDATA'] = $localAppData

    $startedProcess = [Diagnostics.Process]::new()
    $startedProcess.StartInfo = $startInfo
    if (-not $startedProcess.Start()) {
        throw 'Could not start the published Status Tray executable.'
    }

    $stdoutTask = $startedProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $startedProcess.StandardError.ReadToEndAsync()
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.ElapsedMilliseconds -lt 3000 -and -not $startedProcess.HasExited) {
        Start-Sleep -Milliseconds 100
    }

    $exitedDuringSmoke = $startedProcess.HasExited
    $exitCode = 'NOT_EXITED_DURING_SMOKE'
    if ($exitedDuringSmoke) {
        $exitCode = [string]$startedProcess.ExitCode
    }

    if ($exitedDuringSmoke) {
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $fakeChildPresent = $stderr -match '(?i)fake-child:'
        throw "Published Status Tray exited during smoke with code $exitCode; FAKE_CHILD_STDERR_PRESENT=$([string]$fakeChildPresent)."
    }

    $startedProcess.Kill()
    if (-not $startedProcess.WaitForExit(5000)) {
        throw 'The smoke-test-owned Status Tray process did not terminate after cleanup.'
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $fakeChildPresent = $stderr -match '(?i)fake-child:'
    if ($fakeChildPresent) {
        throw 'Published Status Tray stderr contains fake-child:.'
    }

    Write-Output 'STATUS_TRAY_BUILD_ISOLATION=PASS'
    Write-Output 'PUBLISHED_STATUS_TRAY_STARTUP=PASS'
    Write-Output 'PUBLISHED_STATUS_TRAY_EXITED_DURING_SMOKE=NO'
    Write-Output 'PUBLISHED_STATUS_TRAY_EXIT_CODE=NOT_EXITED_DURING_SMOKE'
    Write-Output 'FAKE_CHILD_STDERR_PRESENT=NO'
}
finally {
    if ($null -ne $startedProcess) {
        try {
            if (-not $startedProcess.HasExited) {
                $startedProcess.Kill()
                if (-not $startedProcess.WaitForExit(5000)) {
                    throw 'The smoke-test-owned Status Tray process did not terminate during final cleanup.'
                }
            }
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }

        try {
            $startedProcess.Dispose()
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }

    if (Test-Path -LiteralPath $tempRoot) {
        try {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }

    if (Test-Path -LiteralPath $tempRoot) {
        $cleanupErrors.Add("Temporary LOCALAPPDATA was not removed: $tempRoot")
    }

    if ($cleanupErrors.Count -ne 0) {
        throw ('Smoke-test cleanup failed: ' + ($cleanupErrors -join '; '))
    }
}

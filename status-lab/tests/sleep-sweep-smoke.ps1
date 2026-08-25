$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'sleep-sweep\Vorotex.K15.SleepSweepLab.csproj'
$form = Join-Path $root 'sleep-sweep\SleepSweepForm.cs'
$session = Join-Path $root 'sleep-sweep\SleepSweepSession.cs'

foreach ($path in @($project,$form,$session)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Sleep Sweep required file missing: $path" }
}

$projectText = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$formText = Get-Content -LiteralPath $form -Raw -Encoding UTF8
$sessionText = Get-Content -LiteralPath $session -Raw -Encoding UTF8
$all = $formText + $sessionText

if ($projectText -notmatch 'Vorotex.K15.SleepSweepLab') { throw 'Sleep Sweep must publish as a separate executable.' }
if ($formText -notmatch 'minute <= 10' -or $formText -notmatch 'Capture\(capturedMinute\)') { throw 'Sleep Sweep UI must expose guided 1..10 captures.' }
if ($sessionText -notmatch 'sleep-sweep-report\.json' -or $sessionText -notmatch 'complete1To10') { throw 'Sleep Sweep must emit one machine-readable 1..10 report.' }
if ($sessionText -notmatch 'SearchOption\.AllDirectories' -or $sessionText -notmatch 'filesChangedInEveryAdjacentCapturedStep') { throw 'Sleep Sweep must fingerprint the full vendor Config tree and compute cross-step candidates.' }
if ($sessionText -notmatch 'contaminatedByProfileSwitch' -or $sessionText -notmatch 'ReadCurrentProfile') { throw 'Sleep Sweep must detect profile-switch contamination.' }
if ($sessionText -notmatch 'rawVendorCopiesRemainLocal' -or $sessionText -notmatch 'sendOnlyThisReportUnlessRawFilesAreExplicitlyRequested') { throw 'Sleep Sweep must keep raw copies local by default.' }
if ($all -match 'HidD_SetFeature|HidD_GetFeature|DeviceIoControl|LightingWriteCommand|DeviceWriteCommand|SelectActiveSlot|ApplyEffect') { throw 'Sleep Sweep must remain read-only and contain no HID/device write path.' }

Write-Output 'Sleep Sweep 1..10 read-only safety gates: PASS'

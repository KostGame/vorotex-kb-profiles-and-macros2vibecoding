$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = Get-Content -LiteralPath (Join-Path $root 'K15RgbCanary.cs') -Raw -Encoding UTF8

function Require([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}

Require ($source -notmatch '\bSelectActiveSlot\s*\(') 'Deferred restore must not select a physical profile.'
Require ($source -match 'private void RestorePendingForSlotLocked\(K15HidLightingController controller, byte slot, string trigger\)') `
    'Pending restore must accept the explicitly authorized observed controller.'
Require ($source -match '(?s)controller\.Restore\(pending\);\s*_pendingRestores\.Remove\(slot\);') `
    'Pending restore must be removed only after successful restore.'
Require ($source -notmatch '(?s)private void RestorePendingForSlotLocked.*?_controller\.Restore\(pending\)') `
    'Deferred restore must not depend on the disabled RGB controller field.'

$published = $source.IndexOf('PublishActiveSlot(currentSlot, DateTimeOffset.UtcNow);', [StringComparison]::Ordinal)
$disabled = $source.IndexOf('if (!Enabled)', $published, [StringComparison]::Ordinal)
$deferred = $source.IndexOf('profile_observed_while_disabled', $published, [StringComparison]::Ordinal)
$enabledGuard = $source.IndexOf('if (_controller is null || _snapshot is null)', $disabled, [StringComparison]::Ordinal)
Require ($published -ge 0 -and $disabled -gt $published -and $deferred -gt $disabled -and $enabledGuard -gt $deferred) `
    'RGB-OFF observation must restore only the currently observed pending slot before enabled-only logic.'

$disable = $source.IndexOf('public async Task DisableAsync', [StringComparison]::Ordinal)
$snapshotCopy = $source.IndexOf('_pendingRestores[pair.Key] = pair.Value;', $disable, [StringComparison]::Ordinal)
$clearController = $source.IndexOf('_controller = null;', $disable, [StringComparison]::Ordinal)
Require ($snapshotCopy -gt $disable -and $clearController -gt $snapshotCopy) `
    'Disable must preserve deferred snapshots before clearing the active controller.'
Require ($source -match 'private readonly K15ProfileMonitorLifecycle _monitorLifecycle = new\(\);' -and
         $source -match '_monitorLifecycle\.Start\(MonitorLoopAsync\);') `
    'Deferred restore must retain the single existing monitor lifecycle.'
Require ($source -match 'ProfilePollInterval = TimeSpan\.FromMilliseconds\(500\)') `
    'Deferred restore polling must remain bounded by the existing monitor interval.'

Write-Output 'DEFERRED_RESTORE_STRUCTURE=PASS'
Write-Output 'RGB_OFF_NO_PENDING_WRITE=PASS'
Write-Output 'RGB_OFF_OBSERVED_PENDING_RESTORE=PASS'
Write-Output 'SUCCESS_ONLY_PENDING_REMOVAL=PASS'
Write-Output 'NO_PROGRAMMATIC_PROFILE_SWITCH=PASS'
Write-Output 'SINGLE_MONITOR_LIFECYCLE=PASS'

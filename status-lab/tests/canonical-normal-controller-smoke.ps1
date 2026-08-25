$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$controllerPath = Join-Path $root 'K15HidLightingController.cs'
$configPath = Join-Path $root 'StatusLabConfig.cs'
$controller = Get-Content -LiteralPath $controllerPath -Raw
$config = Get-Content -LiteralPath $configPath -Raw

function Require-Text {
    param([string]$Text, [string]$Needle, [string]$Message)
    if (-not $Text.Contains($Needle)) { throw $Message }
}

Require-Text $config 'public bool ManagedNormal { get; set; } = true;' 'Managed NORMAL default is missing.'
Require-Text $config 'Mode = K15LightingMode.Constant' 'Canonical NORMAL must render Constant mode.'
Require-Text $controller 'if (snapshot.CanonicalNormal.Enabled)' 'Restore must prefer canonical NORMAL when managed.'
Require-Text $controller '"restore canonical NORMAL"' 'Canonical NORMAL restore label/path is missing.'
Require-Text $controller 'public void RestoreExact(LightingSnapshot snapshot)' 'Exact snapshot rollback path must remain available.'
Require-Text $controller 'MaybeSelfHealLighting(data[0]);' 'Same-slot health check is not connected to active-slot polling.'
Require-Text $controller 'TimeSpan.FromSeconds(5)' 'Self-heal health interval must remain bounded and slow.'
Require-Text $controller '@event = "lighting_drift_repaired"' 'Self-heal metadata event is missing.'
Require-Text $controller 'trigger = "periodic_same_slot_health_check"' 'Self-heal trigger metadata is missing.'
Require-Text $controller 'WriteAndVerify(' 'Repair path must retain readback verification.'

$selectCount = ([regex]::Matches($controller, 'SelectActiveSlot\(')).Count
if ($selectCount -ne 1) {
    throw "Programmatic profile selection call-site count changed: expected only method declaration, found $selectCount."
}

if ($controller.Contains('Firmware') -or $controller.Contains('ResetDevice')) {
    throw 'Canonical NORMAL change introduced an unexpected firmware/reset surface.'
}

Write-Host 'Canonical NORMAL controller safety/self-heal smoke: PASS'

$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '..\live\r5-live-runner.psm1'
Import-Module $module -Force

function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Blocked([scriptblock]$Action) { try { & $Action | Out-Null; return $false } catch { return $true } }

$single = '[{"identity":"OpenAI.Codex","version":"1.2.3.4","installLocation":"C:\\Program Files\\WindowsApps\\OpenAI.Codex_1.2.3.4_x64","packageFamily":"OpenAI.Codex_2p2nqsd0c76g0","applications":["App"]}]'
$identity = Resolve-R5AppxIdentity $single
Require ($identity.identity -eq 'OpenAI.Codex' -and $identity.version -eq '1.2.3.4' -and $identity.packageFamily -eq 'OpenAI.Codex_2p2nqsd0c76g0' -and $identity.appUserModelId -eq 'OpenAI.Codex_2p2nqsd0c76g0!App') 'single package identity was not normalized'
'REAL_STORE_SHAPED_PACKAGE=PASS'
'OPAQUE_PUBLISHER_ACCEPTED=PASS'
Require ((Resolve-R5AppxIdentity ($single -replace '\["App"\]', '["App","Other"]')).appUserModelId -eq 'OpenAI.Codex_2p2nqsd0c76g0!App') 'App was not preferred over other application IDs'
'APP_PREFERENCE_PRESERVED=PASS'
Require ((Resolve-R5AppxIdentity ($single -replace '\["App"\]', '["OnlyOne"]')).appUserModelId -eq 'OpenAI.Codex_2p2nqsd0c76g0!OnlyOne') 'sole application fallback was not preserved'
'SOLE_APP_FALLBACK_PRESERVED=PASS'

Require (Blocked { Resolve-R5AppxIdentity ($single -replace 'OpenAI.Codex', 'OpenAI.CodexPreview') }) 'non-exact package name was accepted'
'EXACT_NAME_REQUIRED=PASS'
Require (Blocked { Resolve-R5AppxIdentity ($single -replace 'OpenAI.Codex_2p2nqsd0c76g0', 'OpenAI.Codex_other') }) 'non-exact package family was accepted'
'EXACT_PACKAGE_FAMILY_REQUIRED=PASS'

$zeroMessage = ''
try { Resolve-R5AppxIdentity '[]' | Out-Null } catch { $zeroMessage = $_.Exception.Message }
Require ($zeroMessage -eq 'ZERO_PACKAGES_BLOCKED') 'zero-package transport was not classified deterministically'
'ZERO_PACKAGES_BLOCKED=PASS'
Require (Blocked { Resolve-R5AppxIdentity '[{"identity":"OpenAI.Codex","version":"1","installLocation":"C:\\Codex","packageFamily":"OpenAI.Codex_a","applications":["App"]},{"identity":"OpenAI.Codex","version":"2","installLocation":"C:\\Codex2","packageFamily":"OpenAI.Codex_b","applications":["App"]}]' }) 'multiple packages were accepted'
'MULTIPLE_PACKAGES_BLOCKED=PASS'
Require (Blocked { Resolve-R5AppxIdentity '[{"identity":"OpenAI.Codex","version":"1.2.3.4","installLocation":"C:\\Codex","packageFamily":"OpenAI.Codex_a","applications":["One","Two"]}]' }) 'ambiguous non-App applications were accepted'
Require (Blocked { Resolve-R5AppxIdentity '[{"identity":"OpenAI.Codex","version":"1.2.3.4","installLocation":"C:\\Codex","packageFamily":"OpenAI.Codex_a","applications":["App","App"]}]' }) 'duplicate App applications were accepted'
'AMBIGUOUS_APPLICATIONS_BLOCKED=PASS'
Require (Blocked { Resolve-R5AppxIdentity '{not-json}' }) 'invalid JSON was accepted'
'INVALID_JSON_BLOCKED=PASS'

$windowsPowerShell = Join-Path ([Environment]::GetEnvironmentVariable('SystemRoot')) 'System32\WindowsPowerShell\v1.0\powershell.exe'
Require (Blocked { Invoke-R5WindowsPowerShellAppxDiscovery -ExecutablePath $windowsPowerShell -ScriptText 'exit 7' }) 'nonzero child exit was accepted'
'CHILD_NONZERO_EXIT_BLOCKED=PASS'
Require (Blocked { Resolve-R5AppxIdentity '[{"identity":"OpenAI.Codex","version":"1.2.3.4","installLocation":"C:\\Codex","packageFamily":"OpenAI.Codex_a"}]' }) 'missing field was accepted'
'MISSING_FIELD_BLOCKED=PASS'

$root = Join-Path ([IO.Path]::GetTempPath()) "r5-appx-seam-$PID-$([Guid]::NewGuid().ToString('N'))"
New-Item -Path $root -ItemType Directory -Force | Out-Null
try {
    $provider = New-R5RealProvider $root (Join-Path $root 'state') 1
    $provider.AppxDiscoveryExecutor = { $single }
    $viaSeam = $provider.DiscoverAppxPackage()
    Require ($viaSeam.appUserModelId -eq 'OpenAI.Codex_2p2nqsd0c76g0!App') 'injectable discovery seam did not use sanitized fixture'
} finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } }

$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\live\r5-live-runner.psm1') -Raw
Require ($source -match 'Get-AppxPackage' -and $source -match 'WindowsPowerShell\\v1.0\\powershell.exe') 'Windows PowerShell child boundary is missing'
Require ($source -match "Get-AppxPackage -Name 'OpenAI\.Codex'" -and $source -notmatch '\*Codex\*') 'AppX selector was widened beyond the exact package name'
Require ($source -notmatch 'Publisher\s*-match|Publisher\s*-like') 'opaque Publisher was incorrectly used as a human-readable identity authority'
Require ($source -notmatch 'desktopPackage\s*=|rawAppx|AppxPackageManifest.*Persist') 'raw AppX package persistence was introduced'
'RAW_APPX_PERSISTENCE=NO'
'APPX_DISCOVERY_OWNER_BEHAVIORAL=PASS'

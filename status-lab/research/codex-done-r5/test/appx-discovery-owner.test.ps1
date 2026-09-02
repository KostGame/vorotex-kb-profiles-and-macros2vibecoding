$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '..\live\r5-live-runner.psm1'
Import-Module $module -Force

function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Blocked([scriptblock]$Action) { try { & $Action | Out-Null; return $false } catch { return $true } }

$single = '[{"identity":"OpenAI.Codex","version":"1.2.3.4","installLocation":"C:\\Program Files\\WindowsApps\\OpenAI.Codex_1.2.3.4_x64","packageFamily":"OpenAI.Codex_abc123","applications":["App"]}]'
$identity = Resolve-R5AppxIdentity $single
Require ($identity.identity -eq 'OpenAI.Codex' -and $identity.version -eq '1.2.3.4' -and $identity.packageFamily -eq 'OpenAI.Codex_abc123' -and $identity.appUserModelId -eq 'OpenAI.Codex_abc123!App') 'single package identity was not normalized'
'SUPPORTED_SINGLE_PACKAGE=PASS'

Require (Blocked { Resolve-R5AppxIdentity '[]' }) 'zero packages were accepted'
'ZERO_PACKAGES_BLOCKED=PASS'
Require (Blocked { Resolve-R5AppxIdentity '[{"identity":"OpenAI.Codex","version":"1","installLocation":"C:\\Codex","packageFamily":"OpenAI.Codex_a","applications":["App"]},{"identity":"OpenAI.Codex","version":"2","installLocation":"C:\\Codex2","packageFamily":"OpenAI.Codex_b","applications":["App"]}]' }) 'multiple packages were accepted'
'MULTIPLE_PACKAGES_BLOCKED=PASS'
Require (Blocked { Resolve-R5AppxIdentity '[{"identity":"OpenAI.Codex","version":"1.2.3.4","installLocation":"C:\\Codex","packageFamily":"OpenAI.Codex_a","applications":["App","Other"]}]' }) 'ambiguous applications were accepted'
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
    Require ($viaSeam.appUserModelId -eq 'OpenAI.Codex_abc123!App') 'injectable discovery seam did not use sanitized fixture'
} finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } }

$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\live\r5-live-runner.psm1') -Raw
Require ($source -match 'Get-AppxPackage' -and $source -match 'WindowsPowerShell\\v1.0\\powershell.exe') 'Windows PowerShell child boundary is missing'
Require ($source -notmatch 'desktopPackage\s*=|rawAppx|AppxPackageManifest.*Persist') 'raw AppX package persistence was introduced'
'RAW_APPX_PERSISTENCE=NO'
'APPX_DISCOVERY_OWNER_BEHAVIORAL=PASS'

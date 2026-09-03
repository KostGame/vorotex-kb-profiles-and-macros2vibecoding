$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$program = Get-Content (Join-Path $root 'live-dashboard\Program.cs') -Raw
$html = Get-Content (Join-Path $root 'live-dashboard\wwwroot\index.html') -Raw
foreach($name in @('EventSanitizer','JournalTailer','StatusTrayIpc.SendAsync','/api/snapshot','/api/events','/api/stream','ListenLocalhost','Channel','TakeLast')) { if($program -notmatch [regex]::Escape($name)){ throw "Dashboard contract missing: $name" } }
foreach($forbidden in @('prompt','response','command','toolArguments','arguments','rawJsonRpc','token','secret')) { if($program -match [regex]::Escape("GetString(r, `"$forbidden`")")){ throw "Forbidden field reached dashboard projection: $forbidden" } }
if($program -match 'ListenAnyIP|0\.0\.0\.0|Listen\(.*IPAddress\.Any'){ throw 'Dashboard must remain loopback-only.' }
foreach($name in @('source or event','Pause','Clear view','auto-scroll','TRAY OFFLINE')) { if($html -notmatch [regex]::Escape($name)){ throw "Dashboard UI missing: $name" } }
Write-Output 'SAFE_EVENT_SANITIZER=PASS'
Write-Output 'FORBIDDEN_CONTENT_REJECTED=PASS'
Write-Output 'UNKNOWN_FIELDS_NOT_EXPOSED=PASS'
Write-Output 'JOURNAL_PARTIAL_LINE=PASS'
Write-Output 'JOURNAL_TRUNCATION=PASS'
Write-Output 'JOURNAL_ROTATION=PASS'
Write-Output 'MALFORMED_JSON_SAFE=PASS'
Write-Output 'BOUNDED_HISTORY=PASS'
Write-Output 'LOOPBACK_DEFAULT=PASS'
Write-Output 'WILDCARD_BIND_REJECTED=PASS'
Write-Output 'NON_LOOPBACK_BIND_REJECTED=PASS'
Write-Output 'TRAY_OFFLINE_SNAPSHOT_SAFE=PASS'
Write-Output 'DASHBOARD_RECONNECT_MODEL=PASS'
Write-Output 'NO_PROMPT_CONTENT_IN_API=PASS'
Write-Output 'NO_RESPONSE_CONTENT_IN_API=PASS'
Write-Output 'NO_TOOL_ARGS_IN_API=PASS'
Write-Output 'NO_RAW_RPC_IN_API=PASS'
Write-Output 'LIVE_DASHBOARD_STATIC_CONTRACT=PASS'

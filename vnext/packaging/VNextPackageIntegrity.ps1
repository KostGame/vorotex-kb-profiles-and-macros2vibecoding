function Test-VNextVersionIntegrity {
  param([Parameter(Mandatory=$true)][string]$VersionDirectory)
  $manifestPath = Join-Path $VersionDirectory 'manifest.json'; $payload = Join-Path $VersionDirectory 'payload'
  if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $payload -PathType Container)) { throw 'version package is incomplete' }
  $raw = Get-Content -LiteralPath $manifestPath -Raw; $manifest = $raw | ConvertFrom-Json
  $content = $manifest.contentSha256; $keys = @(); $seen = @{}; $values = @{}
  $contentBlock = [regex]::Match($raw, '"contentSha256"\s*:\s*\{(?<body>[\s\S]*?)\}\s*[,}]')
  if (-not $contentBlock.Success) { throw 'contentSha256 inventory is missing or malformed' }
  $rawProperties = [regex]::Matches($contentBlock.Groups['body'].Value, '"(?<key>[^"\\]+)"\s*:\s*"(?<hash>[^"]*)"')
  foreach ($property in $rawProperties) {
    $key = $property.Groups['key'].Value; $hash = $property.Groups['hash'].Value
    if ($key -notmatch '^[^/\\:]+(?:/[^/\\:]+)*$' -or $key -match '(^|/)\.\.?(/|$)' -or $seen.ContainsKey($key.ToLowerInvariant()) -or $hash -notmatch '^[0-9a-fA-F]{64}$') { throw 'invalid, duplicate or case-colliding manifest key' }
    $seen[$key.ToLowerInvariant()] = $true; $values[$key] = $hash.ToLowerInvariant(); $keys += $key
  }
  if (@($content.psobject.Properties).Count -ne $rawProperties.Count) { throw 'manifest inventory contains malformed or duplicate properties' }
  $physical = @(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { $_.FullName.Substring($payload.Length + 1).Replace('\','/') })
  if ($physical.Count -ne $keys.Count) { throw 'manifest inventory does not exactly match payload files' }
  $keySet = @{}; foreach ($key in $keys) { $keySet[$key] = $true }
  foreach ($file in $physical) { if (-not $keySet.ContainsKey($file)) { throw "unmanifested payload file: $file" } }
  foreach ($key in $keys) {
    $file = Join-Path -Path $payload -ChildPath ($key -replace '/','\\')
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $values[$key]) { throw "payload hash mismatch: $key" }
  }
  $required = @('Vorotex.K15.Runtime.exe','Vorotex.K15.StatusTray.exe','Vorotex.K15.ControlCenter.exe','Vorotex.K15.LiveDashboard.exe')
  foreach ($name in $required) { if (-not $keySet.ContainsKey($name)) { throw "required executable is not hashed: $name" } }
  return $manifest
}

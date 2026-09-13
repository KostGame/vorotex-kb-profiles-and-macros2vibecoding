function Get-VNextFileSha256 {
  [CmdletBinding()]
  param([Parameter(Mandatory=$true)][Alias('Path')][string]$LiteralPath)

  $stream = $null
  $sha = $null
  try {
    $stream = [IO.File]::OpenRead($LiteralPath)
    $sha = [Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
  }
  finally {
    if ($stream) { $stream.Dispose() }
    if ($sha) { $sha.Dispose() }
  }
}

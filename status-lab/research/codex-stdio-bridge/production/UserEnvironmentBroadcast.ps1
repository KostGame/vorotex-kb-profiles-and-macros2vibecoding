Set-StrictMode -Version Latest

function Invoke-UserEnvironmentBroadcast {
    [CmdletBinding()]
    param(
        [ValidateSet('Real', 'FakeSuccess', 'FakeFailure')]
        [string] $Mode = 'Real',
        [int] $TimeoutMilliseconds = 5000
    )
    if ($Mode -eq 'FakeSuccess') { return [pscustomobject]@{ Success = $true; Contract = 'FAKE' } }
    if ($Mode -eq 'FakeFailure') { return [pscustomobject]@{ Success = $false; Contract = 'FAKE'; Win32Error = 0; Error = 'injected broadcast failure' } }
    if ($TimeoutMilliseconds -lt 1 -or $TimeoutMilliseconds -gt 60000) { throw 'broadcast timeout must be between 1 and 60000 milliseconds' }
    if (-not ('UserEnvironmentBroadcastNative' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UserEnvironmentBroadcastNative
{
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    public static bool Send(int timeoutMilliseconds, out int error)
    {
        IntPtr text = Marshal.StringToHGlobalUni("Environment");
        try
        {
            UIntPtr result;
            IntPtr returned = SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, text, SMTO_ABORTIFHUNG, (uint)timeoutMilliseconds, out result);
            error = returned == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            // Only a non-zero API return proves success; zero includes timeout/failure.
            return returned != IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(text); }
    }
}
'@
    }
    $win32Error = 0
    $success = [UserEnvironmentBroadcastNative]::Send($TimeoutMilliseconds, [ref]$win32Error)
    if (-not $success) { return [pscustomobject]@{ Success = $false; Contract = 'SENDMESSAGE_TIMEOUT_NONZERO_RETURN'; Win32Error = $win32Error } }
    return [pscustomobject]@{ Success = $true; Contract = 'SENDMESSAGE_TIMEOUT_NONZERO_RETURN'; Win32Error = 0 }
}

function Assert-UserEnvironmentBroadcast {
    param([string] $Mode = 'Real')
    $result = Invoke-UserEnvironmentBroadcast -Mode $Mode
    if (-not $result.Success) { throw "User environment broadcast failed: contract=$($result.Contract) win32Error=$($result.Win32Error) error=$($result.Error)" }
    return $result
}

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace K15.CodexBridge.WindowsAdapter;

internal sealed class AdapterConfigurationException : Exception
{
    public AdapterConfigurationException(string message) : base(message) { }
}

internal static class Program
{
    private const int ConfigurationErrorExitCode = 2;
    private const int AdapterFailureExitCode = 1;
    private const string NodePathVariable = "CODEX_BRIDGE_NODE_PATH";
    private const string ChildPathVariable = "CODEX_BRIDGE_CHILD_PATH";
    private const string WrapperPathVariable = "CODEX_BRIDGE_WRAPPER_PATH";
    private const uint FileNameNormalized = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        StringBuilder pathBuffer,
        uint pathBufferLength,
        uint flags);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var adapterPath = ResolveCurrentAdapterPath();
            var nodePath = RequireExecutablePath(NodePathVariable);
            RejectSelfTarget(NodePathVariable, nodePath, adapterPath);
            RejectSelfTarget(ChildPathVariable, Environment.GetEnvironmentVariable(ChildPathVariable), adapterPath);
            var wrapperPath = ResolveWrapperPath();

            var startInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(wrapperPath);
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var child = new Process { StartInfo = startInfo };
            if (!child.Start())
            {
                throw new InvalidOperationException("child process did not start");
            }

            var inputPump = PumpInputAsync(child);
            var outputPump = PumpOutputAsync(child);
            var errorPump = PumpErrorAsync(child);

            await child.WaitForExitAsync();
            // Do not let an already-exited child wait for Desktop-side stdin EOF.
            // Closing the child input preserves EOF when the child is still draining,
            // while stdout/stderr remain fully drained before exit propagation.
            try
            {
                child.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child may have closed stdin already.
            }
            if (inputPump.IsCompleted)
            {
                await inputPump;
            }
            await Task.WhenAll(outputPump, errorPump);
            return child.ExitCode;
        }
        catch (AdapterConfigurationException)
        {
            WriteDiagnostic("codex bridge adapter: invalid configuration");
            return ConfigurationErrorExitCode;
        }
        catch (Win32Exception)
        {
            WriteDiagnostic("codex bridge adapter: process launch failed");
            return AdapterFailureExitCode;
        }
        catch (IOException)
        {
            WriteDiagnostic("codex bridge adapter: stream forwarding failed");
            return AdapterFailureExitCode;
        }
        catch (InvalidOperationException)
        {
            WriteDiagnostic("codex bridge adapter: process operation failed");
            return AdapterFailureExitCode;
        }
        catch (UnauthorizedAccessException)
        {
            WriteDiagnostic("codex bridge adapter: process access failed");
            return AdapterFailureExitCode;
        }
    }

    private static string RequireExecutablePath(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !File.Exists(value))
        {
            throw new AdapterConfigurationException(variableName + " must name an existing absolute file");
        }

        return value;
    }

    private static string ResolveCurrentAdapterPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath) || !File.Exists(processPath))
        {
            throw new AdapterConfigurationException("current adapter path is unavailable");
        }

        return CanonicalPath(processPath);
    }

    private static void RejectSelfTarget(string variableName, string? configuredPath, string adapterPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)
            || !Path.IsPathFullyQualified(configuredPath)
            || !File.Exists(configuredPath))
        {
            return;
        }

        var canonicalTarget = CanonicalPath(configuredPath);
        if (PathsEqual(canonicalTarget, adapterPath))
        {
            throw new AdapterConfigurationException(variableName + " resolves to the adapter");
        }
    }

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        using var handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, FileNameNormalized);
            if (length == 0)
            {
                throw new AdapterConfigurationException("path cannot be canonicalized");
            }

            if (length < buffer.Capacity)
            {
                return buffer.ToString();
            }

            capacity = checked((int)length + 1);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWrapperPath()
    {
        var configured = Environment.GetEnvironmentVariable(WrapperPathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
            {
                throw new AdapterConfigurationException(WrapperPathVariable + " must name an existing absolute file");
            }

            return configured;
        }

        var packaged = Path.Combine(AppContext.BaseDirectory, "transparent-wrapper.mjs");
        if (!File.Exists(packaged))
        {
            throw new AdapterConfigurationException("packaged wrapper is unavailable");
        }

        return packaged;
    }

    private static async Task PumpInputAsync(Process child)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream);
        }
        catch (IOException)
        {
            // The child may close stdin before the desktop-side input reaches EOF.
        }
        finally
        {
            child.StandardInput.Close();
        }
    }

    private static async Task PumpOutputAsync(Process child)
    {
        await child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
    }

    private static async Task PumpErrorAsync(Process child)
    {
        await child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
    }

    private static void WriteDiagnostic(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        try
        {
            Console.OpenStandardError().Write(bytes, 0, bytes.Length);
        }
        catch (IOException)
        {
            // The desktop-side stderr may already be closed.
        }
    }
}

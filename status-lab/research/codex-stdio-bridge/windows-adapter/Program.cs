using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private const string ChildSha256Variable = "CODEX_BRIDGE_CHILD_SHA256";
    private const string WrapperPathVariable = "CODEX_BRIDGE_WRAPPER_PATH";
    private const uint FileNameNormalized = 0;
    private static readonly string[] DesktopCoreExecutableNames =
    [
        "codex.exe",
        "codex-code-mode-host.exe",
        "codex-windows-sandbox-setup.exe",
        "codex-command-runner.exe"
    ];
    internal sealed record ChildResolution(string Path, string? Sha256Override);
    private sealed record RuntimeFile(string Name, string SourcePath, long Length, string Sha256);

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
            var nodePath = ResolveNodePath();
            RejectSelfTarget(NodePathVariable, nodePath, adapterPath);
            var childResolution = ResolveChildPath(adapterPath);
            RejectSelfTarget(ChildPathVariable, childResolution.Path, adapterPath);
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
            startInfo.Environment[ChildPathVariable] = childResolution.Path;
            if (!string.IsNullOrWhiteSpace(childResolution.Sha256Override))
            {
                startInfo.Environment[ChildSha256Variable] = childResolution.Sha256Override;
            }
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
        return RequireExecutablePathValue(Environment.GetEnvironmentVariable(variableName), variableName);
    }

    private static string RequireExecutablePathValue(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !File.Exists(value))
        {
            throw new AdapterConfigurationException(label + " must name an existing absolute file");
        }

        return Path.GetFullPath(value);
    }

    private static string ResolveNodePath()
    {
        var configured = Environment.GetEnvironmentVariable(NodePathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return RequireExecutablePathValue(configured, NodePathVariable);
        }

        var packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "node", "node.exe"));
        return RequireExecutablePathValue(packaged, "packaged node");
    }

    private static ChildResolution ResolveChildPath(string adapterPath)
    {
        var configured = Environment.GetEnvironmentVariable(ChildPathVariable);
        var runtimeRoot = GetRuntimeRoot();
        var configuredInRuntimeRoot = IsPathWithin(configured, runtimeRoot);

        if (string.IsNullOrWhiteSpace(configured) || configuredInRuntimeRoot)
        {
            var desktopResolution = TryResolveCurrentDesktopRuntime(runtimeRoot);
            if (desktopResolution is not null)
            {
                return desktopResolution;
            }

            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new AdapterConfigurationException("current Codex Desktop runtime is unavailable");
            }
        }

        var configuredPath = RequireExecutablePath(ChildPathVariable);
        RejectSelfTarget(ChildPathVariable, configuredPath, adapterPath);
        return new ChildResolution(configuredPath, null);
    }

    private static string GetRuntimeRoot()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new AdapterConfigurationException("LocalAppData is unavailable");
        }

        return Path.GetFullPath(Path.Combine(localAppData, "OpenAI", "Codex", "bin"));
    }

    private static bool IsPathWithin(string? value, string root)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullValue = Path.GetFullPath(value);
        return fullValue.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static ChildResolution? TryResolveCurrentDesktopRuntime(string runtimeRoot)
    {
        var resourcesDirectory = ResolveActiveCodexResourcesDirectory();
        return resourcesDirectory is null
            ? null
            : ResolveDesktopRuntimeFromResources(resourcesDirectory, runtimeRoot);
    }

    internal static ChildResolution ResolveDesktopRuntimeFromResources(string resourcesDirectory, string runtimeRoot)
    {
        var runtimeFiles = DesktopCoreExecutableNames
            .Select(name => DescribeRuntimeFile(resourcesDirectory, name))
            .ToArray();
        var generation = ComputeRuntimeGeneration(runtimeFiles);
        var generationDirectory = Path.Combine(runtimeRoot, generation);

        Directory.CreateDirectory(runtimeRoot);
        var runtimeRootInfo = new DirectoryInfo(runtimeRoot);
        if ((runtimeRootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AdapterConfigurationException("Codex runtime root must not be a reparse point");
        }

        if (!RuntimeGenerationMatches(generationDirectory, runtimeFiles))
        {
            RepairOrMaterializeRuntimeGeneration(runtimeRoot, generationDirectory, generation, runtimeFiles);
        }

        if (!RuntimeGenerationMatches(generationDirectory, runtimeFiles))
        {
            throw new AdapterConfigurationException("current Desktop runtime materialization failed");
        }

        var child = runtimeFiles.Single(file => string.Equals(file.Name, "codex.exe", StringComparison.Ordinal));
        return new ChildResolution(
            Path.GetFullPath(Path.Combine(generationDirectory, child.Name)),
            child.Sha256);
    }

    private static RuntimeFile DescribeRuntimeFile(string resourcesDirectory, string name)
    {
        var sourcePath = RequireExecutablePathValue(
            Path.Combine(resourcesDirectory, name),
            $"current Desktop {name}");
        var info = new FileInfo(sourcePath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AdapterConfigurationException($"current Desktop {name} must not be a reparse point");
        }

        return new RuntimeFile(name, sourcePath, info.Length, Sha256File(sourcePath));
    }

    private static string ComputeRuntimeGeneration(IReadOnlyList<RuntimeFile> runtimeFiles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in runtimeFiles)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Name));
            hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(file.Sha256));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    private static bool RuntimeGenerationMatches(string generationDirectory, IReadOnlyList<RuntimeFile> runtimeFiles)
    {
        if (!Directory.Exists(generationDirectory))
        {
            return false;
        }

        var directoryInfo = new DirectoryInfo(generationDirectory);
        if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        foreach (var file in runtimeFiles)
        {
            var destinationPath = Path.Combine(generationDirectory, file.Name);
            if (!File.Exists(destinationPath))
            {
                return false;
            }

            var destinationInfo = new FileInfo(destinationPath);
            if ((destinationInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || destinationInfo.Length != file.Length
                || !string.Equals(Sha256File(destinationPath), file.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return Directory
            .EnumerateFileSystemEntries(generationDirectory)
            .All(entry => runtimeFiles.Any(file =>
                string.Equals(Path.GetFileName(entry), file.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static void RepairOrMaterializeRuntimeGeneration(
        string runtimeRoot,
        string generationDirectory,
        string generation,
        IReadOnlyList<RuntimeFile> runtimeFiles)
    {
        if (Directory.Exists(generationDirectory))
        {
            var generationInfo = new DirectoryInfo(generationDirectory);
            if ((generationInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new AdapterConfigurationException("current Desktop runtime generation must not be a reparse point");
            }

            var allowedNames = runtimeFiles
                .Select(file => file.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = Directory
                .EnumerateFileSystemEntries(generationDirectory)
                .ToArray();

            foreach (var entry in entries)
            {
                var entryName = Path.GetFileName(entry);
                if (!allowedNames.Contains(entryName)
                    || !File.Exists(entry)
                    || Directory.Exists(entry))
                {
                    throw new AdapterConfigurationException("current Desktop runtime generation contains unexpected entries");
                }

                var entryInfo = new FileInfo(entry);
                if ((entryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AdapterConfigurationException("current Desktop runtime generation contains a reparse point");
                }
            }

            foreach (var entry in entries)
            {
                File.Delete(entry);
            }
            Directory.Delete(generationDirectory, recursive: false);
        }

        var stagingDirectory = Path.Combine(
            runtimeRoot,
            $".staging-{generation}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            foreach (var file in runtimeFiles)
            {
                var destinationPath = Path.Combine(stagingDirectory, file.Name);
                File.Copy(file.SourcePath, destinationPath, overwrite: false);

                var copiedInfo = new FileInfo(destinationPath);
                if ((copiedInfo.Attributes & FileAttributes.ReparsePoint) != 0
                    || copiedInfo.Length != file.Length
                    || !string.Equals(Sha256File(destinationPath), file.Sha256, StringComparison.Ordinal))
                {
                    throw new AdapterConfigurationException($"copied Desktop runtime file failed verification: {file.Name}");
                }
            }

            try
            {
                Directory.Move(stagingDirectory, generationDirectory);
            }
            catch (IOException) when (RuntimeGenerationMatches(generationDirectory, runtimeFiles))
            {
                // Another bridge/Desktop process won the same-generation relocation race.
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static string? ResolveActiveCodexResourcesDirectory()
    {
        var resourcesDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                var normalized = Path.GetFullPath(executablePath);
                if (!normalized.Contains(
                        $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}OpenAI.Codex_",
                        StringComparison.OrdinalIgnoreCase)
                    || !normalized.EndsWith(
                        $"{Path.DirectorySeparatorChar}app{Path.DirectorySeparatorChar}ChatGPT.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                resourcesDirectories.Add(Path.Combine(
                    Path.GetDirectoryName(normalized)!,
                    "resources"));
            }
            catch (Win32Exception)
            {
                // Ignore unrelated/inaccessible ChatGPT processes.
            }
            catch (UnauthorizedAccessException)
            {
                // A different packaged ChatGPT process may deny module inspection.
            }
            catch (InvalidOperationException)
            {
                // Process may exit while inventory is being read.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (resourcesDirectories.Count == 0)
        {
            return null;
        }

        if (resourcesDirectories.Count != 1)
        {
            throw new AdapterConfigurationException("multiple Codex Desktop package roots are active");
        }

        return resourcesDirectories.Single();
    }

    private static string Sha256File(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

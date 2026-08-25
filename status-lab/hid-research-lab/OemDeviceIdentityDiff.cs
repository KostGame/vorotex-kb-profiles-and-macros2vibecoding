using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemIdentityCallSite(
    string ImportName,
    uint Rva,
    int FileOffset,
    string Section,
    uint ContextStartRva,
    string ContextHex);

internal sealed record OemIdentityImport(
    string Dll,
    string Name,
    uint IatRva,
    string IatVa,
    List<OemIdentityCallSite> CallSites);

internal sealed record OemIdentityTokenOccurrence(
    string Token,
    string RelativePath,
    string Encoding,
    long FileOffset,
    uint? Rva,
    string? Section,
    string Snippet);

internal sealed record OemIdentityConstantOccurrence(
    string Label,
    string ValueHex,
    int WidthBytes,
    int FileOffset,
    uint Rva,
    string Section,
    string ContextHex);

internal sealed record OemIdentityPackageFile(
    string RelativePath,
    long Length,
    string Sha256,
    string[] IdentitySnippets);

internal sealed record OemIdentityBinaryReport(
    string Executable,
    string ExecutableSha256,
    long ExecutableSize,
    string Machine,
    bool Pe32Plus,
    string ImageBase,
    List<OemIdentityImport> RelevantImports,
    List<OemIdentityTokenOccurrence> TokenOccurrences,
    List<OemIdentityConstantOccurrence> ConstantOccurrences,
    List<OemIdentityPackageFile> PackageFiles);

internal sealed record OemIdentityDiffCandidate(
    int Rank,
    string Category,
    string Summary,
    string EvidenceA,
    string EvidenceB,
    string Confidence);

internal sealed record OemDeviceIdentityDiffReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Purpose,
    object Safety,
    OemIdentityBinaryReport A,
    OemIdentityBinaryReport B,
    List<OemIdentityDiffCandidate> Candidates,
    string[] Notes);

internal static class OemDeviceIdentityDiffAnalyzer
{
    private static readonly HashSet<string> RelevantImportNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HidD_GetAttributes",
        "HidD_GetProductString",
        "HidD_GetManufacturerString",
        "HidD_GetSerialNumberString",
        "HidD_GetPreparsedData",
        "HidD_FreePreparsedData",
        "HidP_GetCaps",
        "SetupDiGetClassDevsA",
        "SetupDiGetClassDevsW",
        "SetupDiEnumDeviceInterfaces",
        "SetupDiGetDeviceInterfaceDetailA",
        "SetupDiGetDeviceInterfaceDetailW",
        "SetupDiEnumDeviceInfo",
        "SetupDiGetDeviceRegistryPropertyA",
        "SetupDiGetDeviceRegistryPropertyW",
        "SetupDiGetDevicePropertyW"
    };

    private static readonly string[] IdentityTokens =
    [
        "VID_", "PID_", "vid_", "pid_",
        "VID_36A4", "VID_B6A4", "PID_4100", "PID_4101",
        "36A4", "B6A4", "4100", "4101",
        "13988", "46756", "16640", "16641",
        "W909", "K15", "VOROTEX", "MKESPN", "SXS",
        "ProductString", "ManufacturerString", "SerialNumberString",
        "GetAttributes", "DeviceInterface", "HID"
    ];

    private static readonly (string Label, ushort Value)[] Known16BitConstants =
    [
        ("VID_36A4", 0x36A4),
        ("VID_B6A4", 0xB6A4),
        ("PID_4100", 0x4100),
        ("PID_4101", 0x4101)
    ];

    public static string? FindSxsW909Executable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SXS-W909", "SXS-W909.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SXS-W909", "SXS-W909.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static OemDeviceIdentityDiffReport Analyze(string exeA, string exeB)
    {
        if (!File.Exists(exeA))
            throw new FileNotFoundException("OEM identity diff EXE A not found.", exeA);
        if (!File.Exists(exeB))
            throw new FileNotFoundException("OEM identity diff EXE B not found.", exeB);

        exeA = Path.GetFullPath(exeA);
        exeB = Path.GetFullPath(exeB);
        var a = AnalyzeOne(exeA);
        var b = AnalyzeOne(exeB);
        var candidates = BuildCandidates(a, b);

        return new OemDeviceIdentityDiffReport(
            1,
            DateTimeOffset.UtcNow,
            "read-only OEM device discovery / identity comparison for VOROTEX K15 and SXS-W909 family configurators",
            new
            {
                executableModified = false,
                executablePatched = false,
                processAttached = false,
                processInjected = false,
                debuggerAttached = false,
                deviceOpened = false,
                featureReportsQueried = false,
                hidWritesPerformed = false,
                vidPidSpoofed = false,
                driverInstalled = false,
                registryModified = false,
                reportContainsOnlyStaticMetadataAndBoundedSnippets = true
            },
            a,
            b,
            candidates,
            [
                "The report ranks static identity/discovery differences; it does not prove a whitelist until a concrete comparison branch or model table is identified.",
                "Known K15 identifiers are searched as text and little-endian integer constants. Integer hits can be false positives and must be interpreted with section/call-site context.",
                "Package scanning excludes arbitrary profile/macro payloads and keeps only bounded snippets around explicit identity tokens.",
                "No HID handle is opened and no vendor process is attached, injected, debugged, or patched."
            ]);
    }

    public static string ToText(OemDeviceIdentityDiffReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - OEM Device Identity Diff");
        sb.AppendLine("Safety: READ-ONLY; no HID handles/writes, no feature reports, no injection/debug attach, no EXE patching or VID/PID spoofing.");
        sb.AppendLine();
        AppendBinary(sb, "A", report.A);
        AppendBinary(sb, "B", report.B);
        sb.AppendLine("Top identity/discovery candidates:");
        foreach (var candidate in report.Candidates.Take(80))
        {
            sb.AppendLine($"  #{candidate.Rank} [{candidate.Confidence}] {candidate.Category}: {candidate.Summary}");
            if (!string.IsNullOrWhiteSpace(candidate.EvidenceA))
                sb.AppendLine("    A: " + candidate.EvidenceA);
            if (!string.IsNullOrWhiteSpace(candidate.EvidenceB))
                sb.AppendLine("    B: " + candidate.EvidenceB);
        }
        sb.AppendLine();
        foreach (var note in report.Notes)
            sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendBinary(StringBuilder sb, string label, OemIdentityBinaryReport report)
    {
        sb.AppendLine($"{label}: {report.Executable}");
        sb.AppendLine($"  SHA256: {report.ExecutableSha256}");
        sb.AppendLine($"  Machine={report.Machine}; PE32+={report.Pe32Plus}; ImageBase={report.ImageBase}");
        sb.AppendLine($"  relevant imports={report.RelevantImports.Count}; identity tokens={report.TokenOccurrences.Count}; known-ID constants={report.ConstantOccurrences.Count}; package files={report.PackageFiles.Count}");
        foreach (var import in report.RelevantImports)
            sb.AppendLine($"    {import.Dll}!{import.Name} IAT=0x{import.IatRva:X8} callSites={import.CallSites.Count}");
        sb.AppendLine();
    }

    private static OemIdentityBinaryReport AnalyzeOne(string exePath)
    {
        var pe = ParsedPe.Parse(exePath);
        var relevantImports = pe.Imports
            .Where(i => RelevantImportNames.Contains(i.Name))
            .Select(i => new OemIdentityImport(
                i.Dll,
                i.Name,
                i.IatRva,
                $"0x{pe.ImageBase + i.IatRva:X}",
                FindDirectCallSites(pe, i)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new OemIdentityBinaryReport(
            Path.GetFileName(exePath),
            Sha256(exePath),
            new FileInfo(exePath).Length,
            $"0x{pe.Machine:X4}",
            pe.Pe32Plus,
            $"0x{pe.ImageBase:X}",
            relevantImports,
            ScanIdentityTokens(exePath, pe),
            ScanKnownConstants(pe),
            ScanPackageIdentityFiles(Path.GetDirectoryName(exePath)!));
    }

    private static List<OemIdentityDiffCandidate> BuildCandidates(OemIdentityBinaryReport a, OemIdentityBinaryReport b)
    {
        var result = new List<OemIdentityDiffCandidate>();
        var rank = 1;

        foreach (var name in RelevantImportNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var ai = a.RelevantImports.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var bi = b.RelevantImports.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (ai is null && bi is null)
                continue;
            if (ai is null || bi is null)
            {
                result.Add(new OemIdentityDiffCandidate(
                    rank++, "import_presence_delta",
                    $"{name} exists only in one OEM build.",
                    ai is null ? "absent" : $"present; callSites={ai.CallSites.Count}",
                    bi is null ? "absent" : $"present; callSites={bi.CallSites.Count}",
                    "medium"));
                continue;
            }

            if (ai.CallSites.Count != bi.CallSites.Count)
            {
                result.Add(new OemIdentityDiffCandidate(
                    rank++, "device_discovery_callsite_count_delta",
                    $"{name} direct call-site count differs.",
                    $"count={ai.CallSites.Count}; RVAs={JoinRvas(ai.CallSites)}",
                    $"count={bi.CallSites.Count}; RVAs={JoinRvas(bi.CallSites)}",
                    "high"));
            }

            var paired = Math.Min(ai.CallSites.Count, bi.CallSites.Count);
            for (var index = 0; index < paired; index++)
            {
                var ac = ai.CallSites[index];
                var bc = bi.CallSites[index];
                var similarity = HexSimilarity(ac.ContextHex, bc.ContextHex);
                var delta = (long)bc.Rva - ac.Rva;
                if (similarity < 0.985 || name.Equals("HidD_GetAttributes", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new OemIdentityDiffCandidate(
                        rank++, "aligned_device_discovery_code_window",
                        $"{name} call-site #{index + 1}: B-A RVA delta={delta:+#;-#;0}, byte similarity={similarity:P1}.",
                        $"RVA=0x{ac.Rva:X8}; section={ac.Section}; context={BoundHex(ac.ContextHex)}",
                        $"RVA=0x{bc.Rva:X8}; section={bc.Section}; context={BoundHex(bc.ContextHex)}",
                        similarity >= 0.90 ? "high" : "medium"));
                }
            }
        }

        foreach (var label in Known16BitConstants.Select(x => x.Label))
        {
            var ah = a.ConstantOccurrences.Where(x => x.Label == label).ToList();
            var bh = b.ConstantOccurrences.Where(x => x.Label == label).ToList();
            var at = a.TokenOccurrences.Where(x => x.Token.Contains(label[(label.IndexOf('_') + 1)..], StringComparison.OrdinalIgnoreCase)).ToList();
            var bt = b.TokenOccurrences.Where(x => x.Token.Contains(label[(label.IndexOf('_') + 1)..], StringComparison.OrdinalIgnoreCase)).ToList();
            if (ah.Count == bh.Count && at.Count == bt.Count)
                continue;
            result.Add(new OemIdentityDiffCandidate(
                rank++, "known_vid_pid_delta",
                $"Known K15 identifier {label} has different static evidence counts.",
                $"integerHits={ah.Count}; textHits={at.Count}; sections={JoinSections(ah)}",
                $"integerHits={bh.Count}; textHits={bt.Count}; sections={JoinSections(bh)}",
                "high"));
        }

        foreach (var token in new[] { "W909", "K15", "VOROTEX", "MKESPN", "SXS", "VID_", "PID_" })
        {
            var ac = a.TokenOccurrences.Count(x => x.Token.Equals(token, StringComparison.OrdinalIgnoreCase));
            var bc = b.TokenOccurrences.Count(x => x.Token.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (ac == bc)
                continue;
            result.Add(new OemIdentityDiffCandidate(
                rank++, "identity_string_delta",
                $"Identity token {token} occurrence count differs.",
                $"count={ac}",
                $"count={bc}",
                token is "VID_" or "PID_" ? "medium" : "context"));
        }

        var packagePaths = a.PackageFiles.Select(x => x.RelativePath)
            .Union(b.PackageFiles.Select(x => x.RelativePath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var path in packagePaths)
        {
            var af = a.PackageFiles.FirstOrDefault(x => x.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            var bf = b.PackageFiles.FirstOrDefault(x => x.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (af is null || bf is null)
            {
                result.Add(new OemIdentityDiffCandidate(
                    rank++, "identity_resource_presence_delta",
                    $"Identity-bearing package file {path} exists only on one side.",
                    af is null ? "absent" : $"sha256={af.Sha256}; snippets={string.Join(" | ", af.IdentitySnippets)}",
                    bf is null ? "absent" : $"sha256={bf.Sha256}; snippets={string.Join(" | ", bf.IdentitySnippets)}",
                    "medium"));
                continue;
            }
            if (af.Sha256 == bf.Sha256)
                continue;
            result.Add(new OemIdentityDiffCandidate(
                rank++, "identity_resource_content_delta",
                $"Identity-bearing package file {path} differs between OEM builds.",
                $"sha256={af.Sha256}; snippets={string.Join(" | ", af.IdentitySnippets)}",
                $"sha256={bf.Sha256}; snippets={string.Join(" | ", bf.IdentitySnippets)}",
                af.IdentitySnippets.Length > 0 || bf.IdentitySnippets.Length > 0 ? "high" : "medium"));
        }

        return result
            .OrderBy(c => ConfidenceRank(c.Confidence))
            .ThenBy(c => c.Rank)
            .Select((c, index) => c with { Rank = index + 1 })
            .Take(160)
            .ToList();
    }

    private static int ConfidenceRank(string value) => value switch
    {
        "high" => 0,
        "medium" => 1,
        "context" => 2,
        _ => 3
    };

    private static string JoinRvas(IEnumerable<OemIdentityCallSite> sites) =>
        string.Join(",", sites.Take(12).Select(x => $"0x{x.Rva:X8}"));

    private static string JoinSections(IEnumerable<OemIdentityConstantOccurrence> hits) =>
        string.Join(",", hits.Select(x => x.Section).Distinct(StringComparer.OrdinalIgnoreCase).Take(12));

    private static string BoundHex(string value) => value.Length <= 192 ? value : value[..192];

    private static double HexSimilarity(string a, string b)
    {
        var ab = Convert.FromHexString(a);
        var bb = Convert.FromHexString(b);
        var length = Math.Min(ab.Length, bb.Length);
        if (length == 0)
            return 0;
        var same = 0;
        for (var i = 0; i < length; i++)
            if (ab[i] == bb[i]) same++;
        return (double)same / Math.Max(ab.Length, bb.Length);
    }

    private static List<OemIdentityCallSite> FindDirectCallSites(ParsedPe pe, PeImport import)
    {
        var result = new List<OemIdentityCallSite>();
        foreach (var section in pe.Sections.Where(s => s.Executable))
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, (uint)Math.Max(0, pe.Bytes.Length - start)));
            var end = start + length;
            for (var off = start; off + 6 <= end; off++)
            {
                if (pe.Bytes[off] != 0xff || pe.Bytes[off + 1] != 0x15)
                    continue;
                var instructionRva = section.VirtualAddress + (uint)(off - start);
                bool matches;
                if (pe.Pe32Plus)
                {
                    var disp = I32(pe.Bytes, off + 2);
                    var target = unchecked((uint)((long)instructionRva + 6 + disp));
                    matches = target == import.IatRva;
                }
                else
                {
                    var absolute = U32(pe.Bytes, off + 2);
                    matches = absolute == checked((uint)(pe.ImageBase + import.IatRva));
                }
                if (!matches)
                    continue;
                var contextStart = Math.Max(start, off - 128);
                var contextEnd = Math.Min(end, off + 134);
                result.Add(new OemIdentityCallSite(
                    import.Name,
                    instructionRva,
                    off,
                    section.Name,
                    section.VirtualAddress + (uint)(contextStart - start),
                    Convert.ToHexString(pe.Bytes.AsSpan(contextStart, contextEnd - contextStart)).ToLowerInvariant()));
            }
        }
        return result.OrderBy(x => x.Rva).ToList();
    }

    private static List<OemIdentityTokenOccurrence> ScanIdentityTokens(string exePath, ParsedPe pe)
    {
        var installRoot = Path.GetDirectoryName(exePath)!;
        var result = new List<OemIdentityTokenOccurrence>();
        foreach (var file in EnumerateIdentityFiles(installRoot, exePath).Take(700))
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length <= 0 || info.Length > 32L * 1024 * 1024)
                    continue;
                var bytes = File.ReadAllBytes(file);
                var relative = Path.GetRelativePath(installRoot, file);
                var isExe = string.Equals(Path.GetFullPath(file), exePath, StringComparison.OrdinalIgnoreCase);
                foreach (var token in IdentityTokens)
                {
                    foreach (var (encodingName, encoding) in new[]
                    {
                        ("ascii", Encoding.ASCII),
                        ("utf16le", Encoding.Unicode)
                    })
                    {
                        var needle = encoding.GetBytes(token);
                        var from = 0;
                        var count = 0;
                        while (from <= bytes.Length - needle.Length && count < 48 && result.Count < 2400)
                        {
                            var found = IndexOf(bytes, needle, from);
                            if (found < 0) break;
                            uint? rva = null;
                            string? section = null;
                            if (isExe)
                            {
                                rva = pe.TryOffsetToRva(found);
                                section = pe.SectionForOffset(found)?.Name;
                            }
                            result.Add(new OemIdentityTokenOccurrence(
                                token,
                                relative,
                                encodingName,
                                found,
                                rva,
                                section,
                                Snippet(bytes, found, needle.Length, encoding)));
                            from = found + Math.Max(1, needle.Length);
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return result
            .GroupBy(x => (x.RelativePath, x.Token, x.Encoding, x.FileOffset))
            .Select(g => g.First())
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FileOffset)
            .ToList();
    }

    private static IEnumerable<string> EnumerateIdentityFiles(string installRoot, string exePath)
    {
        yield return exePath;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFullPath(file), exePath, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file);
            var identityNamed = name.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("product", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("Set.ini", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("config", StringComparison.OrdinalIgnoreCase);
            if (!identityNamed && extension is not ".ini" and not ".xml" and not ".cfg" and not ".conf")
                continue;
            yield return file;
        }
    }

    private static List<OemIdentityConstantOccurrence> ScanKnownConstants(ParsedPe pe)
    {
        var result = new List<OemIdentityConstantOccurrence>();
        foreach (var section in pe.Sections.Where(s => s.Name is ".text" or ".rdata" or ".data"))
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, (uint)Math.Max(0, pe.Bytes.Length - start)));
            var end = start + length;
            foreach (var (label, value) in Known16BitConstants)
            {
                var shortNeedle = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(shortNeedle, value);
                ScanConstant(result, pe, section, start, end, label, value, shortNeedle, 2, 96);
                var intNeedle = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(intNeedle, value);
                ScanConstant(result, pe, section, start, end, label, value, intNeedle, 4, 96);
            }
        }
        return result.OrderBy(x => x.Rva).ToList();
    }

    private static void ScanConstant(
        List<OemIdentityConstantOccurrence> result,
        ParsedPe pe,
        Section section,
        int start,
        int end,
        string label,
        ushort value,
        byte[] needle,
        int width,
        int cap)
    {
        var count = 0;
        var from = start;
        while (from <= end - needle.Length && count < cap && result.Count < 1800)
        {
            var found = IndexOf(pe.Bytes, needle, from, end);
            if (found < 0) break;
            var contextStart = Math.Max(start, found - 24);
            var contextEnd = Math.Min(end, found + needle.Length + 24);
            result.Add(new OemIdentityConstantOccurrence(
                label,
                $"0x{value:X4}",
                width,
                found,
                section.VirtualAddress + (uint)(found - start),
                section.Name,
                Convert.ToHexString(pe.Bytes.AsSpan(contextStart, contextEnd - contextStart)).ToLowerInvariant()));
            from = found + needle.Length;
            count++;
        }
    }

    private static List<OemIdentityPackageFile> ScanPackageIdentityFiles(string installRoot)
    {
        var result = new List<OemIdentityPackageFile>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories); }
        catch { return result; }
        foreach (var file in files.Take(1200))
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length <= 0 || info.Length > 4L * 1024 * 1024)
                    continue;
                var name = Path.GetFileName(file);
                var extension = Path.GetExtension(file);
                if (extension is not ".ini" and not ".xml" and not ".cfg" and not ".conf" and not ".txt")
                    continue;
                var identityNamed = name.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("product", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                                    name.Equals("Set.ini", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("config", StringComparison.OrdinalIgnoreCase);
                var bytes = File.ReadAllBytes(file);
                var snippets = new List<string>();
                foreach (var token in IdentityTokens.Take(18))
                {
                    foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
                    {
                        var needle = encoding.GetBytes(token);
                        var found = IndexOf(bytes, needle, 0);
                        if (found >= 0)
                            snippets.Add($"{token}: {Snippet(bytes, found, needle.Length, encoding)}");
                        if (snippets.Count >= 12) break;
                    }
                    if (snippets.Count >= 12) break;
                }
                if (!identityNamed && snippets.Count == 0)
                    continue;
                result.Add(new OemIdentityPackageFile(
                    Path.GetRelativePath(installRoot, file),
                    info.Length,
                    Sha256(file),
                    snippets.ToArray()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return result.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Snippet(byte[] bytes, int offset, int matchLength, Encoding encoding)
    {
        var radius = encoding == Encoding.Unicode ? 80 : 64;
        var start = Math.Max(0, offset - radius);
        if (encoding == Encoding.Unicode && (start & 1) != 0) start++;
        var end = Math.Min(bytes.Length, offset + matchLength + radius);
        if (encoding == Encoding.Unicode && ((end - start) & 1) != 0) end--;
        var text = encoding.GetString(bytes, start, Math.Max(0, end - start));
        var cleaned = new string(text.Select(ch => char.IsControl(ch) && ch is not '\t' ? ' ' : ch).ToArray());
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 220 ? cleaned[..220] : cleaned;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start, int? limit = null)
    {
        var end = Math.Min(limit ?? haystack.Length, haystack.Length);
        for (var index = start; index <= end - needle.Length; index++)
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle)) return index;
        return -1;
    }

    private static string Sha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record PeImport(string Dll, string Name, uint IatRva);

    private sealed record Section(
        string Name,
        uint VirtualSize,
        uint VirtualAddress,
        uint RawSize,
        uint RawPointer,
        uint Characteristics)
    {
        public bool Executable => (Characteristics & 0x20000000) != 0;
    }

    private sealed class ParsedPe
    {
        public byte[] Bytes { get; }
        public ushort Machine { get; }
        public bool Pe32Plus { get; }
        public ulong ImageBase { get; }
        public List<Section> Sections { get; }
        public List<PeImport> Imports { get; }

        private ParsedPe(byte[] bytes, ushort machine, bool pe32Plus, ulong imageBase, List<Section> sections, List<PeImport> imports)
        {
            Bytes = bytes;
            Machine = machine;
            Pe32Plus = pe32Plus;
            ImageBase = imageBase;
            Sections = sections;
            Imports = imports;
        }

        public static ParsedPe Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("Selected OEM executable is not a PE image.");
            var peOffset = I32(bytes, 0x3c);
            if (peOffset < 0 || peOffset + 24 >= bytes.Length || bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
                throw new InvalidDataException("PE header not found.");
            var machine = U16(bytes, peOffset + 4);
            var sectionCount = U16(bytes, peOffset + 6);
            var optionalSize = U16(bytes, peOffset + 20);
            var optional = peOffset + 24;
            var magic = U16(bytes, optional);
            var pe32Plus = magic == 0x20b;
            if (!pe32Plus && magic != 0x10b)
                throw new InvalidDataException($"Unsupported PE optional-header magic 0x{magic:X4}.");
            ulong imageBase = pe32Plus ? U64(bytes, optional + 24) : U32(bytes, optional + 28);
            var dataDirectory = optional + (pe32Plus ? 112 : 96);
            var importRva = U32(bytes, dataDirectory + 8);
            var sectionTable = optional + optionalSize;
            var sections = new List<Section>();
            for (var index = 0; index < sectionCount; index++)
            {
                var off = sectionTable + index * 40;
                Ensure(bytes, off, 40);
                sections.Add(new Section(
                    Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'),
                    U32(bytes, off + 8),
                    U32(bytes, off + 12),
                    U32(bytes, off + 16),
                    U32(bytes, off + 20),
                    U32(bytes, off + 36)));
            }
            var imports = ParseImports(bytes, pe32Plus, importRva, sections);
            return new ParsedPe(bytes, machine, pe32Plus, imageBase, sections, imports);
        }

        public uint? TryOffsetToRva(int offset)
        {
            var section = SectionForOffset(offset);
            return section is null ? null : section.VirtualAddress + (uint)(offset - (int)section.RawPointer);
        }

        public Section? SectionForOffset(int offset) => Sections.FirstOrDefault(section =>
            offset >= section.RawPointer && offset < section.RawPointer + section.RawSize);

        private static List<PeImport> ParseImports(byte[] bytes, bool pe32Plus, uint importRva, List<Section> sections)
        {
            var result = new List<PeImport>();
            if (importRva == 0) return result;
            var descriptorOffset = RvaToOffset(importRva, sections, bytes.Length);
            var pointerSize = pe32Plus ? 8 : 4;
            for (var descriptor = descriptorOffset; descriptor + 20 <= bytes.Length; descriptor += 20)
            {
                var originalThunk = U32(bytes, descriptor);
                var nameRva = U32(bytes, descriptor + 12);
                var firstThunk = U32(bytes, descriptor + 16);
                if (originalThunk == 0 && nameRva == 0 && firstThunk == 0) break;
                var dll = ReadAsciiZ(bytes, RvaToOffset(nameRva, sections, bytes.Length));
                var lookupRva = originalThunk != 0 ? originalThunk : firstThunk;
                var lookupOffset = RvaToOffset(lookupRva, sections, bytes.Length);
                for (var index = 0; ; index++)
                {
                    var thunkOffset = lookupOffset + index * pointerSize;
                    Ensure(bytes, thunkOffset, pointerSize);
                    var thunk = pe32Plus ? U64(bytes, thunkOffset) : U32(bytes, thunkOffset);
                    if (thunk == 0) break;
                    var ordinalMask = pe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                    if ((thunk & ordinalMask) != 0) continue;
                    var nameOffset = RvaToOffset(checked((uint)thunk), sections, bytes.Length) + 2;
                    var name = ReadAsciiZ(bytes, nameOffset);
                    result.Add(new PeImport(dll, name, checked(firstThunk + (uint)(index * pointerSize))));
                }
            }
            return result;
        }
    }

    private static int RvaToOffset(uint rva, List<Section> sections, int fileLength)
    {
        var section = sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
        if (section is null) throw new InvalidDataException($"RVA 0x{rva:X8} is outside PE sections.");
        var offset = checked((int)(section.RawPointer + (rva - section.VirtualAddress)));
        Ensure(fileLength, offset, 1);
        return offset;
    }

    private static string ReadAsciiZ(byte[] bytes, int offset)
    {
        var end = offset;
        while (end < bytes.Length && bytes[end] != 0 && end - offset < 1024) end++;
        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private static ushort U16(byte[] bytes, int offset) { Ensure(bytes, offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)); }
    private static uint U32(byte[] bytes, int offset) { Ensure(bytes, offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)); }
    private static ulong U64(byte[] bytes, int offset) { Ensure(bytes, offset, 8); return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)); }
    private static int I32(byte[] bytes, int offset) { Ensure(bytes, offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)); }
    private static void Ensure(byte[] bytes, int offset, int length) => Ensure(bytes.Length, offset, length);
    private static void Ensure(int total, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > total - length) throw new InvalidDataException("PE structure exceeds file bounds.");
    }
}
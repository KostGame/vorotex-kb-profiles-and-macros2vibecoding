using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Vorotex.K15.VendorStaticLab;

namespace Vorotex.K15.HidResearchLab;

internal sealed record SleepUiTokenOccurrence(
    string Token,
    string EvidenceClass,
    string RelativePath,
    string Encoding,
    long FileOffset,
    uint? Rva,
    string Snippet,
    uint[] XrefRvas);

internal sealed record SleepUiTraceCandidate(
    string Token,
    string EvidenceClass,
    uint XrefRva,
    uint? NearestSetFeatureRva,
    long? SetFeatureDelta,
    uint? NearestGetFeatureRva,
    long? GetFeatureDelta,
    string Confidence,
    string Note);

internal sealed record KeyboardSleepUiTraceReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Purpose,
    object Safety,
    string TargetExecutable,
    string TargetSha256,
    bool KeyboardSpecificResourceFound,
    bool GenericSettingMoreResourceFound,
    List<SleepUiTokenOccurrence> TokenOccurrences,
    List<SleepUiTraceCandidate> Candidates,
    List<PeImport> HidFeatureImports,
    List<StaticCallSite> SetFeatureCallSites,
    List<StaticCallSite> GetFeatureCallSites,
    string[] TraceGraph,
    string[] Notes);

internal static class KeyboardSleepUiTraceAnalyzer
{
    private static readonly (string Token, string EvidenceClass)[] Tokens =
    [
        ("KBSpecialFuncSet.xml", "keyboard_specific_resource"),
        ("Slider_Sleep_Time", "keyboard_ui_control"),
        ("Edit_Sleep_Time", "keyboard_ui_control"),
        ("Value_Sleep_Time", "keyboard_ui_control"),
        ("SleepTime", "keyboard_sleep_token"),
        ("SavePowerSelect", "persisted_generic_power_key"),
        ("setting_more.xml", "generic_device_resource"),
        ("PowerSavingMode", "generic_device_power_ui")
    ];

    public static KeyboardSleepUiTraceReport Analyze(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("Vendor executable was not found.", exePath);

        var baseReport = VendorPeAnalyzer.Analyze(exePath);
        var image = PeStringImage.Parse(exePath);
        var installRoot = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        var occurrences = ScanExactTokens(installRoot, exePath, image);

        var keyboardSpecific = occurrences.Any(o =>
            o.RelativePath.EndsWith("KBSpecialFuncSet.xml", StringComparison.OrdinalIgnoreCase) ||
            o.Token is "Slider_Sleep_Time" or "Edit_Sleep_Time" or "Value_Sleep_Time");
        var genericSetting = occurrences.Any(o =>
            o.RelativePath.EndsWith("setting_more.xml", StringComparison.OrdinalIgnoreCase) ||
            o.Token == "PowerSavingMode");

        var candidates = BuildCandidates(occurrences, baseReport.SetFeatureCallSites, baseReport.GetFeatureCallSites);
        var traceGraph = candidates
            .OrderBy(c => ConfidenceRank(c.Confidence))
            .ThenBy(c => c.SetFeatureDelta is long d ? Math.Abs(d) : long.MaxValue)
            .Take(80)
            .Select(c =>
            {
                var set = c.NearestSetFeatureRva is uint setRva
                    ? $"HidD_SetFeature@0x{setRva:X8} Δ={c.SetFeatureDelta:+#;-#;0}"
                    : "HidD_SetFeature=none";
                var get = c.NearestGetFeatureRva is uint getRva
                    ? $"HidD_GetFeature@0x{getRva:X8} Δ={c.GetFeatureDelta:+#;-#;0}"
                    : "HidD_GetFeature=none";
                return $"{c.Token} -> xref 0x{c.XrefRva:X8} -> {set}; {get} [{c.Confidence}]";
            })
            .ToArray();

        var notes = new List<string>
        {
            "This report is proximity/xref evidence only; it does not prove the sleep write payload or selector.",
            "KBSpecialFuncSet.xml and Slider/Edit/Value_Sleep_Time are treated as keyboard-specific evidence.",
            "setting_more.xml / PowerSavingMode are kept as generic-device context because the same resource set also contains mouse-oriented controls.",
            "No vendor process was attached or injected. No HID handle was opened by this static trace."
        };
        if (!keyboardSpecific)
            notes.Add("Keyboard-specific sleep resource tokens were not found in the selected package.");
        if (baseReport.SetFeatureCallSites.Count == 0)
            notes.Add("No statically resolved HidD_SetFeature call-site was found; dynamic owner capture may still provide file/runtime correlation evidence.");

        return new KeyboardSleepUiTraceReport(
            1,
            DateTimeOffset.UtcNow,
            "read-only keyboard sleep UI xref trace for VOROTEX/MKESPN-family configurators",
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
                driverInstalled = false,
                reportContainsOnlyStaticMetadataAndBoundedSnippets = true
            },
            Path.GetFileName(exePath),
            Sha256(exePath),
            keyboardSpecific,
            genericSetting,
            occurrences,
            candidates,
            baseReport.RelevantImports,
            baseReport.SetFeatureCallSites,
            baseReport.GetFeatureCallSites,
            traceGraph,
            notes.ToArray());
    }

    public static string ToText(KeyboardSleepUiTraceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - Keyboard Sleep UI Trace");
        sb.AppendLine($"Target: {report.TargetExecutable}");
        sb.AppendLine($"SHA256: {report.TargetSha256}");
        sb.AppendLine("Safety: READ-ONLY; no HID writes, no feature-report query transport, no injection, no patching.");
        sb.AppendLine($"Keyboard-specific resource evidence: {(report.KeyboardSpecificResourceFound ? "YES" : "NO")}");
        sb.AppendLine($"Generic setting_more evidence: {(report.GenericSettingMoreResourceFound ? "YES" : "NO")}");
        sb.AppendLine();
        sb.AppendLine("Exact UI/resource tokens:");
        foreach (var occurrence in report.TokenOccurrences.Take(240))
        {
            sb.AppendLine($"  [{occurrence.EvidenceClass}] {occurrence.RelativePath} @{occurrence.FileOffset} {occurrence.Token} ({occurrence.Encoding})");
            if (!string.IsNullOrWhiteSpace(occurrence.Snippet))
                sb.AppendLine($"    {occurrence.Snippet}");
            foreach (var xref in occurrence.XrefRvas.Take(24))
                sb.AppendLine($"    xref RVA 0x{xref:X8}");
        }
        sb.AppendLine();
        sb.AppendLine("Candidate UI -> HID proximity chain:");
        foreach (var line in report.TraceGraph)
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("HID feature imports:");
        foreach (var import in report.HidFeatureImports)
            sb.AppendLine($"  {import.Dll}!{import.Name} IAT RVA=0x{import.IatRva:X8}");
        sb.AppendLine();
        foreach (var note in report.Notes)
            sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static List<SleepUiTraceCandidate> BuildCandidates(
        IEnumerable<SleepUiTokenOccurrence> occurrences,
        IReadOnlyList<StaticCallSite> setSites,
        IReadOnlyList<StaticCallSite> getSites)
    {
        var result = new List<SleepUiTraceCandidate>();
        foreach (var occurrence in occurrences)
        {
            foreach (var xref in occurrence.XrefRvas)
            {
                var set = Nearest(xref, setSites);
                var get = Nearest(xref, getSites);
                var setDelta = set is null ? (long?)null : (long)set.Rva - xref;
                var getDelta = get is null ? (long?)null : (long)get.Rva - xref;
                var generic = occurrence.EvidenceClass.StartsWith("generic_", StringComparison.Ordinal) ||
                              occurrence.EvidenceClass == "persisted_generic_power_key";
                var distance = setDelta is long d ? Math.Abs(d) : long.MaxValue;
                var confidence = generic
                    ? "context-only"
                    : distance <= 0x800 ? "high-proximity"
                    : distance <= 0x3000 ? "medium-proximity"
                    : distance <= 0x8000 ? "low-proximity"
                    : "xref-only";
                result.Add(new SleepUiTraceCandidate(
                    occurrence.Token,
                    occurrence.EvidenceClass,
                    xref,
                    set?.Rva,
                    setDelta,
                    get?.Rva,
                    getDelta,
                    confidence,
                    generic
                        ? "Generic device UI evidence is not sufficient to attribute the path to K15."
                        : "Proximity is a lead for reverse engineering, not proof of control-flow reachability."));
            }
        }
        return result
            .GroupBy(c => (c.Token, c.XrefRva))
            .Select(g => g.First())
            .OrderBy(c => c.XrefRva)
            .ToList();
    }

    private static StaticCallSite? Nearest(uint xref, IReadOnlyList<StaticCallSite> sites) =>
        sites.OrderBy(s => Math.Abs((long)s.Rva - xref)).FirstOrDefault();

    private static int ConfidenceRank(string confidence) => confidence switch
    {
        "high-proximity" => 0,
        "medium-proximity" => 1,
        "low-proximity" => 2,
        "xref-only" => 3,
        _ => 4
    };

    private static List<SleepUiTokenOccurrence> ScanExactTokens(string installRoot, string exePath, PeStringImage image)
    {
        var result = new List<SleepUiTokenOccurrence>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories);
        }
        catch
        {
            files = new[] { exePath };
        }

        foreach (var file in files.Take(1500))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length <= 0 || info.Length > 64L * 1024 * 1024)
                    continue;
                var bytes = File.ReadAllBytes(file);
                var relative = Path.GetRelativePath(installRoot, file);
                var isExe = string.Equals(Path.GetFullPath(file), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);

                foreach (var (token, evidenceClass) in Tokens)
                {
                    foreach (var (encodingName, encoding) in new[]
                    {
                        ("utf8", (Encoding)new UTF8Encoding(false)),
                        ("utf16le", Encoding.Unicode)
                    })
                    {
                        var needle = encoding.GetBytes(token);
                        var from = 0;
                        var perToken = 0;
                        while (from <= bytes.Length - needle.Length && perToken < 64 && result.Count < 1600)
                        {
                            var found = IndexOf(bytes, needle, from);
                            if (found < 0)
                                break;
                            uint? rva = null;
                            uint[] xrefs = [];
                            if (isExe)
                            {
                                rva = image.TryOffsetToRva(found);
                                if (rva is uint targetRva)
                                    xrefs = image.FindXrefs(targetRva).Take(64).ToArray();
                            }
                            result.Add(new SleepUiTokenOccurrence(
                                token,
                                RefineEvidenceClass(evidenceClass, relative),
                                relative,
                                encodingName,
                                found,
                                rva,
                                Snippet(bytes, found, needle.Length, encoding),
                                xrefs));
                            perToken++;
                            from = found + Math.Max(1, needle.Length);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
            if (result.Count >= 1600)
                break;
        }

        return result
            .GroupBy(o => (o.RelativePath, o.FileOffset, o.Token))
            .Select(g => g.OrderByDescending(o => o.XrefRvas.Length).First())
            .OrderBy(o => o.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.FileOffset)
            .ToList();
    }

    private static string RefineEvidenceClass(string evidenceClass, string relativePath)
    {
        if (relativePath.EndsWith("KBSpecialFuncSet.xml", StringComparison.OrdinalIgnoreCase))
            return "keyboard_specific_resource";
        if (relativePath.EndsWith("setting_more.xml", StringComparison.OrdinalIgnoreCase))
            return "generic_device_resource";
        return evidenceClass;
    }

    private static string Snippet(byte[] bytes, int offset, int matchLength, Encoding encoding)
    {
        var radius = encoding == Encoding.Unicode ? 90 : 70;
        var start = Math.Max(0, offset - radius);
        if (encoding == Encoding.Unicode && (start & 1) != 0)
            start++;
        var end = Math.Min(bytes.Length, offset + matchLength + radius);
        if (encoding == Encoding.Unicode && ((end - start) & 1) != 0)
            end--;
        var text = encoding.GetString(bytes, start, Math.Max(0, end - start));
        var cleaned = new string(text.Select(ch => char.IsControl(ch) && ch is not '\t' ? ' ' : ch).ToArray());
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 220 ? cleaned[..220] : cleaned;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var index = start; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
                return index;
        }
        return -1;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class PeStringImage
    {
        private sealed record Section(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer, uint Characteristics)
        {
            public bool Executable => (Characteristics & 0x20000000) != 0;
        }

        private readonly byte[] _bytes;
        private readonly bool _pe32Plus;
        private readonly ulong _imageBase;
        private readonly List<Section> _sections;

        private PeStringImage(byte[] bytes, bool pe32Plus, ulong imageBase, List<Section> sections)
        {
            _bytes = bytes;
            _pe32Plus = pe32Plus;
            _imageBase = imageBase;
            _sections = sections;
        }

        public static PeStringImage Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("Selected vendor executable is not a PE image.");
            var peOffset = I32(bytes, 0x3c);
            if (peOffset < 0 || peOffset + 24 >= bytes.Length || bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
                throw new InvalidDataException("PE header not found.");
            var sectionCount = U16(bytes, peOffset + 6);
            var optionalSize = U16(bytes, peOffset + 20);
            var optional = peOffset + 24;
            var magic = U16(bytes, optional);
            var pe32Plus = magic == 0x20b;
            if (!pe32Plus && magic != 0x10b)
                throw new InvalidDataException($"Unsupported PE optional-header magic 0x{magic:X4}.");
            ulong imageBase = pe32Plus ? U64(bytes, optional + 24) : U32(bytes, optional + 28);
            var table = optional + optionalSize;
            var sections = new List<Section>();
            for (var index = 0; index < sectionCount; index++)
            {
                var off = table + index * 40;
                Ensure(bytes, off, 40);
                sections.Add(new Section(
                    Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'),
                    U32(bytes, off + 8),
                    U32(bytes, off + 12),
                    U32(bytes, off + 16),
                    U32(bytes, off + 20),
                    U32(bytes, off + 36)));
            }
            return new PeStringImage(bytes, pe32Plus, imageBase, sections);
        }

        public uint? TryOffsetToRva(int offset)
        {
            foreach (var section in _sections)
            {
                if (offset >= section.RawPointer && offset < section.RawPointer + section.RawSize)
                    return section.VirtualAddress + (uint)(offset - section.RawPointer);
            }
            return null;
        }

        public IEnumerable<uint> FindXrefs(uint targetRva)
        {
            var result = new HashSet<uint>();
            var targetVa = _imageBase + targetRva;
            foreach (var section in _sections.Where(s => s.Executable))
            {
                var start = checked((int)section.RawPointer);
                var length = checked((int)Math.Min(section.RawSize, (uint)Math.Max(0, _bytes.Length - start)));
                var end = start + length;
                if (!_pe32Plus && targetVa <= uint.MaxValue)
                {
                    var target = checked((uint)targetVa);
                    for (var off = start; off + 4 <= end; off++)
                    {
                        if (U32(_bytes, off) == target)
                            result.Add(section.VirtualAddress + (uint)(off - start));
                    }
                }
                else if (_pe32Plus)
                {
                    for (var off = start; off + 7 <= end; off++)
                    {
                        if (_bytes[off] is not (0x48 or 0x4c) || _bytes[off + 1] != 0x8d)
                            continue;
                        var modrm = _bytes[off + 2];
                        if ((modrm & 0xc7) != 0x05)
                            continue;
                        var instructionRva = section.VirtualAddress + (uint)(off - start);
                        var disp = I32(_bytes, off + 3);
                        var resolved = unchecked((uint)((long)instructionRva + 7 + disp));
                        if (resolved == targetRva)
                            result.Add(instructionRva);
                    }
                }
            }
            return result.OrderBy(rva => rva);
        }

        private static void Ensure(byte[] bytes, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new InvalidDataException("Truncated PE structure.");
        }

        private static ushort U16(byte[] bytes, int offset)
        {
            Ensure(bytes, offset, 2);
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }

        private static uint U32(byte[] bytes, int offset)
        {
            Ensure(bytes, offset, 4);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        }

        private static ulong U64(byte[] bytes, int offset)
        {
            Ensure(bytes, offset, 8);
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        }

        private static int I32(byte[] bytes, int offset)
        {
            Ensure(bytes, offset, 4);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Vorotex.K15.VendorStaticLab;

internal sealed record PeImport(string Dll, string Name, uint IatRva, string IatVa);
internal sealed record StaticCallSite(
    string ImportName,
    string Kind,
    uint Rva,
    int FileOffset,
    string Section,
    uint ContextStartRva,
    string ContextHex,
    string[] NearbyKeywordXrefs);
internal sealed record StaticKeywordMatch(
    string RelativePath,
    string Keyword,
    string Encoding,
    long FileOffset,
    uint? Rva,
    string Snippet,
    uint[] XrefRvas);
internal sealed record VendorStaticReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Purpose,
    object Safety,
    string Executable,
    string ExecutableSha256,
    long ExecutableSize,
    string Machine,
    bool Pe32Plus,
    string ImageBase,
    List<PeImport> RelevantImports,
    List<StaticCallSite> SetFeatureCallSites,
    List<StaticCallSite> GetFeatureCallSites,
    List<StaticKeywordMatch> KeywordMatches,
    string ConclusionHint);

internal static class VendorPeAnalyzer
{
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

    private sealed record ParsedPe(
        byte[] Bytes,
        ushort Machine,
        bool Pe32Plus,
        ulong ImageBase,
        List<Section> Sections,
        List<PeImport> Imports);

    private static readonly string[] Keywords =
    [
        "sleep", "Sleep", "SLEEP", "standby", "Standby", "idle", "Idle",
        "timeout", "Timeout", "power", "Power", "suspend", "Suspend",
        "hibernate", "Hibernate", "SleepTime", "SleepTimeout", "SleepTimeOut",
        "Время сна", "время сна", "Сон", "сон", "сна",
        "休眠", "睡眠", "待机", "电源"
    ];

    public static string? FindVendorExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "VOROTEX-K15-PRO", "VOROTEX-K15-PRO.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VOROTEX-K15-PRO", "VOROTEX-K15-PRO.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static VendorStaticReport Analyze(string exePath)
    {
        var pe = ParsePe(exePath);
        var relevant = pe.Imports
            .Where(i => i.Name is "HidD_SetFeature" or "HidD_GetFeature" or "HidD_GetAttributes" or "HidP_GetCaps")
            .ToList();

        var installRoot = Path.GetDirectoryName(exePath)!;
        var keywordMatches = ScanInstallTree(installRoot, exePath, pe);

        var setImports = relevant.Where(i => i.Name == "HidD_SetFeature").ToArray();
        var getImports = relevant.Where(i => i.Name == "HidD_GetFeature").ToArray();
        var setSites = FindImportCallSites(pe, setImports, keywordMatches);
        var getSites = FindImportCallSites(pe, getImports, keywordMatches);

        return new VendorStaticReport(
            1,
            DateTimeOffset.UtcNow,
            "read-only static localization of VOROTEX K15 HID feature-report code paths",
            new
            {
                executableModified = false,
                processInjected = false,
                deviceOpened = false,
                hidReadsPerformed = false,
                hidWritesPerformed = false,
                driverInstalled = false,
                reportContainsOnlyStaticMetadataAndBoundedHexWindows = true
            },
            Path.GetFileName(exePath),
            Sha256(exePath),
            new FileInfo(exePath).Length,
            $"0x{pe.Machine:X4}",
            pe.Pe32Plus,
            $"0x{pe.ImageBase:X}",
            relevant,
            setSites,
            getSites,
            keywordMatches,
            "Prioritize HidD_SetFeature call-sites that are near xrefs to sleep/idle/power strings. If no useful xref exists, use the bounded call-site hex windows to guide the next dynamic capture step.");
    }

    public static string ToText(VendorStaticReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 Vendor Static Lab");
        sb.AppendLine($"EXE: {report.Executable}");
        sb.AppendLine($"SHA256: {report.ExecutableSha256}");
        sb.AppendLine($"Machine: {report.Machine}; PE32+: {report.Pe32Plus}; ImageBase: {report.ImageBase}");
        sb.AppendLine("Writes/injection/device access: NONE");
        sb.AppendLine();
        sb.AppendLine("Relevant imports:");
        foreach (var import in report.RelevantImports)
            sb.AppendLine($"  {import.Dll}!{import.Name} IAT RVA=0x{import.IatRva:X8} VA={import.IatVa}");
        sb.AppendLine();
        sb.AppendLine($"HidD_SetFeature call-sites: {report.SetFeatureCallSites.Count}");
        foreach (var call in report.SetFeatureCallSites)
        {
            sb.AppendLine($"  RVA 0x{call.Rva:X8} [{call.Kind}] section={call.Section}");
            foreach (var near in call.NearbyKeywordXrefs)
                sb.AppendLine($"    near: {near}");
        }
        sb.AppendLine();
        sb.AppendLine($"HidD_GetFeature call-sites: {report.GetFeatureCallSites.Count}");
        foreach (var call in report.GetFeatureCallSites)
            sb.AppendLine($"  RVA 0x{call.Rva:X8} [{call.Kind}] section={call.Section}");
        sb.AppendLine();
        sb.AppendLine($"Sleep/power keyword matches: {report.KeywordMatches.Count}");
        foreach (var match in report.KeywordMatches.Take(120))
        {
            sb.AppendLine($"  {match.RelativePath} @{match.FileOffset}: {match.Keyword} [{match.Encoding}] {match.Snippet}");
            foreach (var xref in match.XrefRvas.Take(12))
                sb.AppendLine($"    xref RVA 0x{xref:X8}");
        }
        sb.AppendLine();
        sb.AppendLine(report.ConclusionHint);
        return sb.ToString();
    }

    private static ParsedPe ParsePe(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidDataException("VOROTEX executable is not a valid DOS/PE image.");

        var peOffset = I32(bytes, 0x3c);
        if (peOffset < 0 || peOffset + 24 >= bytes.Length ||
            bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
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
            var name = Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0');
            sections.Add(new Section(
                name,
                U32(bytes, off + 8),
                U32(bytes, off + 12),
                U32(bytes, off + 16),
                U32(bytes, off + 20),
                U32(bytes, off + 36)));
        }

        var imports = ParseImports(bytes, pe32Plus, imageBase, importRva, sections);
        return new ParsedPe(bytes, machine, pe32Plus, imageBase, sections, imports);
    }

    private static List<PeImport> ParseImports(
        byte[] bytes, bool pe32Plus, ulong imageBase, uint importRva, List<Section> sections)
    {
        var result = new List<PeImport>();
        if (importRva == 0)
            return result;
        var descriptorOffset = RvaToOffset(importRva, sections, bytes.Length);
        var pointerSize = pe32Plus ? 8 : 4;

        for (var descriptor = descriptorOffset; descriptor + 20 <= bytes.Length; descriptor += 20)
        {
            var originalThunk = U32(bytes, descriptor);
            var nameRva = U32(bytes, descriptor + 12);
            var firstThunk = U32(bytes, descriptor + 16);
            if (originalThunk == 0 && nameRva == 0 && firstThunk == 0)
                break;

            var dll = ReadAsciiZ(bytes, RvaToOffset(nameRva, sections, bytes.Length));
            var lookupRva = originalThunk != 0 ? originalThunk : firstThunk;
            var lookupOffset = RvaToOffset(lookupRva, sections, bytes.Length);

            for (var index = 0; ; index++)
            {
                var thunkOffset = lookupOffset + index * pointerSize;
                Ensure(bytes, thunkOffset, pointerSize);
                var thunk = pe32Plus ? U64(bytes, thunkOffset) : U32(bytes, thunkOffset);
                if (thunk == 0)
                    break;

                var ordinalMask = pe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                if ((thunk & ordinalMask) != 0)
                    continue;

                var importByNameRva = checked((uint)thunk);
                var nameOffset = RvaToOffset(importByNameRva, sections, bytes.Length) + 2;
                var name = ReadAsciiZ(bytes, nameOffset);
                var iatRva = checked(firstThunk + (uint)(index * pointerSize));
                result.Add(new PeImport(dll, name, iatRva, $"0x{imageBase + iatRva:X}"));
            }
        }
        return result;
    }

    private static List<StaticCallSite> FindImportCallSites(
        ParsedPe pe, IReadOnlyCollection<PeImport> imports, List<StaticKeywordMatch> keywordMatches)
    {
        var raw = new List<(string Name, string Kind, uint Rva, int Offset, Section Section)>();
        foreach (var import in imports)
        {
            var thunkRvas = new HashSet<uint>();
            foreach (var section in pe.Sections.Where(s => s.Executable))
            {
                var start = checked((int)section.RawPointer);
                var length = checked((int)Math.Min(section.RawSize, (uint)Math.Max(0, pe.Bytes.Length - start)));
                var end = start + length;
                for (var off = start; off + 6 <= end; off++)
                {
                    if (pe.Bytes[off] != 0xff || pe.Bytes[off + 1] is not (0x15 or 0x25))
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
                    if (pe.Bytes[off + 1] == 0x15)
                        raw.Add((import.Name, "direct_iat_call", instructionRva, off, section));
                    else
                        thunkRvas.Add(instructionRva);
                }
            }

            if (thunkRvas.Count > 0)
            {
                foreach (var section in pe.Sections.Where(s => s.Executable))
                {
                    var start = checked((int)section.RawPointer);
                    var length = checked((int)Math.Min(section.RawSize, (uint)Math.Max(0, pe.Bytes.Length - start)));
                    var end = start + length;
                    for (var off = start; off + 5 <= end; off++)
                    {
                        if (pe.Bytes[off] != 0xe8)
                            continue;
                        var instructionRva = section.VirtualAddress + (uint)(off - start);
                        var rel = I32(pe.Bytes, off + 1);
                        var target = unchecked((uint)((long)instructionRva + 5 + rel));
                        if (thunkRvas.Contains(target))
                            raw.Add((import.Name, "call_via_import_thunk", instructionRva, off, section));
                    }
                }
            }
        }

        return raw
            .GroupBy(x => (x.Name, x.Rva))
            .Select(g => g.First())
            .OrderBy(x => x.Rva)
            .Select(x =>
            {
                var contextStart = Math.Max(checked((int)x.Section.RawPointer), x.Offset - 96);
                var sectionEnd = Math.Min(pe.Bytes.Length, checked((int)(x.Section.RawPointer + x.Section.RawSize)));
                var contextEnd = Math.Min(sectionEnd, x.Offset + 96);
                var contextRva = x.Section.VirtualAddress + (uint)(contextStart - (int)x.Section.RawPointer);
                var near = keywordMatches
                    .SelectMany(m => m.XrefRvas.Select(rva => (Match: m, Rva: rva)))
                    .Where(pair => Math.Abs((long)pair.Rva - x.Rva) <= 0x3000)
                    .OrderBy(pair => Math.Abs((long)pair.Rva - x.Rva))
                    .Take(24)
                    .Select(pair => $"{pair.Match.Keyword} xref=0x{pair.Rva:X8} delta={(long)pair.Rva - x.Rva:+#;-#;0}")
                    .ToArray();
                return new StaticCallSite(
                    x.Name,
                    x.Kind,
                    x.Rva,
                    x.Offset,
                    x.Section.Name,
                    contextRva,
                    Convert.ToHexString(pe.Bytes.AsSpan(contextStart, contextEnd - contextStart)).ToLowerInvariant(),
                    near);
            })
            .ToList();
    }

    private static List<StaticKeywordMatch> ScanInstallTree(string installRoot, string exePath, ParsedPe exePe)
    {
        var matches = new List<StaticKeywordMatch>();
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
                if (info.Length == 0 || info.Length > 64L * 1024 * 1024)
                    continue;
                var bytes = File.ReadAllBytes(file);
                var relative = Path.GetRelativePath(installRoot, file);
                var isExe = string.Equals(Path.GetFullPath(file), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);

                foreach (var keyword in Keywords)
                {
                    foreach (var spec in EncodedVariants(keyword))
                    {
                        var from = 0;
                        while (from <= bytes.Length - spec.Bytes.Length && matches.Count < 800)
                        {
                            var found = IndexOf(bytes, spec.Bytes, from);
                            if (found < 0)
                                break;
                            uint? rva = null;
                            uint[] xrefs = [];
                            if (isExe)
                            {
                                rva = TryOffsetToRva(found, exePe.Sections);
                                if (rva is uint stringRva)
                                    xrefs = FindStaticXrefs(exePe, stringRva).Take(64).ToArray();
                            }
                            matches.Add(new StaticKeywordMatch(
                                relative,
                                keyword,
                                spec.Name,
                                found,
                                rva,
                                Snippet(bytes, found, spec.Bytes.Length, spec.Encoding),
                                xrefs));
                            from = found + Math.Max(1, spec.Bytes.Length);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A locked/opaque vendor file does not invalidate the rest of the static scan.
            }
            if (matches.Count >= 800)
                break;
        }

        return matches
            .GroupBy(m => (m.RelativePath, m.FileOffset, m.Keyword))
            .Select(g => g.OrderByDescending(x => x.XrefRvas.Length).First())
            .OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.FileOffset)
            .ToList();
    }

    private static IEnumerable<(string Name, Encoding Encoding, byte[] Bytes)> EncodedVariants(string keyword)
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in new[]
        {
            ("utf8", (Encoding)new UTF8Encoding(false)),
            ("utf16le", Encoding.Unicode)
        })
        {
            var bytes = pair.Item2.GetBytes(keyword);
            var key = Convert.ToHexString(bytes);
            if (yielded.Add(key))
                yield return (pair.Item1, pair.Item2, bytes);
        }
    }

    private static IEnumerable<uint> FindStaticXrefs(ParsedPe pe, uint targetRva)
    {
        var xrefs = new HashSet<uint>();
        var targetVa = pe.ImageBase + targetRva;
        foreach (var section in pe.Sections.Where(s => s.Executable))
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, (uint)Math.Max(0, pe.Bytes.Length - start)));
            var end = start + length;

            if (!pe.Pe32Plus && targetVa <= uint.MaxValue)
            {
                var target = checked((uint)targetVa);
                for (var off = start; off + 4 <= end; off++)
                {
                    if (U32(pe.Bytes, off) == target)
                        xrefs.Add(section.VirtualAddress + (uint)(off - start));
                }
            }
            else if (pe.Pe32Plus)
            {
                for (var off = start; off + 7 <= end; off++)
                {
                    if (pe.Bytes[off] is not (0x48 or 0x4c))
                        continue;
                    if (pe.Bytes[off + 1] != 0x8d)
                        continue;
                    var modrm = pe.Bytes[off + 2];
                    if ((modrm & 0xc7) != 0x05)
                        continue;
                    var instructionRva = section.VirtualAddress + (uint)(off - start);
                    var disp = I32(pe.Bytes, off + 3);
                    var resolved = unchecked((uint)((long)instructionRva + 7 + disp));
                    if (resolved == targetRva)
                        xrefs.Add(instructionRva);
                }
            }
        }
        return xrefs.OrderBy(x => x);
    }

    private static string Snippet(byte[] bytes, int offset, int matchLength, Encoding encoding)
    {
        var radius = encoding == Encoding.Unicode ? 96 : 72;
        var start = Math.Max(0, offset - radius);
        if (encoding == Encoding.Unicode && (start & 1) != 0)
            start++;
        var end = Math.Min(bytes.Length, offset + matchLength + radius);
        if (encoding == Encoding.Unicode && ((end - start) & 1) != 0)
            end--;
        string value;
        try
        {
            value = encoding.GetString(bytes, start, Math.Max(0, end - start));
        }
        catch
        {
            return string.Empty;
        }
        var cleaned = new string(value.Select(ch => char.IsControl(ch) && ch is not '\t' ? ' ' : ch).ToArray());
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 240 ? cleaned[..240] : cleaned;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (needle.Length == 0)
            return -1;
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    private static int RvaToOffset(uint rva, IReadOnlyList<Section> sections, int fileLength)
    {
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + span)
            {
                var delta = rva - section.VirtualAddress;
                var offset = checked((int)(section.RawPointer + delta));
                if (offset < 0 || offset >= fileLength)
                    break;
                return offset;
            }
        }
        if (rva < fileLength)
            return checked((int)rva);
        throw new InvalidDataException($"RVA 0x{rva:X8} is outside mapped PE sections.");
    }

    private static uint? TryOffsetToRva(int offset, IReadOnlyList<Section> sections)
    {
        foreach (var section in sections)
        {
            if (offset >= section.RawPointer && offset < section.RawPointer + section.RawSize)
                return section.VirtualAddress + (uint)(offset - section.RawPointer);
        }
        return null;
    }

    private static string ReadAsciiZ(byte[] bytes, int offset)
    {
        Ensure(bytes, offset, 1);
        var end = offset;
        while (end < bytes.Length && bytes[end] != 0 && end - offset < 4096)
            end++;
        return Encoding.ASCII.GetString(bytes, offset, end - offset);
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

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

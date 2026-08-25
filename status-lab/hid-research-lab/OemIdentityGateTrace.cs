using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemIdentityGateDeviceRow(
    int? DevType,
    int? DevCmpStr,
    string Pid,
    string Vid,
    int? IsUsb,
    string DevName,
    string UiTextName);

internal sealed record OemIdentityGateTokenRef(
    string Token,
    string Encoding,
    int FileOffset,
    uint? Rva,
    List<uint> DirectXrefRvas,
    long? NearestProductStringCallDistance);

internal sealed record OemIdentityGateSide(
    string Executable,
    string NdeviceRelativePath,
    List<OemIdentityGateDeviceRow> Rows,
    List<uint> ProductStringCallSites,
    List<OemIdentityGateTokenRef> TokenRefs);

internal sealed record OemIdentityGateTraceReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemIdentityGateSide A,
    OemIdentityGateSide B,
    bool VidPidMatrixEqual,
    bool DevCmpStrEqual,
    bool DevNameEqual,
    bool UiTextNameEqual,
    int EvidenceScore,
    List<string> Evidence,
    List<string> Notes);

internal static class OemIdentityGateTraceAnalyzer
{
    private static readonly string[] TraceTokens =
    [
        "Ndevice.json", "DevCmpStr", "DevName", "UITextName", "Pid", "Vid"
    ];

    public static OemIdentityGateTraceReport Analyze(string exeA, string exeB)
    {
        if (!File.Exists(exeA))
            throw new FileNotFoundException("OEM identity-gate EXE A not found.", exeA);
        if (!File.Exists(exeB))
            throw new FileNotFoundException("OEM identity-gate EXE B not found.", exeB);

        exeA = Path.GetFullPath(exeA);
        exeB = Path.GetFullPath(exeB);

        var broad = OemDeviceIdentityDiffAnalyzer.Analyze(exeA, exeB);
        var a = AnalyzeSide(exeA, broad.A);
        var b = AnalyzeSide(exeB, broad.B);

        var matrixA = a.Rows.Select(MatrixKey).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var matrixB = b.Rows.Select(MatrixKey).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var vidPidMatrixEqual = matrixA.SequenceEqual(matrixB, StringComparer.OrdinalIgnoreCase);

        var cmpA = a.Rows.Select(r => r.DevCmpStr).OrderBy(x => x).ToArray();
        var cmpB = b.Rows.Select(r => r.DevCmpStr).OrderBy(x => x).ToArray();
        var devCmpStrEqual = cmpA.SequenceEqual(cmpB);

        var namesA = a.Rows.OrderBy(MatrixKey, StringComparer.OrdinalIgnoreCase).Select(r => r.DevName).ToArray();
        var namesB = b.Rows.OrderBy(MatrixKey, StringComparer.OrdinalIgnoreCase).Select(r => r.DevName).ToArray();
        var uiA = a.Rows.OrderBy(MatrixKey, StringComparer.OrdinalIgnoreCase).Select(r => r.UiTextName).ToArray();
        var uiB = b.Rows.OrderBy(MatrixKey, StringComparer.OrdinalIgnoreCase).Select(r => r.UiTextName).ToArray();
        var devNameEqual = namesA.SequenceEqual(namesB, StringComparer.OrdinalIgnoreCase);
        var uiTextNameEqual = uiA.SequenceEqual(uiB, StringComparer.OrdinalIgnoreCase);

        var allCmpOne = a.Rows.Count > 0 && b.Rows.Count > 0 &&
                        a.Rows.All(r => r.DevCmpStr == 1) && b.Rows.All(r => r.DevCmpStr == 1);
        var productCallsPresent = a.ProductStringCallSites.Count > 0 && b.ProductStringCallSites.Count > 0;
        var devNameNearA = HasNearProductXref(a, "DevName", 0x8000);
        var devNameNearB = HasNearProductXref(b, "DevName", 0x8000);
        var cmpNearA = HasNearProductXref(a, "DevCmpStr", 0x8000);
        var cmpNearB = HasNearProductXref(b, "DevCmpStr", 0x8000);

        var score = 0;
        var evidence = new List<string>();
        if (vidPidMatrixEqual)
        {
            score += 2;
            evidence.Add("Ndevice.json exposes the same normalized DevType/PID/VID/isUSB matrix on both OEM sides.");
        }
        else
        {
            evidence.Add("Ndevice.json normalized DevType/PID/VID/isUSB matrix differs between OEM sides.");
        }

        if (devCmpStrEqual)
        {
            score += 1;
            evidence.Add("DevCmpStr values are equal across the compared model rows.");
        }
        if (allCmpOne)
        {
            score += 2;
            evidence.Add("Every compared model row uses DevCmpStr=1.");
        }
        if (!devNameEqual)
        {
            score += 2;
            evidence.Add("DevName differs while the normalized VID/PID transport matrix remains aligned.");
        }
        if (!uiTextNameEqual)
            evidence.Add("UITextName also differs between OEM packages.");

        if (productCallsPresent)
        {
            score += 2;
            evidence.Add($"Both binaries have statically identified HidD_GetProductString call-sites (A={a.ProductStringCallSites.Count}, B={b.ProductStringCallSites.Count}).");
        }

        if (devNameNearA && devNameNearB)
        {
            score += 1;
            evidence.Add("Both binaries contain a direct static xref candidate to DevName within 0x8000 bytes of a HidD_GetProductString call-site.");
        }
        if (cmpNearA && cmpNearB)
        {
            score += 1;
            evidence.Add("Both binaries contain a direct static xref candidate to DevCmpStr within 0x8000 bytes of a HidD_GetProductString call-site.");
        }

        var verdict = SelectVerdict(vidPidMatrixEqual, allCmpOne, devNameEqual, productCallsPresent, score);
        return new OemIdentityGateTraceReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "read-only static trace of the OEM model/product identity gate after HID discovery",
            new
            {
                executableModified = false,
                packageModified = false,
                processAttached = false,
                processInjected = false,
                debuggerAttached = false,
                deviceOpened = false,
                featureReportsQueried = false,
                hidWritesPerformed = false,
                vidPidSpoofed = false,
                driverInstalled = false,
                registryModified = false,
                profileSelectionChanged = false,
                sleepSettingChanged = false
            },
            a,
            b,
            vidPidMatrixEqual,
            devCmpStrEqual,
            devNameEqual,
            uiTextNameEqual,
            score,
            evidence,
            [
                "PRODUCT_STRING_GATE_LIKELY is intentionally weaker than proof of the exact runtime comparison branch.",
                "PRODUCT_STRING_GATE_PROVEN_STATICALLY is reserved and is not emitted by this implementation without direct data-flow proof.",
                "Direct xref candidates are absolute-address references found in .text; proximity is evidence, not proof of semantic use.",
                "No HID handle is opened and no vendor process or package is modified."
            ]);
    }

    public static string ToText(OemIdentityGateTraceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - OEM Identity Gate Trace");
        sb.AppendLine("Safety: READ-ONLY; no HID handles/writes, no feature reports, no vendor-process attach/injection, no patching or VID/PID spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Evidence score: {report.EvidenceScore}");
        sb.AppendLine($"VID/PID matrix equal: {report.VidPidMatrixEqual}");
        sb.AppendLine($"DevCmpStr equal: {report.DevCmpStrEqual}");
        sb.AppendLine($"DevName equal: {report.DevNameEqual}");
        sb.AppendLine($"UITextName equal: {report.UiTextNameEqual}");
        sb.AppendLine();
        AppendSide(sb, "A", report.A);
        AppendSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence)
            sb.AppendLine("  - " + item);
        sb.AppendLine();
        foreach (var note in report.Notes)
            sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static OemIdentityGateSide AnalyzeSide(string exe, OemIdentityBinaryReport broad)
    {
        var root = Path.GetDirectoryName(exe)!;
        var ndevice = Path.Combine(root, "res", "Home", "Icons", "Ndevice.json");
        if (!File.Exists(ndevice))
            throw new FileNotFoundException("Ndevice.json was not found beside the selected OEM executable.", ndevice);

        var rows = ParseRows(ndevice);
        var productCalls = broad.RelevantImports
            .FirstOrDefault(i => i.Name.Equals("HidD_GetProductString", StringComparison.OrdinalIgnoreCase))?
            .CallSites.Select(c => c.Rva).OrderBy(x => x).ToList() ?? [];

        var layout = PeLayout.Parse(exe);
        var refs = new List<OemIdentityGateTokenRef>();
        foreach (var token in TraceTokens)
            refs.AddRange(FindTokenRefs(layout, token, productCalls));

        return new OemIdentityGateSide(
            Path.GetFileName(exe),
            Path.GetRelativePath(root, ndevice),
            rows,
            productCalls,
            refs);
    }

    private static List<OemIdentityGateDeviceRow> ParseRows(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var rows = new List<OemIdentityGateDeviceRow>();
        Walk(doc.RootElement, rows);
        if (rows.Count == 0)
            throw new InvalidDataException("No device rows with Pid/Vid were found in Ndevice.json.");
        return rows;
    }

    private static void Walk(JsonElement element, List<OemIdentityGateDeviceRow> rows)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "Pid", out var pid) && TryGetString(element, "Vid", out var vid))
            {
                rows.Add(new OemIdentityGateDeviceRow(
                    TryGetInt(element, "DevType"),
                    TryGetInt(element, "DevCmpStr"),
                    pid,
                    vid,
                    TryGetInt(element, "isUSB"),
                    GetString(element, "DevName"),
                    GetString(element, "UITextName")));
            }
            foreach (var property in element.EnumerateObject())
                Walk(property.Value, rows);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                Walk(child, rows);
        }
    }

    private static IEnumerable<OemIdentityGateTokenRef> FindTokenRefs(PeLayout pe, string token, List<uint> productCalls)
    {
        foreach (var (encoding, pattern) in new[]
                 {
                     ("ascii", Encoding.ASCII.GetBytes(token)),
                     ("utf16le", Encoding.Unicode.GetBytes(token))
                 })
        {
            foreach (var fileOffset in FindAll(pe.Bytes, pattern).Take(24))
            {
                var rva = pe.FileOffsetToRva(fileOffset);
                var xrefs = rva is null ? [] : pe.FindAbsoluteTextXrefs(rva.Value).Take(24).ToList();
                long? nearest = null;
                if (xrefs.Count > 0 && productCalls.Count > 0)
                    nearest = xrefs.SelectMany(x => productCalls.Select(p => Math.Abs((long)x - p))).Min();
                yield return new OemIdentityGateTokenRef(token, encoding, fileOffset, rva, xrefs, nearest);
            }
        }
    }

    private static bool HasNearProductXref(OemIdentityGateSide side, string token, long maxDistance) =>
        side.TokenRefs.Any(r => r.Token.Equals(token, StringComparison.OrdinalIgnoreCase) &&
                                r.NearestProductStringCallDistance is not null &&
                                r.NearestProductStringCallDistance <= maxDistance);

    private static string SelectVerdict(bool matrixEqual, bool allCmpOne, bool devNameEqual, bool productCalls, int score)
    {
        if (!matrixEqual)
            return "IDENTITY_GATE_UNRESOLVED";
        if (matrixEqual && allCmpOne && !devNameEqual && productCalls && score >= 8)
            return "PRODUCT_STRING_GATE_LIKELY";
        if (matrixEqual && !productCalls)
            return "VID_PID_GATE_NOT_SUPPORTED_BY_MODEL_TABLE";
        return "IDENTITY_GATE_UNRESOLVED";
    }

    private static string MatrixKey(OemIdentityGateDeviceRow row)
    {
        var vids = row.Vid.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.ToLowerInvariant())
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
        return $"type={row.DevType?.ToString() ?? "?"};pid={row.Pid.ToLowerInvariant()};vid={string.Join(',', vids)};usb={row.IsUsb?.ToString() ?? "?"}";
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return value.Length > 0;
        }
        if (property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetRawText();
            return true;
        }
        return false;
    }

    private static string GetString(JsonElement element, string name) =>
        TryGetString(element, name, out var value) ? value : string.Empty;

    private static int? TryGetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            return number;
        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number))
            return number;
        return null;
    }

    private static List<int> FindAll(byte[] bytes, byte[] pattern)
    {
        var result = new List<int>();
        if (pattern.Length == 0 || bytes.Length < pattern.Length)
            return result;
        for (var i = 0; i <= bytes.Length - pattern.Length; i++)
        {
            if (!bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                continue;
            result.Add(i);
            i += pattern.Length - 1;
        }
        return result;
    }

    private static void AppendSide(StringBuilder sb, string label, OemIdentityGateSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  Ndevice: {side.NdeviceRelativePath}");
        sb.AppendLine($"  HidD_GetProductString call-sites: {string.Join(", ", side.ProductStringCallSites.Select(x => $"0x{x:X8}"))}");
        foreach (var row in side.Rows.OrderBy(MatrixKey, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"  row: DevType={row.DevType}; DevCmpStr={row.DevCmpStr}; Pid={row.Pid}; Vid={row.Vid}; isUSB={row.IsUsb}; DevName={row.DevName}; UITextName={row.UiTextName}");
        foreach (var token in side.TokenRefs.Where(x => x.DirectXrefRvas.Count > 0).Take(24))
            sb.AppendLine($"  xref: {token.Token}/{token.Encoding} RVA={(token.Rva is null ? "n/a" : $"0x{token.Rva:X8}")} refs={string.Join(",", token.DirectXrefRvas.Select(x => $"0x{x:X8}"))} nearestProduct={token.NearestProductStringCallDistance?.ToString() ?? "n/a"}");
        sb.AppendLine();
    }

    private sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize)
    {
        public bool ContainsFileOffset(int offset) => offset >= RawOffset && (uint)offset < RawOffset + RawSize;
    }

    private sealed class PeLayout
    {
        public required byte[] Bytes { get; init; }
        public required uint ImageBase { get; init; }
        public required List<PeSection> Sections { get; init; }

        public static PeLayout Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("Not a PE executable.");
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
            if (peOffset < 0 || peOffset + 0x100 > bytes.Length || bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
                throw new InvalidDataException("Invalid PE header.");
            var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 6, 2));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 20, 2));
            var optional = peOffset + 24;
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional, 2));
            if (magic != 0x10B)
                throw new InvalidDataException("OEM identity gate trace currently expects PE32 OEM executables.");
            var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optional + 28, 4));
            var sectionTable = optional + optionalSize;
            var sections = new List<PeSection>();
            for (var i = 0; i < sectionCount; i++)
            {
                var off = sectionTable + (i * 40);
                if (off + 40 > bytes.Length)
                    break;
                var name = Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0');
                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 8, 4));
                var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 12, 4));
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 16, 4));
                var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 20, 4));
                sections.Add(new PeSection(name, virtualAddress, virtualSize, rawOffset, rawSize));
            }
            return new PeLayout { Bytes = bytes, ImageBase = imageBase, Sections = sections };
        }

        public uint? FileOffsetToRva(int fileOffset)
        {
            var section = Sections.FirstOrDefault(s => s.ContainsFileOffset(fileOffset));
            return section is null ? null : section.VirtualAddress + ((uint)fileOffset - section.RawOffset);
        }

        public IEnumerable<uint> FindAbsoluteTextXrefs(uint targetRva)
        {
            var targetVa = ImageBase + targetRva;
            Span<byte> needle = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(needle, targetVa);
            foreach (var section in Sections.Where(s => s.Name.Equals(".text", StringComparison.OrdinalIgnoreCase)))
            {
                var start = (int)section.RawOffset;
                var end = Math.Min(Bytes.Length, start + (int)section.RawSize);
                for (var i = start; i <= end - 4; i++)
                {
                    if (!Bytes.AsSpan(i, 4).SequenceEqual(needle))
                        continue;
                    yield return section.VirtualAddress + ((uint)i - section.RawOffset);
                }
            }
        }
    }
}

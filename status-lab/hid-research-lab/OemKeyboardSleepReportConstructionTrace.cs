using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemSleepReportBufferReference(
    uint Rva,
    int RelativeToSetFeature,
    int ReportOffset,
    string Access,
    string Mnemonic,
    string Bytes,
    string Text,
    string ValueKind,
    string ValueExpression);

internal sealed record OemSleepReportHelperCandidate(
    uint BufferLeaRva,
    int RelativeToSetFeature,
    uint CallRva,
    string Target,
    string[] Steps);

internal sealed record OemSleepReportConstructionSide(
    string Executable,
    string Sha256,
    uint SetFeatureCallRva,
    int ReportBaseDisplacement,
    OemSleepReportBufferReference[] References,
    OemSleepReportHelperCandidate[] HelperCandidates,
    string Fingerprint,
    string[] Notes);

internal sealed record OemSleepReportConstructionTraceReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemSleepReportConstructionSide A,
    OemSleepReportConstructionSide B,
    int CorrespondingReferenceCount,
    int CorrespondingWriteCount,
    int CorrespondingHelperCount,
    string[] CorrespondingKeys,
    string[] Evidence,
    string[] Notes);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    internal static OemSleepReportConstructionTraceReport AnalyzeKeyboardSleepReportConstruction(string exeA, string exeB)
    {
        var baseline = AnalyzeKeyboardSleepReportRecovered(exeA, exeB);
        var a = TraceSleepReportConstruction(Path.GetFullPath(exeA), baseline.A);
        var b = TraceSleepReportConstruction(Path.GetFullPath(exeB), baseline.B);

        var aRefs = a.References.ToDictionary(ConstructionReferenceKey, x => x, StringComparer.Ordinal);
        var bRefs = b.References.ToDictionary(ConstructionReferenceKey, x => x, StringComparer.Ordinal);
        var correspondingKeys = aRefs.Keys.Intersect(bRefs.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var correspondingWrites = correspondingKeys.Count(k => aRefs[k].Access == "write" && bRefs[k].Access == "write");

        var aHelpers = a.HelperCandidates.Select(ConstructionHelperKey).ToHashSet(StringComparer.Ordinal);
        var bHelpers = b.HelperCandidates.Select(ConstructionHelperKey).ToHashSet(StringComparer.Ordinal);
        var correspondingHelpers = aHelpers.Intersect(bHelpers, StringComparer.Ordinal).Count();

        var verdict = correspondingWrites > 0
            ? "REPORT_BUFFER_WRITES_CORRESPONDING"
            : correspondingHelpers > 0
                ? "REPORT_CONSTRUCTION_HELPER_CORRESPONDING"
                : correspondingKeys.Length > 0
                    ? "REPORT_CONSTRUCTION_REFERENCES_CORRESPONDING"
                    : "REPORT_CONSTRUCTION_SLICE_UNRESOLVED";

        var evidence = new List<string>();
        if (correspondingKeys.Length > 0)
            evidence.Add($"Recovered {correspondingKeys.Length} instruction-shape/report-offset references at matching positions relative to the exact SetFeature call on both OEM binaries.");
        if (correspondingWrites > 0)
            evidence.Add($"Recovered {correspondingWrites} corresponding direct write(s) into the 41-byte EBP-relative report buffer on both OEM binaries.");
        if (correspondingHelpers > 0)
            evidence.Add($"Recovered {correspondingHelpers} corresponding helper-call shape(s) that receive the report-buffer base address before SetFeature.");

        return new OemSleepReportConstructionTraceReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static raw-code reverse slice of the proven 41-byte SetFeature report buffer before keyboard SleepTime provenance is promoted",
            new
            {
                executableModified = false,
                packageModified = false,
                processStarted = false,
                processAttached = false,
                processInjected = false,
                debuggerAttached = false,
                deviceOpened = false,
                featureReportsQueried = false,
                hidWritesPerformed = false,
                reportReplayed = false,
                sleepSettingChanged = false,
                firmwareModified = false
            },
            a,
            b,
            correspondingKeys.Length,
            correspondingWrites,
            correspondingHelpers,
            correspondingKeys.Take(160).ToArray(),
            evidence.ToArray(),
            [
                "This stage proves report-construction structure only. A matching write or helper is not SleepTime provenance by itself.",
                "Candidates are accepted from exact one-instruction decodes anchored at each raw .text RVA, then cross-correlated by position relative to the already-proven SetFeature call.",
                "The scan is bounded to 0x2400 bytes before the exact SetFeature call and only retains EBP-relative references inside the proven 41-byte report range.",
                "No OEM code is executed and no HID/device handle is opened."
            ]);
    }

    internal static string KeyboardSleepReportConstructionToText(OemSleepReportConstructionTraceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - SleepTime SetFeature Report Construction Slice");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, feature execution/replay, process attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Corresponding references: {report.CorrespondingReferenceCount}");
        sb.AppendLine($"Corresponding writes: {report.CorrespondingWriteCount}");
        sb.AppendLine($"Corresponding helpers: {report.CorrespondingHelperCount}");
        sb.AppendLine();
        AppendSleepConstructionSide(sb, "A", report.A);
        AppendSleepConstructionSide(sb, "B", report.B);
        sb.AppendLine("Corresponding keys:");
        foreach (var key in report.CorrespondingKeys) sb.AppendLine("  " + key);
        sb.AppendLine();
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        foreach (var note in report.Notes) sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendSleepConstructionSide(StringBuilder sb, string label, OemSleepReportConstructionSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  SHA256={side.Sha256}");
        sb.AppendLine($"  SetFeature=0x{side.SetFeatureCallRva:X8}; reportBase=EBP{FormatSignedHex(side.ReportBaseDisplacement)}");
        sb.AppendLine("  report references:");
        foreach (var r in side.References.Take(220))
            sb.AppendLine($"    rel={r.RelativeToSetFeature,6} report[{r.ReportOffset,2}] {r.Access,-7} {r.Mnemonic,-8} @0x{r.Rva:X8} {r.ValueKind}={r.ValueExpression} :: {r.Bytes} {r.Text}");
        sb.AppendLine("  report-buffer helper candidates:");
        foreach (var h in side.HelperCandidates.Take(80))
        {
            sb.AppendLine($"    lea=0x{h.BufferLeaRva:X8} rel={h.RelativeToSetFeature} call=0x{h.CallRva:X8} target={h.Target}");
            foreach (var step in h.Steps) sb.AppendLine("      " + step);
        }
        sb.AppendLine($"  fingerprint={side.Fingerprint}");
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static OemSleepReportConstructionSide TraceSleepReportConstruction(string exe, OemKeyboardSleepReportSide baseline)
    {
        var call = baseline.SetFeatureCalls.FirstOrDefault(x => x.ReportLength41Proven && x.StackBufferBaseDisplacement is not null)
            ?? throw new InvalidDataException("A proven 41-byte SetFeature call with an EBP-relative report pointer is required before report-construction tracing.");
        var pe = NdevicePe.Parse(exe);
        var baseDisp = call.StackBufferBaseDisplacement!.Value;
        var startRva = call.CallRva > 0x2400 ? call.CallRva - 0x2400u : pe.TextStart;
        startRva = Math.Max(startRva, pe.TextStart);
        var refs = new List<OemSleepReportBufferReference>();

        for (var rva = startRva; rva < call.CallRva; rva++)
        {
            var item = DecodeOneRawInstruction(pe, rva);
            if (item is null) continue;
            var ins = item.Instruction;
            if (ins.Op0Kind != OpKind.Memory && ins.Op1Kind != OpKind.Memory) continue;
            if (Normalize(ins.MemoryBase) != Register.EBP) continue;
            var disp = SignedDisp(ins);
            if (disp < baseDisp || disp >= baseDisp + 41L) continue;

            var offset = checked((int)(disp - baseDisp));
            var access = ins.Mnemonic == Mnemonic.Lea
                ? "address"
                : IsMemoryWrite(ins) && ins.Op0Kind == OpKind.Memory
                    ? "write"
                    : "read";
            var (valueKind, valueExpression) = DescribeConstructionValue(ins, access);
            refs.Add(new OemSleepReportBufferReference(
                rva,
                checked((int)((long)rva - call.CallRva)),
                offset,
                access,
                ins.Mnemonic.ToString(),
                SleepBytes(pe, item),
                item.Text,
                valueKind,
                valueExpression));
        }

        refs = refs
            .GroupBy(x => (x.Rva, x.ReportOffset, x.Access, x.Mnemonic))
            .Select(x => x.First())
            .OrderBy(x => x.Rva)
            .ToList();

        var helpers = refs
            .Where(x => x.Access == "address" && x.ReportOffset == 0)
            .Select(x => TraceConstructionHelperAfterLea(pe, x, call.CallRva))
            .Where(x => x is not null)
            .Cast<OemSleepReportHelperCandidate>()
            .GroupBy(ConstructionHelperKey, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.CallRva)
            .ToArray();

        var fp = string.Join('|', refs.Select(ConstructionReferenceKey));
        var notes = new List<string>();
        if (refs.Count == 0) notes.Add("No EBP-relative references inside the proven 41-byte report range were recovered in the bounded raw-code slice.");
        if (refs.Count > 0 && !refs.Any(x => x.Access == "write")) notes.Add("Report-buffer references were recovered, but no direct write into the buffer was conservatively identified yet.");
        if (helpers.Length == 0) notes.Add("No bounded helper call receiving the report-buffer base address was recovered from an address-reference candidate.");

        return new OemSleepReportConstructionSide(
            Path.GetFileName(exe),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant(),
            call.CallRva,
            baseDisp,
            refs.ToArray(),
            helpers,
            fp,
            notes.ToArray());
    }

    private static NdeviceDecoded? DecodeOneRawInstruction(NdevicePe pe, uint rva)
    {
        if (rva < pe.TextStart || rva >= pe.TextEnd) return null;
        var end = Math.Min(pe.TextEnd, rva + 15u);
        var decoded = DecodeRange(pe, rva, end);
        var item = decoded.FirstOrDefault(x => x.Rva == rva);
        if (item is null || item.Instruction.Code == Code.INVALID || item.Instruction.Length <= 0 || item.Instruction.Length > 15) return null;
        return item;
    }

    private static (string Kind, string Expression) DescribeConstructionValue(Instruction ins, string access)
    {
        if (access == "address" && ins.Op0Kind == OpKind.Register)
            return ("register", Normalize(ins.Op0Register).ToString());
        if (access == "write")
        {
            if (TryInstructionImmediate(ins, 1, out var immediate)) return ("immediate", $"0x{immediate:X}");
            if (ins.Op1Kind == OpKind.Register) return ("register", Normalize(ins.Op1Register).ToString());
            if (ins.Op1Kind == OpKind.Memory) return ("memory", "memory");
            return ("unresolved", "?");
        }
        if (ins.Op0Kind == OpKind.Register) return ("register", Normalize(ins.Op0Register).ToString());
        return ("unresolved", "?");
    }

    private static OemSleepReportHelperCandidate? TraceConstructionHelperAfterLea(NdevicePe pe, OemSleepReportBufferReference reference, uint setFeatureRva)
    {
        var seq = DecodeRange(pe, reference.Rva, Math.Min(pe.TextEnd, reference.Rva + 0x70u));
        if (seq.Count == 0 || seq[0].Rva != reference.Rva) return null;
        var lea = seq[0].Instruction;
        if (lea.Mnemonic != Mnemonic.Lea || lea.Op0Kind != OpKind.Register) return null;
        var reportReg = Normalize(lea.Op0Register);
        var pushed = false;
        var steps = new List<string> { $"0x{seq[0].Rva:X8} {seq[0].Text}" };

        foreach (var item in seq.Skip(1).Take(14))
        {
            if (item.Rva >= setFeatureRva) break;
            var ins = item.Instruction;
            steps.Add($"0x{item.Rva:X8} {item.Text}");
            if (ins.Mnemonic == Mnemonic.Push && ins.Op0Kind == OpKind.Register && Normalize(ins.Op0Register) == reportReg)
            {
                pushed = true;
                continue;
            }
            if (ins.Mnemonic != Mnemonic.Call) continue;
            if (!pushed) return null;
            var symbol = pe.ResolveImport(ins);
            var target = symbol;
            if (target is null && IsDirectBranch(ins)) target = $"0x{checked((uint)ins.NearBranchTarget):X8}";
            target ??= "unresolved";
            if (target.Contains("HidD_SetFeature", StringComparison.OrdinalIgnoreCase)) return null;
            return new OemSleepReportHelperCandidate(
                reference.Rva,
                reference.RelativeToSetFeature,
                item.Rva,
                target,
                steps.ToArray());
        }
        return null;
    }

    private static string ConstructionReferenceKey(OemSleepReportBufferReference r) =>
        $"rel={r.RelativeToSetFeature};off={r.ReportOffset};access={r.Access};mn={r.Mnemonic}";

    private static string ConstructionHelperKey(OemSleepReportHelperCandidate h) =>
        $"leaRel={h.RelativeToSetFeature};callDelta={checked((int)((long)h.CallRva - h.BufferLeaRva))}";

    private static string FormatSignedHex(long value) => value < 0 ? $"-0x{Math.Abs(value):X}" : $"+0x{value:X}";
}

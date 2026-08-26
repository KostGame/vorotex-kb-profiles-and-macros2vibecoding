using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemSleepPayloadHelperInstruction(
    uint Rva,
    int RelativeToHelper,
    string Bytes,
    string Text,
    string Mnemonic,
    string? CallTarget,
    string Shape);

internal sealed record OemSleepPayloadHelperEntry(
    uint CallRva,
    int RelativeToSetFeature,
    uint TargetRva,
    string[] CallerPushes,
    int? StackCleanupBytes,
    string CallerAbiClass,
    string SemanticClass,
    bool SemanticProven,
    OemSleepPayloadHelperInstruction[] Body,
    string BodyFingerprint,
    string[] Evidence,
    string[] Notes);

internal sealed record OemSleepPayloadHelperSide(
    string Executable,
    string Sha256,
    uint SetFeatureCallRva,
    OemSleepPayloadHelperEntry[] Helpers,
    string[] Notes);

internal sealed record OemSleepPayloadHelperPair(
    int RelativeToSetFeature,
    uint ACallRva,
    uint BCallRva,
    uint ATargetRva,
    uint BTargetRva,
    bool CallerShapeMatches,
    bool BodyFingerprintMatches,
    string ASemanticClass,
    string BSemanticClass,
    bool SemanticsMatch,
    bool SemanticsProvenOnBoth,
    string[] Evidence);

internal sealed record OemSleepPayloadHelperSemanticsReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemSleepPayloadHelperSide A,
    OemSleepPayloadHelperSide B,
    OemSleepPayloadHelperPair[] CorrespondingPairs,
    string[] Evidence,
    string[] Notes);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    internal static OemSleepPayloadHelperSemanticsReport AnalyzeKeyboardSleepPayloadHelperSemantics(string exeA, string exeB)
    {
        var seed = AnalyzeKeyboardSleepReportPayloadSeed(exeA, exeB);
        var a = TracePayloadHelperSide(Path.GetFullPath(exeA), seed.A);
        var b = TracePayloadHelperSide(Path.GetFullPath(exeB), seed.B);

        var aByRel = a.Helpers.GroupBy(x => x.RelativeToSetFeature).ToDictionary(g => g.Key, g => g.First());
        var bByRel = b.Helpers.GroupBy(x => x.RelativeToSetFeature).ToDictionary(g => g.Key, g => g.First());
        var rels = aByRel.Keys.Intersect(bByRel.Keys).OrderBy(x => x).ToArray();
        var pairs = new List<OemSleepPayloadHelperPair>();

        foreach (var rel in rels)
        {
            var ah = aByRel[rel];
            var bh = bByRel[rel];
            var callerMatch = NormalizeCallerPushes(ah.CallerPushes) == NormalizeCallerPushes(bh.CallerPushes)
                && ah.StackCleanupBytes == bh.StackCleanupBytes;
            var bodyMatch = ah.BodyFingerprint.Length > 0
                && string.Equals(ah.BodyFingerprint, bh.BodyFingerprint, StringComparison.Ordinal);
            var semanticsMatch = ah.SemanticClass != "UNRESOLVED"
                && string.Equals(ah.SemanticClass, bh.SemanticClass, StringComparison.Ordinal);
            var provenBoth = semanticsMatch && ah.SemanticProven && bh.SemanticProven;
            var evidence = new List<string>();
            if (callerMatch) evidence.Add("Caller push/cleanup shape matches after OEM address normalization.");
            if (bodyMatch) evidence.Add("Helper body normalized instruction fingerprint matches.");
            if (provenBoth) evidence.Add($"Both helper bodies statically classify as {ah.SemanticClass}.");
            pairs.Add(new OemSleepPayloadHelperPair(
                rel,
                ah.CallRva,
                bh.CallRva,
                ah.TargetRva,
                bh.TargetRva,
                callerMatch,
                bodyMatch,
                ah.SemanticClass,
                bh.SemanticClass,
                semanticsMatch,
                provenBoth,
                evidence.ToArray()));
        }

        var provenPairs = pairs.Count(x => x.SemanticsProvenOnBoth);
        var structuralPairs = pairs.Count(x => x.CallerShapeMatches && x.BodyFingerprintMatches);
        var verdict = provenPairs > 0
            ? "REPORT_PAYLOAD_HELPER_SEMANTICS_PROVEN"
            : structuralPairs > 0
                ? "REPORT_PAYLOAD_HELPERS_STRUCTURALLY_CORRESPONDING"
                : pairs.Count > 0
                    ? "REPORT_PAYLOAD_HELPERS_POSITIONALLY_CORRESPONDING"
                    : "REPORT_PAYLOAD_HELPER_TRACE_UNRESOLVED";

        var evidenceSummary = new List<string>();
        if (pairs.Count > 0)
            evidenceSummary.Add($"Paired {pairs.Count} report+1 consumer call(s) by exact position relative to SetFeature, intentionally ignoring OEM-specific target RVA/delta.");
        if (structuralPairs > 0)
            evidenceSummary.Add($"Recovered {structuralPairs} pair(s) with matching caller shape and helper-body fingerprint.");
        if (provenPairs > 0)
            evidenceSummary.Add($"Recovered {provenPairs} pair(s) with matching statically proven helper semantics.");

        return new OemSleepPayloadHelperSemanticsReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static semantic expansion of report+1 consumer helpers; fixes OEM target-delta false-negative pairing without promoting proximity to SleepTime provenance",
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
            pairs.ToArray(),
            evidenceSummary.ToArray(),
            [
                "Correspondence is keyed by RelativeToSetFeature rather than direct-call target delta because the owner report proved identical call positions with OEM-relocated helper bodies.",
                "MEMSET_LIKE/MEMCPY_LIKE is PROVEN only when the decoded helper body contains a matching rep-stos/rep-movs primitive or resolves a nested import with that semantic name.",
                "Caller ABI shape can nominate ZERO_FILL_CANDIDATE or BOUNDED_COPY_CANDIDATE, but ABI shape alone never sets SemanticProven=true.",
                "Even proven generic payload init/copy semantics do not prove which upstream source byte is keyboard SleepTime.",
                "No OEM code is executed and no HID/device handle is opened."
            ]);
    }

    internal static string KeyboardSleepPayloadHelperSemanticsToText(OemSleepPayloadHelperSemanticsReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - SleepTime Payload Helper Semantics Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, feature execution/replay, process attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Corresponding helper pairs: {report.CorrespondingPairs.Length}");
        sb.AppendLine();
        AppendPayloadHelperSide(sb, "A", report.A);
        AppendPayloadHelperSide(sb, "B", report.B);
        sb.AppendLine("Corresponding pairs:");
        foreach (var pair in report.CorrespondingPairs)
        {
            sb.AppendLine($"  relSet={pair.RelativeToSetFeature} A call=0x{pair.ACallRva:X8}->0x{pair.ATargetRva:X8} B call=0x{pair.BCallRva:X8}->0x{pair.BTargetRva:X8}");
            sb.AppendLine($"    callerMatch={pair.CallerShapeMatches} bodyMatch={pair.BodyFingerprintMatches} semantics={pair.ASemanticClass}/{pair.BSemanticClass} provenBoth={pair.SemanticsProvenOnBoth}");
            foreach (var item in pair.Evidence) sb.AppendLine("    - " + item);
        }
        sb.AppendLine();
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        foreach (var note in report.Notes) sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendPayloadHelperSide(StringBuilder sb, string label, OemSleepPayloadHelperSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  SHA256={side.Sha256}");
        sb.AppendLine($"  SetFeature=0x{side.SetFeatureCallRva:X8}");
        foreach (var h in side.Helpers)
        {
            sb.AppendLine($"  helper call=0x{h.CallRva:X8} relSet={h.RelativeToSetFeature} target=0x{h.TargetRva:X8}");
            sb.AppendLine($"    cleanup={h.StackCleanupBytes?.ToString(CultureInfo.InvariantCulture) ?? "?"} callerAbi={h.CallerAbiClass} semantic={h.SemanticClass} proven={h.SemanticProven}");
            sb.AppendLine("    caller pushes:");
            foreach (var p in h.CallerPushes) sb.AppendLine("      " + p);
            sb.AppendLine("    helper body:");
            foreach (var ins in h.Body.Take(100))
                sb.AppendLine($"      +0x{ins.RelativeToHelper:X3} @0x{ins.Rva:X8} {ins.Bytes,-24} {ins.Text}{(ins.CallTarget is null ? string.Empty : " -> " + ins.CallTarget)}");
            sb.AppendLine($"    bodyFingerprint={h.BodyFingerprint}");
            foreach (var item in h.Evidence) sb.AppendLine("    EVIDENCE: " + item);
            foreach (var note in h.Notes) sb.AppendLine("    NOTE: " + note);
        }
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static OemSleepPayloadHelperSide TracePayloadHelperSide(string exe, OemSleepPayloadSeedSide seed)
    {
        var pe = NdevicePe.Parse(exe);
        var uniqueCalls = seed.Anchors
            .SelectMany(a => a.CallCandidates)
            .GroupBy(c => c.CallRva)
            .Select(g => g.OrderBy(c => Math.Abs(c.RelativeToAnchor)).First())
            .OrderBy(c => c.CallRva)
            .ToArray();
        var helpers = new List<OemSleepPayloadHelperEntry>();

        foreach (var call in uniqueCalls)
        {
            if (!TryParseDirectTargetRva(call.Target, out var targetRva)) continue;
            var body = DecodePayloadHelperBody(pe, targetRva);
            var pushes = RecoverCallerPushes(pe, call.CallRva, 3);
            var cleanup = RecoverStackCleanup(pe, call.CallRva);
            var callerAbi = ClassifyPayloadCallerAbi(call.RelativeToSetFeature, pushes, cleanup);
            var (semanticClass, semanticProven, semanticEvidence) = ClassifyPayloadHelperSemantics(body);
            var notes = new List<string>();
            if (body.Length == 0) notes.Add("Direct helper target was recovered but no bounded instruction-aligned body could be decoded.");
            if (!semanticProven && callerAbi != "UNRESOLVED") notes.Add("Caller ABI nominates a payload primitive, but semantic proof remains intentionally false until helper-body evidence supports it.");
            helpers.Add(new OemSleepPayloadHelperEntry(
                call.CallRva,
                call.RelativeToSetFeature,
                targetRva,
                pushes,
                cleanup,
                callerAbi,
                semanticClass,
                semanticProven,
                body,
                string.Join('|', body.Select(x => x.Shape)),
                semanticEvidence,
                notes.ToArray()));
        }

        var sideNotes = new List<string>();
        if (uniqueCalls.Length == 0) sideNotes.Add("No report+1 consumer call was recovered by the payload-seed trace.");
        if (uniqueCalls.Length > 0 && helpers.Count == 0) sideNotes.Add("Report+1 calls existed, but none had a parseable direct helper target.");
        return new OemSleepPayloadHelperSide(
            seed.Executable,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant(),
            seed.SetFeatureCallRva,
            helpers.ToArray(),
            sideNotes.ToArray());
    }

    private static OemSleepPayloadHelperInstruction[] DecodePayloadHelperBody(NdevicePe pe, uint targetRva)
    {
        if (targetRva < pe.TextStart || targetRva >= pe.TextEnd) return [];
        var seq = DecodeRange(pe, targetRva, Math.Min(pe.TextEnd, targetRva + 0x180u));
        var result = new List<OemSleepPayloadHelperInstruction>();
        foreach (var item in seq.Take(120))
        {
            var ins = item.Instruction;
            string? callTarget = null;
            if (ins.Mnemonic == Mnemonic.Call)
            {
                callTarget = pe.ResolveImport(ins);
                if (callTarget is null && IsDirectBranch(ins)) callTarget = "direct";
                callTarget ??= "unresolved-call";
            }
            result.Add(new OemSleepPayloadHelperInstruction(
                item.Rva,
                checked((int)((long)item.Rva - targetRva)),
                SleepBytes(pe, item),
                item.Text,
                ins.Mnemonic.ToString(),
                callTarget,
                PayloadHelperInstructionShape(ins, callTarget)));
            if (ins.Mnemonic is Mnemonic.Ret or Mnemonic.Retf) break;
        }
        return result.ToArray();
    }

    private static (string SemanticClass, bool Proven, string[] Evidence) ClassifyPayloadHelperSemantics(OemSleepPayloadHelperInstruction[] body)
    {
        var evidence = new List<string>();
        var memsetImport = body.FirstOrDefault(x => x.CallTarget?.Contains("memset", StringComparison.OrdinalIgnoreCase) == true);
        if (memsetImport is not null)
        {
            evidence.Add($"Nested import resolves to memset-like symbol at helper +0x{memsetImport.RelativeToHelper:X}.");
            return ("MEMSET_LIKE", true, evidence.ToArray());
        }
        var copyImport = body.FirstOrDefault(x => x.CallTarget is not null &&
            (x.CallTarget.Contains("memcpy", StringComparison.OrdinalIgnoreCase) || x.CallTarget.Contains("memmove", StringComparison.OrdinalIgnoreCase)));
        if (copyImport is not null)
        {
            evidence.Add($"Nested import resolves to memcpy/memmove-like symbol at helper +0x{copyImport.RelativeToHelper:X}.");
            return ("MEMCPY_LIKE", true, evidence.ToArray());
        }

        var hasRepStos = body.Any(x => x.Shape.StartsWith("REP:Stos", StringComparison.Ordinal));
        if (hasRepStos)
        {
            evidence.Add("Helper body contains a REP STOS primitive.");
            return ("MEMSET_LIKE", true, evidence.ToArray());
        }
        var hasRepMovs = body.Any(x => x.Shape.StartsWith("REP:Movs", StringComparison.Ordinal));
        if (hasRepMovs)
        {
            evidence.Add("Helper body contains a REP MOVS primitive.");
            return ("MEMCPY_LIKE", true, evidence.ToArray());
        }
        return ("UNRESOLVED", false, []);
    }

    private static string[] RecoverCallerPushes(NdevicePe pe, uint callRva, int maxPushes)
    {
        var prior = DecodeBackwardsExact(pe, callRva, 24);
        var pushes = new List<string>();
        for (var i = prior.Count - 1; i >= 0 && pushes.Count < maxPushes; i--)
        {
            var ins = prior[i].Instruction;
            if (ins.Mnemonic == Mnemonic.Call && pushes.Count > 0) break;
            if (ins.Mnemonic != Mnemonic.Push) continue;
            pushes.Add($"0x{prior[i].Rva:X8} {prior[i].Text}");
        }
        pushes.Reverse();
        return pushes.ToArray();
    }

    private static List<NdeviceDecoded> DecodeBackwardsExact(NdevicePe pe, uint endRva, int maxInstructions)
    {
        var reverse = new List<NdeviceDecoded>();
        var current = endRva;
        for (var n = 0; n < maxInstructions && current > pe.TextStart; n++)
        {
            NdeviceDecoded? best = null;
            for (var distance = 1u; distance <= 15u && current >= pe.TextStart + distance; distance++)
            {
                var candidate = DecodeOneRawInstruction(pe, current - distance);
                if (candidate is null) continue;
                if ((ulong)candidate.Rva + (uint)candidate.Instruction.Length != current) continue;
                if (best is null || candidate.Instruction.Length > best.Instruction.Length) best = candidate;
            }
            if (best is null) break;
            reverse.Add(best);
            current = best.Rva;
        }
        reverse.Reverse();
        return reverse;
    }

    private static int? RecoverStackCleanup(NdevicePe pe, uint callRva)
    {
        var seq = DecodeRange(pe, callRva, Math.Min(pe.TextEnd, callRva + 0x20u));
        var callIndex = seq.FindIndex(x => x.Rva == callRva);
        if (callIndex < 0) return null;
        foreach (var item in seq.Skip(callIndex + 1).Take(4))
        {
            var ins = item.Instruction;
            if (ins.Mnemonic != Mnemonic.Add || ins.Op0Kind != OpKind.Register || Normalize(ins.Op0Register) != Register.ESP) continue;
            if (TryInstructionImmediate(ins, 1, out var immediate)) return checked((int)immediate);
        }
        return null;
    }

    private static string ClassifyPayloadCallerAbi(int relativeToSetFeature, string[] pushes, int? cleanup)
    {
        var joined = string.Join(" | ", pushes);
        if (cleanup == 12 && relativeToSetFeature == -771 && joined.Contains("push", StringComparison.OrdinalIgnoreCase))
            return "ZERO_FILL_CANDIDATE";
        if (cleanup == 12 && relativeToSetFeature == -718 && joined.Contains("[ebp-22Ch]", StringComparison.OrdinalIgnoreCase))
            return "BOUNDED_COPY_CANDIDATE";
        return "UNRESOLVED";
    }

    private static string PayloadHelperInstructionShape(Instruction ins, string? callTarget)
    {
        var prefix = ins.HasRepPrefix ? "REP:" : string.Empty;
        if (ins.Mnemonic == Mnemonic.Call)
            return prefix + "Call:" + (callTarget is null ? "unknown" : callTarget.StartsWith("direct", StringComparison.OrdinalIgnoreCase) ? "direct" : callTarget);
        var operands = new List<string>();
        for (var i = 0; i < ins.OpCount; i++)
        {
            var kind = ins.GetOpKind(i);
            operands.Add(kind switch
            {
                OpKind.Register => "R",
                OpKind.Memory => $"M:{Normalize(ins.MemoryBase)}:{Normalize(ins.MemoryIndex)}",
                OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 or OpKind.FarBranch16 or OpKind.FarBranch32 => "B",
                _ when kind.ToString().StartsWith("Immediate", StringComparison.Ordinal) => "I",
                _ => kind.ToString()
            });
        }
        return prefix + ins.Mnemonic + "(" + string.Join(',', operands) + ")";
    }

    private static string NormalizeCallerPushes(string[] pushes)
    {
        return string.Join('|', pushes.Select(x =>
        {
            var space = x.IndexOf(' ');
            var text = space >= 0 ? x[(space + 1)..] : x;
            return System.Text.RegularExpressions.Regex.Replace(text, "0x[0-9A-Fa-f]+|[0-9A-Fa-f]{6,8}h", "ADDR");
        }));
    }

    private static bool TryParseDirectTargetRva(string target, out uint rva)
    {
        rva = 0;
        if (!target.StartsWith("direct:0x", StringComparison.Ordinal)) return false;
        var start = "direct:0x".Length;
        var end = target.IndexOf(';', start);
        var hex = end >= 0 ? target[start..end] : target[start..];
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rva);
    }
}

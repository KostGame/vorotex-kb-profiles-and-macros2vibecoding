using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemSleepPayloadInstruction(
    uint Rva,
    int RelativeToSetFeature,
    int RelativeToAnchor,
    string Bytes,
    string Text,
    string Mnemonic,
    string? CallTarget);

internal sealed record OemSleepPayloadCallCandidate(
    uint CallRva,
    int RelativeToSetFeature,
    int RelativeToAnchor,
    string Target,
    string AddressRegister,
    string[] Steps);

internal sealed record OemSleepPayloadAnchor(
    uint AnchorRva,
    int RelativeToSetFeature,
    int ReportOffset,
    string AddressRegister,
    OemSleepPayloadInstruction[] Instructions,
    OemSleepPayloadCallCandidate[] CallCandidates,
    string Fingerprint);

internal sealed record OemSleepPayloadSeedSide(
    string Executable,
    string Sha256,
    uint SetFeatureCallRva,
    OemSleepPayloadAnchor[] Anchors,
    string Fingerprint,
    string[] Notes);

internal sealed record OemSleepPayloadSeedTraceReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemSleepPayloadSeedSide A,
    OemSleepPayloadSeedSide B,
    int CorrespondingAnchorCount,
    int CorrespondingCallCount,
    string[] CorrespondingKeys,
    string[] Evidence,
    string[] Notes);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    internal static OemSleepPayloadSeedTraceReport AnalyzeKeyboardSleepReportPayloadSeed(string exeA, string exeB)
    {
        var construction = AnalyzeKeyboardSleepReportConstruction(exeA, exeB);
        var a = TracePayloadSeedSide(Path.GetFullPath(exeA), construction.A);
        var b = TracePayloadSeedSide(Path.GetFullPath(exeB), construction.B);

        var aAnchors = a.Anchors.ToDictionary(PayloadAnchorKey, x => x, StringComparer.Ordinal);
        var bAnchors = b.Anchors.ToDictionary(PayloadAnchorKey, x => x, StringComparer.Ordinal);
        var correspondingAnchors = aAnchors.Keys.Intersect(bAnchors.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var aCalls = a.Anchors.SelectMany(x => x.CallCandidates).Select(PayloadCallKey).ToHashSet(StringComparer.Ordinal);
        var bCalls = b.Anchors.SelectMany(x => x.CallCandidates).Select(PayloadCallKey).ToHashSet(StringComparer.Ordinal);
        var correspondingCalls = aCalls.Intersect(bCalls, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var verdict = correspondingCalls.Length > 0
            ? "REPORT_PLUS1_PAYLOAD_CALLS_CORRESPONDING"
            : correspondingAnchors.Length > 0
                ? "REPORT_PLUS1_WINDOWS_CORRESPONDING"
                : "REPORT_PLUS1_PAYLOAD_UNRESOLVED";

        var evidence = new List<string>();
        if (correspondingAnchors.Length > 0)
            evidence.Add($"Recovered {correspondingAnchors.Length} matching report+1 address-anchor window(s) at the same positions relative to SetFeature on both OEM binaries.");
        if (correspondingCalls.Length > 0)
            evidence.Add($"Recovered {correspondingCalls.Length} matching call candidate(s) fed by the report+1 address register on both OEM binaries.");

        return new OemSleepPayloadSeedTraceReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static trace of the two proven report+1 address seeds toward payload construction helpers before HidD_SetFeature",
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
            correspondingAnchors.Length,
            correspondingCalls.Length,
            correspondingAnchors.Concat(correspondingCalls).Take(200).ToArray(),
            evidence.ToArray(),
            [
                "The report+1 address anchors are construction evidence only. A helper receiving report+1 is not automatically a SleepTime helper.",
                "The trace starts only from report offset +1 anchors already recovered by the proven 41-byte construction slice.",
                "Register aliasing is bounded locally from each LEA to nearby pushes/calls; unresolved or overwritten aliases are not promoted.",
                "No OEM code is executed and no HID/device handle is opened."
            ]);
    }

    internal static string KeyboardSleepReportPayloadSeedToText(OemSleepPayloadSeedTraceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - SleepTime Report+1 Payload Seed Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, feature execution/replay, process attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Corresponding anchors: {report.CorrespondingAnchorCount}");
        sb.AppendLine($"Corresponding calls: {report.CorrespondingCallCount}");
        sb.AppendLine();
        AppendPayloadSeedSide(sb, "A", report.A);
        AppendPayloadSeedSide(sb, "B", report.B);
        sb.AppendLine("Corresponding keys:");
        foreach (var key in report.CorrespondingKeys) sb.AppendLine("  " + key);
        sb.AppendLine();
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        foreach (var note in report.Notes) sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendPayloadSeedSide(StringBuilder sb, string label, OemSleepPayloadSeedSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  SHA256={side.Sha256}");
        sb.AppendLine($"  SetFeature=0x{side.SetFeatureCallRva:X8}");
        foreach (var anchor in side.Anchors)
        {
            sb.AppendLine($"  anchor report[{anchor.ReportOffset}] @0x{anchor.AnchorRva:X8} rel={anchor.RelativeToSetFeature} reg={anchor.AddressRegister}");
            sb.AppendLine("    instruction window:");
            foreach (var ins in anchor.Instructions.Take(80))
                sb.AppendLine($"      relAnchor={ins.RelativeToAnchor,4} relSet={ins.RelativeToSetFeature,5} @0x{ins.Rva:X8} {ins.Bytes,-24} {ins.Text}{(ins.CallTarget is null ? string.Empty : " -> " + ins.CallTarget)}");
            sb.AppendLine("    report+1 call candidates:");
            foreach (var call in anchor.CallCandidates)
            {
                sb.AppendLine($"      call=0x{call.CallRva:X8} relAnchor={call.RelativeToAnchor} relSet={call.RelativeToSetFeature} target={call.Target} via={call.AddressRegister}");
                foreach (var step in call.Steps) sb.AppendLine("        " + step);
            }
            sb.AppendLine($"    fingerprint={anchor.Fingerprint}");
        }
        sb.AppendLine($"  fingerprint={side.Fingerprint}");
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static OemSleepPayloadSeedSide TracePayloadSeedSide(string exe, OemSleepReportConstructionSide construction)
    {
        var pe = NdevicePe.Parse(exe);
        var seedRefs = construction.References
            .Where(x => x.Access == "address" && x.ReportOffset == 1 && string.Equals(x.Mnemonic, Mnemonic.Lea.ToString(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Rva)
            .ToArray();

        var anchors = new List<OemSleepPayloadAnchor>();
        foreach (var seed in seedRefs)
        {
            var end = Math.Min(pe.TextEnd, Math.Min(construction.SetFeatureCallRva, seed.Rva + 0xA0u));
            var seq = DecodeRange(pe, seed.Rva, end);
            if (seq.Count == 0 || seq[0].Rva != seed.Rva || seq[0].Instruction.Mnemonic != Mnemonic.Lea || seq[0].Instruction.Op0Kind != OpKind.Register)
                continue;

            var addressReg = Normalize(seq[0].Instruction.Op0Register);
            var instructions = seq.Take(80).Select(item =>
            {
                string? target = null;
                if (item.Instruction.Mnemonic == Mnemonic.Call)
                {
                    target = pe.ResolveImport(item.Instruction);
                    if (target is null && IsDirectBranch(item.Instruction))
                    {
                        var targetRva = checked((uint)item.Instruction.NearBranchTarget);
                        var delta = checked((long)targetRva - item.Rva);
                        target = $"direct:0x{targetRva:X8};delta={delta}";
                    }
                    target ??= "unresolved-call";
                }
                return new OemSleepPayloadInstruction(
                    item.Rva,
                    checked((int)((long)item.Rva - construction.SetFeatureCallRva)),
                    checked((int)((long)item.Rva - seed.Rva)),
                    SleepBytes(pe, item),
                    item.Text,
                    item.Instruction.Mnemonic.ToString(),
                    target);
            }).ToArray();

            var candidates = TracePayloadSeedCalls(pe, seq.Take(80).ToList(), addressReg, construction.SetFeatureCallRva, seed.Rva);
            var fp = string.Join('|', instructions.Select(PayloadInstructionKey));
            anchors.Add(new OemSleepPayloadAnchor(
                seed.Rva,
                seed.RelativeToSetFeature,
                seed.ReportOffset,
                addressReg.ToString(),
                instructions,
                candidates,
                fp));
        }

        var notes = new List<string>();
        if (seedRefs.Length == 0) notes.Add("The construction slice did not expose a report+1 address seed on this OEM binary.");
        if (seedRefs.Length > 0 && anchors.Count == 0) notes.Add("Report+1 seeds existed, but no instruction-aligned local window could be decoded from them.");
        if (anchors.Count > 0 && anchors.All(x => x.CallCandidates.Length == 0)) notes.Add("Report+1 local windows were recovered, but no bounded call consuming the address-register alias was proven yet.");

        return new OemSleepPayloadSeedSide(
            Path.GetFileName(exe),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant(),
            construction.SetFeatureCallRva,
            anchors.ToArray(),
            string.Join("||", anchors.Select(PayloadAnchorKey)),
            notes.ToArray());
    }

    private static OemSleepPayloadCallCandidate[] TracePayloadSeedCalls(
        NdevicePe pe,
        List<NdeviceDecoded> seq,
        Register initialAddressReg,
        uint setFeatureRva,
        uint anchorRva)
    {
        var aliases = new HashSet<Register> { initialAddressReg };
        var result = new List<OemSleepPayloadCallCandidate>();
        var lastAliasPushIndex = -100;
        var lastAliasRegister = initialAddressReg;

        for (var i = 1; i < seq.Count; i++)
        {
            var item = seq[i];
            if (item.Rva >= setFeatureRva) break;
            var ins = item.Instruction;

            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Register)
            {
                var dst = Normalize(ins.Op0Register);
                var src = Normalize(ins.Op1Register);
                if (aliases.Contains(src)) aliases.Add(dst);
                else if (aliases.Contains(dst) && dst != initialAddressReg) aliases.Remove(dst);
            }
            else if (ins.Op0Kind == OpKind.Register && aliases.Contains(Normalize(ins.Op0Register)) && ins.Mnemonic != Mnemonic.Push)
            {
                var dst = Normalize(ins.Op0Register);
                if (dst != initialAddressReg) aliases.Remove(dst);
            }

            if (ins.Mnemonic == Mnemonic.Push && ins.Op0Kind == OpKind.Register)
            {
                var pushed = Normalize(ins.Op0Register);
                if (aliases.Contains(pushed))
                {
                    lastAliasPushIndex = i;
                    lastAliasRegister = pushed;
                }
                continue;
            }

            if (ins.Mnemonic != Mnemonic.Call) continue;
            if (i - lastAliasPushIndex > 6) continue;

            var target = pe.ResolveImport(ins);
            if (target is null && IsDirectBranch(ins))
            {
                var targetRva = checked((uint)ins.NearBranchTarget);
                var delta = checked((long)targetRva - item.Rva);
                target = $"direct:0x{targetRva:X8};delta={delta}";
            }
            target ??= "unresolved-call";
            if (target.Contains("HidD_SetFeature", StringComparison.OrdinalIgnoreCase)) continue;

            var from = Math.Max(0, lastAliasPushIndex - 4);
            var steps = seq.Skip(from).Take(i - from + 1).Select(x => $"0x{x.Rva:X8} {x.Text}").ToArray();
            result.Add(new OemSleepPayloadCallCandidate(
                item.Rva,
                checked((int)((long)item.Rva - setFeatureRva)),
                checked((int)((long)item.Rva - anchorRva)),
                target,
                lastAliasRegister.ToString(),
                steps));
            lastAliasPushIndex = -100;
        }

        return result
            .GroupBy(x => (x.CallRva, x.Target, x.AddressRegister))
            .Select(x => x.First())
            .OrderBy(x => x.CallRva)
            .ToArray();
    }

    private static string PayloadInstructionKey(OemSleepPayloadInstruction x)
    {
        var call = x.CallTarget is null
            ? string.Empty
            : x.CallTarget.StartsWith("direct:", StringComparison.Ordinal)
                ? x.CallTarget[(x.CallTarget.IndexOf(";delta=", StringComparison.Ordinal) + 1)..]
                : x.CallTarget;
        return $"rel={x.RelativeToAnchor};mn={x.Mnemonic};call={call}";
    }

    private static string PayloadAnchorKey(OemSleepPayloadAnchor x) =>
        $"anchorRel={x.RelativeToSetFeature};off={x.ReportOffset};fp={x.Fingerprint}";

    private static string PayloadCallKey(OemSleepPayloadCallCandidate x) =>
        $"callRelSet={x.RelativeToSetFeature};callRelAnchor={x.RelativeToAnchor};target={NormalizePayloadCallTarget(x.Target)}";

    private static string NormalizePayloadCallTarget(string target)
    {
        if (!target.StartsWith("direct:", StringComparison.Ordinal)) return target;
        var marker = target.IndexOf(";delta=", StringComparison.Ordinal);
        return marker >= 0 ? target[(marker + 1)..] : "direct";
    }
}

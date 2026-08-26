using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemNdeviceFieldEdge(
    string Field,
    long ExpectedMember,
    long LocalMemberDisplacement,
    uint[] WriteRvas,
    bool ExactLocalWriteProven,
    string[] Notes);

internal sealed record OemNdeviceAggregateCaller(
    long LocalObjectBase,
    uint CopyCallRva,
    uint CopyHelperRva,
    uint ObjectSize,
    string ContainerBaseRegister,
    long ContainerEndOffset,
    string[] Steps,
    string Fingerprint);

internal sealed record OemNdeviceHelperTrace(
    uint EntryRva,
    bool SourcePointerRecovered,
    bool DestinationPointerRecovered,
    bool Member20Copied,
    bool Member3EcCopied,
    string[] Steps,
    string[] NestedCalls,
    string Fingerprint,
    string[] Notes);

internal sealed record OemNdeviceAggregateSide(
    string Executable,
    uint ParserJoinRva,
    OemNdeviceFieldEdge DevName,
    OemNdeviceFieldEdge DevCmpStr,
    OemNdeviceAggregateCaller? Caller,
    OemNdeviceHelperTrace? Helper,
    bool LocalLayoutProven,
    bool AggregateCallerProven,
    bool AggregateCopyProven,
    string[] Notes);

internal sealed record OemNdeviceAggregateReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemNdeviceAggregateSide A,
    OemNdeviceAggregateSide B,
    bool LocalLayoutCorrespondence,
    bool CallerCorrespondence,
    bool HelperCorrespondence,
    bool DevNameToMember20Proven,
    bool DevCmpStrToMember3EcProven,
    string[] Evidence,
    string[] Notes);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    private const long DevNameMember = 0x20;
    private const long DevCmpMember = 0x3EC;
    private const uint NdeviceSize = 0x434;

    public static OemNdeviceAggregateReport Analyze(string exeA, string exeB)
    {
        var previous = OemIdentityFieldProvenanceAnalyzer.Analyze(exeA, exeB);
        var old = OemIdentityObjectCommitAnalyzer.Analyze(exeA, exeB);
        var a = AnalyzeSide(Path.GetFullPath(exeA), previous.A, old.A);
        var b = AnalyzeSide(Path.GetFullPath(exeB), previous.B, old.B);

        var localCorrespondence = a.LocalLayoutProven && b.LocalLayoutProven &&
                                  a.Caller is not null && b.Caller is not null &&
                                  a.Caller.LocalObjectBase == b.Caller.LocalObjectBase &&
                                  a.Caller.ObjectSize == b.Caller.ObjectSize &&
                                  a.DevName.LocalMemberDisplacement == b.DevName.LocalMemberDisplacement &&
                                  a.DevCmpStr.LocalMemberDisplacement == b.DevCmpStr.LocalMemberDisplacement;

        var callerCorrespondence = a.Caller is not null && b.Caller is not null &&
                                   string.Equals(a.Caller.Fingerprint, b.Caller.Fingerprint, StringComparison.Ordinal);
        var helperCorrespondence = a.Helper is not null && b.Helper is not null &&
                                   string.Equals(a.Helper.Fingerprint, b.Helper.Fingerprint, StringComparison.Ordinal);

        var nameProven = localCorrespondence && callerCorrespondence && helperCorrespondence &&
                         a.AggregateCopyProven && b.AggregateCopyProven &&
                         a.Helper!.Member20Copied && b.Helper!.Member20Copied;
        var cmpProven = localCorrespondence && callerCorrespondence && helperCorrespondence &&
                        a.AggregateCopyProven && b.AggregateCopyProven &&
                        a.Helper!.Member3EcCopied && b.Helper!.Member3EcCopied;

        var verdict = nameProven && cmpProven ? "NDEVICE_AGGREGATE_COPY_COMPLETE" :
                      nameProven || cmpProven ? "NDEVICE_AGGREGATE_COPY_PARTIAL" :
                      a.Helper is not null && b.Helper is not null ? "AGGREGATE_COPY_HELPERS_TRACED" :
                      "NDEVICE_AGGREGATE_COPY_UNRESOLVED";

        var evidence = new List<string>();
        if (localCorrespondence)
            evidence.Add($"Both OEMs recover the same local Ndevice geometry: base [EBP{SignedHex(a.Caller!.LocalObjectBase)}], size 0x{a.Caller.ObjectSize:X}, DevName +0x20 and DevCmpStr +0x3EC.");
        if (callerCorrespondence)
            evidence.Add("Both OEM parser tails expose the same normalized aggregate-container commit caller shape.");
        if (helperCorrespondence)
            evidence.Add("Both aggregate copy helpers expose the same normalized source/destination transfer fingerprint.");
        if (nameProven)
            evidence.Add("DevName parser data reaches local +0x20 and the aggregate helper explicitly copies source +0x20 to destination +0x20 on both OEM sides.");
        if (cmpProven)
            evidence.Add("DevCmpStr parser data reaches local +0x3EC and the aggregate helper explicitly copies source +0x3EC to destination +0x3EC on both OEM sides.");

        return new OemNdeviceAggregateReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static proof/disproof of local Ndevice aggregate commit through the parser-tail copy helper",
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
                vidPidSpoofed = false,
                productStringSpoofed = false,
                driverInstalled = false,
                registryModified = false,
                profileSelectionChanged = false,
                keymapModified = false,
                macroModified = false,
                lightingModified = false,
                sleepSettingChanged = false,
                firmwareModified = false
            },
            a, b,
            localCorrespondence,
            callerCorrespondence,
            helperCorrespondence,
            nameProven,
            cmpProven,
            evidence.ToArray(),
            [
                "A local field edge requires an exact field-match CFG write to localBase + expectedMember; cross-OEM similarity alone is not accepted.",
                "The aggregate caller requires the exact local-object address as source, a direct helper target, container-end as destination this, and an explicit +0x434 end advance.",
                "A field is promoted to PROVEN only when the helper trace explicitly transfers the same source/destination member offset, directly or through a semantically resolved copy helper.",
                "Caller shape, helper proximity, object size and equal offsets are evidence only unless the source/destination chain is explicit.",
                "All analysis reads executable bytes only; OEM code is not executed and no keyboard/device handle is opened."
            ]);
    }

    private static OemNdeviceAggregateSide AnalyzeSide(
        string exe,
        OemIdentityFieldProvenanceSide previous,
        OemIdentityObjectCommitSide old)
    {
        var pe = NdevicePe.Parse(exe);
        var join = old.DevName.CommonJoinRva ?? old.DevCmpStr.CommonJoinRva ?? 0;
        var notes = new List<string>();
        if (join == 0)
        {
            notes.Add("No parser join anchor survived the previous object-commit stage.");
            return EmptySide(exe, previous, notes);
        }

        var caller = FindAggregateCaller(pe, join, old.ProductStringCallRva, NdeviceSize);
        if (caller is null)
        {
            notes.Add("No parser-tail aggregate caller with local source and +0x434 container-end advance was recovered.");
            return EmptySide(exe, previous, notes, join);
        }

        var nameDisp = caller.LocalObjectBase + DevNameMember;
        var cmpDisp = caller.LocalObjectBase + DevCmpMember;
        var nameWrites = RecoverExactFieldWrites(pe, previous.DevName, join, nameDisp);
        var cmpWrites = RecoverExactFieldWrites(pe, previous.DevCmpStr, join, cmpDisp);
        var name = new OemNdeviceFieldEdge(
            "DevName", DevNameMember, nameDisp, nameWrites,
            nameWrites.Length > 0,
            nameWrites.Length > 0 ? [$"Exact match-path CFG write reaches [EBP{SignedHex(nameDisp)}] = localBase+0x20."] : ["No exact DevName write to localBase+0x20 was recovered."]);
        var cmp = new OemNdeviceFieldEdge(
            "DevCmpStr", DevCmpMember, cmpDisp, cmpWrites,
            cmpWrites.Length > 0,
            cmpWrites.Length > 0 ? [$"Exact match-path CFG write reaches [EBP{SignedHex(cmpDisp)}] = localBase+0x3EC."] : ["No exact DevCmpStr write to localBase+0x3EC was recovered."]);

        var helper = TraceAggregateHelper(pe, caller.CopyHelperRva, DevNameMember, DevCmpMember);
        var localLayout = name.ExactLocalWriteProven && cmp.ExactLocalWriteProven;
        var callerProven = caller.ObjectSize == NdeviceSize;
        var copyProven = localLayout && callerProven && helper.SourcePointerRecovered && helper.DestinationPointerRecovered &&
                         (helper.Member20Copied || helper.Member3EcCopied);
        if (!localLayout) notes.Add("Two-field local Ndevice layout remains incomplete on this OEM side.");
        if (!helper.Member20Copied) notes.Add("Aggregate helper has not yet proved transfer of +0x20.");
        if (!helper.Member3EcCopied) notes.Add("Aggregate helper has not yet proved transfer of +0x3EC.");

        return new OemNdeviceAggregateSide(
            Path.GetFileName(exe), join, name, cmp, caller, helper,
            localLayout, callerProven, copyProven, notes.ToArray());
    }

    private static OemNdeviceAggregateSide EmptySide(
        string exe,
        OemIdentityFieldProvenanceSide previous,
        List<string> notes,
        uint join = 0)
    {
        var name = new OemNdeviceFieldEdge("DevName", DevNameMember, 0, [], false, ["Local aggregate base unresolved."]);
        var cmp = new OemNdeviceFieldEdge("DevCmpStr", DevCmpMember, 0, [], false, ["Local aggregate base unresolved."]);
        return new OemNdeviceAggregateSide(Path.GetFileName(exe), join, name, cmp, null, null, false, false, false, notes.ToArray());
    }

    public static string ToText(OemNdeviceAggregateReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - Ndevice Aggregate Copy Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, feature reports, process attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Local layout correspondence: {report.LocalLayoutCorrespondence}");
        sb.AppendLine($"Caller correspondence: {report.CallerCorrespondence}");
        sb.AppendLine($"Helper correspondence: {report.HelperCorrespondence}");
        sb.AppendLine($"DevName -> +0x20 proven: {report.DevNameToMember20Proven}");
        sb.AppendLine($"DevCmpStr -> +0x3EC proven: {report.DevCmpStrToMember3EcProven}");
        sb.AppendLine();
        AppendSide(sb, "A", report.A);
        AppendSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var e in report.Evidence) sb.AppendLine("  - " + e);
        sb.AppendLine();
        foreach (var n in report.Notes) sb.AppendLine("NOTE: " + n);
        return sb.ToString();
    }

    private static void AppendSide(StringBuilder sb, string label, OemNdeviceAggregateSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}; join=0x{side.ParserJoinRva:X8}; localLayout={side.LocalLayoutProven}; caller={side.AggregateCallerProven}; copy={side.AggregateCopyProven}");
        foreach (var f in new[] { side.DevName, side.DevCmpStr })
            sb.AppendLine($"  {f.Field}: local=[EBP{SignedHex(f.LocalMemberDisplacement)}] expected=+0x{f.ExpectedMember:X} exact={f.ExactLocalWriteProven} writes={JoinHex(f.WriteRvas)}");
        if (side.Caller is not null)
        {
            var c = side.Caller;
            sb.AppendLine($"  caller: localBase=[EBP{SignedHex(c.LocalObjectBase)}] call=0x{c.CopyCallRva:X8} helper=0x{c.CopyHelperRva:X8} size=0x{c.ObjectSize:X} end=[{c.ContainerBaseRegister}+0x{c.ContainerEndOffset:X}]");
            sb.AppendLine($"    fingerprint={c.Fingerprint}");
            foreach (var x in c.Steps) sb.AppendLine("    " + x);
        }
        if (side.Helper is not null)
        {
            var h = side.Helper;
            sb.AppendLine($"  helper: entry=0x{h.EntryRva:X8}; src={h.SourcePointerRecovered}; dst={h.DestinationPointerRecovered}; +20={h.Member20Copied}; +3EC={h.Member3EcCopied}");
            sb.AppendLine($"    fingerprint={h.Fingerprint}");
            foreach (var x in h.Steps.Take(100)) sb.AppendLine("    " + x);
            foreach (var x in h.NestedCalls.Take(40)) sb.AppendLine("    nested: " + x);
            foreach (var x in h.Notes) sb.AppendLine("    NOTE: " + x);
        }
        foreach (var n in side.Notes) sb.AppendLine("  NOTE: " + n);
        sb.AppendLine();
    }

    private static string SignedHex(long value) => value < 0 ? $"-0x{-value:X}" : $"+0x{value:X}";
    private static string JoinHex(uint[] values) => values.Length == 0 ? "none" : string.Join(',', values.Select(x => $"0x{x:X8}"));
}
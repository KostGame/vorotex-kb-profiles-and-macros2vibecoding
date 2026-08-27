using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemSleepPayloadValue(
    string Expression,
    string Kind,
    uint? DefinitionRva,
    string[] Steps,
    string[] UnresolvedEdges);

internal sealed record OemSleepPayloadCopyTrace(
    uint CallRva,
    int RelativeToSetFeature,
    uint HelperRva,
    string HelperSemantic,
    bool HelperSemanticProven,
    bool HelperAbiProven,
    string SourceArgument,
    int? SourceStackDisplacement,
    string DestinationArgument,
    int? DestinationStackDisplacement,
    int? DestinationReportOffset,
    string CountArgument,
    int? CopyLength,
    int? MaximumCopyLength,
    int? SignedUpperBound,
    bool NonNegativeCountProven,
    bool CopyCountUpperBoundProven,
    bool ReportPayloadCopyProven,
    string[] CallerWindow,
    string[] Evidence,
    string[] UnresolvedEdges);

internal sealed record OemSleepPayloadSourceStore(
    string Slot,
    uint StoreRva,
    int RelativeToSetFeature,
    string Instruction,
    OemSleepPayloadValue Value,
    string[] UnresolvedEdges);

internal sealed record OemPayloadSourcePointerDefinition(
    string Oem,
    string Status,
    uint Rva,
    string BasicBlock,
    string[] PredecessorBlocks,
    string Instruction,
    string DefinitionKind,
    string SourceExpression,
    uint? NearestDefinitionRva,
    string? NearestDefinition,
    string[] Evidence,
    string[] UnresolvedEdges);

internal sealed record OemPayloadSourcePointerAlias(
    string Oem,
    string Status,
    uint Rva,
    string BasicBlock,
    string[] PredecessorBlocks,
    string Instruction,
    string AliasKind,
    string AliasExpression,
    string Target,
    string[] Evidence,
    string[] UnresolvedEdges);

internal sealed record OemPayloadSourceObjectProvenance(
    string Oem,
    string Status,
    uint? Rva,
    string BasicBlock,
    string ObjectKind,
    string Expression,
    int? MemberOffset,
    string Producer,
    string[] Evidence,
    string[] UnresolvedEdges);

internal sealed record OemPayloadSourceBufferWrite(
    string Oem,
    string Status,
    uint Rva,
    string BasicBlock,
    string Instruction,
    int? SourceOffset,
    int Width,
    string ValueExpression,
    string Transformation,
    string[] Evidence,
    string[] UnresolvedEdges);

internal sealed record OemPayloadOffsetToReportOffset(
    string Oem,
    string Status,
    int SourceOffset,
    int ReportOffset,
    int Width,
    uint CopyCallRva,
    string Condition,
    string[] Evidence);

internal sealed record OemPayloadSleepTrace(
    string Oem,
    string Status,
    uint? Rva,
    string Stage,
    string InstructionOrAnchor,
    string NextEdge,
    string[] Evidence,
    string[] UnresolvedEdges,
    int? SourceOffset = null,
    int? ReportOffset = null,
    uint? CopyCallRva = null,
    uint? SetFeatureCallRva = null);

internal sealed record OemSleepPayloadReportField(
    int StartOffset,
    int Length,
    string Source,
    string Provenance,
    bool Proven);

internal sealed record OemSleepPayloadSourceSide(
    string Executable,
    string Sha256,
    uint SetFeatureCallRva,
    int? SetFeatureLength,
    int? ReportBaseStackDisplacement,
    bool ReportId6Proven,
    bool SetFeatureAbiProven,
    OemSleepPayloadCopyTrace? Copy,
    OemSleepPayloadSourceStore[] SourceDefinitions,
    OemPayloadSourcePointerDefinition[] SourcePointerDefinitions,
    OemPayloadSourcePointerAlias[] SourcePointerAliases,
    OemPayloadSourceObjectProvenance[] SourceObjectProvenance,
    OemPayloadSourceBufferWrite[] SourceBufferWrites,
    OemPayloadOffsetToReportOffset[] SourceOffsetToReportOffset,
    OemPayloadSleepTrace[] SleepTimeForwardTrace,
    OemPayloadSleepTrace[] SleepTimeBackwardTrace,
    OemSleepPayloadReportField[] ReportMap,
    string SleepTimeCandidateStatus,
    string[] SleepTimeEvidence,
    string[] UnresolvedEdges,
    string NormalizedFingerprint);

internal sealed record OemSleepPayloadSourceCorrelation(
    bool SetFeatureAbiMatches,
    bool CopyAbiMatches,
    bool SourceSlotMatches,
    bool ReportMapMatches,
    bool SleepTimePathMatches,
    string[] Evidence);

internal sealed record OemSleepPayloadSourceReport(
    int Schema,
    string Verdict,
    string Purpose,
    object Safety,
    OemSleepPayloadSourceSide A,
    OemSleepPayloadSourceSide B,
    OemSleepPayloadSourceCorrelation CrossOem,
    string[] Evidence,
    string[] UnresolvedEdges,
    string[] Notes);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    private static readonly string[] SleepTimeAnchors =
    [
        "KBSpecialFuncSet.xml",
        "Slider_Sleep_Time",
        "Edit_Sleep_Time",
        "Value_Sleep_Time",
        "SleepTime"
    ];

    internal static OemSleepPayloadSourceReport AnalyzeKeyboardSleepPayloadSource(string exeA, string exeB)
    {
        var construction = AnalyzeKeyboardSleepReportConstruction(exeA, exeB);
        var helpers = AnalyzeKeyboardSleepPayloadHelperSemantics(exeA, exeB);
        var a = TracePayloadSourceSide(Path.GetFullPath(exeA), helpers.A, construction.A);
        var b = TracePayloadSourceSide(Path.GetFullPath(exeB), helpers.B, construction.B);

        var setFeatureMatch = a.SetFeatureAbiProven && b.SetFeatureAbiProven &&
                              a.SetFeatureLength == 41 && b.SetFeatureLength == 41 &&
                              a.ReportId6Proven && b.ReportId6Proven &&
                              a.ReportBaseStackDisplacement == b.ReportBaseStackDisplacement;
        var copyMatch = a.Copy is not null && b.Copy is not null &&
                        a.Copy.HelperSemanticProven && b.Copy.HelperSemanticProven &&
                        a.Copy.HelperAbiProven && b.Copy.HelperAbiProven &&
                        string.Equals(a.Copy.HelperSemantic, b.Copy.HelperSemantic, StringComparison.Ordinal) &&
                        a.Copy.DestinationReportOffset == b.Copy.DestinationReportOffset &&
                        a.Copy.MaximumCopyLength == b.Copy.MaximumCopyLength &&
                        a.Copy.SignedUpperBound == b.Copy.SignedUpperBound &&
                        a.Copy.NonNegativeCountProven == b.Copy.NonNegativeCountProven;
        var sourceSlotMatch = a.Copy?.SourceStackDisplacement is not null &&
                              a.Copy.SourceStackDisplacement == b.Copy?.SourceStackDisplacement;
        var sourceDefinitionsMatch = a.SourcePointerDefinitions.Length > 0 && b.SourcePointerDefinitions.Length > 0 &&
                                     string.Equals(SourceDefinitionKey(a.SourceDefinitions), SourceDefinitionKey(b.SourceDefinitions), StringComparison.Ordinal);

        var reportMapMatch = HasNormalizedReportMap(a.ReportMap) && HasNormalizedReportMap(b.ReportMap) &&
                             ReportMapKey(a.ReportMap) == ReportMapKey(b.ReportMap);
        var sleepTimePathMatch = IsExactSleepTimeProof(a) && IsExactSleepTimeProof(b);
        var correlationEvidence = new List<string>();
        if (setFeatureMatch) correlationEvidence.Add("Both OEMs recover the exact 41-byte HidD_SetFeature stack-buffer ABI and [EBP-0x228] report base.");
        if (copyMatch) correlationEvidence.Add("Both OEMs recover the same decoded MEMCPY_LIKE helper ABI and destination report+1; copy-count bound status is reported separately.");
        if (sourceSlotMatch) correlationEvidence.Add("Both OEM callers pass local source slot [EBP-0x22C] to the memcpy-like helper.");
        if (reportMapMatch) correlationEvidence.Add("The report-byte map normalizes across both OEM binaries.");

        var sourceStructureProven = setFeatureMatch && copyMatch && sourceSlotMatch && sourceDefinitionsMatch && reportMapMatch &&
                                    IsProvenSourceStructure(a) && IsProvenSourceStructure(b);
        var reportProven = a.SetFeatureAbiProven && b.SetFeatureAbiProven && a.ReportId6Proven && b.ReportId6Proven &&
                           a.Copy?.ReportPayloadCopyProven == true && b.Copy?.ReportPayloadCopyProven == true;
        var sleepTimeToPayloadTraced = a.SleepTimeForwardTrace.Any(x => x.Status == "INFERRED") &&
                                       b.SleepTimeForwardTrace.Any(x => x.Status == "INFERRED");
        var upstreamRecovered = a.SourcePointerDefinitions.Length > 0 || b.SourcePointerDefinitions.Length > 0 ||
                                a.SourcePointerAliases.Length > 0 || b.SourcePointerAliases.Length > 0 ||
                                a.SourceObjectProvenance.Any(x => x.Status != "UNRESOLVED") || b.SourceObjectProvenance.Any(x => x.Status != "UNRESOLVED");
        var verdict = sleepTimePathMatch ? "SLEEPTIME_PAYLOAD_FIELD_PROVEN" :
                      sleepTimeToPayloadTraced ? "SLEEPTIME_TO_PAYLOAD_TRACED" :
                      sourceStructureProven ? "PAYLOAD_SOURCE_STRUCTURE_PROVEN" :
                      upstreamRecovered || reportProven ? "SLEEPTIME_PAYLOAD_SOURCE_PARTIAL" :
                      "KEYBOARD_SLEEP_REPORT_UNRESOLVED";
        var evidence = new List<string>();
        if (reportProven) evidence.Add("PROVEN transport only: the exact report is statically reconstructed as report[0]=0x06 plus conditionally copied source bytes in report[1..40]; this is not a SleepTime field proof.");
        if (sourceStructureProven) evidence.Add("Cross-OEM normalized structure proves the upstream local payload source slot without using OEM-specific helper target RVAs.");
        if (!sleepTimePathMatch) evidence.Add("UNRESOLVED SleepTime: no direct static UI/config-to-concrete-source-byte edge was recovered; the verdict is deliberately capped below field proof.");
        var unresolved = a.UnresolvedEdges.Concat(b.UnresolvedEdges).Distinct(StringComparer.Ordinal).Take(96).ToArray();

        return new OemSleepPayloadSourceReport(
            2,
            verdict,
            "strict deterministic offline CFG-aware provenance trace from keyboard SleepTime anchors through the exact HidD_SetFeature report, proven memcpy-like helper, local source slot and conditional report-byte map; no SleepTime field is inferred from a numeric range or proximity",
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
                firmwareModified = false,
                registryModified = false,
                driverModified = false
            },
            a,
            b,
            new OemSleepPayloadSourceCorrelation(setFeatureMatch, copyMatch, sourceSlotMatch, reportMapMatch, sleepTimePathMatch, correlationEvidence.ToArray()),
            evidence.ToArray(),
            unresolved,
            [
                "The memcpy ABI is proven from decoded helper instructions after EDI/ESI saves: source=[ESP+0x10], count=[ESP+0x14], destination=[ESP+0x0C], plus REP MOVSB.",
                "Source-slot definitions and aliases use a bounded containing-function CFG, predecessor reachability and dominance; unresolved phi-like and helper edges are retained explicitly.",
                "No value range, packet appearance or proximity is labeled SleepTime without a direct static data path.",
                "All analysis reads executable bytes only. No OEM code is executed and no HID/device handle is opened."
            ]);
    }

    internal static string KeyboardSleepPayloadSourceToText(OemSleepPayloadSourceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - SleepTime Payload Source Provenance");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, feature execution/replay, OEM process launch, attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine();
        AppendPayloadSourceSide(sb, "A", report.A);
        AppendPayloadSourceSide(sb, "B", report.B);
        sb.AppendLine("Cross-OEM correlation:");
        sb.AppendLine($"  SetFeature ABI matches: {report.CrossOem.SetFeatureAbiMatches}");
        sb.AppendLine($"  Copy ABI matches: {report.CrossOem.CopyAbiMatches}");
        sb.AppendLine($"  Source slot matches: {report.CrossOem.SourceSlotMatches}");
        sb.AppendLine($"  Report map matches: {report.CrossOem.ReportMapMatches}");
        sb.AppendLine($"  SleepTime path matches: {report.CrossOem.SleepTimePathMatches}");
        foreach (var item in report.CrossOem.Evidence) sb.AppendLine("  - " + item);
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        sb.AppendLine("Unresolved edges:");
        foreach (var item in report.UnresolvedEdges) sb.AppendLine("  - " + item);
        foreach (var item in report.Notes) sb.AppendLine("NOTE: " + item);
        return sb.ToString();
    }

    private static OemSleepPayloadSourceSide TracePayloadSourceSide(string exe, OemSleepPayloadHelperSide helpers, OemSleepReportConstructionSide construction)
    {
        var pe = NdevicePe.Parse(exe);
        var tracedSetFeature = TraceSetFeatureAbi(pe, helpers.SetFeatureCallRva);
        var constructionReportId6 = construction.References.Any(x => x.Access == "write" && x.ReportOffset == 0 && x.ValueKind == "immediate" && x.ValueExpression == "0x6");
        var setFeature = tracedSetFeature with
        {
            ReportBase = construction.ReportBaseDisplacement,
            ReportId6 = constructionReportId6,
            Proven = tracedSetFeature.ReportLength == 41 && constructionReportId6
        };
        var helper = helpers.Helpers
            .Where(x => x.SemanticProven && x.SemanticClass == "MEMCPY_LIKE")
            .OrderBy(x => Math.Abs(x.RelativeToSetFeature + 718))
            .FirstOrDefault();
        var copy = helper is null ? null : TracePayloadCopy(pe, helpers.SetFeatureCallRva, setFeature.ReportBase, helper);
        var oem = Path.GetFileName(exe);
        var provenance = copy?.SourceStackDisplacement is null
            ? new OemPayloadSourceProvenance([], [], [], [], [], [], [], ["No exact source stack slot was recovered for CFG provenance."], "unresolved")
            : AnalyzePayloadSourceProvenance(pe, oem, helpers.SetFeatureCallRva, copy.CallRva, copy.SourceStackDisplacement.Value, copy.MaximumCopyLength ?? copy.SignedUpperBound ?? copy.CopyLength ?? 0, copy.NonNegativeCountProven);
        var stores = provenance.Definitions.Select(definition => new OemSleepPayloadSourceStore(
            StackSlot(copy?.SourceStackDisplacement ?? 0),
            definition.Rva,
            checked((int)((long)definition.Rva - helpers.SetFeatureCallRva)),
            definition.Instruction,
            new OemSleepPayloadValue(definition.SourceExpression, definition.DefinitionKind, definition.NearestDefinitionRva, definition.Evidence, definition.UnresolvedEdges),
            definition.UnresolvedEdges)).ToArray();
        var map = new List<OemSleepPayloadReportField>();
        if (setFeature.ReportBase is not null) map.Add(new OemSleepPayloadReportField(0, 1, "0x06", "direct stack write before HidD_SetFeature", setFeature.ReportId6));
        if (copy is not null)
        {
            var mappedLength = copy.MaximumCopyLength ?? copy.SignedUpperBound ?? copy.CopyLength ?? 0;
            var countCondition = copy.NonNegativeCountProven
                ? $"count is nonnegative and bounded at {mappedLength}"
                : $"CONDITIONAL: count must be nonnegative and <= signed upper bound {copy.SignedUpperBound?.ToString() ?? "unresolved"}";
            map.Add(new OemSleepPayloadReportField(copy.DestinationReportOffset ?? -1, mappedLength, copy.SourceArgument,
                $"MEMCPY_LIKE call 0x{copy.CallRva:X8}; {copy.CountArgument}; {countCondition}", copy.ReportPayloadCopyProven));
        }
        var unresolved = new List<string>();
        if (!setFeature.Proven) unresolved.Add($"SetFeature ABI at 0x{helpers.SetFeatureCallRva:X8}: exact 41-byte report arguments were not fully recovered.");
        if (copy is null) unresolved.Add($"No proven MEMCPY_LIKE report+1 consumer was recovered before HidD_SetFeature 0x{helpers.SetFeatureCallRva:X8}.");
        else
        {
            unresolved.AddRange(copy.UnresolvedEdges);
            unresolved.AddRange(provenance.UnresolvedEdges);
            if (stores.Length == 0 && copy.SourceStackDisplacement is not null) unresolved.Add($"Source slot {StackSlot(copy.SourceStackDisplacement.Value)} at copy call 0x{copy.CallRva:X8}: no unique direct source-pointer definition was recovered in the bounded CFG predecessor slice {provenance.FunctionRange}.");
            unresolved.AddRange(stores.SelectMany(x => x.UnresolvedEdges));
        }
        var sleepEvidence = provenance.SleepTimeForward.Concat(provenance.SleepTimeBackward).SelectMany(x => x.Evidence).Distinct(StringComparer.Ordinal).ToArray();
        var sleepTimeProven = IsExactSleepTimeProof(
            helpers.SetFeatureCallRva, setFeature.Proven, setFeature.ReportId6, copy,
            provenance.Writes, provenance.OffsetMap, map.ToArray(), provenance.SleepTimeBackward);
        if (!sleepTimeProven) unresolved.Add($"No bounded static data-flow edge connects a keyboard-specific SleepTime value to source slot {(copy?.SourceStackDisplacement is null ? "unresolved" : StackSlot(copy.SourceStackDisplacement.Value))} and a concrete copied report byte.");
        var fingerprint = $"SET:{setFeature.ReportLength}:{setFeature.ReportBase}:{setFeature.ReportId6}|COPY:{copy?.HelperSemantic}:{copy?.HelperAbiProven}:{copy?.SourceStackDisplacement}:{copy?.DestinationReportOffset}:{copy?.MaximumCopyLength}:{copy?.SignedUpperBound}:{copy?.NonNegativeCountProven}|DEF:{SourceDefinitionKey(stores)}|ALIAS:{provenance.Aliases.Length}|OBJ:{string.Join(',', provenance.Objects.Select(x => x.ObjectKind).Distinct())}|MAP:{ReportMapKey(map)}";
        return new OemSleepPayloadSourceSide(
            Path.GetFileName(exe),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant(),
            helpers.SetFeatureCallRva,
            setFeature.ReportLength,
            setFeature.ReportBase,
            setFeature.ReportId6,
            setFeature.Proven,
            copy,
            stores,
            provenance.Definitions,
            provenance.Aliases,
            provenance.Objects,
            provenance.Writes,
            provenance.OffsetMap,
            provenance.SleepTimeForward,
            provenance.SleepTimeBackward,
            map.ToArray(),
            sleepTimeProven ? "PROVEN" : "UNRESOLVED",
            sleepEvidence,
            unresolved.Distinct(StringComparer.Ordinal).ToArray(),
            fingerprint);
    }

    private sealed record SetFeatureAbi(int? ReportLength, int? ReportBase, bool ReportId6, bool Proven, string[] Evidence);

    private static SetFeatureAbi TraceSetFeatureAbi(NdevicePe pe, uint callRva)
    {
        var prior = DecodeBackwardsExact(pe, callRva, 1400);
        var pushes = TakeLastPushes(prior, 3);
        int? length = null;
        int? reportBase = null;
        var evidence = new List<string>();
        if (pushes.Length == 3)
        {
            if (TryInstructionImmediate(pushes[0].Instruction, 0, out var immediate))
            {
                length = checked((int)immediate);
                evidence.Add($"0x{pushes[0].Rva:X8} {pushes[0].Text} supplies HidD_SetFeature length.");
            }
            if (pushes[1].Instruction.Op0Kind == OpKind.Register)
            {
                var value = TraceRegisterValue(prior, IndexOfRva(prior, pushes[1].Rva), Normalize(pushes[1].Instruction.Op0Register));
                if (value.Kind == "STACK_ADDRESS" && TryParseStackSlot(value.Expression, out var slot))
                {
                    reportBase = slot;
                    evidence.AddRange(value.Steps);
                }
            }
        }
        var reportId6 = reportBase is not null && prior.Any(x =>
            x.Instruction.Mnemonic == Mnemonic.Mov && x.Instruction.Op0Kind == OpKind.Memory &&
            TryStackSlot(x.Instruction, out var slot) && slot == reportBase &&
            TryInstructionImmediate(x.Instruction, 1, out var value) && value == 0x06);
        if (reportId6) evidence.Add($"A direct write sets report[0] at {StackSlot(reportBase!.Value)} to 0x06.");
        return new SetFeatureAbi(length, reportBase, reportId6, length == 41 && reportBase is not null && reportId6, evidence.ToArray());
    }

    private static OemSleepPayloadCopyTrace TracePayloadCopy(NdevicePe pe, uint setFeatureRva, int? reportBase, OemSleepPayloadHelperEntry helper)
    {
        var prior = DecodeBackwardsExact(pe, helper.CallRva, 96);
        var pushes = TakeLastPushes(prior, 3);
        var helperBody = DecodeRange(pe, helper.TargetRva, Math.Min(pe.TextEnd, helper.TargetRva + 0x60u));
        var helperAbi = HelperMemcpyAbiProven(helperBody);
        var evidence = new List<string>();
        var unresolved = new List<string>();
        if (helperAbi) evidence.Add("Decoded helper entry proves source=[ESP+0x10], count=[ESP+0x14], destination=[ESP+0x0C], and REP MOVSB.");
        else unresolved.Add($"Helper 0x{helper.TargetRva:X8}: source/count/destination ABI mapping or REP MOVSB was not fully decoded.");
        var source = new OemSleepPayloadValue("unresolved", "UNRESOLVED", null, [], []);
        var destination = source;
        var count = source;
        if (pushes.Length == 3)
        {
            count = DescribePushValue(prior, pushes[0]);
            source = DescribePushValue(prior, pushes[1]);
            destination = DescribePushValue(prior, pushes[2]);
        }
        else unresolved.Add($"Copy call 0x{helper.CallRva:X8}: exactly three caller pushes were not recovered.");
        var sourceSlot = source.Kind == "STACK_VALUE" && TryParseStackSlot(source.Expression, out var parsedSource) ? parsedSource : (int?)null;
        var destinationSlot = destination.Kind == "STACK_ADDRESS" && TryParseStackSlot(destination.Expression, out var parsedDestination) ? parsedDestination : (int?)null;
        var bound = RecoverCopyCountUpperBound(prior, pushes.Length == 3 ? pushes[0].Rva : helper.CallRva);
        if (bound.SignedUpperBound is not null) evidence.AddRange(bound.Evidence);
        var destinationOffset = reportBase is not null && destinationSlot is not null ? destinationSlot.Value - reportBase.Value : (int?)null;
        var copyLength = count.Kind == "IMMEDIATE" && int.TryParse(count.Expression, out var parsedCount) ? parsedCount : (int?)null;
        var nonNegativeCountProven = copyLength is >= 0 || bound.NonNegativeCountProven;
        var maximumCopyLength = copyLength is >= 0 ? copyLength : bound.Maximum;
        var copyCountUpperBoundProven = copyLength is >= 0 || bound.Proven;
        if (copyLength is not null) evidence.AddRange(count.Steps);
        if (sourceSlot is not null) evidence.AddRange(source.Steps);
        if (destinationSlot is not null) evidence.AddRange(destination.Steps);
        if (sourceSlot is null) unresolved.Add($"Copy source at 0x{helper.CallRva:X8}: no exact EBP-relative source slot was recovered.");
        if (destinationOffset != 1) unresolved.Add($"Copy destination at 0x{helper.CallRva:X8}: report+1 destination was not proven.");
        if (copyLength != 40 && !(copyCountUpperBoundProven && maximumCopyLength == 40)) unresolved.Add($"Copy count at 0x{helper.CallRva:X8}: signedUpperBound={bound.SignedUpperBound?.ToString() ?? "?"}; nonNegativeCountProven={bound.NonNegativeCountProven}; no unconditional non-negative maximum of 40 was recovered.");
        var reportPayload = helper.SemanticProven && helperAbi && sourceSlot is not null && destinationOffset == 1;
        if (reportPayload)
        {
            var lengthText = maximumCopyLength is int length ? $"up to report[1..{length}]" : "conditionally into report[1..]";
            evidence.Add($"Copy call 0x{helper.CallRva:X8} transfers source bytes from {StackSlot(sourceSlot!.Value)} {lengthText}; report-byte mapping remains conditional on a non-negative copy count.");
        }
        return new OemSleepPayloadCopyTrace(
            helper.CallRva,
            helper.RelativeToSetFeature,
            helper.TargetRva,
            helper.SemanticClass,
            helper.SemanticProven,
            helperAbi,
            source.Expression,
            sourceSlot,
            destination.Expression,
            destinationSlot,
            destinationOffset,
            count.Expression,
            copyLength,
            maximumCopyLength,
            bound.SignedUpperBound,
            nonNegativeCountProven,
            copyCountUpperBoundProven,
            reportPayload,
            prior.TakeLast(40).Select(x => $"relSet={checked((int)((long)x.Rva - setFeatureRva))} @0x{x.Rva:X8} {SleepBytes(pe, x)} {x.Text}").ToArray(),
            evidence.Distinct(StringComparer.Ordinal).ToArray(),
            unresolved.Concat(source.UnresolvedEdges).Concat(destination.UnresolvedEdges).Concat(count.UnresolvedEdges).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static OemSleepPayloadSourceStore[] TraceSourceStores(NdevicePe pe, uint setFeatureRva, uint copyCallRva, int sourceSlot)
    {
        var all = DecodeRange(pe, pe.TextStart, pe.TextEnd);
        var callIndex = all.FindIndex(x => x.Rva == copyCallRva);
        if (callIndex < 0) return [];
        var bounds = FindSleepFunctionBounds(all, callIndex);
        var prior = all.Skip(bounds.StartIndex).Take(callIndex - bounds.StartIndex).ToArray();
        var stores = new List<OemSleepPayloadSourceStore>();
        for (var i = 0; i < prior.Length; i++)
        {
            var instruction = prior[i].Instruction;
            if (instruction.Mnemonic != Mnemonic.Mov || instruction.Op0Kind != OpKind.Memory || !TryStackSlot(instruction, out var slot) || slot != sourceSlot) continue;
            var value = DescribeStoreValue(prior, i, instruction);
            stores.Add(new OemSleepPayloadSourceStore(
                StackSlot(sourceSlot),
                prior[i].Rva,
                checked((int)((long)prior[i].Rva - setFeatureRva)),
                prior[i].Text,
                value,
                value.UnresolvedEdges.Concat([$"Control-flow dominance for store 0x{prior[i].Rva:X8} into {StackSlot(sourceSlot)} is not proven by this bounded linear trace."]).ToArray()));
        }
        return stores.TakeLast(16).ToArray();
    }

    private static OemSleepPayloadValue DescribePushValue(IReadOnlyList<NdeviceDecoded> prior, NdeviceDecoded push)
    {
        if (TryInstructionImmediate(push.Instruction, 0, out var immediate)) return new OemSleepPayloadValue(immediate.ToString(), "IMMEDIATE", push.Rva, [$"0x{push.Rva:X8} {push.Text}"], []);
        if (push.Instruction.Op0Kind == OpKind.Memory && TryStackSlot(push.Instruction, out var slot)) return new OemSleepPayloadValue(StackSlot(slot), "STACK_VALUE", push.Rva, [$"0x{push.Rva:X8} {push.Text}"], []);
        if (push.Instruction.Op0Kind == OpKind.Register)
        {
            var value = TraceRegisterValue(prior, IndexOfRva(prior, push.Rva), Normalize(push.Instruction.Op0Register));
            return value with { Steps = new[] { $"0x{push.Rva:X8} {push.Text}" }.Concat(value.Steps).ToArray() };
        }
        return new OemSleepPayloadValue(push.Text, "UNRESOLVED", push.Rva, [], ["Push operand is outside the bounded static value resolver."]);
    }

    private static OemSleepPayloadValue DescribeStoreValue(IReadOnlyList<NdeviceDecoded> prior, int storeIndex, Instruction instruction)
    {
        if (TryInstructionImmediate(instruction, 1, out var immediate)) return new OemSleepPayloadValue(immediate.ToString(), "IMMEDIATE", null, [], []);
        if (instruction.Op1Kind == OpKind.Memory && TryStackSlot(instruction, out var slot)) return new OemSleepPayloadValue(StackSlot(slot), "STACK_VALUE", null, [], []);
        if (instruction.Op1Kind == OpKind.Register) return TraceRegisterValue(prior, storeIndex, Normalize(instruction.Op1Register));
        return new OemSleepPayloadValue("unresolved", "UNRESOLVED", null, [], ["Source-slot store right-hand side is not an immediate, EBP-relative slot or register."]);
    }

    private static OemSleepPayloadValue TraceRegisterValue(IReadOnlyList<NdeviceDecoded> prior, int beforeIndex, Register register)
    {
        var current = register;
        var steps = new List<string>();
        for (var i = beforeIndex - 1; i >= 0; i--)
        {
            var instruction = prior[i].Instruction;
            if (instruction.Op0Kind != OpKind.Register || Normalize(instruction.Op0Register) != current || !WritesRegister(instruction)) continue;
            steps.Add($"0x{prior[i].Rva:X8} {prior[i].Text}");
            if (instruction.Mnemonic == Mnemonic.Mov)
            {
                if (TryInstructionImmediate(instruction, 1, out var immediate)) return new OemSleepPayloadValue(immediate.ToString(), "IMMEDIATE", prior[i].Rva, steps.ToArray(), []);
                if (instruction.Op1Kind == OpKind.Memory && TryStackSlot(instruction, out var slot)) return new OemSleepPayloadValue(StackSlot(slot), "STACK_VALUE", prior[i].Rva, steps.ToArray(), []);
                if (instruction.Op1Kind == OpKind.Memory) return new OemSleepPayloadValue(prior[i].Text, "MEMORY_VALUE", prior[i].Rva, steps.ToArray(), ["Register definition reads non-EBP memory; upstream pointer identity remains unresolved."]);
                if (instruction.Op1Kind == OpKind.Register) { current = Normalize(instruction.Op1Register); continue; }
            }
            if (instruction.Mnemonic == Mnemonic.Lea && instruction.Op1Kind == OpKind.Memory && TryStackSlot(instruction, out var address)) return new OemSleepPayloadValue(StackSlot(address), "STACK_ADDRESS", prior[i].Rva, steps.ToArray(), []);
            return new OemSleepPayloadValue(prior[i].Text, "UNRESOLVED", prior[i].Rva, steps.ToArray(), ["Nearest register definition is outside the bounded static value resolver."]);
        }
        return new OemSleepPayloadValue(current.ToString(), "UNRESOLVED", null, steps.ToArray(), ["No bounded prior definition was found for the register."]);
    }

    private static bool HelperMemcpyAbiProven(IEnumerable<NdeviceDecoded> body) =>
        body.Any(x => IsMovFromEsp(x.Instruction, Register.ESI, 0x10)) &&
        body.Any(x => IsMovFromEsp(x.Instruction, Register.ECX, 0x14)) &&
        body.Any(x => IsMovFromEsp(x.Instruction, Register.EDI, 0x0C)) &&
        body.Any(x => x.Instruction.HasRepPrefix && x.Instruction.Mnemonic is Mnemonic.Movsb or Mnemonic.Movsw or Mnemonic.Movsd);

    private static bool IsMovFromEsp(Instruction instruction, Register destination, int displacement) =>
        instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register && Normalize(instruction.Op0Register) == destination &&
        instruction.Op1Kind == OpKind.Memory && Normalize(instruction.MemoryBase) == Register.ESP && SignedDisp(instruction) == displacement;

    private static NdeviceDecoded[] TakeLastPushes(IReadOnlyList<NdeviceDecoded> sequence, int count)
    {
        var result = new List<NdeviceDecoded>();
        for (var i = sequence.Count - 1; i >= 0 && result.Count < count; i--)
        {
            if (sequence[i].Instruction.Mnemonic == Mnemonic.Call && result.Count > 0) break;
            if (sequence[i].Instruction.Mnemonic == Mnemonic.Push) result.Add(sequence[i]);
        }
        result.Reverse();
        return result.ToArray();
    }

    private static int IndexOfRva(IReadOnlyList<NdeviceDecoded> values, uint rva)
    {
        for (var i = 0; i < values.Count; i++) if (values[i].Rva == rva) return i;
        return values.Count;
    }

    private static bool TryStackSlot(Instruction instruction, out int displacement)
    {
        displacement = 0;
        if (Normalize(instruction.MemoryBase) != Register.EBP) return false;
        displacement = checked((int)SignedDisp(instruction));
        return true;
    }

    private static string StackSlot(int displacement) => displacement < 0 ? $"[EBP-0x{-displacement:X}]" : $"[EBP+0x{displacement:X}]";

    private sealed record CopyCountUpperBound(int? Maximum, int? SignedUpperBound, bool NonNegativeCountProven, bool Proven, string[] Evidence);

    private static CopyCountUpperBound RecoverCopyCountUpperBound(IReadOnlyList<NdeviceDecoded> prior, uint countPushRva)
    {
        var pushIndex = IndexOfRva(prior, countPushRva);
        var start = Math.Max(0, pushIndex - 12);
        var window = prior.Skip(start).Take(pushIndex - start).ToArray();
        for (var i = window.Length - 1; i >= 0; i--)
        {
            var instruction = window[i].Instruction;
            if (instruction.Mnemonic != Mnemonic.Cmp || instruction.Op0Kind != OpKind.Register || Normalize(instruction.Op0Register) != Register.EAX || !TryInstructionImmediate(instruction, 1, out var limit)) continue;
            var hasJge = window.Skip(i + 1).Any(x => x.Instruction.Mnemonic == Mnemonic.Jge);
            if (!hasJge || limit == 0) continue;
            var load = window.Take(i).LastOrDefault(x => x.Instruction.Mnemonic == Mnemonic.Mov && x.Instruction.Op0Kind == OpKind.Register && Normalize(x.Instruction.Op0Register) == Register.EAX);
            var loadText = load is null ? "EAX" : $"0x{load.Rva:X8} {load.Text}";
            var signedUpperBound = checked((int)limit - 1);
            return new CopyCountUpperBound(
                null,
                signedUpperBound,
                false,
                false,
                [$"{loadText}; 0x{window[i].Rva:X8} {window[i].Text}; fall-through reaches the copy only when signed EAX < 0x{limit:X}, proving SignedUpperBound={signedUpperBound} but not a non-negative lower bound.",
                 "JGE is signed: a negative EAX is not ruled out before REP MOVSB; NonNegativeCountProven=false and MaximumCopyLength remains null."]);
        }
        return new CopyCountUpperBound(null, null, false, false, ["No bounded cmp/jge guard for the memcpy count was recovered immediately before the count push."]);
    }

    private static bool WritesRegister(Instruction instruction) => instruction.Mnemonic is
        Mnemonic.Mov or Mnemonic.Lea or Mnemonic.Xor or Mnemonic.Add or Mnemonic.Sub or Mnemonic.Inc or Mnemonic.Dec or Mnemonic.Pop;

    private static bool TryParseStackSlot(string expression, out int displacement)
    {
        displacement = 0;
        if (!expression.StartsWith("[EBP", StringComparison.OrdinalIgnoreCase) || !expression.EndsWith(']')) return false;
        var text = expression[4..^1];
        if (text.Length < 4 || (text[0] != '+' && text[0] != '-') || !text[1..].StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(text[3..], System.Globalization.NumberStyles.HexNumber, null, out var value)) return false;
        displacement = text[0] == '-' ? -value : value;
        return true;
    }

    private static string ReportMapKey(IEnumerable<OemSleepPayloadReportField> map) =>
        string.Join('|', map.OrderBy(x => x.StartOffset).Select(x => $"{x.StartOffset}:{x.Length}:{(x.Provenance.StartsWith("MEMCPY", StringComparison.Ordinal) ? "COPY" : x.Source)}"));

    private static bool HasNormalizedReportMap(IEnumerable<OemSleepPayloadReportField> map)
    {
        var fields = map.ToArray();
        return fields.Any(x => x.StartOffset == 0 && x.Length == 1 && x.Source == "0x06" && x.Proven) &&
               fields.Any(x => x.StartOffset == 1 && x.Length > 0 && x.Proven && x.Provenance.StartsWith("MEMCPY_LIKE", StringComparison.Ordinal));
    }

    private static bool IsProvenSourceStructure(OemSleepPayloadSourceSide side)
    {
        if (!side.SetFeatureAbiProven || side.SetFeatureLength != 41 || !side.ReportId6Proven ||
            side.Copy is not { } copy || !copy.HelperSemanticProven || !copy.HelperAbiProven ||
            copy.SourceStackDisplacement is null || copy.DestinationReportOffset != 1 || !copy.ReportPayloadCopyProven)
            return false;

        return side.SourceDefinitions.Length > 0 &&
               side.SourcePointerDefinitions.Any(x => x.Status == "PROVEN") &&
               side.SourceObjectProvenance.Any(x => x.Status == "PROVEN") &&
               side.SourceBufferWrites.Any(x => x.Status == "PROVEN" && x.SourceOffset is >= 0 and < 40 && x.Width > 0) &&
               side.SourceOffsetToReportOffset.Length > 0 &&
               HasNormalizedReportMap(side.ReportMap);
    }

    private static bool IsExactSleepTimeProof(OemSleepPayloadSourceSide side) =>
        IsExactSleepTimeProof(side.SetFeatureCallRva, side.SetFeatureAbiProven, side.ReportId6Proven, side.Copy,
            side.SourceBufferWrites, side.SourceOffsetToReportOffset, side.ReportMap,
            side.SleepTimeBackwardTrace.Concat(side.SleepTimeForwardTrace));

    private static bool IsExactSleepTimeProof(
        uint setFeatureCallRva,
        bool setFeatureAbiProven,
        bool reportId6Proven,
        OemSleepPayloadCopyTrace? copy,
        IEnumerable<OemPayloadSourceBufferWrite> writes,
        IEnumerable<OemPayloadOffsetToReportOffset> offsetMap,
        IEnumerable<OemSleepPayloadReportField> reportMap,
        IEnumerable<OemPayloadSleepTrace> traces)
    {
        var candidate = traces.FirstOrDefault(x => x.Stage == "sleep_value" && x.Status == "PROVEN");
        if (!setFeatureAbiProven || !reportId6Proven || copy is not { } provenCopy ||
            !provenCopy.ReportPayloadCopyProven || !provenCopy.CopyCountUpperBoundProven || !provenCopy.NonNegativeCountProven ||
            candidate is null || candidate.SourceOffset is not int sourceOffset || candidate.ReportOffset is not int reportOffset ||
            candidate.CopyCallRva != provenCopy.CallRva || candidate.SetFeatureCallRva != setFeatureCallRva ||
            reportOffset != sourceOffset + 1 || candidate.UnresolvedEdges.Length != 0)
            return false;

        return writes.Any(x => x.Status == "PROVEN" && x.SourceOffset == sourceOffset && x.Width > 0) &&
               offsetMap.Any(x => x.Status == "PROVEN" && x.SourceOffset == sourceOffset && x.ReportOffset == reportOffset &&
                                  x.CopyCallRva == provenCopy.CallRva && x.Width > 0) &&
               reportMap.Any(x => x.Proven && x.StartOffset <= reportOffset && reportOffset < x.StartOffset + x.Length);
    }

    private static string SourceDefinitionKey(IEnumerable<OemSleepPayloadSourceStore> stores) =>
        string.Join('|', stores.Select(x => $"{x.RelativeToSetFeature}:{x.Value.Kind}").TakeLast(8));

    private static void AppendPayloadSourceSide(StringBuilder sb, string label, OemSleepPayloadSourceSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}; SHA256={side.Sha256}");
        sb.AppendLine($"  HidD_SetFeature=0x{side.SetFeatureCallRva:X8}; len={side.SetFeatureLength?.ToString() ?? "?"}; reportBase={(side.ReportBaseStackDisplacement is null ? "?" : StackSlot(side.ReportBaseStackDisplacement.Value))}; reportId6={side.ReportId6Proven}; ABI={side.SetFeatureAbiProven}");
        if (side.Copy is not null)
        {
            var copy = side.Copy;
            sb.AppendLine($"  memcpy-like call=0x{copy.CallRva:X8} relSet={copy.RelativeToSetFeature} helper=0x{copy.HelperRva:X8} semantic={copy.HelperSemantic} semanticProven={copy.HelperSemanticProven} abiProven={copy.HelperAbiProven}");
            sb.AppendLine($"    source={copy.SourceArgument}; destination={copy.DestinationArgument}; destinationReportOffset={copy.DestinationReportOffset?.ToString() ?? "?"}; count={copy.CountArgument}; exactCopyLength={copy.CopyLength?.ToString() ?? "?"}; signedUpperBound={copy.SignedUpperBound?.ToString() ?? "?"}; nonNegativeCountProven={copy.NonNegativeCountProven}; maximumCopyLength={copy.MaximumCopyLength?.ToString() ?? "?"}; bounded={copy.CopyCountUpperBoundProven}; reportPayloadCopy={copy.ReportPayloadCopyProven}");
            foreach (var item in copy.Evidence) sb.AppendLine("    EVIDENCE: " + item);
            foreach (var item in copy.UnresolvedEdges) sb.AppendLine("    UNRESOLVED: " + item);
            sb.AppendLine("    caller window:");
            foreach (var item in copy.CallerWindow) sb.AppendLine("      " + item);
        }
        sb.AppendLine("  source definitions:");
        foreach (var store in side.SourceDefinitions)
        {
            sb.AppendLine($"    {store.Slot} store=0x{store.StoreRva:X8} relSet={store.RelativeToSetFeature} :: {store.Instruction}");
            sb.AppendLine($"      value={store.Value.Expression}; kind={store.Value.Kind}; definition={(store.Value.DefinitionRva is null ? "?" : $"0x{store.Value.DefinitionRva:X8}")}");
            foreach (var step in store.Value.Steps) sb.AppendLine("      " + step);
            foreach (var item in store.UnresolvedEdges) sb.AppendLine("      UNRESOLVED: " + item);
        }
        sb.AppendLine("  SourcePointerDefinitions:");
        foreach (var item in side.SourcePointerDefinitions)
            sb.AppendLine($"    [{item.Status}] RVA=0x{item.Rva:X8}; block={item.BasicBlock}; kind={item.DefinitionKind}; source={item.SourceExpression}; instruction={item.Instruction}");
        sb.AppendLine("  SourcePointerAliases:");
        foreach (var item in side.SourcePointerAliases)
            sb.AppendLine($"    [{item.Status}] RVA=0x{item.Rva:X8}; block={item.BasicBlock}; kind={item.AliasKind}; {item.AliasExpression} -> {item.Target}; instruction={item.Instruction}");
        sb.AppendLine("  SourceObjectProvenance:");
        foreach (var item in side.SourceObjectProvenance)
            sb.AppendLine($"    [{item.Status}] RVA={(item.Rva is null ? "?" : $"0x{item.Rva:X8}")}; block={item.BasicBlock}; kind={item.ObjectKind}; expr={item.Expression}; producer={item.Producer}");
        sb.AppendLine("  SourceBufferWrites:");
        foreach (var item in side.SourceBufferWrites)
            sb.AppendLine($"    [{item.Status}] RVA=0x{item.Rva:X8}; sourceOffset={item.SourceOffset?.ToString() ?? "?"}; width={item.Width}; value={item.ValueExpression}; instruction={item.Instruction}; transform={item.Transformation}");
        sb.AppendLine("  SourceOffsetToReportOffset:");
        foreach (var item in side.SourceOffsetToReportOffset)
            sb.AppendLine($"    [{item.Status}] source[{item.SourceOffset}] -> report[{item.ReportOffset}] width={item.Width}; copyCall=0x{item.CopyCallRva:X8}; when={item.Condition}");
        sb.AppendLine("  SleepTimeForwardTrace:");
        foreach (var item in side.SleepTimeForwardTrace)
        {
            sb.AppendLine($"    [{item.Status}] RVA={(item.Rva is null ? "?" : $"0x{item.Rva:X8}")}; stage={item.Stage}; {item.InstructionOrAnchor} -> {item.NextEdge}");
            foreach (var edge in item.UnresolvedEdges) sb.AppendLine("      UNRESOLVED: " + edge);
        }
        sb.AppendLine("  SleepTimeBackwardTrace:");
        foreach (var item in side.SleepTimeBackwardTrace)
        {
            sb.AppendLine($"    [{item.Status}] RVA={(item.Rva is null ? "?" : $"0x{item.Rva:X8}")}; stage={item.Stage}; {item.InstructionOrAnchor} -> {item.NextEdge}");
            foreach (var edge in item.UnresolvedEdges) sb.AppendLine("      UNRESOLVED: " + edge);
        }
        sb.AppendLine("  UnresolvedEdges:");
        foreach (var edge in side.UnresolvedEdges) sb.AppendLine("    " + edge);
        sb.AppendLine("  report map:");
        foreach (var field in side.ReportMap) sb.AppendLine($"    report[{field.StartOffset}..{field.StartOffset + Math.Max(0, field.Length - 1)}] <- {field.Source}; {field.Provenance}; proven={field.Proven}");
        sb.AppendLine($"  SleepTime candidate: {side.SleepTimeCandidateStatus}");
        foreach (var item in side.SleepTimeEvidence) sb.AppendLine("    " + item);
        sb.AppendLine($"  fingerprint={side.NormalizedFingerprint}");
        sb.AppendLine();
    }
}

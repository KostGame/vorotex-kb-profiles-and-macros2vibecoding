using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemPayloadSourceProvenance(
    OemPayloadSourcePointerDefinition[] Definitions,
    OemPayloadSourcePointerAlias[] Aliases,
    OemPayloadSourceObjectProvenance[] Objects,
    OemPayloadSourceBufferWrite[] Writes,
    OemPayloadOffsetToReportOffset[] OffsetMap,
    OemPayloadSleepTrace[] SleepTimeForward,
    OemPayloadSleepTrace[] SleepTimeBackward,
    string[] UnresolvedEdges,
    string FunctionRange);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    private sealed record PayloadCfgBlock(int Id, int StartIndex, int EndIndex, List<int> Predecessors, List<int> Successors);

    private sealed record PayloadCfg(
        IReadOnlyList<NdeviceDecoded> Instructions,
        IReadOnlyList<PayloadCfgBlock> Blocks,
        int[] InstructionToBlock);

    private sealed record PayloadNearestDefinition(
        uint? Rva,
        string? Instruction,
        bool PhiMerge,
        string[] UnresolvedEdges);

    private sealed record PayloadOutParameterResult(
        uint CallRva,
        uint TargetRva,
        bool HelperWriteProven,
        uint? HelperWriteRva,
        string? HelperWrite,
        string[] Evidence,
        string[] UnresolvedEdges);

    private sealed record PayloadRegisterOrigin(
        string Status,
        uint? Rva,
        string Expression,
        string Producer,
        string[] Evidence,
        string[] UnresolvedEdges);

    private sealed record PayloadDescriptorMemberWrite(
        string Status,
        uint Rva,
        int Block,
        int MemberOffset,
        string Instruction,
        string ValueExpression,
        PayloadRegisterOrigin? ValueOrigin,
        string[] Evidence,
        string[] UnresolvedEdges);

    private sealed record PayloadDirectCaller(
        uint Rva,
        string FlowKind,
        NdeviceDecoded[] StackArguments,
        NdeviceDecoded[] Context,
        bool ArgumentSetupUnique,
        string[] Evidence,
        string[] UnresolvedEdges);

    private static OemPayloadSourceProvenance AnalyzePayloadSourceProvenance(
        NdevicePe pe,
        string oem,
        uint setFeatureRva,
        uint copyCallRva,
        int sourceSlot,
        int? copyMappingUpperBound,
        bool nonNegativeCountProven)
    {
        var functionEntry = FindPayloadPrologueRva(pe, copyCallRva);
        if (functionEntry is null)
        {
            return new OemPayloadSourceProvenance([], [], [], [], [], [], [],
                [$"{oem} @0x{copyCallRva:X8}: no validated EBP-frame function entry was found in the bounded raw .text predecessor range."],
                "unresolved");
        }
        var decoded = DecodePayloadFunction(pe, functionEntry.Value, copyCallRva, 0x10000u);
        var copyIndex = decoded.FindIndex(x => x.Rva == copyCallRva);
        if (copyIndex < 0)
        {
            return new OemPayloadSourceProvenance([], [], [], [], [], [], [],
                [$"{oem} copy call 0x{copyCallRva:X8} was not present after decoding from validated entry 0x{functionEntry.Value:X8}."],
                "unresolved");
        }

        var bounds = FindPayloadFunctionBounds(decoded, copyIndex);
        var function = decoded.Skip(bounds.StartIndex).Take(bounds.EndIndex - bounds.StartIndex + 1).ToArray();
        var localCopyIndex = copyIndex - bounds.StartIndex;
        var cfg = BuildPayloadCfg(function);
        var copyBlock = cfg.InstructionToBlock[localCopyIndex];
        var predecessorReachable = BackwardReachableBlocks(cfg, copyBlock);
        var definitions = new List<OemPayloadSourcePointerDefinition>();
        var aliases = new List<OemPayloadSourcePointerAlias>();
        var objects = new List<OemPayloadSourceObjectProvenance>();
        var writes = new List<OemPayloadSourceBufferWrite>();
        var unresolved = new List<string>();
        var directDefinitionCandidates = new List<(int DefinitionIndex, bool DominatesCopy, bool PhiMerge)>();
        var analysisLength = copyMappingUpperBound ?? 0;

        foreach (var block in cfg.Blocks.Where(x => predecessorReachable.Contains(x.Id)))
        {
            for (var i = block.StartIndex; i <= block.EndIndex; i++)
            {
                var item = function[i];
                var instruction = item.Instruction;
                var predecessorBlocks = block.Predecessors.Select(x => BlockLabel(cfg, x)).ToArray();
                var dominatesCopy = Dominates(cfg, block.Id, copyBlock) && (block.Id != copyBlock || i < localCopyIndex);

                if (IsStackMemoryOperand(instruction, 0, sourceSlot) && IsStackWrite(instruction))
                {
                    var nearest = NearestDefinitionForStore(cfg, i, instruction);
                    var source = DescribeSourceExpression(instruction);
                    (string Expression, int Offset)? memberFromNearest = null;
                    NdeviceDecoded? nearestItem = nearest.Rva is uint nearestRva ? cfg.Instructions.FirstOrDefault(x => x.Rva == nearestRva) : null;
                    if (nearestItem is not null && nearestItem.Instruction.Op1Kind == OpKind.Memory &&
                        Normalize(nearestItem.Instruction.MemoryBase) is not (Register.None or Register.EBP or Register.ESP))
                    {
                        var nearestMember = (Expression: FormatPayloadMemory(nearestItem.Instruction), Offset: checked((int)SignedDisp(nearestItem.Instruction)));
                        memberFromNearest = nearestMember;
                        source = nearestMember.Expression;
                    }
                    const string status = "INFERRED";
                    var evidence = new List<string>
                    {
                        $"{oem} @0x{item.Rva:X8}: writes source slot {StackSlot(sourceSlot)} in {BlockLabel(cfg, block.Id)}.",
                        $"{oem} @0x{copyCallRva:X8}: copy call reads {StackSlot(sourceSlot)}."
                    };
                    if (dominatesCopy) evidence.Add($"{oem} CFG dominance: {BlockLabel(cfg, block.Id)} dominates the copy block {BlockLabel(cfg, copyBlock)}.");
                    else unresolved.Add($"{oem} @0x{item.Rva:X8}: source-slot definition is predecessor-reachable but does not dominate copy 0x{copyCallRva:X8}.");
                    if (memberFromNearest is not null && nearestItem is not null)
                    {
                        var baseRegister = Normalize(nearestItem.Instruction.MemoryBase);
                        var baseOrigin = TracePayloadRegisterOrigin(cfg, FindInstructionIndex(cfg.Instructions, nearestItem.Rva), baseRegister, 3, []);
                        var countRead = cfg.Instructions.Take(localCopyIndex).LastOrDefault(x =>
                            x.Instruction.Mnemonic == Mnemonic.Mov && x.Instruction.Op1Kind == OpKind.Memory &&
                            Normalize(x.Instruction.MemoryBase) == baseRegister && SignedDisp(x.Instruction) == memberFromNearest.Value.Offset + 4);
                        var memberEvidence = new List<string>(evidence)
                        {
                            $"{oem} @0x{nearestItem.Rva:X8}: {Normalize(nearestItem.Instruction.Op0Register)} receives the source pointer from {memberFromNearest.Value.Expression}."
                        };
                        if (countRead is not null) memberEvidence.Add($"{oem} @0x{countRead.Rva:X8}: reads adjacent length member [{baseRegister}+0x{memberFromNearest.Value.Offset + 4:X}] for the same bounded copy.");
                        memberEvidence.AddRange(baseOrigin.Evidence);
                        aliases.Add(new OemPayloadSourcePointerAlias(
                            oem, "PROVEN", nearestItem.Rva, BlockLabel(cfg, cfg.InstructionToBlock[FindInstructionIndex(cfg.Instructions, nearestItem.Rva)]), predecessorBlocks,
                            nearestItem.Text, "register_from_object_member", Normalize(nearestItem.Instruction.Op0Register).ToString(), memberFromNearest.Value.Expression,
                            memberEvidence.ToArray(), baseOrigin.UnresolvedEdges));
                        objects.Add(new OemPayloadSourceObjectProvenance(
                            oem, "PROVEN", nearestItem.Rva, BlockLabel(cfg, cfg.InstructionToBlock[FindInstructionIndex(cfg.Instructions, nearestItem.Rva)]), "pointer_length_descriptor_members",
                            $"pointer={memberFromNearest.Value.Expression}; length=[{baseRegister}+0x{memberFromNearest.Value.Offset + 4:X}]", memberFromNearest.Value.Offset,
                            $"source pointer member plus adjacent count member; base origin: {baseOrigin.Expression}", memberEvidence.ToArray(), baseOrigin.UnresolvedEdges));

                        if (baseOrigin.Rva is uint entrySnapshotRva && baseOrigin.Status == "PROVEN" && baseOrigin.Expression == Register.ESP.ToString())
                        {
                            var snapshotIndex = FindInstructionIndex(cfg.Instructions, entrySnapshotRva);
                            var entryIndex = snapshotIndex - 1;
                            if (entryIndex >= 0)
                            {
                                var entryInstruction = cfg.Instructions[entryIndex];
                                if (entryInstruction.Instruction.Mnemonic == Mnemonic.Push && entryInstruction.Instruction.Op0Kind == OpKind.Register && Normalize(entryInstruction.Instruction.Op0Register) == baseRegister && memberFromNearest.Value.Offset == 8)
                                {
                                    var pointerArgument = "[entry ESP+0x4]";
                                    var lengthArgument = "[entry ESP+0x8]";
                                    var entryEvidence = new[]
                                    {
                                        $"{oem} @0x{entryInstruction.Rva:X8}: pushes {baseRegister}, saving the caller register at entry ESP-0x4.",
                                        $"{oem} @0x{entrySnapshotRva:X8}: mov {baseRegister},esp establishes [EBX+0x8] = {pointerArgument} and [EBX+0xC] = {lengthArgument}.",
                                        $"{oem} @0x{nearestItem.Rva:X8}: loads the source pointer from [EBX+0x8]; @0x{countRead?.Rva:X8} loads the bounded length from [EBX+0xC]."
                                    };
                                    var callerEdge = new[] { $"The pointer and length are incoming stack arguments at entry 0x{entryInstruction.Rva:X8}; direct caller setup must be decoded before attributing a payload field or SleepTime value." };
                                    aliases.Add(new OemPayloadSourcePointerAlias(oem, "PROVEN", entryInstruction.Rva, BlockLabel(cfg, cfg.InstructionToBlock[entryIndex]),
                                        cfg.Blocks[cfg.InstructionToBlock[entryIndex]].Predecessors.Select(x => BlockLabel(cfg, x)).ToArray(),
                                        $"{entryInstruction.Text}; {cfg.Instructions[snapshotIndex].Text}", "incoming_stack_argument_pointer", pointerArgument,
                                        memberFromNearest.Value.Expression, entryEvidence, callerEdge));
                                    objects.Add(new OemPayloadSourceObjectProvenance(oem, "PROVEN", entryInstruction.Rva, BlockLabel(cfg, cfg.InstructionToBlock[entryIndex]),
                                        "stack_call_argument_pair", $"pointer={pointerArgument}; length={lengthArgument}", 4,
                                        "entry stack arguments preserved through push EBX / mov EBX,ESP", entryEvidence, callerEdge));
                                }
                            }
                        }

                        var descriptorWrites = RecoverDescriptorMemberWrites(
                            oem, cfg, predecessorReachable, baseRegister, memberFromNearest.Value.Offset, copyBlock, localCopyIndex);
                        var snapshotWrites = baseOrigin.Rva is uint snapshotRva && baseOrigin.Status == "PROVEN" && baseOrigin.Expression == Register.ESP.ToString()
                            ? RecoverStackSnapshotMemberWrites(oem, cfg, snapshotRva, memberFromNearest.Value.Offset, copyBlock, localCopyIndex)
                            : [];
                        var allDescriptorWrites = descriptorWrites.Concat(snapshotWrites).OrderBy(x => x.Rva).ToArray();
                        if (snapshotWrites.Length == 0 && baseOrigin.Rva is uint emptySnapshotRva && baseOrigin.Status == "PROVEN" && baseOrigin.Expression == Register.ESP.ToString())
                        {
                            var snapshotIndex = FindInstructionIndex(cfg.Instructions, emptySnapshotRva);
                            var startIndex = Math.Max(cfg.Blocks[cfg.InstructionToBlock[snapshotIndex]].StartIndex, snapshotIndex - 12);
                            var preview = string.Join(" | ", cfg.Instructions.Skip(startIndex).Take(snapshotIndex - startIndex).Select(x => $"0x{x.Rva:X8}:{x.Text}"));
                            unresolved.Add($"{oem} @0x{emptySnapshotRva:X8}: no implicit PUSH producer maps to descriptor members [EBX+0x{memberFromNearest.Value.Offset:X}]/[EBX+0x{memberFromNearest.Value.Offset + 4:X}] in the bounded predecessor window: {preview}");
                        }
                        foreach (var descriptorWrite in allDescriptorWrites)
                        {
                            var descriptorMember = $"[{baseRegister}+0x{descriptorWrite.MemberOffset:X}]";
                            var descriptorEvidence = new List<string>(descriptorWrite.Evidence);
                            if (descriptorWrite.ValueOrigin is not null) descriptorEvidence.AddRange(descriptorWrite.ValueOrigin.Evidence);
                            aliases.Add(new OemPayloadSourcePointerAlias(
                                oem, descriptorWrite.Status, descriptorWrite.Rva, BlockLabel(cfg, descriptorWrite.Block),
                                cfg.Blocks[descriptorWrite.Block].Predecessors.Select(x => BlockLabel(cfg, x)).ToArray(), descriptorWrite.Instruction,
                                descriptorWrite.MemberOffset == memberFromNearest.Value.Offset ? "descriptor_pointer_member_write" : "descriptor_length_member_write",
                                descriptorWrite.ValueExpression, descriptorMember, descriptorEvidence.ToArray(),
                                descriptorWrite.UnresolvedEdges.Concat(descriptorWrite.ValueOrigin?.UnresolvedEdges ?? []).Distinct(StringComparer.Ordinal).ToArray()));
                            objects.Add(new OemPayloadSourceObjectProvenance(
                                oem, descriptorWrite.Status, descriptorWrite.Rva, BlockLabel(cfg, descriptorWrite.Block), "stack_pointer_length_descriptor_write",
                                descriptorMember, descriptorWrite.MemberOffset,
                                descriptorWrite.ValueOrigin is null ? "direct descriptor member write" : $"direct descriptor member write; value origin: {descriptorWrite.ValueOrigin.Expression}",
                                descriptorEvidence.ToArray(), descriptorWrite.UnresolvedEdges.Concat(descriptorWrite.ValueOrigin?.UnresolvedEdges ?? []).Distinct(StringComparer.Ordinal).ToArray()));
                        }
                        if (allDescriptorWrites.Length == 0 && baseOrigin.Status == "PROVEN" && baseOrigin.Expression == Register.ESP.ToString())
                        {
                            unresolved.Add($"{oem} @0x{nearestItem.Rva:X8}: source descriptor pointer member {memberFromNearest.Value.Expression} is read from the ESP-based stack descriptor, but no dominating direct write to [{baseRegister}+0x{memberFromNearest.Value.Offset:X}] was decoded before copy 0x{copyCallRva:X8}; caller/callee stack ownership is the next bounded edge.");
                        }
                    }

                    var definitionIndex = definitions.Count;
                    definitions.Add(new OemPayloadSourcePointerDefinition(
                        oem, status, item.Rva, BlockLabel(cfg, block.Id), predecessorBlocks, item.Text,
                        "direct_stack_write", source, nearest.Rva, nearest.Instruction, evidence.ToArray(),
                        nearest.UnresolvedEdges.Concat(dominatesCopy ? [] : ["Conditional/phi-like predecessor path prevents a unique reaching definition."]).ToArray()));
                    directDefinitionCandidates.Add((definitionIndex, dominatesCopy, nearest.PhiMerge));

                    if (TryObjectMemberOperand(instruction, 1, out var member))
                    {
                        objects.Add(new OemPayloadSourceObjectProvenance(
                            oem, "INFERRED", item.Rva, BlockLabel(cfg, block.Id), "object_member_pointer",
                            member.Expression, member.Offset, "direct source-slot write", evidence.ToArray(),
                            ["Object base provenance must be traced through the defining register before assigning a payload field."]));
                    }
                    else if (instruction.Op1Kind == OpKind.Register && nearest.Instruction is not null && nearest.Instruction.Contains("call ", StringComparison.OrdinalIgnoreCase))
                    {
                        objects.Add(new OemPayloadSourceObjectProvenance(
                            oem, "INFERRED", item.Rva, BlockLabel(cfg, block.Id), "helper_return_pointer",
                            source, null, nearest.Instruction, evidence.ToArray(),
                            ["A direct helper return was observed, but its return-object ownership is not yet proven."]));
                    }
                }

                if (IsStackMemoryOperand(instruction, 0, sourceSlot) && instruction.Mnemonic == Mnemonic.Lea)
                {
                    // LEA never has a memory destination; retained for defensive completeness.
                    continue;
                }

                if (instruction.Mnemonic == Mnemonic.Lea && instruction.Op0Kind == OpKind.Register && IsStackMemoryOperand(instruction, 1, sourceSlot))
                {
                    var target = Normalize(instruction.Op0Register).ToString();
                    var outParameter = FindOutParameterCall(pe, cfg, i, Normalize(instruction.Op0Register));
                    var status = outParameter?.HelperWriteProven == true ? "PROVEN" : "INFERRED";
                    var evidence = new List<string>
                    {
                        $"{oem} @0x{item.Rva:X8}: LEA creates alias {target} -> {StackSlot(sourceSlot)} in {BlockLabel(cfg, block.Id)}."
                    };
                    var unresolvedEdges = new List<string>();
                    if (outParameter is not null)
                    {
                        evidence.AddRange(outParameter.Evidence);
                        unresolvedEdges.AddRange(outParameter.UnresolvedEdges);
                        aliases.Add(new OemPayloadSourcePointerAlias(
                            oem, status, outParameter.CallRva, BlockLabel(cfg, cfg.InstructionToBlock[FindInstructionIndex(function, outParameter.CallRva)]),
                            predecessorBlocks, $"call 0x{outParameter.TargetRva:X8}", "out_parameter_call",
                            target, StackSlot(sourceSlot), outParameter.Evidence, outParameter.UnresolvedEdges));
                        objects.Add(new OemPayloadSourceObjectProvenance(
                            oem, status, outParameter.HelperWriteRva ?? outParameter.CallRva,
                            BlockLabel(cfg, block.Id), "helper_owned_or_out_parameter_memory", StackSlot(sourceSlot), null,
                            $"caller 0x{outParameter.CallRva:X8} -> helper 0x{outParameter.TargetRva:X8}", outParameter.Evidence,
                            outParameter.UnresolvedEdges));
                    }
                    else
                    {
                        unresolvedEdges.Add($"{oem} @0x{item.Rva:X8}: no bounded direct caller use of the {target} alias was recovered.");
                    }
                    aliases.Add(new OemPayloadSourcePointerAlias(
                        oem, status, item.Rva, BlockLabel(cfg, block.Id), predecessorBlocks, item.Text,
                        "stack_address", target, StackSlot(sourceSlot), evidence.ToArray(), unresolvedEdges.ToArray()));
                }

                if (IsStackMemoryOperand(instruction, 1, sourceSlot) && instruction.Op0Kind == OpKind.Register)
                {
                    aliases.Add(new OemPayloadSourcePointerAlias(
                        oem, "INFERRED", item.Rva, BlockLabel(cfg, block.Id), predecessorBlocks, item.Text,
                        "stack_value_load", StackSlot(sourceSlot), Normalize(instruction.Op0Register).ToString(),
                        [$"{oem} @0x{item.Rva:X8}: reads the source-slot value into {Normalize(instruction.Op0Register)}."],
                        ["A register read is an alias/use, not itself a source-pointer definition."]));
                }
                else if (instruction.Mnemonic == Mnemonic.Push && IsStackMemoryOperand(instruction, 0, sourceSlot))
                {
                    aliases.Add(new OemPayloadSourcePointerAlias(
                        oem, item.Rva == copyCallRva ? "PROVEN" : "INFERRED", item.Rva, BlockLabel(cfg, block.Id), predecessorBlocks, item.Text,
                        "stack_value_push", StackSlot(sourceSlot), "call_argument",
                        [$"{oem} @0x{item.Rva:X8}: pushes {StackSlot(sourceSlot)} as a call argument."],
                        item.Rva == copyCallRva ? [] : ["The push is a use; it is not yet tied to the memcpy-like consumer."]));
                }
            }
        }

        var directDefinitionCount = directDefinitionCandidates.Count;
        foreach (var candidate in directDefinitionCandidates)
        {
            var current = definitions[candidate.DefinitionIndex];
            var proven = directDefinitionCount == 1 && candidate.DominatesCopy && !candidate.PhiMerge;
            var finalEdges = proven
                ? current.UnresolvedEdges
                : current.UnresolvedEdges.Concat([$"Final CFG reaching-definition count for {StackSlot(sourceSlot)} is {directDefinitionCount}; source-pointer uniqueness/dominance is not proven."]).Distinct(StringComparer.Ordinal).ToArray();
            definitions[candidate.DefinitionIndex] = current with
            {
                Status = proven ? "PROVEN" : "INFERRED",
                UnresolvedEdges = finalEdges
            };
        }

        var writeCandidates = RecoverStackBufferWrites(oem, cfg, predecessorReachable, sourceSlot, analysisLength);
        writes.AddRange(writeCandidates);

        var entryCallers = FindDirectPayloadCallers(pe, function[0].Rva);
        if (entryCallers.Length == 0)
        {
            unresolved.Add($"{oem} entry 0x{function[0].Rva:X8}: no direct E8/E9 xref was found in .text; the incoming pointer/length argument producer is an indirect or non-local control-flow edge.");
        }
        foreach (var caller in entryCallers)
        {
            var setupRecovered = caller.ArgumentSetupUnique && caller.StackArguments.Length == 2;
            var setupText = setupRecovered
                ? $"arg2(length)={caller.StackArguments[0].Text}; arg1(pointer)={caller.StackArguments[1].Text}"
                : "contiguous two-PUSH caller argument setup was not uniquely decoded";
            var callerEvidence = caller.Evidence.Concat(new[] { $"{oem} @0x{caller.Rva:X8}: {setupText}." }).ToArray();
            var callerEdges = caller.UnresolvedEdges.Concat(setupRecovered
                ? new[] { "Caller argument values are recovered syntactically; their producer/value provenance must be traced before assigning source-buffer fields or SleepTime." }
                : new[] { "Direct entry xref exists, but bounded pre-call argument decoding is incomplete or ambiguous." }).ToArray();
            aliases.Add(new OemPayloadSourcePointerAlias(oem, setupRecovered ? "PROVEN" : "INFERRED", caller.Rva, "external .text caller", [],
                $"{caller.FlowKind} 0x{function[0].Rva:X8}", "direct_entry_call", setupText, "entry stack arguments", callerEvidence, callerEdges));
            objects.Add(new OemPayloadSourceObjectProvenance(oem, setupRecovered ? "PROVEN" : "INFERRED", caller.Rva, "external .text caller",
                "direct_entry_call_setup", setupText, null, $"{caller.FlowKind} to entry 0x{function[0].Rva:X8}", callerEvidence, callerEdges));

            if (setupRecovered && TryInstructionImmediate(caller.StackArguments[1].Instruction, 0, out var pointerValue) && pointerValue >= pe.ImageBase)
            {
                var pointerRva64 = pointerValue - pe.ImageBase;
                if (pointerRva64 <= uint.MaxValue)
                {
                    var pointerRva = (uint)pointerRva64;
                    var section = pe.Sections.FirstOrDefault(x => x.Contains(pointerRva));
                    if (section is not null)
                    {
                        var globalEvidence = callerEvidence.Concat(new[] { $"{oem}: arg1 absolute pointer 0x{pointerValue:X8} resolves to RVA 0x{pointerRva:X8} in section {section.Name}." }).ToArray();
                        objects.Add(new OemPayloadSourceObjectProvenance(oem, "PROVEN", caller.Rva, "external .text caller",
                            "image_global_source_buffer", $"VA=0x{pointerValue:X8}; RVA=0x{pointerRva:X8}; section={section.Name}", 0,
                            $"direct arg1 pointer for entry 0x{function[0].Rva:X8}", globalEvidence,
                            ["Static writes to this image-global buffer are recorded separately; caller-value provenance is still required for SleepTime attribution."]));
                        var globalWrites = RecoverCallerGlobalBufferWrites(oem, caller, pointerValue, analysisLength);
                        writes.AddRange(globalWrites);
                        foreach (var globalWrite in globalWrites)
                        {
                            if (TryEbpLocalSlot(globalWrite.ValueExpression, out var localSlot))
                                objects.AddRange(RecoverCallerStackLocalDefinitions(pe, oem, caller.Rva, globalWrite.SourceOffset ?? 0, localSlot));
                    }
                }
            }
            }


        }

        if (definitions.Count == 0)
        {
            unresolved.Add($"{oem} @0x{copyCallRva:X8}: no direct write to {StackSlot(sourceSlot)} exists in the bounded containing-function CFG predecessor slice {FunctionRange(bounds)}.");
            objects.Add(new OemPayloadSourceObjectProvenance(
                oem, "UNRESOLVED", copyCallRva, BlockLabel(cfg, copyBlock), "unknown_pointer",
                StackSlot(sourceSlot), null, "copy-source local", [],
                ["The local is read as a pointer by memcpy, but no unique direct stack write was statically found; LEA/out-parameter aliases remain the next bounded producer candidates."]));
        }

        var map = copyMappingUpperBound is int mappingUpperBound
            ? Enumerable.Range(0, Math.Max(0, mappingUpperBound))
                .Select(offset => new OemPayloadOffsetToReportOffset(
                    oem, "PROVEN", offset, offset + 1, 1, copyCallRva,
                    nonNegativeCountProven
                        ? $"when 0 <= copy count <= {mappingUpperBound} and count > {offset}"
                        : $"CONDITIONAL ONLY: when copy count is nonnegative and <= signed upper bound {mappingUpperBound}, and count > {offset}",
                    [$"{oem} @0x{copyCallRva:X8}: decoded REP MOVSB helper maps source[{offset}] to report[{offset + 1}] conditionally on count > {offset}."]))
                .ToArray()
            : [];
        if (copyMappingUpperBound is null)
            unresolved.Add($"{oem} @0x{copyCallRva:X8}: no usable copy-count upper bound was recovered; source/report offset map is omitted.");

        var forward = BuildSleepTimeForwardTrace(pe, oem, copyCallRva, sourceSlot);
        var backward = BuildSleepTimeBackwardTrace(oem, copyCallRva, sourceSlot, definitions, objects, writes);
        unresolved.AddRange(forward.SelectMany(x => x.UnresolvedEdges));
        unresolved.AddRange(backward.SelectMany(x => x.UnresolvedEdges));
        unresolved.AddRange(definitions.SelectMany(x => x.UnresolvedEdges));
        unresolved.AddRange(aliases.SelectMany(x => x.UnresolvedEdges));
        unresolved.AddRange(objects.SelectMany(x => x.UnresolvedEdges));
        unresolved.AddRange(writes.SelectMany(x => x.UnresolvedEdges));

        return new OemPayloadSourceProvenance(
            definitions.OrderBy(x => x.Rva).Take(256).ToArray(),
            aliases.OrderBy(x => x.Rva).Take(384).ToArray(),
            objects.OrderBy(x => x.Rva ?? uint.MaxValue).Take(192).ToArray(),
            writes.OrderBy(x => x.Rva).Take(256).ToArray(),
            map,
            forward,
            backward,
            unresolved.Distinct(StringComparer.Ordinal).Take(256).ToArray(),
            FunctionRange(bounds));
    }

    private static OemPayloadSourceBufferWrite[] RecoverCallerGlobalBufferWrites(
        string oem,
        PayloadDirectCaller caller,
        ulong bufferAddress,
        int maximumCopyLength)
    {
        if (maximumCopyLength <= 0) return [];
        var bufferEnd = bufferAddress + checked((ulong)maximumCopyLength);
        var writes = new List<OemPayloadSourceBufferWrite>();
        foreach (var item in caller.Context)
        {
            var instruction = item.Instruction;
            if (instruction.Op0Kind != OpKind.Memory || !IsStackWrite(instruction)) continue;
            var displacement = instruction.MemoryDisplacement64;
            if (displacement < bufferAddress || displacement >= bufferEnd) continue;
            var sourceOffset = checked((int)(displacement - bufferAddress));
            var directOffset = Normalize(instruction.MemoryBase) == Register.None && Normalize(instruction.MemoryIndex) == Register.None;
            var status = caller.ArgumentSetupUnique && directOffset ? "PROVEN" : "INFERRED";
            var valueOrigin = TraceCallerWriteValue(caller, item);
            var unresolved = (directOffset ? Array.Empty<string>() : ["The write uses a register/indexed address expression; its precise source-buffer offset is dynamic rather than a statically fixed byte."]).Concat(valueOrigin.UnresolvedEdges).ToArray();
            writes.Add(new OemPayloadSourceBufferWrite(
                oem, status, item.Rva, $"caller @0x{caller.Rva:X8}", item.Text, sourceOffset,
                PayloadWriteWidth(instruction), valueOrigin.Expression,
                (directOffset ? "direct absolute image-global source-buffer write" : "indexed image-global source-buffer write") + "; " + valueOrigin.Producer,
                [$"{oem} @0x{item.Rva:X8}: writes source buffer VA 0x{bufferAddress:X8}+0x{sourceOffset:X} before direct entry call 0x{caller.Rva:X8}."],
                unresolved));
        }
        return writes.GroupBy(x => (x.Rva, x.SourceOffset, x.Instruction)).Select(x => x.First()).OrderBy(x => x.Rva).ToArray();
    }

    private static PayloadRegisterOrigin TraceCallerWriteValue(PayloadDirectCaller caller, NdeviceDecoded write)
    {
        var instruction = write.Instruction;
        if (TryInstructionImmediate(instruction, 1, out var immediate))
        {
            return new PayloadRegisterOrigin("PROVEN", write.Rva, $"0x{immediate:X}", "immediate write value", [], []);
        }
        if (instruction.Op1Kind != OpKind.Register)
        {
            return new PayloadRegisterOrigin("INFERRED", write.Rva, DescribeSourceExpression(instruction), "non-register write value", [],
                ["The source-buffer write value is not an immediate or register operand that can be followed by the bounded caller slice."]);
        }
        var register = Normalize(instruction.Op1Register);
        var index = FindInstructionIndex(caller.Context, write.Rva);
        for (var i = index - 1; i >= 0; i--)
        {
            var candidate = caller.Context[i];
            var candidateInstruction = candidate.Instruction;
            if (!WritesRegister(candidateInstruction) || candidateInstruction.Op0Kind != OpKind.Register || Normalize(candidateInstruction.Op0Register) != register) continue;
            if (candidateInstruction.Mnemonic == Mnemonic.Mov && candidateInstruction.Op1Kind == OpKind.Memory)
            {
                var expression = FormatPayloadMemory(candidateInstruction);
                return new PayloadRegisterOrigin("PROVEN", candidate.Rva, expression, $"@0x{candidate.Rva:X8}: {candidate.Text}",
                    [$"@0x{candidate.Rva:X8}: direct local/memory definition reaches the source-buffer write @0x{write.Rva:X8}."],
                    ["The immediate value producer for this local/memory read is outside the bounded caller window and must be traced before SleepTime attribution."]);
            }
            if (TryInstructionImmediate(candidateInstruction, 1, out var sourceImmediate))
            {
                return new PayloadRegisterOrigin("PROVEN", candidate.Rva, $"0x{sourceImmediate:X}", $"@0x{candidate.Rva:X8}: {candidate.Text}", [], []);
            }
            return new PayloadRegisterOrigin("INFERRED", candidate.Rva, register.ToString(), $"@0x{candidate.Rva:X8}: {candidate.Text}", [],
                ["The nearest bounded caller definition is not a directly decodable mov/immediate producer."]);
        }
        return new PayloadRegisterOrigin("UNRESOLVED", null, register.ToString(), "no bounded caller definition", [],
            ["No bounded caller-context definition was found for the source-buffer write register."]);
    }



    private static OemPayloadSourceObjectProvenance[] RecoverCallerStackLocalDefinitions(
        NdevicePe pe,
        string oem,
        uint callerRva,
        int sourceOffset,
        int localSlot,
        int localDepth = 2,
        HashSet<(uint CallerRva, int LocalSlot)>? localVisited = null)
    {
        localVisited ??= [];
        if (localDepth < 0 || !localVisited.Add((callerRva, localSlot)))
            return [new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", callerRva, "local-value recursion boundary",
                "caller_ebp_local_definition", $"{StackSlot(localSlot)} -> source[{sourceOffset}]", sourceOffset,
                "bounded local-slot recursion cycle/depth limit", [],
                [$"{oem} @0x{callerRva:X8}: local slot {StackSlot(localSlot)} reached a bounded recursion boundary; no value was inferred."])];
        var entry = FindPayloadPrologueRva(pe, callerRva);
        if (entry is null)
        {
            return [new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", callerRva, "caller entry unresolved",
                "caller_ebp_local_definition", StackSlot(localSlot), sourceOffset, "no validated caller EBP-frame entry", [],
                [$"{oem} @0x{callerRva:X8}: cannot recover producers of {StackSlot(localSlot)} because no bounded EBP-frame caller entry was validated."])];
        }
        var decoded = DecodePayloadFunction(pe, entry.Value, callerRva, 0x10000u);
        var callerIndex = decoded.FindIndex(x => x.Rva == callerRva);
        if (callerIndex < 0)
        {
            return [new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", callerRva, "caller decode unresolved",
                "caller_ebp_local_definition", StackSlot(localSlot), sourceOffset, $"entry 0x{entry.Value:X8} does not decode to caller", [],
                [$"{oem}: bounded decode from 0x{entry.Value:X8} did not reach caller 0x{callerRva:X8}."])];
        }
        var bounds = FindPayloadFunctionBounds(decoded, callerIndex);
        var function = decoded.Skip(bounds.StartIndex).Take(bounds.EndIndex - bounds.StartIndex + 1).ToArray();
        var localCallerIndex = callerIndex - bounds.StartIndex;
        var cfg = BuildPayloadCfg(function);
        var callerBlock = cfg.InstructionToBlock[localCallerIndex];
        var relevantBlocks = BackwardReachableBlocks(cfg, callerBlock);
        var result = new List<OemPayloadSourceObjectProvenance>();
        result.Add(new OemPayloadSourceObjectProvenance(oem, "PROVEN", function[0].Rva, FunctionRange(bounds),
            "caller_function_boundary", $"entry=0x{function[0].Rva:X8}; direct call=0x{callerRva:X8}", null,
            "validated EBP-frame caller function containing the image-global-buffer call",
            [$"{oem} @0x{function[0].Rva:X8}: validated EBP-frame function entry reaches direct buffer call @0x{callerRva:X8}."], []));
        foreach (var block in cfg.Blocks.Where(x => relevantBlocks.Contains(x.Id)))
        {
            for (var i = block.StartIndex; i <= Math.Min(block.EndIndex, localCallerIndex - 1); i++)
            {
                var item = cfg.Instructions[i];
                var instruction = item.Instruction;
                if (!IsStackMemoryOperand(instruction, 0, localSlot) || !IsStackWrite(instruction)) continue;
                var nearest = NearestDefinitionForStore(cfg, i, instruction);
                var source = DescribeSourceExpression(instruction);
                if (nearest.Rva is uint nearestRva)
                {
                    var nearestItem = cfg.Instructions.FirstOrDefault(x => x.Rva == nearestRva);
                    if (nearestItem is not null && nearestItem.Instruction.Op1Kind == OpKind.Memory) source = FormatPayloadMemory(nearestItem.Instruction);
                }
                var dominatesCaller = Dominates(cfg, block.Id, callerBlock) && (block.Id != callerBlock || i < localCallerIndex);
                var status = dominatesCaller && !nearest.PhiMerge ? "PROVEN" : "INFERRED";
                var evidence = new List<string>
                {
                    $"{oem} @0x{item.Rva:X8}: writes {StackSlot(localSlot)} which feeds source[{sourceOffset}] through caller @0x{callerRva:X8}.",
                    $"{oem} @0x{callerRva:X8}: direct entry call sends the image-global source buffer to the memcpy path."
                };
                if (nearest.Rva is uint definitionRva) evidence.Add($"{oem} @0x{definitionRva:X8}: nearest bounded definition for the local-store source is {nearest.Instruction}.");
                result.Add(new OemPayloadSourceObjectProvenance(oem, status, item.Rva, BlockLabel(cfg, block.Id),
                    "caller_ebp_local_definition", $"{StackSlot(localSlot)} -> source[{sourceOffset}]", sourceOffset,
                    source, evidence.ToArray(), nearest.UnresolvedEdges.Concat(dominatesCaller ? [] : ["The local write is predecessor-reachable but does not dominate the direct caller edge."]).ToArray()));
                if (instruction.Op1Kind == OpKind.Register)
                {
                    var sourceRegister = Normalize(instruction.Op1Register);
                    var reaching = FindPayloadReachingRegisterDefinitions(cfg, i, sourceRegister);
                    foreach (var reachingDefinition in reaching)
                    {
                        result.Add(new OemPayloadSourceObjectProvenance(oem, reaching.Length == 1 && dominatesCaller ? "PROVEN" : "INFERRED",
                            reachingDefinition.Rva, BlockLabel(cfg, cfg.InstructionToBlock[FindInstructionIndex(cfg.Instructions, reachingDefinition.Rva)]),
                            "caller_local_register_definition", $"{sourceRegister} -> {StackSlot(localSlot)} -> source[{sourceOffset}]", sourceOffset,
                            reachingDefinition.Text,
                            [$"{oem} @0x{reachingDefinition.Rva:X8}: CFG-reaching definition of {sourceRegister} for local store @0x{item.Rva:X8}.",
                             $"{oem} @0x{callerRva:X8}: direct call carries source[{sourceOffset}] to the memcpy path."],
                            reaching.Length == 1 ? [] : ["Multiple CFG-reaching definitions form a phi-like value merge."]));
                        var origin = TracePayloadRegisterOrigin(cfg, FindInstructionIndex(cfg.Instructions, reachingDefinition.Rva) + 1, sourceRegister, 4, []);
                        result.Add(new OemPayloadSourceObjectProvenance(oem,
                            origin.Rva is not null && origin.Status == "PROVEN" && origin.UnresolvedEdges.Length == 0 ? "PROVEN" : "INFERRED",
                            origin.Rva ?? reachingDefinition.Rva, BlockLabel(cfg, cfg.InstructionToBlock[FindInstructionIndex(cfg.Instructions, reachingDefinition.Rva)]),
                            "caller_local_register_value_origin", $"{sourceRegister} -> {StackSlot(localSlot)} -> source[{sourceOffset}]", sourceOffset,
                            origin.Expression,
                            origin.Evidence.Select(x => x.StartsWith("@", StringComparison.Ordinal) ? $"{oem} {x}" : x).ToArray(), origin.UnresolvedEdges));
                        var reachingInstruction = reachingDefinition.Instruction;
                        if (reachingInstruction.Mnemonic == Mnemonic.Lea && reachingInstruction.Op1Kind == OpKind.Memory &&
                            Normalize(reachingInstruction.MemoryBase) is not (Register.None or Register.EBP or Register.ESP))
                        {
                            var baseRegister = Normalize(reachingInstruction.MemoryBase);
                            if (FindPayloadReachingRegisterDefinitions(cfg, FindInstructionIndex(cfg.Instructions, reachingDefinition.Rva), baseRegister).Length == 0)
                                result.AddRange(RecoverLiveInRegisterFromDirectCallers(pe, oem, function[0].Rva, callerRva,
                                    sourceOffset, localSlot, baseRegister, 2, []));
                        }
                        if (localDepth > 0 && TryEmbeddedEbpLocalSlot(origin.Expression, out var nestedSlot) && nestedSlot != localSlot)
                            result.AddRange(RecoverCallerStackLocalDefinitions(pe, oem, callerRva, sourceOffset, nestedSlot,
                                localDepth - 1, new HashSet<(uint CallerRva, int LocalSlot)>(localVisited)));
                    }
                    if (reaching.Length == 0)
                        result.AddRange(RecoverLiveInRegisterFromDirectCallers(pe, oem, function[0].Rva, callerRva,
                            sourceOffset, localSlot, sourceRegister, 2, []));
                }
            }
        }
        if (result.Count == 0)
        {
            result.Add(new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", callerRva, BlockLabel(cfg, callerBlock),
                "caller_ebp_local_definition", $"{StackSlot(localSlot)} -> source[{sourceOffset}]", sourceOffset,
                "no bounded direct local write", [],
                [$"{oem} @0x{callerRva:X8}: no direct write to {StackSlot(localSlot)} was found in the bounded caller CFG predecessor slice {FunctionRange(bounds)}."]));
        }
        return result.OrderBy(x => x.Rva ?? uint.MaxValue).Take(128).ToArray();
    }

    private static OemPayloadSourceObjectProvenance[] RecoverLiveInRegisterFromDirectCallers(
        NdevicePe pe, string oem, uint calleeEntryRva, uint sinkCallRva, int sourceOffset, int localSlot,
        Register register, int depth, HashSet<(uint EntryRva, Register Register)> visited)
    {
        var expression = $"{register} -> {StackSlot(localSlot)} -> source[{sourceOffset}]";
        if (depth <= 0)
            return [new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", calleeEntryRva, "interprocedural depth limit",
                "live_in_register_value", expression, sourceOffset, "bounded direct-caller recursion depth limit", [],
                [$"{oem} @0x{calleeEntryRva:X8}: {register} remains a live-in value after the bounded direct-caller recursion limit."])];
        if (!visited.Add((calleeEntryRva, register)))
            return [new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", calleeEntryRva, "interprocedural cycle",
                "live_in_register_value", expression, sourceOffset, "revisited direct-caller function/register pair", [],
                [$"{oem} @0x{calleeEntryRva:X8}: bounded direct-caller tracing revisited live-in {register}; recursion stopped without inferring a value."])];

        var directCallers = FindDirectPayloadCallRvas(pe, calleeEntryRva);
        if (directCallers.Length == 0)
        {
            var pointerReferences = FindPayloadFunctionPointerDataReferences(pe, calleeEntryRva);
            if (pointerReferences.Length > 0)
                return pointerReferences.Select(reference => new OemPayloadSourceObjectProvenance(oem, "INFERRED", reference.Rva,
                    reference.Section, "function_pointer_data_reference", expression, sourceOffset,
                    $"image data stores VA 0x{pe.ImageBase + calleeEntryRva:X8} for entry 0x{calleeEntryRva:X8}",
                    [$"{oem} @0x{reference.Rva:X8}: {reference.Section} contains absolute pointer to function entry 0x{calleeEntryRva:X8}.",
                     $"{oem} @0x{sinkCallRva:X8}: callee sends source[{sourceOffset}] through the image-global buffer to the proven memcpy path."],
                    ["A data/vtable-like function pointer proves a static reference but not the dispatch object's identity, invocation branch, or live-in register value."])).ToArray();
            return [new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", calleeEntryRva, "direct caller unresolved",
                "live_in_register_value", expression, sourceOffset, "no direct E8/E9 caller", [],
                [$"{oem} @0x{calleeEntryRva:X8}: live-in {register} has no direct .text E8/E9 caller xref or image-data pointer reference; remaining producer may be indirect dispatch, a callback, or an external entry edge."])];
        }

        var result = new List<OemPayloadSourceObjectProvenance>();
        foreach (var directCallRva in directCallers)
        {
            var callerEntry = FindPayloadPrologueRva(pe, directCallRva);
            if (callerEntry is null)
            {
                result.Add(new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", directCallRva, "direct caller entry unresolved",
                    "live_in_register_value", expression, sourceOffset, "no validated EBP-frame caller entry", [],
                    [$"{oem} @0x{directCallRva:X8}: direct call to 0x{calleeEntryRva:X8} was found, but its EBP-frame caller boundary could not be validated."]));
                continue;
            }
            var decoded = DecodePayloadFunction(pe, callerEntry.Value, directCallRva, 0x10000u);
            var directCallIndex = decoded.FindIndex(x => x.Rva == directCallRva);
            if (directCallIndex < 0)
            {
                result.Add(new OemPayloadSourceObjectProvenance(oem, "UNRESOLVED", directCallRva, "direct caller decode unresolved",
                    "live_in_register_value", expression, sourceOffset, $"entry=0x{callerEntry.Value:X8} did not decode to direct call", [],
                    [$"{oem} @0x{callerEntry.Value:X8}: bounded decode did not reach direct call @0x{directCallRva:X8}."]));
                continue;
            }
            var bounds = FindPayloadFunctionBounds(decoded, directCallIndex);
            var function = decoded.Skip(bounds.StartIndex).Take(bounds.EndIndex - bounds.StartIndex + 1).ToArray();
            var cfg = BuildPayloadCfg(function);
            var origin = TracePayloadRegisterOrigin(cfg, directCallIndex - bounds.StartIndex, register, 4, []);
            var status = origin.Rva is null ? "UNRESOLVED" : origin.Status == "PROVEN" && origin.UnresolvedEdges.Length == 0 ? "PROVEN" : "INFERRED";
            var evidence = new List<string>
            {
                $"{oem} @0x{directCallRva:X8}: direct call transfers live-in {register} to callee entry 0x{calleeEntryRva:X8}.",
                $"{oem} @0x{sinkCallRva:X8}: callee sends source[{sourceOffset}] through the image-global buffer to the proven memcpy path."
            };
            evidence.AddRange(origin.Evidence.Select(x => x.StartsWith("@", StringComparison.Ordinal) ? $"{oem} {x}" : x));
            result.Add(new OemPayloadSourceObjectProvenance(oem, status, origin.Rva ?? directCallRva, FunctionRange(bounds),
                "live_in_register_caller_value", expression, sourceOffset, origin.Expression, evidence.ToArray(), origin.UnresolvedEdges));
            if (origin.Rva is null)
                result.AddRange(RecoverLiveInRegisterFromDirectCallers(pe, oem, function[0].Rva, directCallRva,
                    sourceOffset, localSlot, register, depth - 1, new HashSet<(uint EntryRva, Register Register)>(visited)));
        }
        return result.OrderBy(x => x.Rva ?? uint.MaxValue).Take(128).ToArray();
    }

    private static uint[] FindDirectPayloadCallRvas(NdevicePe pe, uint targetRva)
    {
        var text = pe.Sections.FirstOrDefault(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        if (text is null) return [];
        var rawStart = checked((int)text.RawPointer);
        var rawEnd = Math.Min(pe.Bytes.Length, checked((int)(text.RawPointer + text.RawSize)));
        var callers = new List<uint>();
        for (var offset = rawStart; offset <= rawEnd - 5; offset++)
        {
            var opcode = pe.Bytes[offset];
            if (opcode is not (0xE8 or 0xE9)) continue;
            var rva = text.VirtualAddress + checked((uint)(offset - rawStart));
            var relative = BitConverter.ToInt32(pe.Bytes, offset + 1);
            if (unchecked((uint)((long)rva + 5 + relative)) == targetRva) callers.Add(rva);
        }
        return callers.Distinct().OrderBy(x => x).Take(256).ToArray();
    }

    private static (uint Rva, string Section)[] FindPayloadFunctionPointerDataReferences(NdevicePe pe, uint entryRva)
    {
        var functionVa = pe.ImageBase + entryRva;
        if (functionVa > uint.MaxValue) return [];
        var references = new List<(uint Rva, string Section)>();
        foreach (var section in pe.Sections.Where(x => !x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase)))
        {
            var rawStart = checked((int)section.RawPointer);
            var rawEnd = Math.Min(pe.Bytes.Length, checked((int)(section.RawPointer + section.RawSize)));
            for (var offset = rawStart; offset <= rawEnd - 4; offset++)
            {
                if (BitConverter.ToUInt32(pe.Bytes, offset) != (uint)functionVa) continue;
                references.Add((section.VirtualAddress + checked((uint)(offset - rawStart)), section.Name));
            }
        }
        return references.Distinct().OrderBy(x => x.Rva).Take(128).ToArray();
    }

    private static bool TryEmbeddedEbpLocalSlot(string expression, out int slot)
    {
        slot = 0;
        const string prefix = "[EBP-0x";
        var start = expression.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return false;
        var valueStart = start + prefix.Length;
        var end = expression.IndexOf(']', valueStart);
        if (end < valueStart) return false;
        try
        {
            slot = -Convert.ToInt32(expression[valueStart..end], 16);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryEbpLocalSlot(string expression, out int slot)
    {
        slot = 0;
        const string prefix = "[EBP-0x";
        if (!expression.StartsWith(prefix, StringComparison.Ordinal) || !expression.EndsWith(']')) return false;
        try
        {
            slot = -Convert.ToInt32(expression[prefix.Length..^1], 16);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }


    private static (uint Start, uint End, int StartIndex, int EndIndex) FindPayloadFunctionBounds(IReadOnlyList<NdeviceDecoded> text, int index)
    {
        var start = Math.Max(0, index - 24000);
        for (var i = index; i >= start; i--)
        {
            if (PayloadLooksFunctionPrologue(text, i)) { start = i; break; }
            if (i < index && text[i].Instruction.Mnemonic == Mnemonic.Ret) { start = i + 1; break; }
        }
        while (start < index && text[start].Instruction.Mnemonic == Mnemonic.Int3) start++;
        var end = Math.Min(text.Count - 1, index + 24000);
        for (var i = index; i <= end; i++)
        {
            if (text[i].Instruction.Mnemonic == Mnemonic.Ret) { end = i; break; }
        }
        return (text[start].Rva, text[end].Rva, start, end);
    }

    private static PayloadDirectCaller[] FindDirectPayloadCallers(NdevicePe pe, uint targetRva)
    {
        var text = pe.Sections.FirstOrDefault(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        if (text is null) return [];
        var rawStart = checked((int)text.RawPointer);
        var rawEnd = Math.Min(pe.Bytes.Length, checked((int)(text.RawPointer + text.RawSize)));
        var callers = new List<PayloadDirectCaller>();
        for (var offset = rawStart; offset <= rawEnd - 5; offset++)
        {
            var opcode = pe.Bytes[offset];
            if (opcode is not (0xE8 or 0xE9)) continue;
            var rva = text.VirtualAddress + checked((uint)(offset - rawStart));
            var relative = BitConverter.ToInt32(pe.Bytes, offset + 1);
            var destination = unchecked((uint)((long)rva + 5 + relative));
            if (destination != targetRva) continue;
            var setup = FindPreCallStackArguments(pe, rva);
            callers.Add(new PayloadDirectCaller(rva, opcode == 0xE8 ? "call" : "jmp", setup.Arguments, setup.Context, setup.Unique,
                new[] { $"@0x{rva:X8}: direct {(opcode == 0xE8 ? "E8 call" : "E9 jump")} xref targets entry 0x{targetRva:X8}." }.Concat(setup.Context.Select(x => $"@0x{x.Rva:X8}: {x.Text}")).ToArray(), setup.UnresolvedEdges));
        }
        return callers.OrderBy(x => x.Rva).Take(256).ToArray();
    }

    private static (NdeviceDecoded[] Arguments, bool Unique, NdeviceDecoded[] Context, string[] UnresolvedEdges) FindPreCallStackArguments(NdevicePe pe, uint callRva)
    {
        var candidates = new Dictionary<string, List<NdeviceDecoded>>(StringComparer.Ordinal);
        var lowerBound = Math.Max(pe.TextStart, callRva > 96 ? callRva - 96 : pe.TextStart);
        for (var start = lowerBound; start < callRva; start++)
        {
            var decoded = DecodeRange(pe, start, callRva);
            if (decoded.Count == 0) continue;
            var last = decoded[^1];
            if (last.Rva + last.Instruction.Length != callRva) continue;
            var trailingPushes = new List<NdeviceDecoded>();
            for (var i = decoded.Count - 1; i >= 0 && decoded[i].Instruction.Mnemonic == Mnemonic.Push; i--) trailingPushes.Add(decoded[i]);
            if (trailingPushes.Count < 2) continue;
            trailingPushes.Reverse();
            var arguments = trailingPushes.TakeLast(2).ToArray();
            var key = string.Join("|", arguments.Select(x => $"{x.Rva:X8}:{x.Text}"));
            if (!candidates.TryGetValue(key, out var existing) || decoded.Count > existing.Count) candidates[key] = decoded;
        }
        if (candidates.Count == 0)
        {
            return ([], false, [], [$"@0x{callRva:X8}: no bounded contiguous two-PUSH setup was decoded immediately before the direct entry xref."]);
        }
        var selectedWindow = candidates.OrderBy(x => x.Key, StringComparer.Ordinal).First().Value;
        var selectedPushes = selectedWindow.TakeLast(16).Reverse().TakeWhile(x => x.Instruction.Mnemonic == Mnemonic.Push).Reverse().TakeLast(2).ToArray();
        var context = selectedWindow.TakeLast(16).ToArray();
        return (selectedPushes, candidates.Count == 1, context,
            candidates.Count == 1 ? [] : [$"@0x{callRva:X8}: {candidates.Count} instruction-boundary candidates produce different bounded pre-call PUSH sequences; setup is retained as inferred."]);
    }


    private static uint? FindPayloadPrologueRva(NdevicePe pe, uint copyCallRva)
    {
        var text = pe.Sections.FirstOrDefault(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        if (text is null) return null;
        var copyOffset = pe.RvaToOffset(copyCallRva);
        var rawStart = checked((int)text.RawPointer);
        var lowerBound = Math.Max(rawStart, copyOffset - 0x20000);
        for (var offset = copyOffset - 3; offset >= lowerBound; offset--)
        {
            if (pe.Bytes[offset] != 0x55 || pe.Bytes[offset + 1] != 0x8B || pe.Bytes[offset + 2] != 0xEC) continue;
            var candidate = text.VirtualAddress + checked((uint)(offset - rawStart));
            var first = DecodeOneRawInstruction(pe, candidate);
            if (first is null || first.Instruction.Mnemonic != Mnemonic.Push || first.Instruction.Op0Kind != OpKind.Register || Normalize(first.Instruction.Op0Register) != Register.EBP) continue;
            var second = DecodeOneRawInstruction(pe, candidate + checked((uint)first.Instruction.Length));
            if (second is null || second.Instruction.Mnemonic != Mnemonic.Mov || second.Instruction.Op0Kind != OpKind.Register || Normalize(second.Instruction.Op0Register) != Register.EBP || second.Instruction.Op1Kind != OpKind.Register || Normalize(second.Instruction.Op1Register) != Register.ESP) continue;
            return candidate;
        }
        return null;
    }

    private static List<NdeviceDecoded> DecodePayloadFunction(NdevicePe pe, uint entryRva, uint copyCallRva, uint byteLimit)
    {
        var endRva = Math.Min(pe.TextEnd, entryRva + byteLimit);
        var start = pe.RvaToOffset(entryRva);
        var end = pe.RvaToOffset(endRva - 1) + 1;
        var decoder = Decoder.Create(pe.Pe32Plus ? 64 : 32, new ByteArrayCodeReader(pe.Bytes.AsSpan(start, end - start).ToArray()));
        decoder.IP = entryRva;
        var formatter = new IntelFormatter();
        var output = new NdeviceFormatterOutput();
        var result = new List<NdeviceDecoded>();
        var sawCopyCall = false;
        while (decoder.IP < endRva && result.Count < 12000)
        {
            decoder.Decode(out var instruction);
            if (instruction.Code == Code.INVALID || instruction.Length == 0) break;
            var rva = checked((uint)instruction.IP);
            formatter.Format(in instruction, output);
            result.Add(new NdeviceDecoded(rva, output.Take(), instruction));
            if (rva == copyCallRva) sawCopyCall = true;
            if (sawCopyCall && instruction.Mnemonic == Mnemonic.Ret) break;
        }
        return result;
    }

    private static bool PayloadLooksFunctionPrologue(IReadOnlyList<NdeviceDecoded> text, int index)
    {
        if (index < 0 || index + 1 >= text.Count) return false;
        var first = text[index].Instruction;
        var second = text[index + 1].Instruction;
        return first.Mnemonic == Mnemonic.Push && first.Op0Kind == OpKind.Register && Normalize(first.Op0Register) == Register.EBP &&
               second.Mnemonic == Mnemonic.Mov && second.Op0Kind == OpKind.Register && Normalize(second.Op0Register) == Register.EBP &&
               second.Op1Kind == OpKind.Register && Normalize(second.Op1Register) == Register.ESP;
    }

    private static PayloadCfg BuildPayloadCfg(IReadOnlyList<NdeviceDecoded> function)
    {
        var byRva = function.Select((item, index) => (item.Rva, index)).ToDictionary(x => x.Rva, x => x.index);
        var starts = new HashSet<int> { 0 };
        for (var i = 0; i < function.Count; i++)
        {
            var instruction = function[i].Instruction;
            if (TryPayloadBranchTarget(instruction, out var target) && byRva.TryGetValue(target, out var targetIndex)) starts.Add(targetIndex);
            if (i + 1 < function.Count && instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.Return or FlowControl.Call)
                starts.Add(i + 1);
        }
        var sorted = starts.OrderBy(x => x).ToArray();
        var blocks = new List<PayloadCfgBlock>();
        var toBlock = new int[function.Count];
        for (var i = 0; i < sorted.Length; i++)
        {
            var start = sorted[i];
            var end = i + 1 < sorted.Length ? sorted[i + 1] - 1 : function.Count - 1;
            var block = new PayloadCfgBlock(i, start, end, [], []);
            blocks.Add(block);
            for (var j = start; j <= end; j++) toBlock[j] = i;
        }
        foreach (var block in blocks)
        {
            var instruction = function[block.EndIndex].Instruction;
            if (TryPayloadBranchTarget(instruction, out var target) && byRva.TryGetValue(target, out var targetIndex)) AddPayloadEdge(blocks, block.Id, toBlock[targetIndex]);
            if (instruction.FlowControl is not (FlowControl.UnconditionalBranch or FlowControl.Return) && block.EndIndex + 1 < function.Count)
                AddPayloadEdge(blocks, block.Id, toBlock[block.EndIndex + 1]);
        }
        return new PayloadCfg(function, blocks, toBlock);
    }

    private static void AddPayloadEdge(IReadOnlyList<PayloadCfgBlock> blocks, int from, int to)
    {
        if (!blocks[from].Successors.Contains(to)) blocks[from].Successors.Add(to);
        if (!blocks[to].Predecessors.Contains(from)) blocks[to].Predecessors.Add(from);
    }

    private static bool Dominates(PayloadCfg cfg, int candidate, int target)
    {
        if (candidate == target || candidate == 0) return true;
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0 && seen.Count < 4096)
        {
            var current = queue.Dequeue();
            if (current == candidate || !seen.Add(current)) continue;
            if (current == target) return false;
            foreach (var successor in cfg.Blocks[current].Successors) queue.Enqueue(successor);
        }
        return true;
    }

    private static HashSet<int> BackwardReachableBlocks(PayloadCfg cfg, int start)
    {
        var result = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0 && result.Count < 4096)
        {
            var current = queue.Dequeue();
            if (!result.Add(current)) continue;
            foreach (var predecessor in cfg.Blocks[current].Predecessors) queue.Enqueue(predecessor);
        }
        return result;
    }

    private static bool TryPayloadBranchTarget(Instruction instruction, out uint target)
    {
        target = 0;
        if (instruction.FlowControl is not (FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)) return false;
        if (instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)) return false;
        target = checked((uint)instruction.NearBranchTarget);
        return true;
    }

    private static bool IsStackMemoryOperand(Instruction instruction, int operand, int slot) =>
        operand < instruction.OpCount && instruction.GetOpKind(operand) == OpKind.Memory && TryStackSlot(instruction, out var candidate) && candidate == slot;

    private static bool IsStackWrite(Instruction instruction) => instruction.Op0Kind == OpKind.Memory && instruction.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Push);

    private static NdeviceDecoded[] FindPayloadReachingRegisterDefinitions(PayloadCfg cfg, int beforeIndex, Register register)
    {
        var sourceBlock = cfg.InstructionToBlock[Math.Clamp(beforeIndex, 0, cfg.Instructions.Count - 1)];
        var queue = new Queue<(int Block, int Before)>();
        var seen = new HashSet<(int Block, Register Register)>();
        queue.Enqueue((sourceBlock, beforeIndex - 1));
        var definitions = new List<NdeviceDecoded>();
        while (queue.Count > 0 && seen.Count < 512)
        {
            var (blockId, before) = queue.Dequeue();
            if (!seen.Add((blockId, register))) continue;
            var block = cfg.Blocks[blockId];
            var limit = Math.Min(before, block.EndIndex);
            var found = false;
            for (var i = limit; i >= block.StartIndex; i--)
            {
                var candidate = cfg.Instructions[i].Instruction;
                if (!WritesRegister(candidate) || candidate.Op0Kind != OpKind.Register || Normalize(candidate.Op0Register) != register) continue;
                definitions.Add(cfg.Instructions[i]);
                found = true;
                break;
            }
            if (!found)
                foreach (var predecessor in block.Predecessors)
                    queue.Enqueue((predecessor, cfg.Blocks[predecessor].EndIndex));
        }
        return definitions.GroupBy(x => x.Rva).Select(x => x.First()).OrderBy(x => x.Rva).Take(128).ToArray();
    }

    private static PayloadNearestDefinition NearestDefinitionForStore(PayloadCfg cfg, int storeIndex, Instruction store)
    {
        if (store.Op1Kind != OpKind.Register) return new PayloadNearestDefinition(null, null, false, []);
        var register = Normalize(store.Op1Register);
        var sourceBlock = cfg.InstructionToBlock[storeIndex];
        var queue = new Queue<(int Block, int Before)>();
        var seen = new HashSet<(int, Register)>();
        queue.Enqueue((sourceBlock, storeIndex - 1));
        var definitions = new List<NdeviceDecoded>();
        while (queue.Count > 0 && seen.Count < 512)
        {
            var (blockId, before) = queue.Dequeue();
            if (!seen.Add((blockId, register))) continue;
            var block = cfg.Blocks[blockId];
            var limit = Math.Min(before, block.EndIndex);
            var found = false;
            for (var i = limit; i >= block.StartIndex; i--)
            {
                var candidate = cfg.Instructions[i].Instruction;
                if (!WritesRegister(candidate) || candidate.Op0Kind != OpKind.Register || Normalize(candidate.Op0Register) != register) continue;
                definitions.Add(cfg.Instructions[i]);
                found = true;
                break;
            }
            if (!found) foreach (var predecessor in block.Predecessors) queue.Enqueue((predecessor, cfg.Blocks[predecessor].EndIndex));
        }
        var primary = definitions.OrderBy(x => x.Rva).FirstOrDefault();
        var phi = definitions.Select(x => x.Rva).Distinct().Take(2).Count() > 1;
        return primary is null
            ? new PayloadNearestDefinition(null, null, false, ["No bounded predecessor definition was found for the source register."])
            : new PayloadNearestDefinition(primary.Rva, primary.Text, phi,
                phi ? ["Multiple predecessor definitions reach the source register; retained as a phi-like static merge."] : []);
    }

    private static PayloadRegisterOrigin TracePayloadRegisterOrigin(
        PayloadCfg cfg,
        int beforeIndex,
        Register register,
        int depth,
        HashSet<(uint Rva, Register Register)> visited)
    {
        if (register == Register.ESP)
        {
            return new PayloadRegisterOrigin("PROVEN", null, Register.ESP.ToString(), "architectural stack pointer",
                ["ESP is the architectural stack-pointer base within the decoded function."], []);
        }
        if (depth <= 0)
        {
            return new PayloadRegisterOrigin("UNRESOLVED", null, register.ToString(), "depth limit", [],
                ["Bounded register-origin recursion reached its depth limit."]);
        }
        var nearest = NearestDefinitionForRegister(cfg, beforeIndex, register);
        if (nearest.Rva is null)
        {
            return new PayloadRegisterOrigin("UNRESOLVED", null, register.ToString(), "no definition", [], nearest.UnresolvedEdges);
        }
        if (!visited.Add((nearest.Rva.Value, register)))
        {
            return new PayloadRegisterOrigin("UNRESOLVED", nearest.Rva, register.ToString(), "visited cycle", [],
                ["Bounded register-origin recursion encountered a previously visited definition."]);
        }
        var item = cfg.Instructions.FirstOrDefault(x => x.Rva == nearest.Rva.Value);
        if (item is null)
        {
            return new PayloadRegisterOrigin("UNRESOLVED", nearest.Rva, register.ToString(), "definition outside CFG", [],
                ["Recovered definition RVA is outside the bounded CFG instruction map."]);
        }
        var instruction = item.Instruction;
        var evidence = new List<string> { $"@0x{item.Rva:X8}: {item.Text} defines {register}." };
        if (instruction.Mnemonic == Mnemonic.Mov && TryInstructionImmediate(instruction, 1, out var immediate))
        {
            return new PayloadRegisterOrigin("PROVEN", item.Rva, $"0x{immediate:X}", item.Text, evidence.ToArray(), nearest.UnresolvedEdges);
        }
        if (instruction.Mnemonic == Mnemonic.Xor && instruction.Op1Kind == OpKind.Register && Normalize(instruction.Op0Register) == Normalize(instruction.Op1Register))
        {
            return new PayloadRegisterOrigin("PROVEN", item.Rva, "0x0", item.Text, evidence.ToArray(), nearest.UnresolvedEdges);
        }
        if (instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub && TryInstructionImmediate(instruction, 1, out var delta))
        {
            var parent = TracePayloadRegisterOrigin(cfg, FindInstructionIndex(cfg.Instructions, item.Rva), register, depth - 1, visited);
            evidence.AddRange(parent.Evidence);
            var operation = instruction.Mnemonic == Mnemonic.Add ? "+" : "-";
            return new PayloadRegisterOrigin(parent.Status, item.Rva, $"({parent.Expression} {operation} 0x{delta:X})", item.Text,
                evidence.ToArray(), parent.UnresolvedEdges);
        }
        if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op1Kind == OpKind.Register)
        {
            var sourceRegister = Normalize(instruction.Op1Register);
            var parent = TracePayloadRegisterOrigin(cfg, FindInstructionIndex(cfg.Instructions, item.Rva), sourceRegister, depth - 1, visited);
            evidence.AddRange(parent.Evidence);
            return new PayloadRegisterOrigin(parent.Status, item.Rva, parent.Expression, item.Text, evidence.ToArray(), parent.UnresolvedEdges);
        }
        if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op1Kind == OpKind.Memory)
        {
            if (TryStackSlot(instruction, out var stackSlot))
            {
                return new PayloadRegisterOrigin("PROVEN", item.Rva, StackSlot(stackSlot), item.Text, evidence.ToArray(), nearest.UnresolvedEdges);
            }
            if (Normalize(instruction.MemoryBase) == Register.None)
            {
                return new PayloadRegisterOrigin("INFERRED", item.Rva, FormatPayloadMemory(instruction), item.Text, evidence.ToArray(),
                    ["Absolute/global memory origin requires producer attribution outside the local function."]);
            }
            var baseRegister = Normalize(instruction.MemoryBase);
            var parent = TracePayloadRegisterOrigin(cfg, FindInstructionIndex(cfg.Instructions, item.Rva), baseRegister, depth - 1, visited);
            evidence.AddRange(parent.Evidence);
            return new PayloadRegisterOrigin("INFERRED", item.Rva, $"{FormatPayloadMemory(instruction)}; base={parent.Expression}", item.Text, evidence.ToArray(), parent.UnresolvedEdges);
        }
        if (instruction.Mnemonic == Mnemonic.Lea && instruction.Op1Kind == OpKind.Memory)
        {
            var baseRegister = Normalize(instruction.MemoryBase);
            if (baseRegister is Register.None or Register.EBP or Register.ESP)
                return new PayloadRegisterOrigin("PROVEN", item.Rva, FormatPayloadMemory(instruction), item.Text, evidence.ToArray(), nearest.UnresolvedEdges);
            var parent = TracePayloadRegisterOrigin(cfg, FindInstructionIndex(cfg.Instructions, item.Rva), baseRegister, depth - 1, visited);
            evidence.AddRange(parent.Evidence);
            var displacement = SignedDisp(instruction);
            var operation = displacement < 0 ? "-" : "+";
            return new PayloadRegisterOrigin(parent.Status, item.Rva, $"({parent.Expression} {operation} 0x{Math.Abs(displacement):X})", item.Text,
                evidence.ToArray(), parent.UnresolvedEdges);
        }
        return new PayloadRegisterOrigin("UNRESOLVED", item.Rva, register.ToString(), item.Text, evidence.ToArray(),
            ["Nearest register definition is not a bounded mov/lea provenance form."]);
    }

    private static PayloadNearestDefinition NearestDefinitionForRegister(PayloadCfg cfg, int beforeIndex, Register register)
    {
        var sourceBlock = cfg.InstructionToBlock[Math.Clamp(beforeIndex, 0, cfg.Instructions.Count - 1)];
        var queue = new Queue<(int Block, int Before)>();
        var seen = new HashSet<(int Block, Register Register)>();
        queue.Enqueue((sourceBlock, beforeIndex - 1));
        var definitions = new List<NdeviceDecoded>();
        while (queue.Count > 0 && seen.Count < 512)
        {
            var (blockId, before) = queue.Dequeue();
            if (!seen.Add((blockId, register))) continue;
            var block = cfg.Blocks[blockId];
            var limit = Math.Min(before, block.EndIndex);
            var found = false;
            for (var i = limit; i >= block.StartIndex; i--)
            {
                var candidate = cfg.Instructions[i].Instruction;
                if (!WritesRegister(candidate) || candidate.Op0Kind != OpKind.Register || Normalize(candidate.Op0Register) != register) continue;
                definitions.Add(cfg.Instructions[i]);
                found = true;
                break;
            }
            if (!found) foreach (var predecessor in block.Predecessors) queue.Enqueue((predecessor, cfg.Blocks[predecessor].EndIndex));
        }
        var primary = definitions.OrderBy(x => x.Rva).FirstOrDefault();
        var phi = definitions.Select(x => x.Rva).Distinct().Take(2).Count() > 1;
        return primary is null
            ? new PayloadNearestDefinition(null, null, false, ["No bounded predecessor definition was found for the register."])
            : new PayloadNearestDefinition(primary.Rva, primary.Text, phi,
                phi ? ["Multiple predecessor definitions reach this register; retained as a phi-like static merge."] : []);
    }

    private static string DescribeSourceExpression(Instruction instruction)
    {
        if (TryInstructionImmediate(instruction, 1, out var immediate)) return $"0x{immediate:X}";
        if (instruction.Op1Kind == OpKind.Register) return Normalize(instruction.Op1Register).ToString();
        if (instruction.Op1Kind == OpKind.Memory) return FormatPayloadMemory(instruction);
        return instruction.ToString();
    }

    private static bool TryObjectMemberOperand(Instruction instruction, int operand, out (string Expression, int Offset) member)
    {
        member = default;
        if (operand >= instruction.OpCount || instruction.GetOpKind(operand) != OpKind.Memory) return false;
        if (Normalize(instruction.MemoryBase) is Register.None or Register.EBP or Register.ESP) return false;
        member = (FormatPayloadMemory(instruction), checked((int)SignedDisp(instruction)));
        return true;
    }

    private static string FormatPayloadMemory(Instruction instruction)
    {
        var baseRegister = Normalize(instruction.MemoryBase);
        var displacement = SignedDisp(instruction);
        if (baseRegister == Register.None) return $"[0x{instruction.MemoryDisplacement64:X}]";
        return displacement == 0 ? $"[{baseRegister}]" : displacement < 0 ? $"[{baseRegister}-0x{-displacement:X}]" : $"[{baseRegister}+0x{displacement:X}]";
    }

    private static PayloadOutParameterResult? FindOutParameterCall(NdevicePe pe, PayloadCfg cfg, int leaIndex, Register addressRegister)
    {
        var block = cfg.Blocks[cfg.InstructionToBlock[leaIndex]];
        var sawPush = false;
        for (var i = leaIndex + 1; i <= Math.Min(block.EndIndex, leaIndex + 20); i++)
        {
            var instruction = cfg.Instructions[i].Instruction;
            if (instruction.Mnemonic == Mnemonic.Push && instruction.Op0Kind == OpKind.Register && Normalize(instruction.Op0Register) == addressRegister) sawPush = true;
            if (instruction.Mnemonic != Mnemonic.Call || !sawPush || instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)) continue;
            var target = checked((uint)instruction.NearBranchTarget);
            return AnalyzeOutParameterHelper(pe, cfg.Instructions[i], target);
        }
        return null;
    }

    private static PayloadOutParameterResult AnalyzeOutParameterHelper(NdevicePe pe, NdeviceDecoded call, uint target)
    {
        var body = DecodeRange(pe, target, Math.Min(pe.TextEnd, target + 0x1000u));
        var argumentRegisters = new HashSet<Register>();
        foreach (var item in body.Take(1200))
        {
            var instruction = item.Instruction;
            if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Memory && IsFirstStackArgument(instruction))
                argumentRegisters.Add(Normalize(instruction.Op0Register));
            if (instruction.Op0Kind == OpKind.Memory && argumentRegisters.Contains(Normalize(instruction.MemoryBase)) && IsStackWrite(instruction))
            {
                return new PayloadOutParameterResult(
                    call.Rva, target, true, item.Rva, item.Text,
                    [$"@0x{call.Rva:X8}: caller passes the local address to helper 0x{target:X8}.", $"@0x{item.Rva:X8}: helper writes through its recovered first stack-argument register {Normalize(instruction.MemoryBase)}."],
                    ["Helper write through the out-parameter is proven, but the written pointer/value provenance needs recursive object/member decoding."]);
            }
        }
        return new PayloadOutParameterResult(
            call.Rva, target, false, null, null,
            [$"@0x{call.Rva:X8}: caller passes a stack-address alias to helper 0x{target:X8}."],
            [$"Helper 0x{target:X8}: no bounded write through the recovered first stack argument was decoded; it may use thiscall/register arguments or dispatch indirectly."]);
    }

    private static bool IsFirstStackArgument(Instruction instruction)
    {
        if (Normalize(instruction.MemoryBase) != Register.EBP) return false;
        return SignedDisp(instruction) == 8;
    }

    private static PayloadDescriptorMemberWrite[] RecoverDescriptorMemberWrites(
        string oem,
        PayloadCfg cfg,
        HashSet<int> relevantBlocks,
        Register baseRegister,
        int pointerOffset,
        int copyBlock,
        int copyIndex)
    {
        var result = new List<PayloadDescriptorMemberWrite>();
        foreach (var block in cfg.Blocks.Where(x => relevantBlocks.Contains(x.Id)))
        {
            for (var i = block.StartIndex; i <= Math.Min(block.EndIndex, copyIndex - 1); i++)
            {
                var item = cfg.Instructions[i];
                var instruction = item.Instruction;
                if (instruction.Op0Kind != OpKind.Memory || !IsStackWrite(instruction) || Normalize(instruction.MemoryBase) != baseRegister) continue;
                var memberOffset = checked((int)SignedDisp(instruction));
                if (memberOffset != pointerOffset && memberOffset != pointerOffset + 4) continue;
                PayloadRegisterOrigin? valueOrigin = null;
                if (instruction.Op1Kind == OpKind.Register)
                {
                    valueOrigin = TracePayloadRegisterOrigin(cfg, i, Normalize(instruction.Op1Register), 5, []);
                }
                var dominatesCopy = Dominates(cfg, block.Id, copyBlock) && (block.Id != copyBlock || i < copyIndex);
                var evidence = new List<string>
                {
                    $"{oem} @0x{item.Rva:X8}: writes descriptor member [{baseRegister}+0x{memberOffset:X}] before the source-pointer copy.",
                    dominatesCopy
                        ? $"{oem} CFG dominance: {BlockLabel(cfg, block.Id)} dominates copy block {BlockLabel(cfg, copyBlock)}."
                        : $"{oem} CFG: descriptor write block {BlockLabel(cfg, block.Id)} is predecessor-reachable but does not dominate copy block {BlockLabel(cfg, copyBlock)}."
                };
                var unresolved = dominatesCopy
                    ? Array.Empty<string>()
                    : [$"Descriptor member write @0x{item.Rva:X8} is conditional/phi-like and cannot be selected as the unique reaching value."];
                result.Add(new PayloadDescriptorMemberWrite(
                    dominatesCopy ? "PROVEN" : "INFERRED", item.Rva, block.Id, memberOffset, item.Text,
                    DescribeSourceExpression(instruction), valueOrigin, evidence.ToArray(), unresolved));
            }
        }
        return result.OrderBy(x => x.Rva).Take(64).ToArray();
    }

    private static PayloadDescriptorMemberWrite[] RecoverStackSnapshotMemberWrites(
        string oem,
        PayloadCfg cfg,
        uint snapshotRva,
        int pointerOffset,
        int copyBlock,
        int copyIndex)
    {
        var snapshotIndex = FindInstructionIndex(cfg.Instructions, snapshotRva);
        var blockId = cfg.InstructionToBlock[snapshotIndex];
        var block = cfg.Blocks[blockId];
        var stackOffset = 0;
        var result = new List<PayloadDescriptorMemberWrite>();
        for (var i = snapshotIndex - 1; i >= block.StartIndex; i--)
        {
            var item = cfg.Instructions[i];
            var instruction = item.Instruction;
            if (instruction.Mnemonic == Mnemonic.Push)
            {
                if (stackOffset == pointerOffset || stackOffset == pointerOffset + 4)
                {
                    var value = DescribePushValue(instruction);
                    var evidence = new[]
                    {
                        $"{oem} @0x{item.Rva:X8}: implicit PUSH store becomes [ESP+0x{stackOffset:X}] at stack snapshot 0x{snapshotRva:X8}.",
                        $"{oem} @0x{snapshotRva:X8}: stack snapshot establishes descriptor member [EBX+0x{stackOffset:X}]."
                    };
                    result.Add(new PayloadDescriptorMemberWrite(
                        "PROVEN", item.Rva, blockId, stackOffset, item.Text, value, null, evidence, []));
                }
                stackOffset += 4;
                continue;
            }
            if (instruction.Mnemonic == Mnemonic.Sub && instruction.Op0Kind == OpKind.Register && Normalize(instruction.Op0Register) == Register.ESP && TryInstructionImmediate(instruction, 1, out var subtract))
            {
                stackOffset += checked((int)subtract);
                continue;
            }
            if (instruction.Mnemonic == Mnemonic.Add && instruction.Op0Kind == OpKind.Register && Normalize(instruction.Op0Register) == Register.ESP && TryInstructionImmediate(instruction, 1, out var add))
            {
                stackOffset -= checked((int)add);
                continue;
            }
            if (instruction.Mnemonic == Mnemonic.Pop)
            {
                stackOffset -= 4;
                continue;
            }
            if (WritesRegister(instruction) && instruction.Op0Kind == OpKind.Register && Normalize(instruction.Op0Register) == Register.ESP) break;
        }
        return result.OrderBy(x => x.MemberOffset).Take(2).ToArray();
    }

    private static string DescribePushValue(Instruction instruction)
    {
        if (TryInstructionImmediate(instruction, 0, out var immediate)) return $"0x{immediate:X}";
        if (instruction.Op0Kind == OpKind.Register) return Normalize(instruction.Op0Register).ToString();
        if (instruction.Op0Kind == OpKind.Memory) return FormatPayloadMemory(instruction);
        return instruction.ToString();
    }


    private static OemPayloadSourceBufferWrite[] RecoverStackBufferWrites(
        string oem,
        PayloadCfg cfg,
        HashSet<int> relevantBlocks,
        int sourceSlot,
        int maximumCopyLength)
    {
        var candidates = new List<OemPayloadSourceBufferWrite>();
        foreach (var block in cfg.Blocks.Where(x => relevantBlocks.Contains(x.Id)))
        {
            for (var i = block.StartIndex; i <= block.EndIndex; i++)
            {
                var item = cfg.Instructions[i];
                var instruction = item.Instruction;
                if (!IsStackMemoryOperand(instruction, 0, sourceSlot) || !IsStackWrite(instruction)) continue;
                candidates.Add(new OemPayloadSourceBufferWrite(
                    oem, "INFERRED", item.Rva, BlockLabel(cfg, block.Id), item.Text, 0,
                    PayloadWriteWidth(instruction), DescribeSourceExpression(instruction), "writes source-slot storage rather than a proven source-buffer byte",
                    [$"{oem} @0x{item.Rva:X8}: stack write is within the source-local storage location."],
                    ["The local is consumed as a pointer; this is not promoted to a payload-byte write without proving the pointer representation."]));
            }
        }
        if (maximumCopyLength <= 0) return candidates.ToArray();
        return candidates.ToArray();
    }

    private static int PayloadWriteWidth(Instruction instruction)
    {
        var text = instruction.MemorySize.ToString();
        if (text.Contains("8", StringComparison.Ordinal)) return 1;
        if (text.Contains("16", StringComparison.Ordinal)) return 2;
        if (text.Contains("32", StringComparison.Ordinal)) return 4;
        if (text.Contains("64", StringComparison.Ordinal)) return 8;
        return 0;
    }

    private static OemPayloadSleepTrace[] BuildSleepTimeForwardTrace(NdevicePe pe, string oem, uint copyCallRva, int sourceSlot)
    {
        var result = new List<OemPayloadSleepTrace>();
        foreach (var anchor in SleepTimeAnchors)
        {
            var rvas = FindPayloadStringRvas(pe, anchor).Take(8).ToArray();
            if (rvas.Length == 0)
            {
                result.Add(new OemPayloadSleepTrace(oem, "UNRESOLVED", null, "resource_anchor", anchor,
                    $"payload source {StackSlot(sourceSlot)} @0x{copyCallRva:X8}", [],
                    [$"{oem}: exact anchor '{anchor}' was not found in this EXE byte image; no UI/control-flow edge is claimed."]));
                continue;
            }
            foreach (var rva in rvas)
            {
                result.Add(new OemPayloadSleepTrace(oem, "UNRESOLVED", rva, "resource_anchor", anchor,
                    $"payload source {StackSlot(sourceSlot)} @0x{copyCallRva:X8}",
                    [$"{oem} @0x{rva:X8}: static resource/string anchor is present."],
                    ["No direct x86 control-flow or data-flow edge from this anchor to the payload-source function has been proven; proximity is intentionally not used."]));
            }
        }
        return result.ToArray();
    }

    private static OemPayloadSleepTrace[] BuildSleepTimeBackwardTrace(
        string oem,
        uint copyCallRva,
        int sourceSlot,
        IReadOnlyCollection<OemPayloadSourcePointerDefinition> definitions,
        IReadOnlyCollection<OemPayloadSourceObjectProvenance> objects,
        IReadOnlyCollection<OemPayloadSourceBufferWrite> writes)
    {
        var status = definitions.Any(x => x.Status == "PROVEN") ? "INFERRED" : "UNRESOLVED";
        var next = definitions.FirstOrDefault()?.Instruction ?? objects.FirstOrDefault()?.Producer ?? "no direct local-pointer definition";
        return
        [
            new OemPayloadSleepTrace(
                oem, status, copyCallRva, "payload_copy_source", StackSlot(sourceSlot), next,
                [$"{oem} @0x{copyCallRva:X8}: exact memcpy source is {StackSlot(sourceSlot)}."],
                definitions.Any() ? ["Source pointer candidates require exact object/value provenance before a SleepTime field can be attributed."] : ["The bounded predecessor CFG contains no unique direct source-slot definition; remaining LEA/out-parameter or call-return edges are recorded separately."]),
            new OemPayloadSleepTrace(
                oem, "UNRESOLVED", null, "sleep_value", "SleepTime", "concrete source-buffer write",
                writes.Any(x => x.SourceOffset is not null) ? ["A source-storage write was observed but is not promoted to a byte-level SleepTime field without direct value provenance."] : [],
                ["No keyboard-specific SleepTime value-to-source-buffer-byte definition is statically proven."])
        ];
    }

    private static uint[] FindPayloadStringRvas(NdevicePe pe, string text)
    {
        var result = new List<uint>();
        var needles = new[] { System.Text.Encoding.ASCII.GetBytes(text), System.Text.Encoding.Unicode.GetBytes(text) };
        foreach (var needle in needles)
        {
            var offset = 0;
            while (offset <= pe.Bytes.Length - needle.Length)
            {
                var relative = pe.Bytes.AsSpan(offset).IndexOf(needle);
                if (relative < 0) break;
                var found = offset + relative;
                var section = pe.Sections.FirstOrDefault(x => found >= x.RawPointer && found < x.RawPointer + x.RawSize);
                if (section is not null) result.Add(section.VirtualAddress + checked((uint)(found - section.RawPointer)));
                offset = found + Math.Max(1, needle.Length);
            }
        }
        return result.Distinct().OrderBy(x => x).ToArray();
    }

    private static int FindInstructionIndex(IReadOnlyList<NdeviceDecoded> values, uint rva)
    {
        for (var i = 0; i < values.Count; i++) if (values[i].Rva == rva) return i;
        return 0;
    }

    private static string BlockLabel(PayloadCfg cfg, int blockId)
    {
        var block = cfg.Blocks[blockId];
        return $"B{blockId:D3}@0x{cfg.Instructions[block.StartIndex].Rva:X8}-0x{cfg.Instructions[block.EndIndex].Rva:X8}";
    }

    private static string FunctionRange((uint Start, uint End, int StartIndex, int EndIndex) bounds) => $"0x{bounds.Start:X8}-0x{bounds.End:X8}";
}

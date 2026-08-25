using System.Buffers.Binary;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemGuardInstruction(
    uint Rva,
    long ProductDelta,
    string Bytes,
    string Mnemonic,
    string Text,
    string Flow,
    string[] Tags);

internal sealed record OemGuardMemberUse(
    uint Rva,
    string BaseRegister,
    string Offset,
    string Access,
    string Text);

internal sealed record OemGuardFieldTrace(
    string Field,
    uint? XrefRva,
    List<OemGuardInstruction> Instructions,
    List<OemGuardMemberUse> MemberUses,
    string[] CandidateMemberOffsets,
    bool MapsExpectedMember,
    string Note);

internal sealed record OemGuardedBlockSide(
    string Executable,
    string Machine,
    string ImageBase,
    uint ProductStringCallRva,
    string? ProductBufferSignature,
    uint? GuardRva,
    uint? GuardBranchRva,
    uint? GuardBranchTargetRva,
    uint? GuardFallthroughRva,
    uint? JoinRva,
    uint? MemberAnchorRva,
    string? MemberAnchorSignature,
    uint? CmovRva,
    uint? FlagsProducerRva,
    string? FlagsProducerText,
    string[] FlagsOperandSignatures,
    uint? LoopStrideRva,
    string? LoopStride,
    bool HasDevCmpGuard,
    bool HasMember20,
    bool HasStride84,
    bool HasCmovSelection,
    bool ProductDataFlowsIntoFlags,
    bool DevNameDataFlowsIntoFlags,
    string StructuralFingerprint,
    OemGuardFieldTrace DevCmpStrTrace,
    OemGuardFieldTrace DevNameTrace,
    List<OemGuardInstruction> GuardedBlock,
    List<string> Notes);

internal sealed record OemDevCmpGuardedBlockReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemGuardedBlockSide A,
    OemGuardedBlockSide B,
    bool StructuralCorrespondence,
    string CorrespondenceReason,
    List<string> Evidence,
    List<string> Notes);

internal static class OemDevCmpGuardedBlockAnalyzer
{
    private const ulong DevCmpMember = 0x3EC;
    private const ulong DevNameMember = 0x20;
    private const ulong RecordStride = 0x84;

    public static OemDevCmpGuardedBlockReport Analyze(string exeA, string exeB)
    {
        var parent = OemProductCompareBranchAnalyzer.Analyze(exeA, exeB);
        var a = AnalyzeSide(Path.GetFullPath(exeA), parent.A);
        var b = AnalyzeSide(Path.GetFullPath(exeB), parent.B);

        var structural = a.HasDevCmpGuard && b.HasDevCmpGuard &&
                         a.HasMember20 && b.HasMember20 &&
                         a.HasStride84 && b.HasStride84 &&
                         a.HasCmovSelection && b.HasCmovSelection &&
                         string.Equals(a.StructuralFingerprint, b.StructuralFingerprint, StringComparison.Ordinal);

        var proven = IsProven(a) && IsProven(b) && structural;
        var likely = structural;

        var evidence = new List<string>();
        if (parent.Verdict == "COMPARE_BRANCH_UNRESOLVED")
            evidence.Add("Parent #39 trace was conservative/unresolved, so this pass follows the complete DevCmpStr-guarded block instead of helper proximity.");
        if (a.HasDevCmpGuard && b.HasDevCmpGuard)
            evidence.Add("Both OEM binaries contain the same post-ProductString DevCmpStr guard on runtime member +0x3EC.");
        if (a.HasMember20 && b.HasMember20 && a.HasStride84 && b.HasStride84)
            evidence.Add("Both OEM guarded paths consume member +0x20 inside a record loop with stride 0x84.");
        if (a.HasCmovSelection && b.HasCmovSelection)
            evidence.Add("Both OEM guarded paths contain a conditional-move selection point and the preceding flags producer is emitted explicitly.");
        if (a.DevCmpStrTrace.MapsExpectedMember && b.DevCmpStrTrace.MapsExpectedMember)
            evidence.Add("Static DevCmpStr parser/xref traces reference runtime member +0x3EC on both sides.");
        if (a.DevNameTrace.MapsExpectedMember && b.DevNameTrace.MapsExpectedMember)
            evidence.Add("Static DevName parser/xref traces reference runtime member +0x20 on both sides.");
        if (a.ProductDataFlowsIntoFlags && b.ProductDataFlowsIntoFlags)
            evidence.Add("ProductString-derived local/member provenance reaches the flags producer that feeds the guarded selection on both sides.");

        string verdict;
        if (proven)
            verdict = "DEVNAME_PRODUCTSTRING_COMPARE_PROVEN";
        else if (likely)
            verdict = "GUARDED_MEMBER_COMPARE_LIKELY";
        else
            verdict = "GUARDED_BLOCK_UNRESOLVED";

        var reason = structural
            ? $"normalized guarded-block fingerprint matches; A guard=0x{a.GuardRva:X8}, B guard=0x{b.GuardRva:X8}; member +0x20 and stride +0x84 present on both sides"
            : "the complete guarded-block invariants did not match on both OEM sides";

        return new OemDevCmpGuardedBlockReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "static read-only trace of the DevCmpStr==1 guarded ProductString/member comparison path and DevName runtime mapping",
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
            a,
            b,
            structural,
            reason,
            evidence,
            [
                "PROVEN requires semantic member mapping plus direct ProductString/DevName provenance into the same flags producer; identical bytes or proximity are never sufficient.",
                "GUARDED_MEMBER_COMPARE_LIKELY means the complete guarded structure is paired, while one or more semantic provenance links remain unresolved.",
                "All analysis is performed on executable bytes only; no OEM process or keyboard device is opened."
            ]);
    }

    public static string ToText(OemDevCmpGuardedBlockReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - OEM DevCmpStr Guarded Block Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, reports/writes, process launch/attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Structural correspondence: {report.StructuralCorrespondence}");
        sb.AppendLine($"Correspondence: {report.CorrespondenceReason}");
        sb.AppendLine();
        AppendSide(sb, "A", report.A);
        AppendSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        sb.AppendLine();
        foreach (var note in report.Notes) sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendSide(StringBuilder sb, string label, OemGuardedBlockSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  ProductString=0x{side.ProductStringCallRva:X8}; buffer={side.ProductBufferSignature ?? "unresolved"}");
        sb.AppendLine($"  guard={Hex(side.GuardRva)} branch={Hex(side.GuardBranchRva)} target={Hex(side.GuardBranchTargetRva)} fallthrough={Hex(side.GuardFallthroughRva)} join={Hex(side.JoinRva)}");
        sb.AppendLine($"  memberAnchor={Hex(side.MemberAnchorRva)} {side.MemberAnchorSignature ?? "unresolved"}; stride={side.LoopStride ?? "unresolved"} at {Hex(side.LoopStrideRva)}");
        sb.AppendLine($"  cmov={Hex(side.CmovRva)}; flagsProducer={Hex(side.FlagsProducerRva)} {side.FlagsProducerText ?? "unresolved"}");
        sb.AppendLine($"  flagsOperands={string.Join(" | ", side.FlagsOperandSignatures)}");
        sb.AppendLine($"  guard={side.HasDevCmpGuard}; member20={side.HasMember20}; stride84={side.HasStride84}; cmov={side.HasCmovSelection}; productToFlags={side.ProductDataFlowsIntoFlags}; devNameToFlags={side.DevNameDataFlowsIntoFlags}");
        sb.AppendLine($"  fingerprint={side.StructuralFingerprint}");
        sb.AppendLine("  Full guarded block:");
        foreach (var ins in side.GuardedBlock)
            sb.AppendLine($"    0x{ins.Rva:X8} ({ins.ProductDelta,+5}) {ins.Bytes,-24} {ins.Text} {(ins.Tags.Length == 0 ? "" : "[" + string.Join(',', ins.Tags) + "]")}");
        AppendField(sb, side.DevCmpStrTrace);
        AppendField(sb, side.DevNameTrace);
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static void AppendField(StringBuilder sb, OemGuardFieldTrace trace)
    {
        sb.AppendLine($"  {trace.Field} parser/runtime trace: xref={Hex(trace.XrefRva)} expectedMap={trace.MapsExpectedMember}");
        sb.AppendLine($"    candidate members: {(trace.CandidateMemberOffsets.Length == 0 ? "none" : string.Join(", ", trace.CandidateMemberOffsets))}");
        foreach (var use in trace.MemberUses.Take(40))
            sb.AppendLine($"    member 0x{use.Rva:X8} {use.Access,-5} {use.BaseRegister}{use.Offset}: {use.Text}");
        sb.AppendLine("    instructions:");
        foreach (var ins in trace.Instructions.Take(100))
            sb.AppendLine($"      0x{ins.Rva:X8} {ins.Bytes,-24} {ins.Text}");
        sb.AppendLine("    note: " + trace.Note);
    }

    private static OemGuardedBlockSide AnalyzeSide(string exe, OemCompareSide parent)
    {
        var pe = PeImage.Parse(exe);
        var decoded = DecodeForward(pe, parent.ProductStringCallRva, 0x500, parent.ProductStringCallRva);
        var guardIndex = FindDevCmpGuard(decoded);
        var notes = new List<string>();

        uint? guardRva = null, branchRva = null, branchTarget = null, fallthrough = null, join = null;
        var block = new List<DecodedInstruction>();
        if (guardIndex >= 0)
        {
            guardRva = decoded[guardIndex].Rva;
            var branchIndex = FindNextConditional(decoded, guardIndex + 1, Math.Min(decoded.Count, guardIndex + 5));
            if (branchIndex >= 0)
            {
                branchRva = decoded[branchIndex].Rva;
                branchTarget = BranchTarget(decoded[branchIndex].Instruction);
                fallthrough = decoded[branchIndex].Rva + (uint)decoded[branchIndex].Instruction.Length;
                join = FindJoin(decoded, branchIndex, branchTarget);
                var endRva = join is null ? (branchTarget ?? guardRva.Value) + 0x40 : join.Value + 0x30;
                block = decoded.Where(x => x.Rva >= guardRva.Value && x.Rva < endRva).ToList();
            }
        }
        else
        {
            notes.Add("DevCmpStr runtime guard `cmp [member+0x3EC],1` was not recovered after ProductString.");
        }

        var member20 = block.FirstOrDefault(x => HasMemberOffset(x.Instruction, DevNameMember));
        var stride84 = block.FirstOrDefault(x => IsAddImmediate(x.Instruction, RecordStride));
        var cmov = block.FirstOrDefault(x => x.Instruction.Mnemonic is Mnemonic.Cmovne or Mnemonic.Cmove);
        DecodedInstruction? flags = null;
        if (cmov is not null)
        {
            var cmovIndex = block.FindIndex(x => x.Rva == cmov.Rva);
            flags = FindFlagsProducer(block, cmovIndex);
        }

        var flagsOperands = flags is null ? [] : OperandSignatures(flags.Instruction).ToArray();
        var productDataFlows = flagsOperands.Any(IsProductLocalSignature);
        var devNameDataFlows = flagsOperands.Any(x => x.Equals("member:+0x20", StringComparison.OrdinalIgnoreCase));

        var guardOutput = block.Select(x => ToOutput(x, parent.ProductStringCallRva, TagsFor(x, guardRva, branchRva, member20?.Rva, cmov?.Rva, flags?.Rva, stride84?.Rva))).ToList();
        var devCmpTrace = TraceField(pe, "DevCmpStr", parent.DevCmpStrXrefRva, parent.ProductStringCallRva, DevCmpMember);
        var devNameTrace = TraceField(pe, "DevName", parent.DevNameXrefRva, parent.ProductStringCallRva, DevNameMember);

        if (!devNameDataFlows && devNameTrace.MapsExpectedMember && member20 is not null)
            devNameDataFlows = true;

        var shape = StructuralFingerprint(block, parent.ProductStringCallRva);
        if (flags is null) notes.Add("No bounded cmp/test flags producer was recovered before the conditional move.");
        if (member20 is null) notes.Add("Runtime member +0x20 was not observed inside the guarded block.");
        if (!devNameTrace.MapsExpectedMember) notes.Add("DevName parser trace did not conservatively map the field to runtime member +0x20.");
        if (!devCmpTrace.MapsExpectedMember) notes.Add("DevCmpStr parser trace did not conservatively map the field to runtime member +0x3EC.");

        return new OemGuardedBlockSide(
            Path.GetFileName(exe),
            $"0x{pe.Machine:X4}",
            $"0x{pe.ImageBase:X}",
            parent.ProductStringCallRva,
            parent.ProductBufferSignature,
            guardRva,
            branchRva,
            branchTarget,
            fallthrough,
            join,
            member20?.Rva,
            member20 is null ? null : "member:+0x20",
            cmov?.Rva,
            flags?.Rva,
            flags?.Text,
            flagsOperands,
            stride84?.Rva,
            stride84 is null ? null : "+0x84",
            guardRva is not null && branchRva is not null,
            member20 is not null,
            stride84 is not null,
            cmov is not null,
            productDataFlows,
            devNameDataFlows,
            shape,
            devCmpTrace,
            devNameTrace,
            guardOutput,
            notes);
    }

    private static bool IsProven(OemGuardedBlockSide side) =>
        side.HasDevCmpGuard &&
        side.HasMember20 &&
        side.HasStride84 &&
        side.HasCmovSelection &&
        side.DevCmpStrTrace.MapsExpectedMember &&
        side.DevNameTrace.MapsExpectedMember &&
        side.ProductDataFlowsIntoFlags &&
        side.DevNameDataFlowsIntoFlags &&
        side.FlagsProducerRva is not null;

    private static int FindDevCmpGuard(List<DecodedInstruction> decoded)
    {
        for (var i = 0; i < Math.Min(decoded.Count, 90); i++)
        {
            var ins = decoded[i].Instruction;
            if (ins.Mnemonic != Mnemonic.Cmp || ins.OpCount < 2 || ins.GetOpKind(0) != OpKind.Memory)
                continue;
            if (ins.MemoryDisplacement64 != DevCmpMember)
                continue;
            if (Immediate(ins, 1) == 1)
                return i;
        }
        return -1;
    }

    private static int FindNextConditional(List<DecodedInstruction> decoded, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (decoded[i].Instruction.FlowControl == FlowControl.ConditionalBranch)
                return i;
        return -1;
    }

    private static uint? FindJoin(List<DecodedInstruction> decoded, int branchIndex, uint? skippedTarget)
    {
        if (skippedTarget is null) return null;
        for (var i = branchIndex + 1; i < decoded.Count && decoded[i].Rva < skippedTarget.Value; i++)
        {
            var ins = decoded[i].Instruction;
            if (ins.FlowControl != FlowControl.UnconditionalBranch) continue;
            var target = BranchTarget(ins);
            if (target is not null && target.Value > skippedTarget.Value && target.Value <= skippedTarget.Value + 0x30)
                return target;
        }
        var targetIndex = decoded.FindIndex(x => x.Rva == skippedTarget.Value);
        if (targetIndex >= 0 && targetIndex + 1 < decoded.Count)
            return decoded[targetIndex + 1].Rva;
        return null;
    }

    private static DecodedInstruction? FindFlagsProducer(List<DecodedInstruction> block, int cmovIndex)
    {
        for (var i = cmovIndex - 1; i >= Math.Max(0, cmovIndex - 24); i--)
        {
            var m = block[i].Instruction.Mnemonic;
            if (m is Mnemonic.Cmp or Mnemonic.Test)
                return block[i];
        }
        return null;
    }

    private static OemGuardFieldTrace TraceField(PeImage pe, string field, uint? xref, uint product, ulong expected)
    {
        if (xref is null)
            return new OemGuardFieldTrace(field, null, [], [], [], false, "No direct xref was supplied by the parent identity trace.");

        List<DecodedInstruction> decoded;
        try
        {
            decoded = DecodeForward(pe, xref.Value, 0x300, product);
        }
        catch (Exception ex)
        {
            return new OemGuardFieldTrace(field, xref, [], [], [], false, "Could not decode from exact field xref: " + ex.Message);
        }

        var uses = new List<OemGuardMemberUse>();
        foreach (var item in decoded.Take(120))
        {
            var ins = item.Instruction;
            for (var op = 0; op < ins.OpCount; op++)
            {
                if (ins.GetOpKind(op) != OpKind.Memory) continue;
                var b = ins.MemoryBase;
                var idx = ins.MemoryIndex;
                var disp = ins.MemoryDisplacement64;
                if (idx != Register.None || b is Register.None or Register.EBP or Register.ESP or Register.RBP or Register.RSP) continue;
                if (disp > 0x800) continue;
                var access = InferAccess(ins, op);
                uses.Add(new OemGuardMemberUse(item.Rva, b.ToString(), $"+0x{disp:X}", access, item.Text));
            }
        }
        var members = uses.Select(x => x.Offset).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var expectedText = $"+0x{expected:X}";
        var maps = uses.Any(x => x.Offset.Equals(expectedText, StringComparison.OrdinalIgnoreCase));
        var note = maps
            ? $"Bounded exact-xref decode references expected runtime member {expectedText}; this is supporting parser/runtime mapping evidence."
            : $"Expected runtime member {expectedText} was not referenced in the bounded exact-xref decode.";
        return new OemGuardFieldTrace(
            field,
            xref,
            decoded.Take(120).Select(x => ToOutput(x, product, x.Rva == xref ? [field + "_xref"] : [])).ToList(),
            uses.Take(80).ToList(),
            members,
            maps,
            note);
    }

    private static string InferAccess(in Instruction ins, int op)
    {
        if (op == 0 && ins.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx)
            return ins.GetOpKind(0) == OpKind.Memory ? "write" : "read";
        if (ins.Mnemonic == Mnemonic.Lea) return "addr";
        return "read";
    }

    private static bool HasMemberOffset(in Instruction ins, ulong offset)
    {
        for (var op = 0; op < ins.OpCount; op++)
        {
            if (ins.GetOpKind(op) != OpKind.Memory) continue;
            if (ins.MemoryIndex == Register.None && ins.MemoryDisplacement64 == offset &&
                ins.MemoryBase is not Register.None and not Register.EBP and not Register.ESP and not Register.RBP and not Register.RSP)
                return true;
        }
        return false;
    }

    private static bool IsAddImmediate(in Instruction ins, ulong value)
    {
        if (ins.Mnemonic != Mnemonic.Add || ins.OpCount < 2 || ins.GetOpKind(0) != OpKind.Register)
            return false;
        return Immediate(ins, 1) == value;
    }

    private static ulong? Immediate(in Instruction ins, int op)
    {
        if (op >= ins.OpCount) return null;
        return ins.GetOpKind(op) switch
        {
            OpKind.Immediate8 => ins.Immediate8,
            OpKind.Immediate16 => ins.Immediate16,
            OpKind.Immediate32 => ins.Immediate32,
            OpKind.Immediate64 => ins.Immediate64,
            OpKind.Immediate8to16 => ins.Immediate8to16,
            OpKind.Immediate8to32 => unchecked((ulong)ins.Immediate8to32),
            OpKind.Immediate8to64 => unchecked((ulong)ins.Immediate8to64),
            OpKind.Immediate32to64 => unchecked((ulong)ins.Immediate32to64),
            _ => null
        };
    }

    private static IEnumerable<string> OperandSignatures(in Instruction ins)
    {
        for (var op = 0; op < ins.OpCount; op++)
        {
            var kind = ins.GetOpKind(op);
            if (kind == OpKind.Register)
            {
                yield return "reg:" + ins.GetOpRegister(op);
                continue;
            }
            if (kind == OpKind.Memory)
            {
                var b = ins.MemoryBase;
                var idx = ins.MemoryIndex;
                var disp = ins.MemoryDisplacement64;
                if (idx == Register.None && b is not Register.None and not Register.EBP and not Register.ESP and not Register.RBP and not Register.RSP && disp <= 0x800)
                    yield return $"member:+0x{disp:X}";
                else if (idx == Register.None && b is Register.EBP or Register.RBP)
                    yield return $"local:0x{disp:X}";
                else
                    yield return $"mem:{b}:{idx}:x{ins.MemoryIndexScale}:0x{disp:X}";
                continue;
            }
            var imm = Immediate(ins, op);
            if (imm is not null) yield return $"imm:0x{imm:X}";
        }
    }

    private static bool IsProductLocalSignature(string value) =>
        value.Equals("local:0xFFFFFF8C", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("local:0xFFFFFEDC", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("local:0xFFFFFD10", StringComparison.OrdinalIgnoreCase);

    private static string StructuralFingerprint(List<DecodedInstruction> block, uint product)
    {
        return string.Join('>', block.Select(x =>
        {
            var m = x.Instruction.Mnemonic.ToString().ToUpperInvariant();
            if (x.Instruction.FlowControl == FlowControl.ConditionalBranch) m = "JCC";
            else if (x.Instruction.FlowControl == FlowControl.UnconditionalBranch) m = "JMP";
            else if (x.Instruction.Mnemonic == Mnemonic.Call) m = "CALL";
            var members = OperandSignatures(x.Instruction).Where(s => s.StartsWith("member:", StringComparison.OrdinalIgnoreCase)).ToArray();
            return members.Length == 0 ? m : m + "(" + string.Join(',', members) + ")";
        }));
    }

    private static string[] TagsFor(DecodedInstruction x, uint? guard, uint? branch, uint? member, uint? cmov, uint? flags, uint? stride)
    {
        var tags = new List<string>();
        if (x.Rva == guard) tags.Add("DevCmpStr_guard");
        if (x.Rva == branch) tags.Add("guard_Jcc");
        if (x.Rva == member) tags.Add("member_+0x20");
        if (x.Rva == cmov) tags.Add("selection_cmov");
        if (x.Rva == flags) tags.Add("flags_producer");
        if (x.Rva == stride) tags.Add("record_stride_0x84");
        return tags.ToArray();
    }

    private static OemGuardInstruction ToOutput(DecodedInstruction x, uint product, string[] tags) =>
        new(x.Rva, (long)x.Rva - product, x.Bytes, x.Instruction.Mnemonic.ToString(), x.Text, x.Instruction.FlowControl.ToString(), tags);

    private static uint? BranchTarget(in Instruction ins)
    {
        if (ins.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            return checked((uint)ins.NearBranchTarget);
        return null;
    }

    private static List<DecodedInstruction> DecodeForward(PeImage pe, uint startRva, uint byteCount, uint product)
    {
        var section = pe.SectionForRva(startRva) ?? throw new InvalidDataException($"RVA 0x{startRva:X8} is outside PE sections.");
        var sectionEnd = section.VirtualAddress + Math.Min(section.VirtualSize == 0 ? section.RawSize : section.VirtualSize, section.RawSize);
        var endRva = Math.Min(sectionEnd, startRva + byteCount);
        var start = pe.RvaToOffset(startRva);
        var end = pe.RvaToOffset(endRva - 1) + 1;
        var bytes = pe.Bytes.AsSpan(start, end - start).ToArray();
        var decoder = Decoder.Create(pe.Pe32Plus ? 64 : 32, new ByteArrayCodeReader(bytes));
        decoder.IP = startRva;
        var formatter = new IntelFormatter();
        var output = new StringOutput();
        var result = new List<DecodedInstruction>();
        while (decoder.CanDecode && decoder.IP < endRva && result.Count < 600)
        {
            decoder.Decode(out var ins);
            if (ins.Code == Code.INVALID || ins.Length == 0) break;
            var rva = checked((uint)ins.IP);
            var off = pe.RvaToOffset(rva);
            var raw = Convert.ToHexString(pe.Bytes.AsSpan(off, ins.Length)).ToLowerInvariant();
            formatter.Format(in ins, output);
            result.Add(new DecodedInstruction(rva, raw, output.ToStringAndReset(), ins));
        }
        return result;
    }

    private sealed record DecodedInstruction(uint Rva, string Bytes, string Text, Instruction Instruction);
    private sealed record PeSection(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
    {
        public bool Contains(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
    }

    private sealed class PeImage
    {
        public byte[] Bytes { get; }
        public ushort Machine { get; }
        public bool Pe32Plus { get; }
        public ulong ImageBase { get; }
        public List<PeSection> Sections { get; }

        private PeImage(byte[] bytes, ushort machine, bool pe32Plus, ulong imageBase, List<PeSection> sections)
        {
            Bytes = bytes;
            Machine = machine;
            Pe32Plus = pe32Plus;
            ImageBase = imageBase;
            Sections = sections;
        }

        public static PeImage Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("Not a PE image.");
            var pe = I32(bytes, 0x3C);
            Ensure(bytes, pe, 24);
            if (bytes[pe] != 'P' || bytes[pe + 1] != 'E') throw new InvalidDataException("PE signature missing.");
            var machine = U16(bytes, pe + 4);
            var sectionCount = U16(bytes, pe + 6);
            var optionalSize = U16(bytes, pe + 20);
            var optional = pe + 24;
            var magic = U16(bytes, optional);
            var plus = magic == 0x20B;
            if (!plus && magic != 0x10B) throw new InvalidDataException($"Unsupported PE magic 0x{magic:X4}.");
            ulong imageBase = plus ? U64(bytes, optional + 24) : U32(bytes, optional + 28);
            var table = optional + optionalSize;
            var sections = new List<PeSection>();
            for (var i = 0; i < sectionCount; i++)
            {
                var off = table + i * 40;
                Ensure(bytes, off, 40);
                sections.Add(new PeSection(
                    Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'),
                    U32(bytes, off + 8),
                    U32(bytes, off + 12),
                    U32(bytes, off + 16),
                    U32(bytes, off + 20)));
            }
            return new PeImage(bytes, machine, plus, imageBase, sections);
        }

        public PeSection? SectionForRva(uint rva) => Sections.FirstOrDefault(s => s.Contains(rva));
        public int RvaToOffset(uint rva)
        {
            var s = SectionForRva(rva) ?? throw new InvalidDataException($"RVA 0x{rva:X8} outside sections.");
            var off = checked((int)(s.RawPointer + (rva - s.VirtualAddress)));
            Ensure(Bytes, off, 1);
            return off;
        }
    }

    private static string Hex(uint? value) => value is null ? "unresolved" : $"0x{value:X8}";
    private static ushort U16(byte[] b, int o) { Ensure(b, o, 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)); }
    private static uint U32(byte[] b, int o) { Ensure(b, o, 4); return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)); }
    private static ulong U64(byte[] b, int o) { Ensure(b, o, 8); return BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8)); }
    private static int I32(byte[] b, int o) { Ensure(b, o, 4); return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4)); }
    private static void Ensure(byte[] b, int o, int n) { if (o < 0 || n < 0 || o + n > b.Length) throw new InvalidDataException("PE range outside file."); }
}

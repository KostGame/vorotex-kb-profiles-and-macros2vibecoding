using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    private enum PtrKind { Source, Dest }
    private readonly record struct PtrTag(PtrKind Kind, long Offset);
    private readonly record struct ValueTag(PtrKind Kind, long Offset);
    private sealed record NdeviceDecoded(uint Rva, string Text, Instruction Instruction);
    private sealed record NdeviceSection(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
    {
        public bool Contains(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
    }
    private sealed record NdeviceImport(string Dll, string Name, uint IatRva);

    private static uint[] RecoverExactFieldWrites(NdevicePe pe, OemFieldProvenance field, uint join, long expectedDisp)
    {
        if (field.XrefRva == 0 || join <= field.XrefRva) return [];
        var branch = FindEqualityBranch(pe, field.XrefRva);
        if (branch.TrueStart is null) return [];
        return TraceCaseCfg(pe, branch.TrueStart.Value, join, branch.FalseTarget, field.XrefRva)
            .Where(x => IsMemoryWrite(x.Instruction) && x.Instruction.Op0Kind == OpKind.Memory &&
                        Normalize(x.Instruction.MemoryBase) == Register.EBP && SignedDisp(x.Instruction) == expectedDisp)
            .Select(x => x.Rva)
            .Distinct()
            .ToArray();
    }

    private static (uint? TrueStart, uint? FalseTarget) FindEqualityBranch(NdevicePe pe, uint xref)
    {
        var decoded = DecodeRange(pe, xref, Math.Min(pe.TextEnd, xref + 0x80));
        var call = decoded.FindIndex(1, x => x.Instruction.Mnemonic == Mnemonic.Call);
        if (call < 0) return (null, null);
        var test = decoded.FindIndex(call + 1, x => x.Instruction.Mnemonic == Mnemonic.Test && MentionsAl(x.Instruction));
        if (test < 0) return (null, null);
        var branch = decoded.Skip(test + 1).Take(4).FirstOrDefault(x => x.Instruction.FlowControl == FlowControl.ConditionalBranch && IsDirectBranch(x.Instruction));
        if (branch is null) return (null, null);
        var fallthrough = branch.Rva + (uint)branch.Instruction.Length;
        var target = checked((uint)branch.Instruction.NearBranchTarget);
        if (branch.Instruction.Mnemonic == Mnemonic.Je) return (fallthrough, target);
        if (branch.Instruction.Mnemonic == Mnemonic.Jne) return (target, fallthrough);
        return (null, null);
    }

    private static List<NdeviceDecoded> TraceCaseCfg(NdevicePe pe, uint start, uint join, uint? falseTarget, uint xref)
    {
        var map = DecodeRange(pe, xref, join).ToDictionary(x => x.Rva);
        var queue = new Queue<uint>();
        var seen = new HashSet<uint>();
        var result = new List<NdeviceDecoded>();
        queue.Enqueue(start);
        while (queue.Count > 0 && seen.Count < 900)
        {
            var rva = queue.Dequeue();
            if (rva == join || rva == falseTarget || rva < xref || rva >= join || !seen.Add(rva)) continue;
            if (!map.TryGetValue(rva, out var item)) continue;
            result.Add(item);
            var next = item.Rva + (uint)item.Instruction.Length;
            if (item.Instruction.Mnemonic == Mnemonic.Ret) continue;
            if (item.Instruction.FlowControl == FlowControl.UnconditionalBranch && IsDirectBranch(item.Instruction))
            {
                queue.Enqueue(checked((uint)item.Instruction.NearBranchTarget));
                continue;
            }
            if (item.Instruction.FlowControl == FlowControl.ConditionalBranch && IsDirectBranch(item.Instruction))
            {
                queue.Enqueue(checked((uint)item.Instruction.NearBranchTarget));
                queue.Enqueue(next);
                continue;
            }
            queue.Enqueue(next);
        }
        return result;
    }

    private static OemNdeviceAggregateCaller? FindAggregateCaller(NdevicePe pe, uint join, uint productRva, uint expectedSize)
    {
        var end = Math.Min(pe.TextEnd, Math.Max(productRva, join + 0x500));
        var body = DecodeRange(pe, join, end);
        for (var i = 0; i < body.Count; i++)
        {
            var source = body[i].Instruction;
            if (source.Mnemonic != Mnemonic.Lea || source.Op0Kind != OpKind.Register || source.Op1Kind != OpKind.Memory ||
                Normalize(source.MemoryBase) != Register.EBP || SignedDisp(source) >= 0) continue;
            var sourceReg = Normalize(source.Op0Register);
            var push = -1;
            for (var p = i + 1; p < Math.Min(body.Count, i + 5); p++)
            {
                var pin = body[p].Instruction;
                if (pin.Mnemonic == Mnemonic.Push && pin.Op0Kind == OpKind.Register && Normalize(pin.Op0Register) == sourceReg)
                {
                    push = p;
                    break;
                }
            }
            if (push < 0) continue;

            (Register Base, long Offset)? containerEnd = null;
            var eaxFeedsEcx = false;
            for (var p = Math.Max(0, i - 4); p < Math.Min(body.Count, push + 12); p++)
            {
                var pin = body[p].Instruction;
                if (pin.Mnemonic == Mnemonic.Mov && pin.Op0Kind == OpKind.Register && Normalize(pin.Op0Register) == Register.EAX &&
                    pin.Op1Kind == OpKind.Memory && !IsStackBase(pin.MemoryBase) && pin.MemoryBase != Register.None)
                    containerEnd = (Normalize(pin.MemoryBase), SignedDisp(pin));
                if (pin.Mnemonic == Mnemonic.Mov && pin.Op0Kind == OpKind.Register && Normalize(pin.Op0Register) == Register.ECX &&
                    pin.Op1Kind == OpKind.Register && Normalize(pin.Op1Register) == Register.EAX)
                    eaxFeedsEcx = true;
            }
            if (containerEnd is null || !eaxFeedsEcx) continue;

            for (var c = push + 1; c < Math.Min(body.Count, push + 14); c++)
            {
                var call = body[c].Instruction;
                if (call.Mnemonic != Mnemonic.Call || !IsDirectBranch(call)) continue;
                var target = checked((uint)call.NearBranchTarget);
                if (target < pe.TextStart || target >= pe.TextEnd) continue;
                for (var a = c + 1; a < Math.Min(body.Count, c + 6); a++)
                {
                    var add = body[a].Instruction;
                    if (add.Mnemonic != Mnemonic.Add || add.Op0Kind != OpKind.Memory ||
                        Normalize(add.MemoryBase) != containerEnd.Value.Base || SignedDisp(add) != containerEnd.Value.Offset) continue;
                    if (!TryImmediate(add, out var size) || size != expectedSize) continue;
                    var steps = body.Skip(Math.Max(0, i - 2)).Take(a - Math.Max(0, i - 2) + 1)
                        .Select(x => $"0x{x.Rva:X8} {x.Text}").ToArray();
                    var fp = $"SRC_LOCAL>DEST_CONTAINER_END>DIRECT_HELPER>ADVANCE_0x{size:X}";
                    return new OemNdeviceAggregateCaller(
                        SignedDisp(source), body[c].Rva, target, size,
                        containerEnd.Value.Base.ToString(), containerEnd.Value.Offset,
                        steps, fp);
                }
            }
        }
        return null;
    }

    private static OemNdeviceHelperTrace TraceAggregateHelper(NdevicePe pe, uint helperRva, long member20, long member3Ec)
    {
        var trace = TraceHelperCore(pe, helperRva, member20, member3Ec, 0);
        return new OemNdeviceHelperTrace(
            helperRva,
            trace.SourceRecovered,
            trace.DestRecovered,
            trace.Member20,
            trace.Member3Ec,
            trace.Steps.ToArray(),
            trace.Nested.ToArray(),
            string.Join('>', trace.Events),
            trace.Notes.ToArray());
    }

    private sealed record HelperResult(
        bool SourceRecovered,
        bool DestRecovered,
        bool Member20,
        bool Member3Ec,
        List<string> Steps,
        List<string> Nested,
        List<string> Events,
        List<string> Notes);

    private static HelperResult TraceHelperCore(NdevicePe pe, uint entry, long member20, long member3Ec, int depth)
    {
        var body = DecodeRange(pe, entry, Math.Min(pe.TextEnd, entry + 0x1800)).Take(1600).ToList();
        var ptr = new Dictionary<Register, PtrTag> { [Register.ECX] = new(PtrKind.Dest, 0) };
        var values = new Dictionary<Register, ValueTag>();
        var recentPush = new Queue<PtrTag>();
        var steps = new List<string>();
        var nested = new List<string>();
        var events = new List<string> { "DEST_THIS" };
        var notes = new List<string>();
        var sourceRecovered = false;
        var destRecovered = true;
        var member20Copied = false;
        var member3EcCopied = false;
        var spDepth = 0L;
        long? ebpDepth = null;

        for (var index = 0; index < body.Count; index++)
        {
            var item = body[index];
            var ins = item.Instruction;
            var text = $"0x{item.Rva:X8} {item.Text}";
            if (ins.Mnemonic == Mnemonic.Ret)
            {
                events.Add("RET");
                break;
            }

            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && Normalize(ins.Op0Register) == Register.EBP &&
                ins.Op1Kind == OpKind.Register && Normalize(ins.Op1Register) == Register.ESP)
            {
                ebpDepth = spDepth;
                events.Add("FRAME");
            }

            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register)
            {
                var dst = Normalize(ins.Op0Register);
                if (ins.Op1Kind == OpKind.Register)
                {
                    var src = Normalize(ins.Op1Register);
                    if (ptr.TryGetValue(src, out var ptag)) ptr[dst] = ptag; else ptr.Remove(dst);
                    if (values.TryGetValue(src, out var vtag)) values[dst] = vtag; else values.Remove(dst);
                }
                else if (ins.Op1Kind == OpKind.Memory)
                {
                    if (IsSourceArgument(ins, spDepth, ebpDepth))
                    {
                        ptr[dst] = new PtrTag(PtrKind.Source, 0);
                        values.Remove(dst);
                        sourceRecovered = true;
                        events.Add("SRC_ARG");
                        steps.Add(text + " ; source object pointer");
                    }
                    else if (TryTaggedMemory(ptr, ins, out var mem))
                    {
                        values[dst] = new ValueTag(mem.Kind, mem.Offset);
                        ptr.Remove(dst);
                        if (mem.Offset == member20 || mem.Offset == member3Ec)
                            steps.Add(text + $" ; load {mem.Kind}+0x{mem.Offset:X}");
                    }
                    else
                    {
                        ptr.Remove(dst);
                        values.Remove(dst);
                    }
                }
            }
            else if (ins.Mnemonic == Mnemonic.Lea && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Memory)
            {
                var dst = Normalize(ins.Op0Register);
                if (TryTaggedMemory(ptr, ins, out var mem))
                {
                    ptr[dst] = mem;
                    values.Remove(dst);
                    if (mem.Offset == member20 || mem.Offset == member3Ec)
                        steps.Add(text + $" ; address {mem.Kind}+0x{mem.Offset:X}");
                }
                else ptr.Remove(dst);
            }

            if (IsMemoryWrite(ins) && ins.Op0Kind == OpKind.Memory && TryTaggedMemory(ptr, ins, out var destMem) && destMem.Kind == PtrKind.Dest &&
                ins.Op1Kind == OpKind.Register && values.TryGetValue(Normalize(ins.Op1Register), out var srcValue) &&
                srcValue.Kind == PtrKind.Source && srcValue.Offset == destMem.Offset)
            {
                if (destMem.Offset == member20) member20Copied = true;
                if (destMem.Offset == member3Ec) member3EcCopied = true;
                if (destMem.Offset == member20 || destMem.Offset == member3Ec)
                {
                    events.Add($"MOV_{destMem.Offset:X}");
                    steps.Add(text + $" ; explicit source+0x{destMem.Offset:X} -> dest+0x{destMem.Offset:X}");
                }
            }

            if (ins.Mnemonic == Mnemonic.Push)
            {
                if (ins.Op0Kind == OpKind.Register && ptr.TryGetValue(Normalize(ins.Op0Register), out var pushed))
                {
                    recentPush.Enqueue(pushed);
                    while (recentPush.Count > 8) recentPush.Dequeue();
                }
                spDepth += pe.Pe32Plus ? 8 : 4;
            }
            else if (ins.Mnemonic == Mnemonic.Pop)
            {
                spDepth = Math.Max(0, spDepth - (pe.Pe32Plus ? 8 : 4));
            }
            else if (ins.Mnemonic is Mnemonic.Sub or Mnemonic.Add && ins.Op0Kind == OpKind.Register && Normalize(ins.Op0Register) == Register.ESP && TryImmediate(ins, out var imm))
            {
                if (ins.Mnemonic == Mnemonic.Sub) spDepth += imm;
                else spDepth = Math.Max(0, spDepth - imm);
            }

            if (ins.Mnemonic == Mnemonic.Call)
            {
                var dest = ptr.TryGetValue(Register.ECX, out var dtag) && dtag.Kind == PtrKind.Dest ? dtag : (PtrTag?)null;
                var src = recentPush.LastOrDefault(x => x.Kind == PtrKind.Source);
                var hasSrc = recentPush.Any(x => x.Kind == PtrKind.Source);
                var symbol = pe.ResolveImport(ins);
                var direct = IsDirectBranch(ins) ? checked((uint?)ins.NearBranchTarget) : null;
                if (dest is not null && hasSrc && dest.Value.Offset == src.Offset && (dest.Value.Offset == member20 || dest.Value.Offset == member3Ec))
                {
                    var accepted = false;
                    if (LooksCopySymbol(symbol)) accepted = true;
                    else if (direct is not null && depth < 2 && direct >= pe.TextStart && direct < pe.TextEnd)
                        accepted = NestedHelperCopiesArgument(pe, direct.Value, depth + 1);
                    nested.Add($"0x{item.Rva:X8} dest+0x{dest.Value.Offset:X} src+0x{src.Offset:X} target={(symbol ?? (direct is null ? "unresolved" : $"0x{direct.Value:X8}"))} accepted={accepted}");
                    if (accepted)
                    {
                        if (dest.Value.Offset == member20) member20Copied = true;
                        if (dest.Value.Offset == member3Ec) member3EcCopied = true;
                        events.Add($"CALL_{dest.Value.Offset:X}");
                    }
                }
                recentPush.Clear();
                values.Clear();
                ptr.Remove(Register.EAX);
                ptr.Remove(Register.ECX);
                ptr.Remove(Register.EDX);
            }
        }

        if (!sourceRecovered) notes.Add("Source object argument was not recovered conservatively from the helper stack frame.");
        if (!member20Copied) notes.Add("No explicit +0x20 copy edge was proven in the bounded helper trace.");
        if (!member3EcCopied) notes.Add("No explicit +0x3EC copy edge was proven in the bounded helper trace.");
        return new HelperResult(sourceRecovered, destRecovered, member20Copied, member3EcCopied, steps, nested, events, notes);
    }

    private static bool NestedHelperCopiesArgument(NdevicePe pe, uint entry, int depth)
    {
        var body = DecodeRange(pe, entry, Math.Min(pe.TextEnd, entry + 0x500)).Take(500).ToList();
        var ptr = new Dictionary<Register, PtrTag> { [Register.ECX] = new(PtrKind.Dest, 0) };
        var values = new Dictionary<Register, ValueTag>();
        var spDepth = 0L;
        long? ebpDepth = null;
        var source = false;
        foreach (var item in body)
        {
            var ins = item.Instruction;
            if (ins.Mnemonic == Mnemonic.Ret) break;
            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && Normalize(ins.Op0Register) == Register.EBP &&
                ins.Op1Kind == OpKind.Register && Normalize(ins.Op1Register) == Register.ESP) ebpDepth = spDepth;
            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register)
            {
                var dst = Normalize(ins.Op0Register);
                if (ins.Op1Kind == OpKind.Register)
                {
                    var src = Normalize(ins.Op1Register);
                    if (ptr.TryGetValue(src, out var p)) ptr[dst] = p;
                    if (values.TryGetValue(src, out var v)) values[dst] = v;
                }
                else if (ins.Op1Kind == OpKind.Memory)
                {
                    if (IsSourceArgument(ins, spDepth, ebpDepth)) { ptr[dst] = new(PtrKind.Source, 0); source = true; }
                    else if (TryTaggedMemory(ptr, ins, out var mem)) values[dst] = new(mem.Kind, mem.Offset);
                }
            }
            if (IsMemoryWrite(ins) && ins.Op0Kind == OpKind.Memory && TryTaggedMemory(ptr, ins, out var dest) && dest.Kind == PtrKind.Dest &&
                ins.Op1Kind == OpKind.Register && values.TryGetValue(Normalize(ins.Op1Register), out var value) &&
                value.Kind == PtrKind.Source && value.Offset == dest.Offset) return source;
            if (ins.Mnemonic == Mnemonic.Push) spDepth += pe.Pe32Plus ? 8 : 4;
            else if (ins.Mnemonic == Mnemonic.Pop) spDepth = Math.Max(0, spDepth - (pe.Pe32Plus ? 8 : 4));
            else if (ins.Mnemonic is Mnemonic.Sub or Mnemonic.Add && ins.Op0Kind == OpKind.Register && Normalize(ins.Op0Register) == Register.ESP && TryImmediate(ins, out var imm))
            {
                if (ins.Mnemonic == Mnemonic.Sub) spDepth += imm; else spDepth = Math.Max(0, spDepth - imm);
            }
        }
        return false;
    }

    private static bool IsSourceArgument(Instruction ins, long spDepth, long? ebpDepth)
    {
        if (ins.Op1Kind != OpKind.Memory) return false;
        var b = Normalize(ins.MemoryBase);
        var d = SignedDisp(ins);
        if (b == Register.ESP) return d - spDepth == 4;
        if (b == Register.EBP && ebpDepth is not null) return d - ebpDepth.Value == 4;
        return false;
    }

    private static bool TryTaggedMemory(Dictionary<Register, PtrTag> ptr, Instruction ins, out PtrTag tag)
    {
        var b = Normalize(ins.MemoryBase);
        if (ptr.TryGetValue(b, out var root))
        {
            tag = new PtrTag(root.Kind, root.Offset + SignedDisp(ins));
            return true;
        }
        tag = default;
        return false;
    }

    private static bool LooksCopySymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var s = symbol.ToLowerInvariant();
        return s.Contains("cduistring") && (s.Contains("??0") || s.Contains("??4") || s.Contains("assign"));
    }

    private static bool MentionsAl(Instruction ins) => Enumerable.Range(0, ins.OpCount).Any(i => ins.GetOpKind(i) == OpKind.Register && ins.GetOpRegister(i) == Register.AL);
    private static bool IsDirectBranch(Instruction ins) => ins.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
    private static bool IsMemoryWrite(Instruction ins) => ins.Mnemonic is Mnemonic.Mov or Mnemonic.Movups or Mnemonic.Movaps or Mnemonic.Movq or Mnemonic.Movdqa or Mnemonic.Movdqu;
    private static bool IsStackBase(Register r) => Normalize(r) is Register.EBP or Register.ESP;
    private static long SignedDisp(Instruction ins) => unchecked((int)(uint)ins.MemoryDisplacement64);

    private static bool TryImmediate(Instruction ins, out uint value)
    {
        value = 0;
        switch (ins.Op1Kind)
        {
            case OpKind.Immediate8: value = ins.Immediate8; return true;
            case OpKind.Immediate8to32: value = unchecked((uint)ins.Immediate8to32); return true;
            case OpKind.Immediate32: value = ins.Immediate32; return true;
            default: return false;
        }
    }

    private static Register Normalize(Register r) => r switch
    {
        Register.AL or Register.AH or Register.AX or Register.EAX or Register.RAX => Register.EAX,
        Register.BL or Register.BH or Register.BX or Register.EBX or Register.RBX => Register.EBX,
        Register.CL or Register.CH or Register.CX or Register.ECX or Register.RCX => Register.ECX,
        Register.DL or Register.DH or Register.DX or Register.EDX or Register.RDX => Register.EDX,
        Register.SI or Register.ESI or Register.RSI => Register.ESI,
        Register.DI or Register.EDI or Register.RDI => Register.EDI,
        Register.BP or Register.EBP or Register.RBP => Register.EBP,
        Register.SP or Register.ESP or Register.RSP => Register.ESP,
        _ => r
    };

    private static List<NdeviceDecoded> DecodeRange(NdevicePe pe, uint startRva, uint endRva)
    {
        startRva = Math.Max(startRva, pe.TextStart);
        endRva = Math.Min(endRva, pe.TextEnd);
        if (endRva <= startRva) return [];
        var start = pe.RvaToOffset(startRva);
        var end = pe.RvaToOffset(endRva - 1) + 1;
        var bytes = pe.Bytes.AsSpan(start, end - start).ToArray();
        var decoder = Decoder.Create(pe.Pe32Plus ? 64 : 32, new ByteArrayCodeReader(bytes));
        decoder.IP = startRva;
        var formatter = new IntelFormatter();
        var output = new NdeviceFormatterOutput();
        var result = new List<NdeviceDecoded>();
        while (decoder.IP < endRva && result.Count < 250000)
        {
            decoder.Decode(out var ins);
            if (ins.Code == Code.INVALID || ins.Length == 0) break;
            var rva = checked((uint)ins.IP);
            formatter.Format(in ins, output);
            result.Add(new NdeviceDecoded(rva, output.Take(), ins));
        }
        return result;
    }

    private sealed class NdeviceFormatterOutput : FormatterOutput
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public string Take() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    private sealed class NdevicePe
    {
        public byte[] Bytes { get; }
        public bool Pe32Plus { get; }
        public ulong ImageBase { get; }
        public List<NdeviceSection> Sections { get; }
        public List<NdeviceImport> Imports { get; }
        public uint TextStart { get; }
        public uint TextEnd { get; }

        private NdevicePe(byte[] bytes, bool plus, ulong imageBase, List<NdeviceSection> sections, List<NdeviceImport> imports)
        {
            Bytes = bytes; Pe32Plus = plus; ImageBase = imageBase; Sections = sections; Imports = imports;
            var text = sections.First(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
            TextStart = text.VirtualAddress;
            TextEnd = text.VirtualAddress + Math.Min(text.VirtualSize == 0 ? text.RawSize : text.VirtualSize, text.RawSize);
        }

        public static NdevicePe Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') throw new InvalidDataException("Not a PE image.");
            var pe = I32(bytes, 0x3C); Ensure(bytes, pe, 24);
            var sectionCount = U16(bytes, pe + 6); var optionalSize = U16(bytes, pe + 20); var optional = pe + 24;
            var magic = U16(bytes, optional); var plus = magic == 0x20B;
            if (!plus && magic != 0x10B) throw new InvalidDataException("Unsupported PE optional header.");
            ulong imageBase = plus ? U64(bytes, optional + 24) : U32(bytes, optional + 28);
            var table = optional + optionalSize; var sections = new List<NdeviceSection>();
            for (var i = 0; i < sectionCount; i++)
            {
                var off = table + i * 40; Ensure(bytes, off, 40);
                sections.Add(new NdeviceSection(Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'), U32(bytes, off + 8), U32(bytes, off + 12), U32(bytes, off + 16), U32(bytes, off + 20)));
            }
            var temp = new NdevicePe(bytes, plus, imageBase, sections, []);
            return new NdevicePe(bytes, plus, imageBase, sections, temp.ParseImports(optional));
        }

        public int RvaToOffset(uint rva)
        {
            var s = Sections.FirstOrDefault(x => x.Contains(rva)) ?? throw new InvalidDataException($"RVA 0x{rva:X8} outside sections.");
            return checked((int)(s.RawPointer + (rva - s.VirtualAddress)));
        }

        public string? ResolveImport(Instruction ins)
        {
            if (ins.Mnemonic != Mnemonic.Call || ins.Op0Kind != OpKind.Memory) return null;
            var address = ins.MemoryDisplacement64;
            var import = Imports.FirstOrDefault(x => ImageBase + x.IatRva == address);
            return import is null ? null : import.Dll + "!" + import.Name;
        }

        private List<NdeviceImport> ParseImports(int optional)
        {
            var dataDirectory = optional + (Pe32Plus ? 112 : 96);
            var importRva = U32(Bytes, dataDirectory + 8);
            if (importRva == 0) return [];
            var result = new List<NdeviceImport>();
            var descriptor = RvaToOffset(importRva);
            for (var d = 0; d < 512; d++, descriptor += 20)
            {
                Ensure(Bytes, descriptor, 20);
                var originalThunk = U32(Bytes, descriptor);
                var nameRva = U32(Bytes, descriptor + 12);
                var firstThunk = U32(Bytes, descriptor + 16);
                if (originalThunk == 0 && nameRva == 0 && firstThunk == 0) break;
                var dll = ReadAsciiZ(RvaToOffset(nameRva), 260);
                var lookupRva = originalThunk != 0 ? originalThunk : firstThunk;
                var step = Pe32Plus ? 8 : 4;
                for (var index = 0; index < 4096; index++)
                {
                    var thunkOff = RvaToOffset(lookupRva + checked((uint)(index * step)));
                    ulong thunk = Pe32Plus ? U64(Bytes, thunkOff) : U32(Bytes, thunkOff);
                    if (thunk == 0) break;
                    var ordinalFlag = Pe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                    string name;
                    if ((thunk & ordinalFlag) != 0) name = "#" + (thunk & 0xFFFF);
                    else name = ReadAsciiZ(RvaToOffset(checked((uint)thunk)) + 2, 512);
                    result.Add(new NdeviceImport(dll, name, firstThunk + checked((uint)(index * step))));
                }
            }
            return result;
        }

        private string ReadAsciiZ(int offset, int max)
        {
            var end = offset;
            while (end < Bytes.Length && end - offset < max && Bytes[end] != 0) end++;
            return Encoding.ASCII.GetString(Bytes, offset, end - offset);
        }
    }

    private static ushort U16(byte[] b, int o) { Ensure(b, o, 2); return BitConverter.ToUInt16(b, o); }
    private static uint U32(byte[] b, int o) { Ensure(b, o, 4); return BitConverter.ToUInt32(b, o); }
    private static ulong U64(byte[] b, int o) { Ensure(b, o, 8); return BitConverter.ToUInt64(b, o); }
    private static int I32(byte[] b, int o) { Ensure(b, o, 4); return BitConverter.ToInt32(b, o); }
    private static void Ensure(byte[] b, int o, int n) { if (o < 0 || n < 0 || o + n > b.Length) throw new InvalidDataException("PE bounds check failed."); }
}
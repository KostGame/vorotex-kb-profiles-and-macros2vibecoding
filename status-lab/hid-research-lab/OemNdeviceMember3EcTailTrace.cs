using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemNdeviceMember3EcTailSide(
    string Executable,
    uint HelperRva,
    uint[] SourceLoadRvas,
    uint[] DirectCopyRvas,
    uint[] StackSpillRvas,
    uint[] StackReloadRvas,
    string[] NestedCalls,
    string[] TailSteps,
    bool SourceLoadProven,
    bool TransferProven,
    string Fingerprint,
    string[] Notes);

internal sealed record OemNdeviceMember3EcTailReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemNdeviceMember3EcTailSide A,
    OemNdeviceMember3EcTailSide B,
    bool SourceLoadCorrespondence,
    bool TransferCorrespondence,
    bool BothTransferProven,
    string[] Evidence,
    string[] Notes);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    private const long Member3EcTailOffset = 0x3EC;

    public static OemNdeviceMember3EcTailReport AnalyzeMember3EcTail(string exeA, string exeB)
    {
        var aggregate = Analyze(exeA, exeB);
        var a = TraceMember3EcSide(Path.GetFullPath(exeA), aggregate.A);
        var b = TraceMember3EcSide(Path.GetFullPath(exeB), aggregate.B);

        var loadCorrespondence = a.SourceLoadProven && b.SourceLoadProven;
        var transferCorrespondence = a.TransferProven == b.TransferProven &&
                                     string.Equals(a.Fingerprint, b.Fingerprint, StringComparison.Ordinal);
        var both = a.TransferProven && b.TransferProven;
        var verdict = both ? "MEMBER3EC_TRANSFER_COMPLETE" :
                      a.TransferProven || b.TransferProven ? "MEMBER3EC_TRANSFER_PARTIAL" :
                      loadCorrespondence ? "MEMBER3EC_LOAD_TAIL_TRACED" :
                      "MEMBER3EC_TRANSFER_UNRESOLVED";

        var evidence = new List<string>();
        if (loadCorrespondence)
            evidence.Add("Both OEM aggregate helpers explicitly load source +0x3EC and expose a bounded post-load instruction tail.");
        if (a.StackSpillRvas.Length > 0 || b.StackSpillRvas.Length > 0)
            evidence.Add("The tail tracer preserved source +0x3EC provenance through bounded stack/local spill slots rather than dropping the value at the first spill.");
        if (both)
            evidence.Add("Both OEM helpers explicitly transfer the source +0x3EC value/address to destination +0x3EC through a resolved direct or nested copy edge.");

        return new OemNdeviceMember3EcTailReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static continuation trace after the aggregate helper loads Ndevice source member +0x3EC",
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
            loadCorrespondence,
            transferCorrespondence,
            both,
            evidence.ToArray(),
            [
                "A source +0x3EC load is evidence only; it is not promoted to transfer proof without an explicit destination +0x3EC edge.",
                "Register aliases and bounded EBP/ESP stack-local spills are tracked conservatively; unresolved calls kill volatile register-value provenance.",
                "Nested helper calls are accepted only when destination +0x3EC and source +0x3EC pointers are both explicit and the nested helper has resolved copy semantics.",
                "The tail window is diagnostic context only and cannot itself promote the verdict.",
                "All analysis reads executable bytes only; no OEM code is executed and no device handle is opened."
            ]);
    }

    private static OemNdeviceMember3EcTailSide TraceMember3EcSide(string exe, OemNdeviceAggregateSide aggregate)
    {
        if (aggregate.Helper is null)
        {
            return new OemNdeviceMember3EcTailSide(
                Path.GetFileName(exe), 0, [], [], [], [], [], [], false, false, "NO_HELPER",
                ["Aggregate helper RVA was not recovered by the preceding trace."]);
        }

        var pe = NdevicePe.Parse(exe);
        var entry = aggregate.Helper.EntryRva;
        var body = DecodeRange(pe, entry, Math.Min(pe.TextEnd, entry + 0x1800)).Take(1600).ToList();
        var ptr = new Dictionary<Register, PtrTag> { [Register.ECX] = new(PtrKind.Dest, 0) };
        var values = new Dictionary<Register, ValueTag>();
        var stackValues = new Dictionary<long, ValueTag>();
        var stackPointers = new Dictionary<long, PtrTag>();
        var recentPtrPush = new Queue<PtrTag>();
        var loads = new List<uint>();
        var copies = new List<uint>();
        var spills = new List<uint>();
        var reloads = new List<uint>();
        var nested = new List<string>();
        var tail = new List<string>();
        var events = new List<string> { "DEST_THIS" };
        var notes = new List<string>();
        var spDepth = 0L;
        long? ebpDepth = null;
        var captureUntil = -1;
        var sourceRecovered = false;
        var transfer = false;

        for (var index = 0; index < body.Count; index++)
        {
            var item = body[index];
            var ins = item.Instruction;
            var text = $"0x{item.Rva:X8} {item.Text}";
            string? annotation = null;

            if (ins.Mnemonic == Mnemonic.Ret)
            {
                if (index <= captureUntil) tail.Add(text + " ; RET");
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
                    if (values.TryGetValue(src, out var vtag))
                    {
                        values[dst] = vtag;
                        if (vtag.Kind == PtrKind.Source && vtag.Offset == Member3EcTailOffset)
                            annotation = $"carry Source+0x{Member3EcTailOffset:X} value into {dst}";
                    }
                    else values.Remove(dst);
                }
                else if (ins.Op1Kind == OpKind.Memory)
                {
                    if (IsSourceArgument(ins, spDepth, ebpDepth))
                    {
                        ptr[dst] = new PtrTag(PtrKind.Source, 0);
                        values.Remove(dst);
                        sourceRecovered = true;
                        events.Add("SRC_ARG");
                    }
                    else if (TryTaggedMemory(ptr, ins, out var mem))
                    {
                        values[dst] = new ValueTag(mem.Kind, mem.Offset);
                        ptr.Remove(dst);
                        if (mem.Kind == PtrKind.Source && mem.Offset == Member3EcTailOffset)
                        {
                            loads.Add(item.Rva);
                            captureUntil = Math.Max(captureUntil, index + 32);
                            events.Add("SRC_LOAD_3EC");
                            annotation = "load Source+0x3EC";
                        }
                    }
                    else if (TryMemberStackSlot(ins, spDepth, ebpDepth, out var slot))
                    {
                        if (stackPointers.TryGetValue(slot, out var savedPtr)) ptr[dst] = savedPtr; else ptr.Remove(dst);
                        if (stackValues.TryGetValue(slot, out var savedValue))
                        {
                            values[dst] = savedValue;
                            if (savedValue.Kind == PtrKind.Source && savedValue.Offset == Member3EcTailOffset)
                            {
                                reloads.Add(item.Rva);
                                events.Add("RELOAD_3EC");
                                annotation = $"reload Source+0x3EC from frame slot {slot:+#;-#;0}";
                            }
                        }
                        else values.Remove(dst);
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
                    if (mem.Offset == Member3EcTailOffset)
                        annotation = $"address {mem.Kind}+0x{Member3EcTailOffset:X}";
                }
                else if (TryMemberStackSlot(ins, spDepth, ebpDepth, out var slot) && stackPointers.TryGetValue(slot, out var savedPtr))
                {
                    ptr[dst] = savedPtr;
                    values.Remove(dst);
                }
                else ptr.Remove(dst);
            }

            if (IsMemoryWrite(ins) && ins.Op0Kind == OpKind.Memory && ins.Op1Kind == OpKind.Register)
            {
                var srcReg = Normalize(ins.Op1Register);
                if (TryMemberStackSlot(ins, spDepth, ebpDepth, out var stackSlot))
                {
                    if (ptr.TryGetValue(srcReg, out var savedPtr)) stackPointers[stackSlot] = savedPtr;
                    else stackPointers.Remove(stackSlot);
                    if (values.TryGetValue(srcReg, out var savedValue))
                    {
                        stackValues[stackSlot] = savedValue;
                        if (savedValue.Kind == PtrKind.Source && savedValue.Offset == Member3EcTailOffset)
                        {
                            spills.Add(item.Rva);
                            events.Add("SPILL_3EC");
                            annotation = $"spill Source+0x3EC into frame slot {stackSlot:+#;-#;0}";
                        }
                    }
                    else stackValues.Remove(stackSlot);
                }

                if (TryTaggedMemory(ptr, ins, out var destMem) && destMem.Kind == PtrKind.Dest &&
                    destMem.Offset == Member3EcTailOffset &&
                    values.TryGetValue(srcReg, out var srcValue) && srcValue.Kind == PtrKind.Source && srcValue.Offset == Member3EcTailOffset)
                {
                    copies.Add(item.Rva);
                    transfer = true;
                    events.Add("MOV_3EC");
                    annotation = "PROVEN Source+0x3EC -> Dest+0x3EC";
                }
            }

            if (ins.Mnemonic == Mnemonic.Push)
            {
                if (ins.Op0Kind == OpKind.Register && ptr.TryGetValue(Normalize(ins.Op0Register), out var pushed))
                {
                    recentPtrPush.Enqueue(pushed);
                    while (recentPtrPush.Count > 8) recentPtrPush.Dequeue();
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
                var src = recentPtrPush.LastOrDefault(x => x.Kind == PtrKind.Source);
                var hasSrc = recentPtrPush.Any(x => x.Kind == PtrKind.Source);
                var symbol = pe.ResolveImport(ins);
                var direct = IsDirectBranch(ins) ? checked((uint?)ins.NearBranchTarget) : null;
                if (dest is not null && hasSrc && dest.Value.Offset == Member3EcTailOffset && src.Offset == Member3EcTailOffset)
                {
                    var accepted = false;
                    if (LooksCopySymbol(symbol)) accepted = true;
                    else if (direct is not null && direct >= pe.TextStart && direct < pe.TextEnd)
                        accepted = NestedHelperCopiesArgument(pe, direct.Value, 1);
                    nested.Add($"0x{item.Rva:X8} dest+0x3EC src+0x3EC target={(symbol ?? (direct is null ? "unresolved" : $"0x{direct.Value:X8}"))} accepted={accepted}");
                    annotation = $"nested +0x3EC copy accepted={accepted}";
                    if (accepted)
                    {
                        transfer = true;
                        events.Add("CALL_3EC");
                    }
                }
                recentPtrPush.Clear();
                values.Remove(Register.EAX);
                values.Remove(Register.ECX);
                values.Remove(Register.EDX);
                ptr.Remove(Register.EAX);
                ptr.Remove(Register.ECX);
                ptr.Remove(Register.EDX);
            }

            if (index <= captureUntil)
                tail.Add(annotation is null ? text : text + " ; " + annotation);
        }

        if (!sourceRecovered) notes.Add("Source object argument was not recovered conservatively from the aggregate helper frame.");
        if (loads.Count == 0) notes.Add("No explicit Source+0x3EC load was recovered.");
        if (!transfer) notes.Add("No explicit Source+0x3EC -> Dest+0x3EC transfer was proven; inspect TailSteps for the unresolved continuation.");
        if (tail.Count == 0) notes.Add("No post-load tail window was emitted because the source +0x3EC load was not found.");

        return new OemNdeviceMember3EcTailSide(
            Path.GetFileName(exe),
            entry,
            loads.Distinct().ToArray(),
            copies.Distinct().ToArray(),
            spills.Distinct().ToArray(),
            reloads.Distinct().ToArray(),
            nested.ToArray(),
            tail.ToArray(),
            loads.Count > 0,
            transfer,
            string.Join('>', events),
            notes.ToArray());
    }

    private static bool TryMemberStackSlot(Instruction ins, long spDepth, long? ebpDepth, out long slot)
    {
        slot = 0;
        var b = Normalize(ins.MemoryBase);
        var d = SignedDisp(ins);
        if (b == Register.ESP)
        {
            slot = d - spDepth;
            return true;
        }
        if (b == Register.EBP && ebpDepth is not null)
        {
            slot = d - ebpDepth.Value;
            return true;
        }
        return false;
    }

    public static string Member3EcTailToText(OemNdeviceMember3EcTailReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - Ndevice Member +0x3EC Tail Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, process attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Source-load correspondence: {report.SourceLoadCorrespondence}");
        sb.AppendLine($"Transfer correspondence: {report.TransferCorrespondence}");
        sb.AppendLine($"Both transfer proven: {report.BothTransferProven}");
        sb.AppendLine();
        AppendMember3EcSide(sb, "A", report.A);
        AppendMember3EcSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var e in report.Evidence) sb.AppendLine("  - " + e);
        sb.AppendLine();
        foreach (var n in report.Notes) sb.AppendLine("NOTE: " + n);
        return sb.ToString();
    }

    private static void AppendMember3EcSide(StringBuilder sb, string label, OemNdeviceMember3EcTailSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}; helper=0x{side.HelperRva:X8}; load={side.SourceLoadProven}; transfer={side.TransferProven}");
        sb.AppendLine($"  source loads: {Member3EcHex(side.SourceLoadRvas)}");
        sb.AppendLine($"  direct copies: {Member3EcHex(side.DirectCopyRvas)}");
        sb.AppendLine($"  stack spills: {Member3EcHex(side.StackSpillRvas)}");
        sb.AppendLine($"  stack reloads: {Member3EcHex(side.StackReloadRvas)}");
        sb.AppendLine($"  fingerprint: {side.Fingerprint}");
        foreach (var call in side.NestedCalls) sb.AppendLine("  nested: " + call);
        sb.AppendLine("  tail:");
        foreach (var step in side.TailSteps) sb.AppendLine("    " + step);
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static string Member3EcHex(IEnumerable<uint> values)
    {
        var a = values.ToArray();
        return a.Length == 0 ? "none" : string.Join(", ", a.Select(x => $"0x{x:X8}"));
    }
}

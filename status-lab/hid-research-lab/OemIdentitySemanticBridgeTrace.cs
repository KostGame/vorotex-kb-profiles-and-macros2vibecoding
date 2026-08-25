using System.Buffers.Binary;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemSemanticInsn(uint Rva, long ProductDelta, string Bytes, string Text, string[] Tags);
internal sealed record OemRawXref(string Token, uint TokenRva, uint RawRva, bool InstructionStart, string Decision);
internal sealed record OemAlignedXref(
    string Token,
    string Encoding,
    uint TokenRva,
    string TokenVa,
    uint InstructionRva,
    string Bytes,
    string Text,
    int OperandIndex,
    string OperandKind,
    string[] CandidateMembers,
    bool MapsExpectedMember,
    List<OemSemanticInsn> Neighborhood);
internal sealed record OemSemanticCall(
    uint CallRva,
    long ProductDelta,
    string Kind,
    uint? DirectTargetRva,
    string? IatVa,
    string? Dll,
    string? Symbol,
    string Text);
internal sealed record OemSemanticHelper(
    uint? EntryRva,
    List<OemSemanticInsn> Instructions,
    List<OemSemanticCall> Calls,
    string[] StringConstants,
    string[] MemberOffsets,
    string Fingerprint);
internal sealed record OemSemanticSide(
    string Executable,
    string Machine,
    string ImageBase,
    uint ProductStringCallRva,
    string? ProductBufferSignature,
    uint? GuardRva,
    uint? MemberAnchorRva,
    uint? FlagsProducerRva,
    List<OemAlignedXref> DevCmpStrAlignedXrefs,
    List<OemAlignedXref> DevNameAlignedXrefs,
    List<OemRawXref> RawXrefs,
    bool DevCmpStrMapsTo3Ec,
    bool DevNameMapsTo20,
    List<OemSemanticCall> ProductToGuardCalls,
    OemSemanticCall? ProductObjectCopyCall,
    OemSemanticCall? Member20HelperCall,
    OemSemanticCall? RecordValueCall,
    OemSemanticCall? BooleanCompareCall,
    OemSemanticHelper Member20Helper,
    bool BooleanReturnFeedsFlags,
    bool CompareSymbolLooksSemantic,
    string StructuralFingerprint,
    List<string> Notes);
internal sealed record OemIdentitySemanticBridgeReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemSemanticSide A,
    OemSemanticSide B,
    bool StructuralCorrespondence,
    bool CompareSymbolCorrespondence,
    List<string> Evidence,
    List<string> Notes);

internal static class OemIdentitySemanticBridgeAnalyzer
{
    private const ulong DevCmpMember = 0x3EC;
    private const ulong DevNameMember = 0x20;

    public static OemIdentitySemanticBridgeReport Analyze(string exeA, string exeB)
    {
        var gate = OemIdentityGateTraceAnalyzer.Analyze(exeA, exeB);
        var guarded = OemDevCmpGuardedBlockAnalyzer.Analyze(exeA, exeB);
        var a = AnalyzeSide(Path.GetFullPath(exeA), gate.A, guarded.A);
        var b = AnalyzeSide(Path.GetFullPath(exeB), gate.B, guarded.B);

        var structural = guarded.StructuralCorrespondence &&
                         string.Equals(a.StructuralFingerprint, b.StructuralFingerprint, StringComparison.Ordinal);
        var symbolA = SymbolFamily(a.BooleanCompareCall);
        var symbolB = SymbolFamily(b.BooleanCompareCall);
        var symbolCorrespondence = symbolA.Length > 0 && string.Equals(symbolA, symbolB, StringComparison.OrdinalIgnoreCase);

        var proven = structural && symbolCorrespondence &&
                     a.DevCmpStrMapsTo3Ec && b.DevCmpStrMapsTo3Ec &&
                     a.DevNameMapsTo20 && b.DevNameMapsTo20 &&
                     a.BooleanReturnFeedsFlags && b.BooleanReturnFeedsFlags &&
                     a.CompareSymbolLooksSemantic && b.CompareSymbolLooksSemantic;
        var helperResolved = structural && symbolCorrespondence &&
                             a.Member20Helper.EntryRva is not null && b.Member20Helper.EntryRva is not null &&
                             a.BooleanReturnFeedsFlags && b.BooleanReturnFeedsFlags;
        var alignedRecovered = a.DevCmpStrAlignedXrefs.Count > 0 && b.DevCmpStrAlignedXrefs.Count > 0 &&
                               a.DevNameAlignedXrefs.Count > 0 && b.DevNameAlignedXrefs.Count > 0;

        var verdict = proven ? "DEVNAME_PRODUCTSTRING_COMPARE_PROVEN" :
                      helperResolved ? "COMPARE_HELPER_SEMANTICS_RESOLVED" :
                      alignedRecovered ? "ALIGNED_XREFS_RECOVERED" :
                      "SEMANTIC_BRIDGE_UNRESOLVED";

        var evidence = new List<string>();
        if (alignedRecovered) evidence.Add("Instruction-aligned DevCmpStr and DevName operand references were recovered on both OEM sides; raw byte hits inside instructions are excluded.");
        if (a.DevCmpStrMapsTo3Ec && b.DevCmpStrMapsTo3Ec) evidence.Add("Aligned DevCmpStr neighborhoods reference runtime member +0x3EC on both sides.");
        if (a.DevNameMapsTo20 && b.DevNameMapsTo20) evidence.Add("Aligned DevName neighborhoods reference runtime member +0x20 on both sides.");
        if (a.Member20Helper.EntryRva is not null && b.Member20Helper.EntryRva is not null) evidence.Add($"The direct helper receiving member +0x20 was decoded on both sides (A=0x{a.Member20Helper.EntryRva:X8}, B=0x{b.Member20Helper.EntryRva:X8}).");
        if (a.BooleanReturnFeedsFlags && b.BooleanReturnFeedsFlags) evidence.Add("The call immediately feeding `test al,al` was recovered on both sides.");
        if (symbolCorrespondence) evidence.Add($"The boolean call resolves to the same normalized import family on both sides: {symbolA}.");
        if (a.CompareSymbolLooksSemantic && b.CompareSymbolLooksSemantic) evidence.Add("Resolved boolean-call symbols contain explicit compare/equality semantics on both sides.");

        return new OemIdentitySemanticBridgeReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "static read-only instruction-aligned semantic bridge from Ndevice identity fields through ProductString to the DevCmpStr guarded comparison",
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
            symbolCorrespondence,
            evidence,
            [
                "Instruction-aligned xrefs are accepted only when a decoded operand equals the exact token VA/RVA; raw byte matches inside instructions are never code anchors.",
                "COMPARE_HELPER_SEMANTICS_RESOLVED does not prove which Ndevice field populated a runtime member unless the aligned parser trace establishes that mapping.",
                "DEVNAME_PRODUCTSTRING_COMPARE_PROVEN requires both semantic member mappings plus an explicit compare/equality symbol feeding the guarded boolean selection on both OEM sides.",
                "All analysis reads executable/resource bytes only; no process or keyboard device is opened."
            ]);
    }

    public static string ToText(OemIdentitySemanticBridgeReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - OEM Identity Semantic Bridge Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, reports/writes, process launch/attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Structural correspondence: {report.StructuralCorrespondence}");
        sb.AppendLine($"Compare symbol correspondence: {report.CompareSymbolCorrespondence}");
        sb.AppendLine();
        AppendSide(sb, "A", report.A);
        AppendSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        sb.AppendLine();
        foreach (var note in report.Notes) sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendSide(StringBuilder sb, string label, OemSemanticSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  ProductString=0x{side.ProductStringCallRva:X8}; buffer={side.ProductBufferSignature ?? "unresolved"}");
        sb.AppendLine($"  guard={Hex(side.GuardRva)}; member20={Hex(side.MemberAnchorRva)}; flags={Hex(side.FlagsProducerRva)}");
        sb.AppendLine($"  aligned DevCmpStr={side.DevCmpStrAlignedXrefs.Count}; maps +0x3EC={side.DevCmpStrMapsTo3Ec}");
        sb.AppendLine($"  aligned DevName={side.DevNameAlignedXrefs.Count}; maps +0x20={side.DevNameMapsTo20}");
        AppendXrefs(sb, side.DevCmpStrAlignedXrefs);
        AppendXrefs(sb, side.DevNameAlignedXrefs);
        sb.AppendLine("  Raw token-VA byte hits:");
        foreach (var raw in side.RawXrefs.Take(40)) sb.AppendLine($"    {raw.Token} raw=0x{raw.RawRva:X8} instructionStart={raw.InstructionStart}: {raw.Decision}");
        sb.AppendLine("  ProductString -> guard calls:");
        foreach (var call in side.ProductToGuardCalls) sb.AppendLine("    " + CallText(call));
        sb.AppendLine($"  product-copy: {(side.ProductObjectCopyCall is null ? "unresolved" : CallText(side.ProductObjectCopyCall))}");
        sb.AppendLine($"  member20-helper-call: {(side.Member20HelperCall is null ? "unresolved" : CallText(side.Member20HelperCall))}");
        sb.AppendLine($"  record-value: {(side.RecordValueCall is null ? "unresolved" : CallText(side.RecordValueCall))}");
        sb.AppendLine($"  boolean-call: {(side.BooleanCompareCall is null ? "unresolved" : CallText(side.BooleanCompareCall))}");
        sb.AppendLine($"  booleanReturnFeedsFlags={side.BooleanReturnFeedsFlags}; compareSymbolLooksSemantic={side.CompareSymbolLooksSemantic}");
        sb.AppendLine($"  member +0x20 helper entry={Hex(side.Member20Helper.EntryRva)}");
        sb.AppendLine($"    strings={(side.Member20Helper.StringConstants.Length == 0 ? "none" : string.Join(" | ", side.Member20Helper.StringConstants))}");
        sb.AppendLine($"    members={(side.Member20Helper.MemberOffsets.Length == 0 ? "none" : string.Join(", ", side.Member20Helper.MemberOffsets))}");
        foreach (var call in side.Member20Helper.Calls.Take(30)) sb.AppendLine("    helper-call " + CallText(call));
        sb.AppendLine($"    fingerprint={side.Member20Helper.Fingerprint}");
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static void AppendXrefs(StringBuilder sb, List<OemAlignedXref> xrefs)
    {
        foreach (var x in xrefs.Take(12))
        {
            sb.AppendLine($"    {x.Token}/{x.Encoding} tokenRVA=0x{x.TokenRva:X8} instruction=0x{x.InstructionRva:X8} op{x.OperandIndex}:{x.OperandKind} mapsExpected={x.MapsExpectedMember}");
            sb.AppendLine($"      {x.Bytes} {x.Text}");
            if (x.CandidateMembers.Length > 0) sb.AppendLine($"      members={string.Join(", ", x.CandidateMembers)}");
        }
    }

    private static string CallText(OemSemanticCall call)
    {
        var target = call.Symbol is not null ? $"{call.Dll}!{call.Symbol}" :
                     call.DirectTargetRva is not null ? $"0x{call.DirectTargetRva:X8}" :
                     call.IatVa ?? "unresolved";
        return $"0x{call.CallRva:X8} delta={call.ProductDelta:+#;-#;0} {call.Kind} {target} :: {call.Text}";
    }

    private static OemSemanticSide AnalyzeSide(string exe, OemIdentityGateSide gate, OemGuardedBlockSide guarded)
    {
        var pe = SemanticPe.Parse(exe);
        var text = DecodeText(pe);
        var starts = text.Select(x => x.Rva).ToHashSet();
        var notes = new List<string>();

        var cmpRefs = FindAlignedTokenXrefs(pe, text, gate, "DevCmpStr", guarded.ProductStringCallRva, DevCmpMember);
        var nameRefs = FindAlignedTokenXrefs(pe, text, gate, "DevName", guarded.ProductStringCallRva, DevNameMember);
        var raw = BuildRawXrefs(pe, starts, gate, "DevCmpStr")
            .Concat(BuildRawXrefs(pe, starts, gate, "DevName"))
            .OrderBy(x => x.RawRva).ToList();

        var productWindow = DecodeForward(pe, guarded.ProductStringCallRva, 0x260, guarded.ProductStringCallRva);
        var calls = productWindow.Where(x => x.Instruction.Mnemonic == Mnemonic.Call)
            .Select(x => ResolveCall(pe, x, guarded.ProductStringCallRva)).ToList();
        OemSemanticCall? AtDelta(long delta) => calls.FirstOrDefault(x => x.ProductDelta == delta);

        var productCopy = AtDelta(73);
        var memberHelperCall = AtDelta(93);
        var recordValue = AtDelta(120);
        var booleanCall = AtDelta(133);
        var helper = memberHelperCall?.DirectTargetRva is null ? EmptyHelper() :
            TraceHelper(pe, memberHelperCall.DirectTargetRva.Value, guarded.ProductStringCallRva);

        var flagsIndex = guarded.FlagsProducerRva is null ? -1 : productWindow.FindIndex(x => x.Rva == guarded.FlagsProducerRva.Value);
        var booleanFeeds = false;
        if (flagsIndex > 0)
        {
            for (var i = flagsIndex - 1; i >= Math.Max(0, flagsIndex - 4); i--)
            {
                if (productWindow[i].Instruction.Mnemonic != Mnemonic.Call) continue;
                booleanFeeds = productWindow[i].Rva == booleanCall?.CallRva;
                break;
            }
        }

        if (cmpRefs.Count == 0) notes.Add("No instruction-aligned DevCmpStr operand reference was recovered; old raw-byte xrefs are not valid code anchors.");
        if (nameRefs.Count == 0) notes.Add("No instruction-aligned DevName operand reference was recovered; old raw-byte xrefs are not valid code anchors.");
        if (booleanCall?.Symbol is null) notes.Add("The call feeding `test al,al` did not resolve to a named PE import; comparison semantics remain structural.");
        if (helper.EntryRva is null) notes.Add("The direct helper receiving member +0x20 was not recovered at ProductString-relative +93.");

        return new OemSemanticSide(
            Path.GetFileName(exe),
            $"0x{pe.Machine:X4}",
            $"0x{pe.ImageBase:X}",
            guarded.ProductStringCallRva,
            guarded.ProductBufferSignature,
            guarded.GuardRva,
            guarded.MemberAnchorRva,
            guarded.FlagsProducerRva,
            cmpRefs,
            nameRefs,
            raw,
            cmpRefs.Any(x => x.MapsExpectedMember),
            nameRefs.Any(x => x.MapsExpectedMember),
            calls.Where(x => x.ProductDelta >= 0 && (guarded.GuardRva is null || x.CallRva < guarded.GuardRva.Value)).ToList(),
            productCopy,
            memberHelperCall,
            recordValue,
            booleanCall,
            helper,
            booleanFeeds,
            LooksCompareSymbol(booleanCall?.Symbol),
            Fingerprint(productWindow, guarded.GuardRva, guarded.FlagsProducerRva),
            notes);
    }

    private static List<OemAlignedXref> FindAlignedTokenXrefs(
        SemanticPe pe,
        List<SemanticDecoded> text,
        OemIdentityGateSide gate,
        string token,
        uint product,
        ulong expectedMember)
    {
        var result = new List<OemAlignedXref>();
        foreach (var tokenRef in gate.TokenRefs.Where(x => x.Token.Equals(token, StringComparison.OrdinalIgnoreCase) && x.Rva is not null))
        {
            var tokenRva = tokenRef.Rva!.Value;
            var tokenVa = pe.ImageBase + tokenRva;
            for (var index = 0; index < text.Count; index++)
            {
                var item = text[index];
                for (var op = 0; op < item.Instruction.OpCount; op++)
                {
                    if (!OperandReferences(item.Instruction, op, tokenVa, tokenRva, out var kind)) continue;
                    var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var neighborhood = new List<OemSemanticInsn>();
                    var end = Math.Min(text.Count, index + 220);
                    for (var i = Math.Max(0, index - 8); i < end; i++)
                    {
                        var di = text[i];
                        foreach (var member in MemberOffsets(di.Instruction)) members.Add(member);
                        if (i < index + 80) neighborhood.Add(ToOutput(di, product, di.Rva == item.Rva ? [token + "_aligned_xref"] : []));
                        if (i > index && di.Instruction.Mnemonic == Mnemonic.Ret) break;
                    }
                    var expected = $"+0x{expectedMember:X}";
                    result.Add(new OemAlignedXref(
                        token, tokenRef.Encoding, tokenRva, $"0x{tokenVa:X}", item.Rva, item.Bytes, item.Text,
                        op, kind, members.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), members.Contains(expected), neighborhood));
                }
            }
        }
        return result.GroupBy(x => (x.TokenRva, x.InstructionRva, x.OperandIndex)).Select(g => g.First())
            .OrderBy(x => Math.Abs((long)x.InstructionRva - product)).Take(24).ToList();
    }

    private static IEnumerable<OemRawXref> BuildRawXrefs(SemanticPe pe, HashSet<uint> starts, OemIdentityGateSide gate, string token)
    {
        foreach (var tokenRef in gate.TokenRefs.Where(x => x.Token.Equals(token, StringComparison.OrdinalIgnoreCase) && x.Rva is not null))
        {
            var targetVa = checked((uint)(pe.ImageBase + tokenRef.Rva!.Value));
            var needle = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(needle, targetVa);
            foreach (var section in pe.Sections.Where(s => s.Name.Equals(".text", StringComparison.OrdinalIgnoreCase)))
            {
                var start = checked((int)section.RawPointer);
                var end = Math.Min(pe.Bytes.Length, start + checked((int)section.RawSize));
                for (var off = start; off <= end - 4; off++)
                {
                    if (!pe.Bytes.AsSpan(off, 4).SequenceEqual(needle)) continue;
                    var rva = section.VirtualAddress + checked((uint)off - section.RawPointer);
                    var aligned = starts.Contains(rva);
                    yield return new OemRawXref(token, tokenRef.Rva.Value, rva, aligned,
                        aligned ? "raw hit begins at an instruction; operand validation is still required" :
                                  "REJECTED: raw hit is inside a decoded instruction/data fragment");
                }
            }
        }
    }

    private static bool OperandReferences(in Instruction ins, int op, ulong targetVa, uint targetRva, out string kind)
    {
        kind = string.Empty;
        if (ins.GetOpKind(op) == OpKind.Memory)
        {
            var value = ins.MemoryDisplacement64;
            if (value == targetVa || value == targetRva) { kind = "memory"; return true; }
            return false;
        }
        var imm = Immediate(ins, op);
        if (imm is not null && (imm.Value == targetVa || imm.Value == targetRva)) { kind = "immediate"; return true; }
        return false;
    }

    private static OemSemanticCall ResolveCall(SemanticPe pe, SemanticDecoded item, uint product)
    {
        var ins = item.Instruction;
        if (ins.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            return new OemSemanticCall(item.Rva, (long)item.Rva - product, "direct", checked((uint)ins.NearBranchTarget), null, null, null, item.Text);
        if (ins.Op0Kind == OpKind.Memory)
        {
            var address = ins.MemoryDisplacement64;
            var import = pe.Imports.FirstOrDefault(x => pe.ImageBase + x.IatRva == address);
            return new OemSemanticCall(item.Rva, (long)item.Rva - product, "iat", null, $"0x{address:X}", import?.Dll, import?.Name, item.Text);
        }
        return new OemSemanticCall(item.Rva, (long)item.Rva - product, "indirect", null, null, null, null, item.Text);
    }

    private static OemSemanticHelper TraceHelper(SemanticPe pe, uint target, uint product)
    {
        List<SemanticDecoded> decoded;
        try { decoded = DecodeForward(pe, target, 0x700, product); }
        catch { return EmptyHelper(); }
        var instructions = new List<OemSemanticInsn>();
        var calls = new List<OemSemanticCall>();
        var strings = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in decoded.Take(220))
        {
            instructions.Add(ToOutput(item, product, item.Rva == target ? ["member20_helper_entry"] : []));
            foreach (var member in MemberOffsets(item.Instruction)) members.Add(member);
            foreach (var address in AddressOperands(item.Instruction))
            {
                var text = pe.TryReadString(address);
                if (!string.IsNullOrWhiteSpace(text)) strings.Add(text!);
            }
            if (item.Instruction.Mnemonic == Mnemonic.Call) calls.Add(ResolveCall(pe, item, product));
            if (item.Instruction.Mnemonic == Mnemonic.Ret) break;
        }
        var fingerprint = string.Join('>', instructions.Take(120).Select(x => x.Text.Split(' ', 2)[0].ToUpperInvariant()));
        return new OemSemanticHelper(target, instructions, calls, strings.Take(40).ToArray(), members.OrderBy(x => x).ToArray(), fingerprint);
    }

    private static OemSemanticHelper EmptyHelper() => new(null, [], [], [], [], string.Empty);

    private static IEnumerable<ulong> AddressOperands(in Instruction ins)
    {
        for (var op = 0; op < ins.OpCount; op++)
        {
            if (ins.GetOpKind(op) == OpKind.Memory)
            {
                if (ins.MemoryBase == Register.None && ins.MemoryIndex == Register.None && ins.MemoryDisplacement64 != 0) yield return ins.MemoryDisplacement64;
                continue;
            }
            var imm = Immediate(ins, op);
            if (imm is not null && imm.Value >= 0x10000) yield return imm.Value;
        }
    }

    private static IEnumerable<string> MemberOffsets(in Instruction ins)
    {
        for (var op = 0; op < ins.OpCount; op++)
        {
            if (ins.GetOpKind(op) != OpKind.Memory || ins.MemoryIndex != Register.None) continue;
            if (ins.MemoryBase is Register.None or Register.EBP or Register.ESP or Register.RBP or Register.RSP) continue;
            if (ins.MemoryDisplacement64 <= 0x1000) yield return $"+0x{ins.MemoryDisplacement64:X}";
        }
    }

    private static bool LooksCompareSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var v = symbol.ToLowerInvariant();
        return v.Contains("compare", StringComparison.Ordinal) || v.Contains("strcmp", StringComparison.Ordinal) ||
               v.Contains("wcscmp", StringComparison.Ordinal) || v.Contains("equal", StringComparison.Ordinal) ||
               v.Contains("operator==", StringComparison.Ordinal) || v.Contains("??8", StringComparison.Ordinal);
    }

    private static string SymbolFamily(OemSemanticCall? call)
    {
        if (call?.Symbol is null) return string.Empty;
        var v = call.Symbol.ToLowerInvariant();
        if (v.Contains("compare", StringComparison.Ordinal)) return "compare";
        if (v.Contains("strcmp", StringComparison.Ordinal) || v.Contains("wcscmp", StringComparison.Ordinal)) return "string-compare";
        if (v.Contains("equal", StringComparison.Ordinal) || v.Contains("operator==", StringComparison.Ordinal) || v.Contains("??8", StringComparison.Ordinal)) return "equality";
        return call.Symbol;
    }

    private static string Fingerprint(List<SemanticDecoded> window, uint? guard, uint? flags)
    {
        var selected = window.Where(x => (guard is null || x.Rva >= guard.Value) && (flags is null || x.Rva <= flags.Value + 12)).Take(80);
        return string.Join('>', selected.Select(x =>
        {
            if (x.Instruction.Mnemonic == Mnemonic.Call) return "CALL";
            if (x.Instruction.FlowControl == FlowControl.ConditionalBranch) return "JCC";
            if (x.Instruction.FlowControl == FlowControl.UnconditionalBranch) return "JMP";
            var members = MemberOffsets(x.Instruction).ToArray();
            var m = x.Instruction.Mnemonic.ToString().ToUpperInvariant();
            return members.Length == 0 ? m : m + "(" + string.Join(',', members) + ")";
        }));
    }

    private static List<SemanticDecoded> DecodeText(SemanticPe pe)
    {
        var result = new List<SemanticDecoded>();
        foreach (var s in pe.Sections.Where(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase)))
        {
            var size = Math.Min(s.VirtualSize == 0 ? s.RawSize : s.VirtualSize, s.RawSize);
            result.AddRange(DecodeRange(pe, s.VirtualAddress, s.VirtualAddress + size));
        }
        return result.OrderBy(x => x.Rva).ToList();
    }

    private static List<SemanticDecoded> DecodeForward(SemanticPe pe, uint startRva, uint count, uint product)
    {
        var section = pe.SectionForRva(startRva) ?? throw new InvalidDataException($"RVA 0x{startRva:X8} outside PE sections.");
        var sectionEnd = section.VirtualAddress + Math.Min(section.VirtualSize == 0 ? section.RawSize : section.VirtualSize, section.RawSize);
        return DecodeRange(pe, startRva, Math.Min(sectionEnd, startRva + count));
    }

    private static List<SemanticDecoded> DecodeRange(SemanticPe pe, uint startRva, uint endRva)
    {
        if (endRva <= startRva) return [];
        var start = pe.RvaToOffset(startRva);
        var end = pe.RvaToOffset(endRva - 1) + 1;
        var bytes = pe.Bytes.AsSpan(start, end - start).ToArray();
        var decoder = Decoder.Create(pe.Pe32Plus ? 64 : 32, new ByteArrayCodeReader(bytes));
        decoder.IP = startRva;
        var formatter = new IntelFormatter();
        var output = new SemanticFormatterOutput();
        var result = new List<SemanticDecoded>();
        while (decoder.CanDecode && decoder.IP < endRva && result.Count < 200000)
        {
            decoder.Decode(out var ins);
            if (ins.Code == Code.INVALID || ins.Length == 0) break;
            var rva = checked((uint)ins.IP);
            var off = pe.RvaToOffset(rva);
            var raw = Convert.ToHexString(pe.Bytes.AsSpan(off, ins.Length)).ToLowerInvariant();
            formatter.Format(in ins, output);
            result.Add(new SemanticDecoded(rva, raw, output.Take(), ins));
        }
        return result;
    }

    private static OemSemanticInsn ToOutput(SemanticDecoded x, uint product, string[] tags) => new(x.Rva, (long)x.Rva - product, x.Bytes, x.Text, tags);
    private static ulong? Immediate(in Instruction ins, int op)
    {
        if (op >= ins.OpCount) return null;
        return ins.GetOpKind(op) switch
        {
            OpKind.Immediate8 => ins.Immediate8,
            OpKind.Immediate16 => ins.Immediate16,
            OpKind.Immediate32 => ins.Immediate32,
            OpKind.Immediate64 => ins.Immediate64,
            OpKind.Immediate8to16 => unchecked((ulong)(long)ins.Immediate8to16),
            OpKind.Immediate8to32 => unchecked((ulong)(long)ins.Immediate8to32),
            OpKind.Immediate8to64 => unchecked((ulong)ins.Immediate8to64),
            OpKind.Immediate32to64 => unchecked((ulong)ins.Immediate32to64),
            _ => null
        };
    }
    private static string Hex(uint? value) => value is null ? "unresolved" : $"0x{value:X8}";

    private sealed record SemanticDecoded(uint Rva, string Bytes, string Text, Instruction Instruction);
    private sealed record SemanticSection(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
    {
        public bool Contains(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
    }
    private sealed record SemanticImport(string Dll, string Name, uint IatRva);

    private sealed class SemanticPe
    {
        public byte[] Bytes { get; }
        public ushort Machine { get; }
        public bool Pe32Plus { get; }
        public ulong ImageBase { get; }
        public List<SemanticSection> Sections { get; }
        public List<SemanticImport> Imports { get; }

        private SemanticPe(byte[] bytes, ushort machine, bool plus, ulong imageBase, List<SemanticSection> sections, List<SemanticImport> imports)
        { Bytes = bytes; Machine = machine; Pe32Plus = plus; ImageBase = imageBase; Sections = sections; Imports = imports; }

        public static SemanticPe Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') throw new InvalidDataException("Not a PE image.");
            var pe = I32(bytes, 0x3C); Ensure(bytes, pe, 24);
            if (bytes[pe] != (byte)'P' || bytes[pe + 1] != (byte)'E') throw new InvalidDataException("PE signature missing.");
            var machine = U16(bytes, pe + 4); var count = U16(bytes, pe + 6); var optionalSize = U16(bytes, pe + 20); var optional = pe + 24;
            var magic = U16(bytes, optional); var plus = magic == 0x20B;
            if (!plus && magic != 0x10B) throw new InvalidDataException($"Unsupported PE magic 0x{magic:X4}.");
            ulong imageBase = plus ? U64(bytes, optional + 24) : U32(bytes, optional + 28);
            var table = optional + optionalSize; var sections = new List<SemanticSection>();
            for (var i = 0; i < count; i++)
            {
                var off = table + i * 40; Ensure(bytes, off, 40);
                sections.Add(new SemanticSection(Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'), U32(bytes, off + 8), U32(bytes, off + 12), U32(bytes, off + 16), U32(bytes, off + 20)));
            }
            var temp = new SemanticPe(bytes, machine, plus, imageBase, sections, []);
            return new SemanticPe(bytes, machine, plus, imageBase, sections, temp.ParseImports(optional));
        }

        private List<SemanticImport> ParseImports(int optional)
        {
            var data = optional + (Pe32Plus ? 112 : 96); Ensure(Bytes, data + 8, 8);
            var importRva = U32(Bytes, data + 8); if (importRva == 0) return [];
            var result = new List<SemanticImport>(); var descriptor = RvaToOffset(importRva);
            for (var d = 0; d < 512; d++, descriptor += 20)
            {
                Ensure(Bytes, descriptor, 20);
                var original = U32(Bytes, descriptor); var nameRva = U32(Bytes, descriptor + 12); var first = U32(Bytes, descriptor + 16);
                if (original == 0 && nameRva == 0 && first == 0) break;
                var dll = ReadAsciiZ(RvaToOffset(nameRva), 260); var intRva = original != 0 ? original : first; var step = Pe32Plus ? 8 : 4;
                for (var index = 0; index < 4096; index++)
                {
                    var thunkOff = RvaToOffset(intRva + checked((uint)(index * step))); ulong thunk = Pe32Plus ? U64(Bytes, thunkOff) : U32(Bytes, thunkOff);
                    if (thunk == 0) break;
                    var flag = Pe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                    string name;
                    if ((thunk & flag) != 0) name = "#" + (thunk & 0xFFFF);
                    else { var byName = RvaToOffset(checked((uint)thunk)); name = ReadAsciiZ(byName + 2, 512); }
                    result.Add(new SemanticImport(dll, name, first + checked((uint)(index * step))));
                }
            }
            return result;
        }

        public SemanticSection? SectionForRva(uint rva) => Sections.FirstOrDefault(s => s.Contains(rva));
        public int RvaToOffset(uint rva)
        {
            var s = SectionForRva(rva) ?? throw new InvalidDataException($"RVA 0x{rva:X8} outside sections.");
            var off = checked((int)(s.RawPointer + rva - s.VirtualAddress)); Ensure(Bytes, off, 1); return off;
        }
        public string? TryReadString(ulong address)
        {
            uint rva;
            if (address >= ImageBase && address - ImageBase <= uint.MaxValue) rva = checked((uint)(address - ImageBase));
            else if (address <= uint.MaxValue) rva = checked((uint)address); else return null;
            var section = SectionForRva(rva); if (section is null || section.Name.Equals(".text", StringComparison.OrdinalIgnoreCase)) return null;
            int off; try { off = RvaToOffset(rva); } catch { return null; }
            var ascii = ReadAsciiZ(off, 120); if (Readable(ascii)) return ascii;
            var unicode = ReadUnicodeZ(off, 120); return Readable(unicode) ? unicode : null;
        }
        private string ReadAsciiZ(int off, int max)
        { var end = Math.Min(Bytes.Length, off + max); var len = 0; while (off + len < end && Bytes[off + len] != 0) len++; return len == 0 ? string.Empty : Encoding.ASCII.GetString(Bytes, off, len); }
        private string ReadUnicodeZ(int off, int max)
        { var chars = new List<char>(); for (var i = 0; i < max && off + i * 2 + 1 < Bytes.Length; i++) { var v = U16(Bytes, off + i * 2); if (v == 0) break; chars.Add((char)v); } return new string(chars.ToArray()); }
        private static bool Readable(string value) => value.Length is >= 1 and <= 120 && value.Count(c => !char.IsControl(c)) >= Math.Max(1, value.Length * 3 / 4);
    }

    private sealed class SemanticFormatterOutput : FormatterOutput
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public string Take() { var value = _sb.ToString(); _sb.Clear(); return value; }
    }

    private static ushort U16(byte[] b, int o) { Ensure(b, o, 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)); }
    private static uint U32(byte[] b, int o) { Ensure(b, o, 4); return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)); }
    private static ulong U64(byte[] b, int o) { Ensure(b, o, 8); return BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8)); }
    private static int I32(byte[] b, int o) { Ensure(b, o, 4); return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4)); }
    private static void Ensure(byte[] b, int o, int n) { if (o < 0 || n < 0 || o + n > b.Length) throw new InvalidDataException("PE range outside file."); }
}

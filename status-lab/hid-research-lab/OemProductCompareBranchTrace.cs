using System.Buffers.Binary;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemCompareInstruction(
    uint Rva,
    string Bytes,
    string Mnemonic,
    string Text,
    string Flow,
    uint? TargetRva,
    string? ImportName,
    string[] AnchorRefs);

internal sealed record OemCompareHelperCandidate(
    uint CallRva,
    uint? TargetRva,
    string? ImportName,
    long ProductDelta,
    string[] ArgumentSignatures,
    bool ProductBufferArgumentMatch,
    bool DevNameArgumentMatch,
    uint? BranchRva,
    uint? BranchTakenRva,
    uint? BranchFallthroughRva,
    int Score,
    string Confidence,
    string Reason,
    string NeighborhoodSignature);

internal sealed record OemCompareSide(
    string Executable,
    string Machine,
    bool Pe32Plus,
    string ImageBase,
    uint ProductStringCallRva,
    uint? DevCmpStrXrefRva,
    uint? DevNameXrefRva,
    uint WindowStartRva,
    uint WindowEndRva,
    string? ProductBufferSignature,
    uint? DevNameAccessorCallRva,
    uint? DevNameAccessorTargetRva,
    string? DevNameStorageSignature,
    List<OemCompareInstruction> AnchorNeighborhood,
    List<OemCompareHelperCandidate> HelperCandidates,
    List<string> Notes);

internal sealed record OemProductCompareBranchReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemCompareSide A,
    OemCompareSide B,
    bool CandidateCorrespondence,
    string CorrespondenceReason,
    List<string> Evidence,
    List<string> Notes);

internal static class OemProductCompareBranchAnalyzer
{
    private static readonly HashSet<string> KnownCompareImports = new(StringComparer.OrdinalIgnoreCase)
    {
        "strcmp", "strncmp", "_stricmp", "_strcmpi", "stricmp", "strnicmp", "_strnicmp",
        "wcscmp", "wcsncmp", "_wcsicmp", "_wcsnicmp",
        "lstrcmpA", "lstrcmpW", "lstrcmpiA", "lstrcmpiW",
        "CompareStringA", "CompareStringW", "CompareStringEx",
        "CompareStringOrdinal"
    };

    public static OemProductCompareBranchReport Analyze(string exeA, string exeB)
    {
        var gate = OemIdentityGateTraceAnalyzer.Analyze(exeA, exeB);
        var a = AnalyzeSide(Path.GetFullPath(exeA), gate.A);
        var b = AnalyzeSide(Path.GetFullPath(exeB), gate.B);
        var (correspondence, correspondenceReason) = CompareCandidates(a, b);

        var evidence = new List<string>();
        if (gate.Verdict == "PRODUCT_STRING_GATE_LIKELY")
            evidence.Add("Parent identity trace reports PRODUCT_STRING_GATE_LIKELY from equal VID/PID transport rows, DevCmpStr=1, differing DevName, and paired ProductString/xref evidence.");

        var provenA = a.HelperCandidates.FirstOrDefault(IsProvenCandidate);
        var provenB = b.HelperCandidates.FirstOrDefault(IsProvenCandidate);
        var likelyA = a.HelperCandidates.FirstOrDefault(c => c.Score >= 5);
        var likelyB = b.HelperCandidates.FirstOrDefault(c => c.Score >= 5);

        string verdict;
        if (provenA is not null && provenB is not null && correspondence)
        {
            verdict = "PRODUCT_STRING_COMPARE_BRANCH_PROVEN";
            evidence.Add("Both OEM binaries contain a recognized string-compare import call with ProductString buffer flow, DevName-derived argument flow, and an immediate conditional branch.");
        }
        else if (likelyA is not null && likelyB is not null && correspondence)
        {
            verdict = "PRODUCT_STRING_COMPARE_HELPER_LIKELY";
            evidence.Add("Both OEM binaries contain corresponding post-ProductString helper/branch candidates with matching local control-flow shape, but the complete DevName-to-compare data-flow is not proven.");
        }
        else
        {
            verdict = "COMPARE_BRANCH_UNRESOLVED";
            evidence.Add("No paired compare/helper candidate satisfied the conservative static data-flow threshold.");
        }

        if (a.ProductBufferSignature is not null && b.ProductBufferSignature is not null)
            evidence.Add($"ProductString buffer argument recovered on both sides (A={a.ProductBufferSignature}; B={b.ProductBufferSignature}).");
        if (a.DevNameAccessorCallRva is not null && b.DevNameAccessorCallRva is not null)
            evidence.Add($"DevName field-reference consumer call recovered on both sides (A=0x{a.DevNameAccessorCallRva:X8}; B=0x{b.DevNameAccessorCallRva:X8}).");

        return new OemProductCompareBranchReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "bounded read-only x86 control-flow/data-flow trace for the OEM ProductString model comparison gate",
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
            correspondence,
            correspondenceReason,
            evidence,
            [
                "PROVEN requires a recognized compare primitive plus ProductString-buffer flow, DevName-derived argument flow, and a nearby conditional branch on both OEM sides.",
                "HELPER_LIKELY requires paired local helper/branch structure and never upgrades proximity alone to proof.",
                "Member-offset matches are supporting evidence only; they are not sufficient for PROVEN.",
                "The analyzer reads executable/resource bytes only. It does not open the keyboard or launch/attach to either OEM process."
            ]);
    }

    public static string ToText(OemProductCompareBranchReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - OEM Product Compare Branch Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no device open, HID reports/writes, process launch/attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Candidate correspondence: {report.CandidateCorrespondence}");
        sb.AppendLine($"Correspondence: {report.CorrespondenceReason}");
        sb.AppendLine();
        AppendSide(sb, "A", report.A);
        AppendSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence)
            sb.AppendLine("  - " + item);
        sb.AppendLine();
        foreach (var note in report.Notes)
            sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendSide(StringBuilder sb, string label, OemCompareSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  Machine={side.Machine}; PE32+={side.Pe32Plus}; ImageBase={side.ImageBase}");
        sb.AppendLine($"  ProductString=0x{side.ProductStringCallRva:X8}; DevCmpStr={(side.DevCmpStrXrefRva is null ? "n/a" : $"0x{side.DevCmpStrXrefRva:X8}")}; DevName={(side.DevNameXrefRva is null ? "n/a" : $"0x{side.DevNameXrefRva:X8}")}");
        sb.AppendLine($"  Window=0x{side.WindowStartRva:X8}..0x{side.WindowEndRva:X8}");
        sb.AppendLine($"  Product buffer={side.ProductBufferSignature ?? "unresolved"}");
        sb.AppendLine($"  DevName accessor call={(side.DevNameAccessorCallRva is null ? "unresolved" : $"0x{side.DevNameAccessorCallRva:X8}")}; storage={side.DevNameStorageSignature ?? "unresolved"}");
        sb.AppendLine("  Anchor neighborhoods:");
        foreach (var instruction in side.AnchorNeighborhood.Take(120))
            sb.AppendLine($"    0x{instruction.Rva:X8} {instruction.Bytes,-24} {instruction.Text} {(instruction.AnchorRefs.Length == 0 ? "" : "[" + string.Join(',', instruction.AnchorRefs) + "]")}");
        sb.AppendLine("  Helper candidates:");
        foreach (var candidate in side.HelperCandidates.Take(20))
        {
            sb.AppendLine($"    score={candidate.Score} [{candidate.Confidence}] call=0x{candidate.CallRva:X8} target={(candidate.TargetRva is null ? candidate.ImportName ?? "indirect" : $"0x{candidate.TargetRva:X8}")} delta={candidate.ProductDelta:+#;-#;0}");
            sb.AppendLine($"      productArg={candidate.ProductBufferArgumentMatch}; devNameArg={candidate.DevNameArgumentMatch}; branch={(candidate.BranchRva is null ? "n/a" : $"0x{candidate.BranchRva:X8}")}");
            sb.AppendLine($"      args={string.Join(" | ", candidate.ArgumentSignatures)}");
            sb.AppendLine($"      reason={candidate.Reason}");
            sb.AppendLine($"      shape={candidate.NeighborhoodSignature}");
        }
        foreach (var note in side.Notes)
            sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static OemCompareSide AnalyzeSide(string exe, OemIdentityGateSide gate)
    {
        var pe = PeView.Parse(exe);
        if (gate.ProductStringCallSites.Count == 0)
            throw new InvalidDataException("No HidD_GetProductString call-site is available from the parent identity trace.");

        var product = gate.ProductStringCallSites.Min();
        var cmpXref = NearestXref(gate, "DevCmpStr", product);
        var nameXref = NearestXref(gate, "DevName", product);
        var anchors = new[] { product, cmpXref ?? product, nameXref ?? product };
        var minAnchor = anchors.Min();
        var maxAnchor = anchors.Max();
        var text = pe.SectionForRva(product) ?? throw new InvalidDataException("ProductString call-site is outside a PE section.");
        var start = Math.Max(text.VirtualAddress, minAnchor > 0x500 ? minAnchor - 0x500 : text.VirtualAddress);
        var sectionEnd = text.VirtualAddress + Math.Min(text.VirtualSize == 0 ? text.RawSize : text.VirtualSize, text.RawSize);
        var end = Math.Min(sectionEnd, maxAnchor + 0x1200);
        if (end <= start)
            throw new InvalidDataException("Invalid bounded ProductString comparison window.");

        var decoded = Decode(pe, start, end, product, cmpXref, nameXref);
        var productIndex = decoded.FindIndex(x => x.Rva == product);
        if (productIndex < 0)
            throw new InvalidDataException("ProductString call-site did not decode at the expected RVA.");

        var productBuffer = RecoverProductBuffer(decoded, productIndex);
        var (devAccessorCall, devAccessorTarget, devStorage) = RecoverDevNameSource(decoded, nameXref);
        var candidates = BuildHelperCandidates(pe, decoded, productIndex, product, productBuffer, devStorage);
        var neighborhoods = SelectAnchorNeighborhood(decoded, product, cmpXref, nameXref, candidates);
        var notes = new List<string>();
        if (pe.Pe32Plus)
            notes.Add("Selected binary is PE32+; the observed OEM pair is expected to be PE32, so operand heuristics are conservative.");
        if (productBuffer is null)
            notes.Add("Could not recover the second ProductString argument from a conventional push-based stdcall sequence.");
        if (nameXref is not null && devAccessorCall is null)
            notes.Add("DevName field xref decoded, but no bounded consumer call was recovered immediately after it.");
        if (devStorage is null)
            notes.Add("DevName-derived return storage was not recovered; local helper candidates cannot be upgraded to PROVEN from field-name proximity.");

        return new OemCompareSide(
            Path.GetFileName(exe),
            $"0x{pe.Machine:X4}",
            pe.Pe32Plus,
            $"0x{pe.ImageBase:X}",
            product,
            cmpXref,
            nameXref,
            start,
            end,
            productBuffer,
            devAccessorCall,
            devAccessorTarget,
            devStorage,
            neighborhoods,
            candidates,
            notes);
    }

    private static uint? NearestXref(OemIdentityGateSide side, string token, uint product)
    {
        var xrefs = side.TokenRefs
            .Where(x => x.Token.Equals(token, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.DirectXrefRvas)
            .Distinct()
            .ToList();
        return xrefs.Count == 0 ? null : xrefs.OrderBy(x => Math.Abs((long)x - product)).First();
    }

    private static List<Decoded> Decode(PeView pe, uint startRva, uint endRva, uint product, uint? cmpXref, uint? nameXref)
    {
        var startOffset = pe.RvaToOffset(startRva);
        var endOffset = pe.RvaToOffset(endRva - 1) + 1;
        var code = pe.Bytes.AsSpan(startOffset, endOffset - startOffset).ToArray();
        var decoder = Decoder.Create(pe.Pe32Plus ? 64 : 32, new ByteArrayCodeReader(code));
        decoder.IP = startRva;
        var formatter = new IntelFormatter();
        var output = new StringOutput();
        var result = new List<Decoded>();

        while (decoder.CanDecode && decoder.IP < endRva)
        {
            decoder.Decode(out var instruction);
            if (instruction.Code == Code.INVALID || instruction.Length == 0)
                break;
            var rva = checked((uint)instruction.IP);
            var fileOffset = pe.RvaToOffset(rva);
            var bytes = Convert.ToHexString(pe.Bytes.AsSpan(fileOffset, instruction.Length)).ToLowerInvariant();
            formatter.Format(in instruction, output);
            var text = output.ToStringAndReset();
            var anchors = new List<string>();
            if (rva == product) anchors.Add("HidD_GetProductString");
            if (cmpXref is not null && rva == cmpXref.Value) anchors.Add("DevCmpStr_xref");
            if (nameXref is not null && rva == nameXref.Value) anchors.Add("DevName_xref");
            var import = ResolveImport(pe, instruction);
            uint? target = null;
            if (instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch)
            {
                if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                    target = checked((uint)instruction.NearBranchTarget);
            }
            result.Add(new Decoded(instruction, rva, bytes, text, import, target, anchors.ToArray()));
        }
        return result;
    }

    private static string? ResolveImport(PeView pe, in Instruction instruction)
    {
        if (instruction.Mnemonic != Mnemonic.Call || instruction.Op0Kind != OpKind.Memory)
            return null;
        var address = instruction.MemoryDisplacement64;
        if (address == 0)
            return null;
        var import = pe.Imports.FirstOrDefault(i => pe.ImageBase + i.IatRva == address);
        return import is null ? null : import.Dll + "!" + import.Name;
    }

    private static string? RecoverProductBuffer(List<Decoded> decoded, int productIndex)
    {
        var pushes = new List<(int Index, string? Signature)>();
        var lower = Math.Max(0, productIndex - 18);
        for (var i = lower; i < productIndex; i++)
        {
            if (decoded[i].Instruction.Mnemonic == Mnemonic.Call)
                pushes.Clear();
            if (decoded[i].Instruction.Mnemonic != Mnemonic.Push)
                continue;
            pushes.Add((i, ResolvePushedOperand(decoded, i)));
        }
        if (pushes.Count < 3)
            return null;
        var lastThree = pushes.TakeLast(3).ToArray();
        return lastThree[1].Signature;
    }

    private static (uint? CallRva, uint? TargetRva, string? Storage) RecoverDevNameSource(List<Decoded> decoded, uint? nameXref)
    {
        if (nameXref is null)
            return (null, null, null);
        var index = decoded.FindIndex(x => x.Rva == nameXref.Value);
        if (index < 0)
            return (null, null, null);
        var limit = Math.Min(decoded.Count, index + 14);
        for (var i = index + 1; i < limit; i++)
        {
            if (decoded[i].Instruction.Mnemonic != Mnemonic.Call)
                continue;
            var call = decoded[i];
            string? storage = null;
            for (var j = i + 1; j < Math.Min(decoded.Count, i + 8); j++)
            {
                var ins = decoded[j].Instruction;
                if (ins.Mnemonic == Mnemonic.Call)
                    break;
                if (ins.Mnemonic == Mnemonic.Mov && ins.OpCount >= 2 &&
                    ins.GetOpKind(1) == OpKind.Register && ins.GetOpRegister(1) == Register.EAX)
                {
                    storage = OperandSignature(ins, 0, normalizeMember: true);
                    if (storage is not null)
                        break;
                }
            }
            return (call.Rva, call.TargetRva, storage);
        }
        return (null, null, null);
    }

    private static List<OemCompareHelperCandidate> BuildHelperCandidates(
        PeView pe,
        List<Decoded> decoded,
        int productIndex,
        uint productRva,
        string? productBuffer,
        string? devStorage)
    {
        var result = new List<OemCompareHelperCandidate>();
        var maxRva = productRva + 0x1800;
        for (var i = productIndex + 1; i < decoded.Count && decoded[i].Rva <= maxRva; i++)
        {
            var item = decoded[i];
            if (item.Instruction.Mnemonic != Mnemonic.Call)
                continue;
            var args = CollectRecentPushArguments(decoded, i, 6);
            var productMatch = productBuffer is not null && args.Any(x => SignaturesCompatible(x, productBuffer, allowMemberOnly: false));
            var devMatch = devStorage is not null && args.Any(x => SignaturesCompatible(x, devStorage, allowMemberOnly: true));
            var (branchRva, taken, fallthrough) = FindResultBranch(decoded, i);
            var knownCompare = IsKnownCompareImport(item.ImportName);
            var score = 0;
            var reasons = new List<string>();
            if (knownCompare) { score += 4; reasons.Add("recognized string compare import"); }
            if (productMatch) { score += 3; reasons.Add("ProductString buffer appears in call arguments"); }
            if (devMatch) { score += 3; reasons.Add("DevName-derived storage/member appears in call arguments"); }
            if (branchRva is not null) { score += 2; reasons.Add("call result is followed by cmp/test + conditional branch"); }
            var delta = (long)item.Rva - productRva;
            if (delta is >= 0 and <= 0x600) { score += 1; reasons.Add("candidate is close after ProductString"); }
            if (score < 3)
                continue;
            var confidence = score >= 10 ? "high" : score >= 6 ? "medium" : "context";
            result.Add(new OemCompareHelperCandidate(
                item.Rva,
                item.TargetRva,
                item.ImportName,
                delta,
                args.ToArray(),
                productMatch,
                devMatch,
                branchRva,
                taken,
                fallthrough,
                score,
                confidence,
                string.Join("; ", reasons),
                NeighborhoodSignature(decoded, i)));
        }
        return result
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.ProductDelta))
            .ThenBy(x => x.CallRva)
            .Take(40)
            .ToList();
    }

    private static List<string> CollectRecentPushArguments(List<Decoded> decoded, int callIndex, int maxArgs)
    {
        var result = new List<string>();
        for (var i = callIndex - 1; i >= 0 && callIndex - i <= 20 && result.Count < maxArgs; i--)
        {
            if (decoded[i].Instruction.Mnemonic == Mnemonic.Call)
                break;
            if (decoded[i].Instruction.Mnemonic != Mnemonic.Push)
                continue;
            var signature = ResolvePushedOperand(decoded, i);
            if (signature is not null)
                result.Add(signature);
        }
        result.Reverse();
        return result;
    }

    private static string? ResolvePushedOperand(List<Decoded> decoded, int pushIndex)
    {
        var push = decoded[pushIndex].Instruction;
        if (push.OpCount == 0)
            return null;
        if (push.GetOpKind(0) == OpKind.Register)
        {
            var register = push.GetOpRegister(0);
            for (var i = pushIndex - 1; i >= Math.Max(0, pushIndex - 12); i--)
            {
                var ins = decoded[i].Instruction;
                if (ins.Mnemonic == Mnemonic.Call)
                    break;
                if (ins.OpCount < 2 || ins.GetOpKind(0) != OpKind.Register || ins.GetOpRegister(0) != register)
                    continue;
                if (ins.Mnemonic == Mnemonic.Lea && ins.GetOpKind(1) == OpKind.Memory)
                    return OperandSignature(ins, 1, normalizeMember: false);
                if (ins.Mnemonic == Mnemonic.Mov)
                {
                    var resolved = OperandSignature(ins, 1, normalizeMember: true);
                    if (resolved is not null)
                        return resolved;
                }
                break;
            }
        }
        return OperandSignature(push, 0, normalizeMember: true);
    }

    private static string? OperandSignature(in Instruction instruction, int operand, bool normalizeMember)
    {
        if (operand >= instruction.OpCount)
            return null;
        var kind = instruction.GetOpKind(operand);
        if (kind == OpKind.Register)
            return "reg:" + instruction.GetOpRegister(operand);
        if (kind == OpKind.Memory)
        {
            var baseReg = instruction.MemoryBase;
            var indexReg = instruction.MemoryIndex;
            var disp = instruction.MemoryDisplacement64;
            if (normalizeMember && baseReg is not Register.EBP and not Register.ESP and not Register.RBP and not Register.RSP && indexReg == Register.None && disp <= 0x800)
                return $"member:+0x{disp:X}";
            return $"mem:{baseReg}:{indexReg}:x{instruction.MemoryIndexScale}:0x{disp:X}";
        }
        return kind switch
        {
            OpKind.Immediate8 => $"imm:0x{instruction.Immediate8:X}",
            OpKind.Immediate16 => $"imm:0x{instruction.Immediate16:X}",
            OpKind.Immediate32 => $"imm:0x{instruction.Immediate32:X}",
            OpKind.Immediate64 => $"imm:0x{instruction.Immediate64:X}",
            OpKind.Immediate8to16 => $"imm:0x{instruction.Immediate8to16:X}",
            OpKind.Immediate8to32 => $"imm:0x{instruction.Immediate8to32:X}",
            OpKind.Immediate8to64 => $"imm:0x{instruction.Immediate8to64:X}",
            OpKind.Immediate32to64 => $"imm:0x{instruction.Immediate32to64:X}",
            _ => null
        };
    }

    private static bool SignaturesCompatible(string a, string b, bool allowMemberOnly)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!allowMemberOnly)
            return false;
        return a.StartsWith("member:", StringComparison.OrdinalIgnoreCase) &&
               b.StartsWith("member:", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static (uint? BranchRva, uint? Taken, uint? Fallthrough) FindResultBranch(List<Decoded> decoded, int callIndex)
    {
        var sawResultCheck = false;
        for (var i = callIndex + 1; i < Math.Min(decoded.Count, callIndex + 8); i++)
        {
            var instruction = decoded[i].Instruction;
            if (instruction.Mnemonic == Mnemonic.Call)
                break;
            if (instruction.Mnemonic is Mnemonic.Test or Mnemonic.Cmp)
            {
                if (instruction.OpCount >= 1 && instruction.GetOpKind(0) == OpKind.Register && instruction.GetOpRegister(0) == Register.EAX)
                    sawResultCheck = true;
            }
            if (sawResultCheck && instruction.FlowControl == FlowControl.ConditionalBranch)
            {
                var target = checked((uint)instruction.NearBranchTarget);
                return (decoded[i].Rva, target, decoded[i].Rva + (uint)instruction.Length);
            }
        }
        return (null, null, null);
    }

    private static string NeighborhoodSignature(List<Decoded> decoded, int center)
    {
        var start = Math.Max(0, center - 4);
        var end = Math.Min(decoded.Count, center + 7);
        return string.Join('>', decoded.Skip(start).Take(end - start).Select(x =>
        {
            if (x.Instruction.FlowControl == FlowControl.ConditionalBranch) return "JCC";
            if (x.Instruction.Mnemonic == Mnemonic.Call) return x.ImportName is null ? "CALL_LOCAL" : "CALL_IMPORT";
            if (x.Instruction.Mnemonic is Mnemonic.Cmp or Mnemonic.Test) return x.Instruction.Mnemonic.ToString().ToUpperInvariant();
            if (x.Instruction.Mnemonic == Mnemonic.Push) return "PUSH";
            if (x.Instruction.Mnemonic == Mnemonic.Lea) return "LEA";
            if (x.Instruction.Mnemonic == Mnemonic.Mov) return "MOV";
            return x.Instruction.Mnemonic.ToString().ToUpperInvariant();
        }));
    }

    private static bool IsKnownCompareImport(string? importName)
    {
        if (string.IsNullOrWhiteSpace(importName))
            return false;
        var bang = importName.LastIndexOf('!');
        var name = bang >= 0 ? importName[(bang + 1)..] : importName;
        return KnownCompareImports.Contains(name);
    }

    private static bool IsProvenCandidate(OemCompareHelperCandidate candidate) =>
        IsKnownCompareImport(candidate.ImportName) &&
        candidate.ProductBufferArgumentMatch &&
        candidate.DevNameArgumentMatch &&
        candidate.BranchRva is not null &&
        candidate.Score >= 10;

    private static (bool Match, string Reason) CompareCandidates(OemCompareSide a, OemCompareSide b)
    {
        foreach (var ac in a.HelperCandidates.Take(12))
        {
            foreach (var bc in b.HelperCandidates.Take(12))
            {
                var importMatch = !string.IsNullOrWhiteSpace(ac.ImportName) &&
                                  !string.IsNullOrWhiteSpace(bc.ImportName) &&
                                  string.Equals(ac.ImportName, bc.ImportName, StringComparison.OrdinalIgnoreCase);
                var shapeMatch = string.Equals(ac.NeighborhoodSignature, bc.NeighborhoodSignature, StringComparison.Ordinal);
                var deltaGap = Math.Abs(ac.ProductDelta - bc.ProductDelta);
                if ((importMatch || shapeMatch) && deltaGap <= 0x180 && ac.BranchRva is not null && bc.BranchRva is not null)
                    return (true, $"paired candidate A=0x{ac.CallRva:X8} B=0x{bc.CallRva:X8}; product-relative delta gap=0x{deltaGap:X}; importMatch={importMatch}; shapeMatch={shapeMatch}");
            }
        }
        return (false, "No top candidate pair had matching import/flow shape within the bounded product-relative delta tolerance.");
    }

    private static List<OemCompareInstruction> SelectAnchorNeighborhood(
        List<Decoded> decoded,
        uint product,
        uint? cmp,
        uint? name,
        List<OemCompareHelperCandidate> candidates)
    {
        var centers = new HashSet<uint> { product };
        if (cmp is not null) centers.Add(cmp.Value);
        if (name is not null) centers.Add(name.Value);
        foreach (var c in candidates.Take(6))
        {
            centers.Add(c.CallRva);
            if (c.BranchRva is not null) centers.Add(c.BranchRva.Value);
        }
        var selected = new SortedDictionary<uint, OemCompareInstruction>();
        foreach (var center in centers)
        {
            var index = decoded.FindIndex(x => x.Rva == center);
            if (index < 0) continue;
            for (var i = Math.Max(0, index - 8); i < Math.Min(decoded.Count, index + 10); i++)
            {
                var x = decoded[i];
                selected[x.Rva] = new OemCompareInstruction(
                    x.Rva,
                    x.Bytes,
                    x.Instruction.Mnemonic.ToString(),
                    x.Text,
                    x.Instruction.FlowControl.ToString(),
                    x.TargetRva,
                    x.ImportName,
                    x.AnchorRefs);
            }
        }
        return selected.Values.Take(240).ToList();
    }

    private sealed record Decoded(
        Instruction Instruction,
        uint Rva,
        string Bytes,
        string Text,
        string? ImportName,
        uint? TargetRva,
        string[] AnchorRefs);

    private sealed record PeImport(string Dll, string Name, uint IatRva);

    private sealed record PeSection(
        string Name,
        uint VirtualSize,
        uint VirtualAddress,
        uint RawSize,
        uint RawPointer,
        uint Characteristics)
    {
        public bool ContainsRva(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
    }

    private sealed class PeView
    {
        public byte[] Bytes { get; }
        public ushort Machine { get; }
        public bool Pe32Plus { get; }
        public ulong ImageBase { get; }
        public List<PeSection> Sections { get; }
        public List<PeImport> Imports { get; }

        private PeView(byte[] bytes, ushort machine, bool pe32Plus, ulong imageBase, List<PeSection> sections, List<PeImport> imports)
        {
            Bytes = bytes;
            Machine = machine;
            Pe32Plus = pe32Plus;
            ImageBase = imageBase;
            Sections = sections;
            Imports = imports;
        }

        public static PeView Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("Selected OEM executable is not a PE image.");
            var peOffset = I32(bytes, 0x3c);
            Ensure(bytes, peOffset, 24);
            if (bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
                throw new InvalidDataException("PE header not found.");
            var machine = U16(bytes, peOffset + 4);
            var sectionCount = U16(bytes, peOffset + 6);
            var optionalSize = U16(bytes, peOffset + 20);
            var optional = peOffset + 24;
            var magic = U16(bytes, optional);
            var pe32Plus = magic == 0x20b;
            if (!pe32Plus && magic != 0x10b)
                throw new InvalidDataException($"Unsupported PE optional-header magic 0x{magic:X4}.");
            ulong imageBase = pe32Plus ? U64(bytes, optional + 24) : U32(bytes, optional + 28);
            var dataDirectory = optional + (pe32Plus ? 112 : 96);
            var importRva = U32(bytes, dataDirectory + 8);
            var sectionTable = optional + optionalSize;
            var sections = new List<PeSection>();
            for (var index = 0; index < sectionCount; index++)
            {
                var off = sectionTable + index * 40;
                Ensure(bytes, off, 40);
                sections.Add(new PeSection(
                    Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'),
                    U32(bytes, off + 8),
                    U32(bytes, off + 12),
                    U32(bytes, off + 16),
                    U32(bytes, off + 20),
                    U32(bytes, off + 36)));
            }
            var imports = ParseImports(bytes, pe32Plus, importRva, sections);
            return new PeView(bytes, machine, pe32Plus, imageBase, sections, imports);
        }

        public PeSection? SectionForRva(uint rva) => Sections.FirstOrDefault(s => s.ContainsRva(rva));

        public int RvaToOffset(uint rva)
        {
            var section = SectionForRva(rva) ?? throw new InvalidDataException($"RVA 0x{rva:X8} is outside PE sections.");
            var offset = checked((int)(section.RawPointer + (rva - section.VirtualAddress)));
            Ensure(Bytes, offset, 1);
            return offset;
        }

        private static List<PeImport> ParseImports(byte[] bytes, bool pe32Plus, uint importRva, List<PeSection> sections)
        {
            var result = new List<PeImport>();
            if (importRva == 0) return result;
            var descriptorOffset = RvaToOffset(importRva, sections, bytes.Length);
            var pointerSize = pe32Plus ? 8 : 4;
            for (var descriptor = descriptorOffset; descriptor + 20 <= bytes.Length; descriptor += 20)
            {
                var originalThunk = U32(bytes, descriptor);
                var nameRva = U32(bytes, descriptor + 12);
                var firstThunk = U32(bytes, descriptor + 16);
                if (originalThunk == 0 && nameRva == 0 && firstThunk == 0) break;
                var dll = ReadAsciiZ(bytes, RvaToOffset(nameRva, sections, bytes.Length));
                var lookupRva = originalThunk != 0 ? originalThunk : firstThunk;
                var lookupOffset = RvaToOffset(lookupRva, sections, bytes.Length);
                for (var index = 0; ; index++)
                {
                    var thunkOffset = lookupOffset + index * pointerSize;
                    Ensure(bytes, thunkOffset, pointerSize);
                    var thunk = pe32Plus ? U64(bytes, thunkOffset) : U32(bytes, thunkOffset);
                    if (thunk == 0) break;
                    var ordinalMask = pe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                    if ((thunk & ordinalMask) != 0) continue;
                    var nameOffset = RvaToOffset(checked((uint)thunk), sections, bytes.Length) + 2;
                    var name = ReadAsciiZ(bytes, nameOffset);
                    result.Add(new PeImport(dll, name, checked(firstThunk + (uint)(index * pointerSize))));
                }
            }
            return result;
        }

        private static int RvaToOffset(uint rva, List<PeSection> sections, int fileLength)
        {
            var section = sections.FirstOrDefault(s => s.ContainsRva(rva));
            if (section is null) throw new InvalidDataException($"RVA 0x{rva:X8} is outside PE sections.");
            var offset = checked((int)(section.RawPointer + (rva - section.VirtualAddress)));
            Ensure(fileLength, offset, 1);
            return offset;
        }
    }

    private static string ReadAsciiZ(byte[] bytes, int offset)
    {
        var end = offset;
        while (end < bytes.Length && bytes[end] != 0 && end - offset < 1024) end++;
        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private static ushort U16(byte[] bytes, int offset) { Ensure(bytes, offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)); }
    private static uint U32(byte[] bytes, int offset) { Ensure(bytes, offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)); }
    private static ulong U64(byte[] bytes, int offset) { Ensure(bytes, offset, 8); return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)); }
    private static int I32(byte[] bytes, int offset) { Ensure(bytes, offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)); }
    private static void Ensure(byte[] bytes, int offset, int length) => Ensure(bytes.Length, offset, length);
    private static void Ensure(int total, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > total - length)
            throw new InvalidDataException("PE structure exceeds file bounds.");
    }
}

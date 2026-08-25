using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemCommitInsn(uint Rva, string Bytes, string Text, string[] Tags);
internal sealed record OemStackSlot(long Displacement, uint[] CaseWrites, uint[] PostJoinReads, bool SurvivesJoin);
internal sealed record OemCommitMemberRef(uint Rva, string Kind, string BaseRegister, long Offset, string Text);
internal sealed record OemCommitChain(
    string Field,
    string Kind,
    long SourceStackSlot,
    uint SourceRva,
    uint CommitRva,
    string DestinationBase,
    long DestinationOffset,
    string? SemanticSymbol,
    bool Explicit,
    string Fingerprint,
    string[] Steps);
internal sealed record OemObjectCommitField(
    string Field,
    uint XrefRva,
    uint? EqualityBranchRva,
    uint? CommonJoinRva,
    string? GuardObjectBase,
    long ExpectedMember,
    List<OemCommitInsn> CaseCfg,
    List<OemStackSlot> StagingSlots,
    List<OemCommitMemberRef> ExpectedMemberRefs,
    List<OemCommitChain> Chains,
    bool JoinRecovered,
    bool StagingRecovered,
    bool Proven,
    string CommitFingerprint,
    List<string> Notes);
internal sealed record OemIdentityObjectCommitSide(
    string Executable,
    uint ProductStringCallRva,
    OemObjectCommitField DevName,
    OemObjectCommitField DevCmpStr,
    List<string> Notes);
internal sealed record OemIdentityObjectCommitReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemIdentityObjectCommitSide A,
    OemIdentityObjectCommitSide B,
    bool DevNameCommitCorrespondence,
    bool DevCmpStrCommitCorrespondence,
    bool DevNameToMember20Proven,
    bool DevCmpStrToMember3EcProven,
    List<string> Evidence,
    List<string> Notes);

internal static class OemIdentityObjectCommitAnalyzer
{
    private const long DevNameMember = 0x20;
    private const long DevCmpMember = 0x3EC;

    public static OemIdentityObjectCommitReport Analyze(string exeA, string exeB)
    {
        var previous = OemIdentityFieldProvenanceAnalyzer.Analyze(exeA, exeB);
        var semantic = OemIdentitySemanticBridgeAnalyzer.Analyze(exeA, exeB);
        var a = AnalyzeSide(Path.GetFullPath(exeA), previous.A, semantic.A);
        var b = AnalyzeSide(Path.GetFullPath(exeB), previous.B, semantic.B);

        var nameCorrespondence = Correspond(a.DevName, b.DevName);
        var cmpCorrespondence = Correspond(a.DevCmpStr, b.DevCmpStr);
        var nameProven = nameCorrespondence && a.DevName.Proven && b.DevName.Proven;
        var cmpProven = cmpCorrespondence && a.DevCmpStr.Proven && b.DevCmpStr.Proven;

        var anyStaging = a.DevName.StagingRecovered || a.DevCmpStr.StagingRecovered ||
                         b.DevName.StagingRecovered || b.DevCmpStr.StagingRecovered;
        var verdict = nameProven && cmpProven ? "IDENTITY_OBJECT_COMMIT_COMPLETE" :
                      nameProven || cmpProven ? "IDENTITY_OBJECT_COMMIT_PARTIAL" :
                      anyStaging ? "STAGING_TO_JOIN_TRACED" :
                      "IDENTITY_OBJECT_COMMIT_UNRESOLVED";

        var evidence = new List<string>();
        if (a.DevName.JoinRecovered && b.DevName.JoinRecovered)
            evidence.Add($"DevName common parser join recovered on both sides (A={Hex(a.DevName.CommonJoinRva)}, B={Hex(b.DevName.CommonJoinRva)}).");
        if (a.DevCmpStr.JoinRecovered && b.DevCmpStr.JoinRecovered)
            evidence.Add($"DevCmpStr common parser join recovered on both sides (A={Hex(a.DevCmpStr.CommonJoinRva)}, B={Hex(b.DevCmpStr.CommonJoinRva)}).");
        if (nameCorrespondence)
            evidence.Add("DevName explicit commit-chain fingerprints correspond between VOROTEX and SXS-W909.");
        if (cmpCorrespondence)
            evidence.Add("DevCmpStr explicit commit-chain fingerprints correspond between VOROTEX and SXS-W909.");
        if (nameProven)
            evidence.Add("DevName staging is explicitly committed to the same runtime object member +0x20 used by the ProductString guard on both OEM sides.");
        if (cmpProven)
            evidence.Add("DevCmpStr staging is explicitly committed to the same runtime object member +0x3EC used by the ProductString guard on both OEM sides.");

        return new OemIdentityObjectCommitReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static data-flow from field-specific parser staging through the common join to the runtime Ndevice object commit",
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
            nameCorrespondence,
            cmpCorrespondence,
            nameProven,
            cmpProven,
            evidence,
            [
                "The previous branch-local boundary was intentionally removed: internal unconditional jumps are followed by a bounded CFG until a repeated parser-case join is reached.",
                "Stack/local values are only staging candidates when written on the field match CFG and read again after the recovered common join.",
                "A direct member-offset match is not proof. An accepted chain must connect a surviving field staging slot to the same guard object base and expected member offset.",
                "CDuiString copy/assignment calls are accepted only when caller argument preparation explicitly supplies both destination member address and a surviving staging source.",
                "Structural correspondence, same offsets, same helpers and proximity remain evidence only.",
                "All analysis reads executable bytes only; no OEM code is executed and no keyboard device is opened."
            ]);
    }

    public static string ToText(OemIdentityObjectCommitReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - OEM Identity Object Commit Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, reports/writes, process launch/attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"DevName commit correspondence: {report.DevNameCommitCorrespondence}");
        sb.AppendLine($"DevCmpStr commit correspondence: {report.DevCmpStrCommitCorrespondence}");
        sb.AppendLine($"DevName -> +0x20 proven: {report.DevNameToMember20Proven}");
        sb.AppendLine($"DevCmpStr -> +0x3EC proven: {report.DevCmpStrToMember3EcProven}");
        sb.AppendLine();
        AppendSide(sb, "A", report.A);
        AppendSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        sb.AppendLine();
        foreach (var note in report.Notes) sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendSide(StringBuilder sb, string label, OemIdentityObjectCommitSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}; ProductString=0x{side.ProductStringCallRva:X8}");
        AppendField(sb, side.DevName);
        AppendField(sb, side.DevCmpStr);
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static void AppendField(StringBuilder sb, OemObjectCommitField field)
    {
        sb.AppendLine($"  {field.Field}: xref=0x{field.XrefRva:X8}; join={Hex(field.CommonJoinRva)}; guardBase={field.GuardObjectBase ?? "unresolved"}; expected=+0x{field.ExpectedMember:X}; proven={field.Proven}");
        sb.AppendLine($"    caseCfg={field.CaseCfg.Count}; staging={field.StagingSlots.Count}; expectedRefs={field.ExpectedMemberRefs.Count}; chains={field.Chains.Count}");
        foreach (var slot in field.StagingSlots.Take(30))
            sb.AppendLine($"    stage [EBP{SignedHex(slot.Displacement)}] writes={JoinHex(slot.CaseWrites)} postReads={JoinHex(slot.PostJoinReads)} survives={slot.SurvivesJoin}");
        foreach (var member in field.ExpectedMemberRefs.Take(30))
            sb.AppendLine($"    member 0x{member.Rva:X8} {member.Kind} [{member.BaseRegister}+0x{member.Offset:X}] :: {member.Text}");
        foreach (var chain in field.Chains.Take(20))
        {
            sb.AppendLine($"    chain {chain.Kind} explicit={chain.Explicit} stage=[EBP{SignedHex(chain.SourceStackSlot)}] source=0x{chain.SourceRva:X8} commit=0x{chain.CommitRva:X8} dst=[{chain.DestinationBase}+0x{chain.DestinationOffset:X}] symbol={chain.SemanticSymbol ?? "n/a"}");
            foreach (var step in chain.Steps) sb.AppendLine("      " + step);
        }
        sb.AppendLine($"    commitFingerprint={field.CommitFingerprint}");
        foreach (var note in field.Notes) sb.AppendLine("    NOTE: " + note);
    }

    private static OemIdentityObjectCommitSide AnalyzeSide(string exe, OemIdentityFieldProvenanceSide previous, OemSemanticSide semantic)
    {
        var pe = CommitPe.Parse(exe);
        var guardBase = FindGuardBase(pe, semantic.GuardRva, DevCmpMember);
        var name = AnalyzeField(pe, previous.DevName, semantic.ProductStringCallRva, guardBase, DevNameMember);
        var cmp = AnalyzeField(pe, previous.DevCmpStr, semantic.ProductStringCallRva, guardBase, DevCmpMember);
        var notes = new List<string>();
        if (guardBase is null)
            notes.Add("Could not recover the ProductString guard object base register; no object-member provenance can be promoted to PROVEN.");
        if (!name.Proven)
            notes.Add("DevName staging-to-+0x20 commit remains unproven on this OEM side.");
        if (!cmp.Proven)
            notes.Add("DevCmpStr staging-to-+0x3EC commit remains unproven on this OEM side.");
        return new OemIdentityObjectCommitSide(Path.GetFileName(exe), semantic.ProductStringCallRva, name, cmp, notes);
    }

    private static OemObjectCommitField AnalyzeField(CommitPe pe, OemFieldProvenance previous, uint productRva, string? guardBase, long expectedMember)
    {
        var notes = new List<string>();
        if (previous.XrefRva == 0)
            return EmptyField(previous.Field, expectedMember, "No aligned parser xref was available from the previous provenance stage.");

        var join = FindRepeatedForwardJoin(pe, previous.XrefRva, productRva);
        if (join is null)
            notes.Add("Repeated forward parser-case join was not recovered.");

        var branchInfo = FindEqualityBranch(pe, previous.XrefRva);
        var caseCfg = branchInfo.TrueStart is null || join is null
            ? new List<Decoded>()
            : TraceCaseCfg(pe, branchInfo.TrueStart.Value, join.Value, branchInfo.FalseTarget, previous.XrefRva);

        var caseWrites = StackWrites(caseCfg);
        var postStart = join ?? previous.XrefRva;
        var postEnd = Math.Min(pe.TextEnd, productRva + 0x180);
        var post = postEnd > postStart ? DecodeRange(pe, postStart, postEnd) : [];

        var slots = new List<OemStackSlot>();
        foreach (var group in caseWrites.GroupBy(x => x.Displacement).OrderBy(x => x.Key))
        {
            var reads = post
                .Where(x => IsStackRead(x.Instruction, group.Key))
                .Select(x => x.Rva)
                .Take(40)
                .ToArray();
            slots.Add(new OemStackSlot(
                group.Key,
                group.Select(x => x.Rva).Distinct().Take(40).ToArray(),
                reads,
                reads.Length > 0));
        }

        var surviving = slots.Where(x => x.SurvivesJoin).Select(x => x.Displacement).ToHashSet();
        if (surviving.Count == 0)
            notes.Add("No field-match stack write was observed again after the recovered parser join.");

        var expectedRefs = FindMemberRefs(post, expectedMember);
        if (expectedRefs.Count == 0)
            notes.Add($"No post-join reference to expected member +0x{expectedMember:X} was found before/through the ProductString guard window.");

        var chains = new List<OemCommitChain>();
        if (guardBase is not null && surviving.Count > 0)
        {
            chains.AddRange(FindDirectWriteChains(post, previous.Field, surviving, guardBase, expectedMember));
            chains.AddRange(FindSemanticCopyChains(pe, post, previous.Field, surviving, guardBase, expectedMember));
        }

        var explicitChains = chains.Where(x => x.Explicit).ToList();
        var proven = explicitChains.Count > 0;
        if (!proven && expectedRefs.Count > 0)
            notes.Add("Expected runtime member is visible post-join, but no explicit source chain from surviving field staging was recovered; offset visibility alone is not proof.");

        var fingerprint = string.Join('|', explicitChains.Select(x => x.Fingerprint).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
        return new OemObjectCommitField(
            previous.Field,
            previous.XrefRva,
            branchInfo.BranchRva,
            join,
            guardBase,
            expectedMember,
            caseCfg.OrderBy(x => x.Rva).Select((x, i) => Out(x, i == 0 ? ["case_cfg"] : [])).ToList(),
            slots,
            expectedRefs,
            chains,
            join is not null,
            surviving.Count > 0,
            proven,
            fingerprint,
            notes);
    }

    private static OemObjectCommitField EmptyField(string field, long expected, string note) =>
        new(field, 0, null, null, null, expected, [], [], [], [], false, false, false, string.Empty, [note]);

    private static (uint? BranchRva, uint? TrueStart, uint? FalseTarget) FindEqualityBranch(CommitPe pe, uint xref)
    {
        var decoded = DecodeRange(pe, xref, Math.Min(pe.TextEnd, xref + 0x80));
        var call = decoded.FindIndex(1, x => x.Instruction.Mnemonic == Mnemonic.Call);
        if (call < 0) return (null, null, null);
        var test = decoded.FindIndex(call + 1, x => x.Instruction.Mnemonic == Mnemonic.Test && MentionsAl(x.Instruction));
        if (test < 0) return (null, null, null);
        var branch = decoded.Skip(test + 1).Take(4).FirstOrDefault(x => x.Instruction.FlowControl == FlowControl.ConditionalBranch);
        if (branch is null) return (null, null, null);
        var fallthrough = branch.Rva + (uint)branch.Instruction.Length;
        var target = checked((uint)branch.Instruction.NearBranchTarget);
        if (branch.Instruction.Mnemonic == Mnemonic.Je)
            return (branch.Rva, fallthrough, target);
        if (branch.Instruction.Mnemonic == Mnemonic.Jne)
            return (branch.Rva, target, fallthrough);
        return (branch.Rva, null, null);
    }

    private static uint? FindRepeatedForwardJoin(CommitPe pe, uint xref, uint product)
    {
        var start = xref > 0x1800 ? xref - 0x1800 : pe.TextStart;
        start = Math.Max(start, pe.TextStart);
        var end = Math.Min(pe.TextEnd, product);
        var decoded = DecodeRange(pe, start, end);
        var candidates = decoded
            .Where(x => x.Instruction.FlowControl == FlowControl.UnconditionalBranch && IsDirectBranch(x.Instruction))
            .Select(x => new { Source = x.Rva, Target = checked((uint)x.Instruction.NearBranchTarget) })
            .Where(x => x.Target > xref && x.Target < product && x.Target > x.Source)
            .GroupBy(x => x.Target)
            .Select(g => new { Target = g.Key, Count = g.Count(), MinDistance = g.Min(x => Math.Abs((long)x.Source - xref)) })
            .Where(x => x.Count >= 2)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.MinDistance)
            .ThenBy(x => x.Target)
            .FirstOrDefault();
        return candidates?.Target;
    }

    private static List<Decoded> TraceCaseCfg(CommitPe pe, uint start, uint join, uint? falseTarget, uint xref)
    {
        var low = xref;
        var high = join;
        if (high <= low) return [];
        var map = DecodeRange(pe, low, high).ToDictionary(x => x.Rva);
        var queue = new Queue<uint>();
        var seen = new HashSet<uint>();
        var result = new List<Decoded>();
        queue.Enqueue(start);
        while (queue.Count > 0 && seen.Count < 600)
        {
            var rva = queue.Dequeue();
            if (rva == join || rva == falseTarget || rva < low || rva >= high || !seen.Add(rva)) continue;
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

    private static List<(long Displacement, uint Rva)> StackWrites(List<Decoded> body)
    {
        var result = new List<(long, uint)>();
        foreach (var item in body)
        {
            var ins = item.Instruction;
            if (!IsSimpleMemoryWrite(ins) || ins.Op0Kind != OpKind.Memory || !IsStackBase(ins.MemoryBase)) continue;
            result.Add((SignedDisp(ins), item.Rva));
        }
        return result;
    }

    private static bool IsStackRead(Instruction ins, long displacement)
    {
        for (var op = 0; op < ins.OpCount; op++)
        {
            if (ins.GetOpKind(op) != OpKind.Memory || !IsStackBase(ins.MemoryBase)) continue;
            if (SignedDisp(ins) != displacement) continue;
            if (op != 0 || !IsSimpleMemoryWrite(ins)) return true;
            if (ins.Mnemonic is Mnemonic.Cmp or Mnemonic.Test) return true;
        }
        return false;
    }

    private static List<OemCommitMemberRef> FindMemberRefs(List<Decoded> body, long expected)
    {
        var result = new List<OemCommitMemberRef>();
        foreach (var item in body)
        {
            var ins = item.Instruction;
            for (var op = 0; op < ins.OpCount; op++)
            {
                if (ins.GetOpKind(op) != OpKind.Memory || IsStackBase(ins.MemoryBase) || ins.MemoryBase == Register.None) continue;
                var disp = SignedDisp(ins);
                if (disp != expected) continue;
                var kind = ins.Mnemonic == Mnemonic.Lea ? "address" :
                           op == 0 && IsSimpleMemoryWrite(ins) ? "write" : "read";
                result.Add(new OemCommitMemberRef(item.Rva, kind, ins.MemoryBase.ToString(), disp, item.Text));
                break;
            }
        }
        return result;
    }

    private static IEnumerable<OemCommitChain> FindDirectWriteChains(
        List<Decoded> body, string field, HashSet<long> seeds, string guardBase, long expected)
    {
        for (var i = 0; i < body.Count; i++)
        {
            var ins = body[i].Instruction;
            if (ins.Mnemonic != Mnemonic.Mov || ins.Op0Kind != OpKind.Memory || ins.Op1Kind != OpKind.Register) continue;
            if (IsStackBase(ins.MemoryBase) || SignedDisp(ins) != expected || !SameRegister(ins.MemoryBase, guardBase)) continue;
            var origin = TraceRegisterOrigin(body, i - 1, ins.Op1Register, seeds, 36);
            if (origin is null) continue;
            yield return new OemCommitChain(
                field,
                "direct-member-write",
                origin.Value.Slot,
                origin.Value.SourceRva,
                body[i].Rva,
                guardBase,
                expected,
                null,
                true,
                $"DIRECT:{field}:+0x{expected:X}:{origin.Value.Mode}",
                origin.Value.Steps.Concat([$"0x{body[i].Rva:X8} {body[i].Text}"]).ToArray());
        }
    }

    private static IEnumerable<OemCommitChain> FindSemanticCopyChains(
        CommitPe pe, List<Decoded> body, string field, HashSet<long> seeds, string guardBase, long expected)
    {
        for (var i = 0; i < body.Count; i++)
        {
            var item = body[i];
            if (item.Instruction.Mnemonic != Mnemonic.Call) continue;
            var symbol = pe.ResolveImport(item.Instruction);
            if (!LooksStringCopy(symbol)) continue;

            var dest = TraceRegisterAddressOrigin(body, i - 1, Register.ECX, guardBase, expected, 16);
            if (dest is null) continue;

            (long Slot, uint SourceRva, string Mode, string[] Steps)? source = null;
            for (var p = i - 1; p >= Math.Max(0, i - 16) && source is null; p--)
            {
                var pin = body[p].Instruction;
                if (pin.Mnemonic != Mnemonic.Push) continue;
                if (pin.Op0Kind == OpKind.Memory && IsStackBase(pin.MemoryBase) && seeds.Contains(SignedDisp(pin)))
                {
                    source = (SignedDisp(pin), body[p].Rva, "stack-argument", [$"0x{body[p].Rva:X8} {body[p].Text}"]);
                    break;
                }
                if (pin.Op0Kind == OpKind.Register)
                    source = TraceRegisterOrigin(body, p - 1, pin.Op0Register, seeds, 24);
            }
            if (source is null) continue;

            yield return new OemCommitChain(
                field,
                "semantic-cduistring-copy",
                source.Value.Slot,
                source.Value.SourceRva,
                item.Rva,
                guardBase,
                expected,
                symbol,
                true,
                $"CDUI_COPY:{field}:+0x{expected:X}:{NormalizeSymbol(symbol)}",
                source.Value.Steps.Concat(dest.Value.Steps).Concat([$"0x{item.Rva:X8} call {symbol}"]).ToArray());
        }
    }

    private static (long Slot, uint SourceRva, string Mode, string[] Steps)? TraceRegisterOrigin(
        List<Decoded> body, int index, Register register, HashSet<long> seeds, int limit)
    {
        var reg = NormalizeRegister(register);
        var steps = new List<string>();
        for (var i = index; i >= 0 && index - i < limit; i--)
        {
            var item = body[i];
            var ins = item.Instruction;
            if (ins.Op0Kind != OpKind.Register || NormalizeRegister(ins.Op0Register) != reg) continue;
            steps.Insert(0, $"0x{item.Rva:X8} {item.Text}");
            if (ins.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx)
            {
                if (ins.Op1Kind == OpKind.Memory && IsStackBase(ins.MemoryBase) && seeds.Contains(SignedDisp(ins)))
                    return (SignedDisp(ins), item.Rva, "stack-value", steps.ToArray());
                if (ins.Op1Kind == OpKind.Register)
                {
                    reg = NormalizeRegister(ins.Op1Register);
                    continue;
                }
            }
            if (ins.Mnemonic == Mnemonic.Lea && ins.Op1Kind == OpKind.Memory && IsStackBase(ins.MemoryBase) && seeds.Contains(SignedDisp(ins)))
                return (SignedDisp(ins), item.Rva, "stack-address", steps.ToArray());
            return null;
        }
        return null;
    }

    private static (uint Rva, string[] Steps)? TraceRegisterAddressOrigin(
        List<Decoded> body, int index, Register register, string guardBase, long expected, int limit)
    {
        var reg = NormalizeRegister(register);
        var steps = new List<string>();
        for (var i = index; i >= 0 && index - i < limit; i--)
        {
            var item = body[i];
            var ins = item.Instruction;
            if (ins.Op0Kind != OpKind.Register || NormalizeRegister(ins.Op0Register) != reg) continue;
            steps.Insert(0, $"0x{item.Rva:X8} {item.Text}");
            if (ins.Mnemonic == Mnemonic.Lea && ins.Op1Kind == OpKind.Memory &&
                SameRegister(ins.MemoryBase, guardBase) && SignedDisp(ins) == expected)
                return (item.Rva, steps.ToArray());
            if (ins.Mnemonic == Mnemonic.Mov && ins.Op1Kind == OpKind.Register)
            {
                reg = NormalizeRegister(ins.Op1Register);
                continue;
            }
            return null;
        }
        return null;
    }

    private static string? FindGuardBase(CommitPe pe, uint? guardRva, long expected)
    {
        if (guardRva is null) return null;
        var decoded = DecodeRange(pe, guardRva.Value, Math.Min(pe.TextEnd, guardRva.Value + 0x20));
        foreach (var item in decoded)
        {
            var ins = item.Instruction;
            for (var op = 0; op < ins.OpCount; op++)
            {
                if (ins.GetOpKind(op) == OpKind.Memory && !IsStackBase(ins.MemoryBase) && ins.MemoryBase != Register.None && SignedDisp(ins) == expected)
                    return NormalizeRegister(ins.MemoryBase).ToString();
            }
        }
        return null;
    }

    private static bool Correspond(OemObjectCommitField a, OemObjectCommitField b)
    {
        if (!a.Proven || !b.Proven || string.IsNullOrWhiteSpace(a.CommitFingerprint) || string.IsNullOrWhiteSpace(b.CommitFingerprint))
            return false;
        return string.Equals(a.CommitFingerprint, b.CommitFingerprint, StringComparison.Ordinal);
    }

    private static bool LooksStringCopy(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var s = symbol.ToLowerInvariant();
        if (!s.Contains("cduistring", StringComparison.Ordinal)) return false;
        return s.Contains("??4", StringComparison.Ordinal) ||
               s.Contains("assign", StringComparison.Ordinal) ||
               (s.Contains("??0", StringComparison.Ordinal) && s.Contains("abv", StringComparison.Ordinal));
    }

    private static string NormalizeSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "unknown";
        var s = symbol.ToLowerInvariant();
        if (s.Contains("??4", StringComparison.Ordinal) || s.Contains("assign", StringComparison.Ordinal)) return "assignment";
        if (s.Contains("??0", StringComparison.Ordinal)) return "copy-constructor";
        return "cduistring-copy";
    }

    private static bool IsSimpleMemoryWrite(Instruction ins) =>
        ins.Mnemonic is Mnemonic.Mov or Mnemonic.Movups or Mnemonic.Movaps or Mnemonic.Movq or Mnemonic.Movdqa or Mnemonic.Movdqu;

    private static bool IsDirectBranch(Instruction ins) =>
        ins.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;

    private static bool MentionsAl(Instruction ins) =>
        Enumerable.Range(0, ins.OpCount).Any(i => ins.GetOpKind(i) == OpKind.Register && ins.GetOpRegister(i) == Register.AL);

    private static bool IsStackBase(Register reg) => reg is Register.EBP or Register.ESP or Register.RBP or Register.RSP;

    private static bool SameRegister(Register reg, string normalized) =>
        string.Equals(NormalizeRegister(reg).ToString(), normalized, StringComparison.OrdinalIgnoreCase);

    private static Register NormalizeRegister(Register reg) => reg switch
    {
        Register.AL or Register.AH or Register.AX or Register.EAX or Register.RAX => Register.EAX,
        Register.BL or Register.BH or Register.BX or Register.EBX or Register.RBX => Register.EBX,
        Register.CL or Register.CH or Register.CX or Register.ECX or Register.RCX => Register.ECX,
        Register.DL or Register.DH or Register.DX or Register.EDX or Register.RDX => Register.EDX,
        Register.SI or Register.ESI or Register.RSI => Register.ESI,
        Register.DI or Register.EDI or Register.RDI => Register.EDI,
        Register.BP or Register.EBP or Register.RBP => Register.EBP,
        Register.SP or Register.ESP or Register.RSP => Register.ESP,
        _ => reg
    };

    private static long SignedDisp(Instruction ins) => unchecked((int)(uint)ins.MemoryDisplacement64);

    private static OemCommitInsn Out(Decoded x, string[] tags) => new(x.Rva, x.Bytes, x.Text, tags);
    private static string Hex(uint? value) => value is null ? "unresolved" : $"0x{value:X8}";
    private static string SignedHex(long value) => value < 0 ? $"-0x{-value:X}" : $"+0x{value:X}";
    private static string JoinHex(uint[] values) => values.Length == 0 ? "none" : string.Join(',', values.Select(x => $"0x{x:X8}"));

    private sealed record Decoded(uint Rva, string Bytes, string Text, Instruction Instruction);
    private sealed record Section(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
    {
        public bool Contains(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
    }
    private sealed record Import(string Dll, string Name, uint IatRva);

    private sealed class CommitPe
    {
        public byte[] Bytes { get; }
        public bool Pe32Plus { get; }
        public ulong ImageBase { get; }
        public List<Section> Sections { get; }
        public List<Import> Imports { get; }
        public uint TextStart { get; }
        public uint TextEnd { get; }

        private CommitPe(byte[] bytes, bool plus, ulong imageBase, List<Section> sections, List<Import> imports)
        {
            Bytes = bytes;
            Pe32Plus = plus;
            ImageBase = imageBase;
            Sections = sections;
            Imports = imports;
            var text = sections.First(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
            TextStart = text.VirtualAddress;
            TextEnd = text.VirtualAddress + Math.Min(text.VirtualSize == 0 ? text.RawSize : text.VirtualSize, text.RawSize);
        }

        public static CommitPe Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') throw new InvalidDataException("Not a PE image.");
            var pe = I32(bytes, 0x3C);
            Ensure(bytes, pe, 24);
            var sectionsCount = U16(bytes, pe + 6);
            var optionalSize = U16(bytes, pe + 20);
            var optional = pe + 24;
            var magic = U16(bytes, optional);
            var plus = magic == 0x20B;
            if (!plus && magic != 0x10B) throw new InvalidDataException("Unsupported PE optional header.");
            ulong imageBase = plus ? U64(bytes, optional + 24) : U32(bytes, optional + 28);
            var table = optional + optionalSize;
            var sections = new List<Section>();
            for (var i = 0; i < sectionsCount; i++)
            {
                var off = table + i * 40;
                Ensure(bytes, off, 40);
                sections.Add(new Section(
                    Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'),
                    U32(bytes, off + 8), U32(bytes, off + 12), U32(bytes, off + 16), U32(bytes, off + 20)));
            }
            var temp = new CommitPe(bytes, plus, imageBase, sections, []);
            return new CommitPe(bytes, plus, imageBase, sections, temp.ParseImports(optional));
        }

        public int RvaToOffset(uint rva)
        {
            var section = Sections.FirstOrDefault(x => x.Contains(rva)) ?? throw new InvalidDataException($"RVA 0x{rva:X8} outside sections.");
            return checked((int)(section.RawPointer + (rva - section.VirtualAddress)));
        }

        public string? ResolveImport(Instruction ins)
        {
            if (ins.Mnemonic != Mnemonic.Call || ins.Op0Kind != OpKind.Memory) return null;
            var address = ins.MemoryDisplacement64;
            var import = Imports.FirstOrDefault(x => ImageBase + x.IatRva == address);
            return import is null ? null : import.Dll + "!" + import.Name;
        }

        private List<Import> ParseImports(int optional)
        {
            var dataDirectory = optional + (Pe32Plus ? 112 : 96);
            var importRva = U32(Bytes, dataDirectory + 8);
            if (importRva == 0) return [];
            var result = new List<Import>();
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
                    result.Add(new Import(dll, name, firstThunk + checked((uint)(index * step))));
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

    private static List<Decoded> DecodeRange(CommitPe pe, uint startRva, uint endRva)
    {
        startRva = Math.Max(startRva, pe.TextStart);
        endRva = Math.Min(endRva, pe.TextEnd);
        if (endRva <= startRva) return [];
        var start = pe.RvaToOffset(startRva);
        var end = pe.RvaToOffset(endRva - 1) + 1;
        var bytes = pe.Bytes.AsSpan(start, end - start).ToArray();
        var decoder = Iced.Intel.Decoder.Create(pe.Pe32Plus ? 64 : 32, new ByteArrayCodeReader(bytes));
        decoder.IP = startRva;
        var formatter = new IntelFormatter();
        var output = new CommitFormatterOutput();
        var result = new List<Decoded>();
        while (decoder.CanDecode && decoder.IP < endRva && result.Count < 250000)
        {
            decoder.Decode(out var ins);
            if (ins.Code == Code.INVALID || ins.Length == 0) break;
            var rva = checked((uint)ins.IP);
            var off = pe.RvaToOffset(rva);
            var raw = Convert.ToHexString(pe.Bytes.AsSpan(off, ins.Length)).ToLowerInvariant();
            formatter.Format(in ins, output);
            result.Add(new Decoded(rva, raw, output.Take(), ins));
        }
        return result;
    }

    private sealed class CommitFormatterOutput : FormatterOutput
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public string Take() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    private static ushort U16(byte[] b, int o) { Ensure(b, o, 2); return BitConverter.ToUInt16(b, o); }
    private static uint U32(byte[] b, int o) { Ensure(b, o, 4); return BitConverter.ToUInt32(b, o); }
    private static ulong U64(byte[] b, int o) { Ensure(b, o, 8); return BitConverter.ToUInt64(b, o); }
    private static int I32(byte[] b, int o) { Ensure(b, o, 4); return BitConverter.ToInt32(b, o); }
    private static void Ensure(byte[] b, int o, int n) { if (o < 0 || n < 0 || o + n > b.Length) throw new InvalidDataException("PE bounds check failed."); }
}
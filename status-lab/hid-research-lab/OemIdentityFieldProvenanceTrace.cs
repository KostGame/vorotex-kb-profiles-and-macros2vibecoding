using System.Buffers.Binary;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemProvenanceInsn(uint Rva, string Bytes, string Text, string[] Tags);
internal sealed record OemMemberEdge(uint Rva, string Kind, string BaseRegister, ulong Offset, string Text);
internal sealed record OemProvenanceHelper(
    uint EntryRva,
    List<OemProvenanceInsn> Instructions,
    List<OemMemberEdge> MemberEdges,
    uint[] DirectCalls,
    string Fingerprint);
internal sealed record OemFieldProvenance(
    string Field,
    uint XrefRva,
    uint? EqualityCallRva,
    uint? TestRva,
    uint? BranchRva,
    string? BranchText,
    string MatchPathDecision,
    List<OemProvenanceInsn> MatchPath,
    List<OemMemberEdge> MemberEdges,
    List<OemProvenanceHelper> Helpers,
    ulong ExpectedMember,
    bool DirectWriteToExpectedMember,
    bool ExpectedMemberUsed,
    bool Proven,
    string Fingerprint,
    List<string> Notes);
internal sealed record OemIdentityFieldProvenanceSide(
    string Executable,
    OemFieldProvenance DevName,
    OemFieldProvenance DevCmpStr,
    List<string> Notes);
internal sealed record OemIdentityFieldProvenanceReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemIdentityFieldProvenanceSide A,
    OemIdentityFieldProvenanceSide B,
    bool DevNamePathCorrespondence,
    bool DevCmpStrPathCorrespondence,
    bool DevNameToMember20Proven,
    bool DevCmpStrToMember3EcProven,
    List<string> Evidence,
    List<string> Notes);

internal static class OemIdentityFieldProvenanceAnalyzer
{
    private const ulong DevNameMember = 0x20;
    private const ulong DevCmpMember = 0x3EC;

    public static OemIdentityFieldProvenanceReport Analyze(string exeA, string exeB)
    {
        var semantic = OemIdentitySemanticBridgeAnalyzer.Analyze(exeA, exeB);
        var a = AnalyzeSide(Path.GetFullPath(exeA), semantic.A);
        var b = AnalyzeSide(Path.GetFullPath(exeB), semantic.B);

        var nameCorrespondence = SameFingerprint(a.DevName.Fingerprint, b.DevName.Fingerprint);
        var cmpCorrespondence = SameFingerprint(a.DevCmpStr.Fingerprint, b.DevCmpStr.Fingerprint);
        var nameProven = nameCorrespondence && a.DevName.Proven && b.DevName.Proven;
        var cmpProven = cmpCorrespondence && a.DevCmpStr.Proven && b.DevCmpStr.Proven;

        var verdict = nameProven && cmpProven ? "IDENTITY_FIELD_PROVENANCE_COMPLETE" :
                      nameProven || cmpProven ? "IDENTITY_FIELD_PROVENANCE_PARTIAL" :
                      a.DevName.MatchPath.Count > 0 && b.DevName.MatchPath.Count > 0 &&
                      a.DevCmpStr.MatchPath.Count > 0 && b.DevCmpStr.MatchPath.Count > 0
                          ? "PARSER_MATCH_PATHS_TRACED"
                          : "IDENTITY_FIELD_PROVENANCE_UNRESOLVED";

        var evidence = new List<string>();
        if (nameCorrespondence)
            evidence.Add("DevName parser match-path fingerprints correspond between VOROTEX and SXS-W909.");
        if (cmpCorrespondence)
            evidence.Add("DevCmpStr parser match-path fingerprints correspond between VOROTEX and SXS-W909.");
        if (nameProven)
            evidence.Add("DevName has an explicit branch-local persistent write to runtime member +0x20 on both OEM sides.");
        if (cmpProven)
            evidence.Add("DevCmpStr has an explicit branch-local persistent write to runtime member +0x3EC on both OEM sides.");
        if (!nameProven && a.DevName.ExpectedMemberUsed && b.DevName.ExpectedMemberUsed)
            evidence.Add("DevName branch/helper traces use +0x20 on both sides, but use/proximity alone is not accepted as provenance proof.");
        if (!cmpProven && a.DevCmpStr.ExpectedMemberUsed && b.DevCmpStr.ExpectedMemberUsed)
            evidence.Add("DevCmpStr branch/helper traces use +0x3EC on both sides, but use/proximity alone is not accepted as provenance proof.");

        return new OemIdentityFieldProvenanceReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static parser data-flow trace from aligned Ndevice field-key branches toward persistent runtime members",
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
                "The trace starts only from instruction-aligned field-key xrefs already recovered by the semantic-bridge stage.",
                "A field is marked PROVEN only for an explicit branch-local persistent memory write at the expected member offset; helper/member proximity is evidence but not proof.",
                "Stack-frame offsets, immediate constants, and large container offsets are not treated as runtime identity members.",
                "Direct helper functions are decoded read-only only to expose bounded member uses and call structure; no code is executed.",
                "All analysis reads executable bytes only; no process or keyboard device is opened."
            ]);
    }

    public static string ToText(OemIdentityFieldProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - OEM Identity Field Provenance Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, reports/writes, process launch/attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"DevName path correspondence: {report.DevNamePathCorrespondence}");
        sb.AppendLine($"DevCmpStr path correspondence: {report.DevCmpStrPathCorrespondence}");
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

    private static void AppendSide(StringBuilder sb, string label, OemIdentityFieldProvenanceSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        AppendField(sb, side.DevName);
        AppendField(sb, side.DevCmpStr);
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static void AppendField(StringBuilder sb, OemFieldProvenance field)
    {
        sb.AppendLine($"  {field.Field}: xref=0x{field.XrefRva:X8}; expected=+0x{field.ExpectedMember:X}; proven={field.Proven}");
        sb.AppendLine($"    equalityCall={Hex(field.EqualityCallRva)}; test={Hex(field.TestRva)}; branch={Hex(field.BranchRva)} {field.BranchText ?? string.Empty}");
        sb.AppendLine($"    match-path={field.MatchPathDecision}; instructions={field.MatchPath.Count}; directExpectedWrite={field.DirectWriteToExpectedMember}; expectedMemberUsed={field.ExpectedMemberUsed}");
        foreach (var edge in field.MemberEdges.Take(40))
            sb.AppendLine($"    edge 0x{edge.Rva:X8} {edge.Kind} [{edge.BaseRegister}+0x{edge.Offset:X}] :: {edge.Text}");
        foreach (var helper in field.Helpers.Take(12))
        {
            sb.AppendLine($"    helper 0x{helper.EntryRva:X8}; members={helper.MemberEdges.Count}; calls={helper.DirectCalls.Length}; fingerprint={helper.Fingerprint}");
            foreach (var edge in helper.MemberEdges.Take(24))
                sb.AppendLine($"      edge 0x{edge.Rva:X8} {edge.Kind} [{edge.BaseRegister}+0x{edge.Offset:X}] :: {edge.Text}");
        }
        sb.AppendLine($"    fingerprint={field.Fingerprint}");
        foreach (var note in field.Notes) sb.AppendLine("    NOTE: " + note);
    }

    private static OemIdentityFieldProvenanceSide AnalyzeSide(string exe, OemSemanticSide semantic)
    {
        var pe = ProvenancePe.Parse(exe);
        var notes = new List<string>();
        var nameXref = semantic.DevNameAlignedXrefs.FirstOrDefault();
        var cmpXref = semantic.DevCmpStrAlignedXrefs.FirstOrDefault();

        var name = nameXref is null
            ? EmptyField("DevName", DevNameMember, "No aligned DevName xref from semantic bridge.")
            : AnalyzeField(pe, nameXref, DevNameMember);
        var cmp = cmpXref is null
            ? EmptyField("DevCmpStr", DevCmpMember, "No aligned DevCmpStr xref from semantic bridge.")
            : AnalyzeField(pe, cmpXref, DevCmpMember);

        if (!name.Proven)
            notes.Add("DevName -> +0x20 remains unproven unless an explicit branch-local persistent write is recovered.");
        if (!cmp.Proven)
            notes.Add("DevCmpStr -> +0x3EC remains unproven unless an explicit branch-local persistent write is recovered.");

        return new OemIdentityFieldProvenanceSide(Path.GetFileName(exe), name, cmp, notes);
    }

    private static OemFieldProvenance AnalyzeField(ProvenancePe pe, OemAlignedXref xref, ulong expectedMember)
    {
        var notes = new List<string>();
        var window = DecodeForward(pe, xref.InstructionRva, 0xA00);
        var xrefIndex = window.FindIndex(x => x.Rva == xref.InstructionRva);
        if (xrefIndex < 0)
            return EmptyField(xref.Token, expectedMember, "Aligned xref could not be decoded from executable bytes.");

        var equalityIndex = FindNext(window, xrefIndex + 1, 8, x => x.Instruction.Mnemonic == Mnemonic.Call);
        var testIndex = FindNext(window, Math.Max(xrefIndex + 1, equalityIndex + 1), 8,
            x => x.Instruction.Mnemonic == Mnemonic.Test && MentionsAl(x.Instruction));
        var branchIndex = FindNext(window, Math.Max(xrefIndex + 1, testIndex + 1), 4,
            x => x.Instruction.FlowControl == FlowControl.ConditionalBranch);

        if (testIndex < 0 || branchIndex < 0)
            notes.Add("Field equality-test / conditional branch pair was not recovered in the bounded xref window.");

        var pathStart = -1;
        var decision = "unresolved";
        if (branchIndex >= 0)
        {
            var branch = window[branchIndex].Instruction;
            if (branch.Mnemonic == Mnemonic.Je)
            {
                pathStart = branchIndex + 1;
                decision = "equality=true follows fallthrough after JE/JZ";
            }
            else if (branch.Mnemonic == Mnemonic.Jne)
            {
                var target = checked((uint)branch.NearBranchTarget);
                pathStart = window.FindIndex(x => x.Rva == target);
                decision = "equality=true follows JNE/JNZ target";
            }
            else
            {
                notes.Add($"Unsupported equality branch mnemonic: {branch.Mnemonic}; match-path not guessed.");
            }
        }

        var matchPath = pathStart >= 0 ? WalkMatchPath(window, pathStart) : [];
        var edges = matchPath.SelectMany(ExtractMemberEdges).ToList();
        var directCalls = matchPath
            .Where(x => x.Instruction.Mnemonic == Mnemonic.Call && IsDirectCall(x.Instruction))
            .Select(x => checked((uint)x.Instruction.NearBranchTarget))
            .Distinct()
            .Take(12)
            .ToArray();
        var helpers = directCalls.Select(target => TraceHelper(pe, target)).ToList();

        var directExpectedWrites = edges.Where(x => x.Offset == expectedMember && x.Kind == "write").ToList();
        var helperExpectedUses = helpers.SelectMany(x => x.MemberEdges).Where(x => x.Offset == expectedMember).ToList();
        var expectedUsed = directExpectedWrites.Count > 0 || edges.Any(x => x.Offset == expectedMember) || helperExpectedUses.Count > 0;

        // Deliberately strict. A helper that merely touches the same offset is not enough to bind the parsed field value to it.
        var proven = directExpectedWrites.Count > 0;
        if (expectedUsed && !proven)
            notes.Add($"+0x{expectedMember:X} is visible in the branch/helper trace, but no explicit branch-local persistent write was recovered; proximity/use is not proof.");
        if (!expectedUsed)
            notes.Add($"No bounded parser match-path/helper use of +0x{expectedMember:X} was recovered.");

        var fingerprint = Fingerprint(matchPath);
        return new OemFieldProvenance(
            xref.Token,
            xref.InstructionRva,
            equalityIndex >= 0 ? window[equalityIndex].Rva : null,
            testIndex >= 0 ? window[testIndex].Rva : null,
            branchIndex >= 0 ? window[branchIndex].Rva : null,
            branchIndex >= 0 ? window[branchIndex].Text : null,
            decision,
            matchPath.Select((x, i) => ToOutput(x, i == 0 ? ["match_path_entry"] : [])).ToList(),
            edges,
            helpers,
            expectedMember,
            directExpectedWrites.Count > 0,
            expectedUsed,
            proven,
            fingerprint,
            notes);
    }

    private static OemFieldProvenance EmptyField(string field, ulong expected, string note) =>
        new(field, 0, null, null, null, null, "unresolved", [], [], [], expected, false, false, false, string.Empty, [note]);

    private static List<Decoded> WalkMatchPath(List<Decoded> window, int start)
    {
        var result = new List<Decoded>();
        for (var i = start; i >= 0 && i < window.Count && result.Count < 180; i++)
        {
            var item = window[i];
            result.Add(item);
            if (item.Instruction.Mnemonic == Mnemonic.Ret) break;
            if (item.Instruction.FlowControl == FlowControl.UnconditionalBranch)
                break; // bounded branch-local evidence only; do not silently cross the parser dispatch join.
        }
        return result;
    }

    private static OemProvenanceHelper TraceHelper(ProvenancePe pe, uint entry)
    {
        List<Decoded> decoded;
        try { decoded = DecodeForward(pe, entry, 0x700); }
        catch { return new OemProvenanceHelper(entry, [], [], [], string.Empty); }

        var body = new List<Decoded>();
        foreach (var item in decoded.Take(160))
        {
            body.Add(item);
            if (item.Instruction.Mnemonic == Mnemonic.Ret) break;
        }
        var edges = body.SelectMany(ExtractMemberEdges).ToList();
        var calls = body
            .Where(x => x.Instruction.Mnemonic == Mnemonic.Call && IsDirectCall(x.Instruction))
            .Select(x => checked((uint)x.Instruction.NearBranchTarget))
            .Distinct()
            .Take(24)
            .ToArray();
        return new OemProvenanceHelper(
            entry,
            body.Select((x, i) => ToOutput(x, i == 0 ? ["helper_entry"] : [])).ToList(),
            edges,
            calls,
            Fingerprint(body));
    }

    private static IEnumerable<OemMemberEdge> ExtractMemberEdges(Decoded item)
    {
        var ins = item.Instruction;
        if (!HasMemoryOperand(ins)) yield break;
        var baseReg = ins.MemoryBase;
        if (baseReg is Register.None or Register.EBP or Register.ESP or Register.RBP or Register.RSP) yield break;
        var offset = ins.MemoryDisplacement64;
        if (offset > 0x1000) yield break;

        var kind = ins.Mnemonic == Mnemonic.Lea ? "address" :
                   ins.Op0Kind == OpKind.Memory && IsWriteMnemonic(ins.Mnemonic) ? "write" : "read";
        yield return new OemMemberEdge(item.Rva, kind, baseReg.ToString(), offset, item.Text);
    }

    private static bool HasMemoryOperand(Instruction ins)
    {
        for (var i = 0; i < ins.OpCount; i++)
            if (ins.GetOpKind(i) == OpKind.Memory) return true;
        return false;
    }

    private static bool IsWriteMnemonic(Mnemonic mnemonic) => mnemonic is
        Mnemonic.Mov or Mnemonic.Add or Mnemonic.Sub or Mnemonic.And or Mnemonic.Or or Mnemonic.Xor or
        Mnemonic.Inc or Mnemonic.Dec or Mnemonic.Xchg;

    private static bool IsDirectCall(Instruction ins) =>
        ins.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;

    private static bool MentionsAl(Instruction ins) =>
        (ins.OpCount > 0 && ins.GetOpKind(0) == OpKind.Register && ins.Op0Register == Register.AL) ||
        (ins.OpCount > 1 && ins.GetOpKind(1) == OpKind.Register && ins.Op1Register == Register.AL);

    private static int FindNext(List<Decoded> items, int start, int maxCount, Func<Decoded, bool> predicate)
    {
        for (var i = Math.Max(0, start); i < items.Count && i < start + maxCount; i++)
            if (predicate(items[i])) return i;
        return -1;
    }

    private static string Fingerprint(IEnumerable<Decoded> items)
    {
        return string.Join('>', items.Take(120).Select(x =>
        {
            var mnemonic = x.Instruction.Mnemonic.ToString().ToUpperInvariant();
            var edge = ExtractMemberEdges(x).FirstOrDefault();
            if (edge is null) return x.Instruction.FlowControl == FlowControl.ConditionalBranch ? "JCC" :
                                     x.Instruction.FlowControl == FlowControl.UnconditionalBranch ? "JMP" :
                                     x.Instruction.Mnemonic == Mnemonic.Call ? "CALL" : mnemonic;
            return $"{mnemonic}({edge.Kind}:+0x{edge.Offset:X})";
        }));
    }

    private static bool SameFingerprint(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.Ordinal);

    private static OemProvenanceInsn ToOutput(Decoded x, string[] tags) => new(x.Rva, x.Bytes, x.Text, tags);
    private static string Hex(uint? value) => value is null ? "unresolved" : $"0x{value:X8}";

    private static List<Decoded> DecodeForward(ProvenancePe pe, uint startRva, uint byteCount)
    {
        var section = pe.SectionForRva(startRva)
            ?? throw new InvalidDataException($"RVA 0x{startRva:X8} outside PE sections.");
        var sectionEnd = section.VirtualAddress + Math.Min(section.VirtualSize == 0 ? section.RawSize : section.VirtualSize, section.RawSize);
        var endRva = Math.Min(sectionEnd, startRva + byteCount);
        var start = pe.RvaToOffset(startRva);
        var end = pe.RvaToOffset(endRva - 1) + 1;
        var bytes = pe.Bytes.AsSpan(start, end - start).ToArray();
        var decoder = Decoder.Create(pe.Pe32Plus ? 64 : 32, new ByteArrayCodeReader(bytes));
        decoder.IP = startRva;
        var formatter = new IntelFormatter();
        var output = new ProvenanceFormatterOutput();
        var result = new List<Decoded>();
        while (decoder.CanDecode && decoder.IP < endRva && result.Count < 20000)
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

    private sealed record Decoded(uint Rva, string Bytes, string Text, Instruction Instruction);
    private sealed record PeSection(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
    {
        public bool Contains(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
    }

    private sealed class ProvenancePe
    {
        public byte[] Bytes { get; }
        public bool Pe32Plus { get; }
        public List<PeSection> Sections { get; }

        private ProvenancePe(byte[] bytes, bool plus, List<PeSection> sections)
        {
            Bytes = bytes;
            Pe32Plus = plus;
            Sections = sections;
        }

        public static ProvenancePe Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("Not a PE image.");
            var pe = I32(bytes, 0x3C);
            Ensure(bytes, pe, 24);
            if (bytes[pe] != (byte)'P' || bytes[pe + 1] != (byte)'E')
                throw new InvalidDataException("PE signature missing.");
            var sectionCount = U16(bytes, pe + 6);
            var optionalSize = U16(bytes, pe + 20);
            var optional = pe + 24;
            var magic = U16(bytes, optional);
            var plus = magic == 0x20B;
            if (!plus && magic != 0x10B)
                throw new InvalidDataException($"Unsupported PE magic 0x{magic:X4}.");
            var table = optional + optionalSize;
            var sections = new List<PeSection>();
            for (var i = 0; i < sectionCount; i++)
            {
                var off = table + i * 40;
                Ensure(bytes, off, 40);
                sections.Add(new PeSection(
                    Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0'),
                    U32(bytes, off + 8), U32(bytes, off + 12), U32(bytes, off + 16), U32(bytes, off + 20)));
            }
            return new ProvenancePe(bytes, plus, sections);
        }

        public PeSection? SectionForRva(uint rva) => Sections.FirstOrDefault(x => x.Contains(rva));

        public int RvaToOffset(uint rva)
        {
            var section = SectionForRva(rva) ?? throw new InvalidDataException($"RVA 0x{rva:X8} outside PE sections.");
            var delta = rva - section.VirtualAddress;
            if (delta >= section.RawSize) throw new InvalidDataException($"RVA 0x{rva:X8} has no raw-file bytes.");
            return checked((int)(section.RawPointer + delta));
        }

        private static ushort U16(byte[] b, int o) { Ensure(b, o, 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)); }
        private static uint U32(byte[] b, int o) { Ensure(b, o, 4); return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)); }
        private static int I32(byte[] b, int o) { Ensure(b, o, 4); return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4)); }
        private static void Ensure(byte[] b, int o, int n)
        {
            if (o < 0 || n < 0 || o > b.Length - n) throw new InvalidDataException("PE offset outside file.");
        }
    }

    private sealed class ProvenanceFormatterOutput : FormatterOutput
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public string Take()
        {
            var text = _sb.ToString();
            _sb.Clear();
            return text;
        }
    }
}
using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemSleepAlignedXref(
    string Token,
    uint TokenRva,
    uint InstructionRva,
    string Bytes,
    string Text,
    int OperandIndex,
    string OperandKind,
    uint FunctionStartRva,
    uint FunctionEndRva);

internal sealed record OemSleepReportWrite(
    int Offset,
    uint InstructionRva,
    string Bytes,
    string Text,
    string ValueKind,
    string ValueExpression,
    bool SleepValueProven);

internal sealed record OemSleepSetFeatureCall(
    uint CallRva,
    string Import,
    bool ReportLength41Proven,
    uint? ReportLength,
    string? ReportBufferSignature,
    int? StackBufferBaseDisplacement,
    string[] ArgumentSteps,
    OemSleepReportWrite[] ReportWrites,
    string[] NearbyCalls,
    string[] Notes);

internal sealed record OemKeyboardSleepReportSide(
    string Executable,
    string Sha256,
    bool KeyboardSpecificResourceFound,
    OemSleepAlignedXref[] AlignedXrefs,
    uint[] CandidateHandlerFunctions,
    OemSleepSetFeatureCall[] SetFeatureCalls,
    string[] HandlerToTransportPaths,
    bool AlignedSleepHandlerRecovered,
    bool SetFeatureTransportRecovered,
    bool ReportBuffer41Recovered,
    bool SleepValueToReportProven,
    string Fingerprint,
    string[] Notes);

internal sealed record OemKeyboardSleepReportTraceReport(
    int Schema,
    DateTimeOffset CreatedUtc,
    string Verdict,
    string Purpose,
    object Safety,
    OemKeyboardSleepReportSide A,
    OemKeyboardSleepReportSide B,
    bool TransportCorrespondence,
    bool ReportConstructionCorrespondence,
    bool BothSleepValueToReportProven,
    string[] Evidence,
    string[] Notes);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    private static readonly string[] KeyboardSleepTokens =
    [
        "KBSpecialFuncSet.xml",
        "Slider_Sleep_Time",
        "Edit_Sleep_Time",
        "Value_Sleep_Time",
        "SleepTime"
    ];

    internal static OemKeyboardSleepReportTraceReport AnalyzeKeyboardSleepReport(string exeA, string exeB)
    {
        var a = AnalyzeKeyboardSleepSide(Path.GetFullPath(exeA));
        var b = AnalyzeKeyboardSleepSide(Path.GetFullPath(exeB));

        var transportCorrespondence = a.SetFeatureTransportRecovered && b.SetFeatureTransportRecovered &&
            a.SetFeatureCalls.Length == b.SetFeatureCalls.Length;
        var reportCorrespondence = a.ReportBuffer41Recovered && b.ReportBuffer41Recovered &&
            string.Equals(a.Fingerprint, b.Fingerprint, StringComparison.Ordinal);
        var bothProven = a.SleepValueToReportProven && b.SleepValueToReportProven;

        var verdict = bothProven && reportCorrespondence
            ? "KEYBOARD_SLEEP_SETFEATURE_REPORT_PROVEN"
            : a.ReportBuffer41Recovered && b.ReportBuffer41Recovered && a.AlignedSleepHandlerRecovered && b.AlignedSleepHandlerRecovered
                ? "KEYBOARD_SLEEP_REPORT_PARTIAL"
                : a.AlignedSleepHandlerRecovered && b.AlignedSleepHandlerRecovered && transportCorrespondence
                    ? "SLEEPTIME_TO_TRANSPORT_TRACED"
                    : "KEYBOARD_SLEEP_REPORT_UNRESOLVED";

        var evidence = new List<string>();
        if (a.AlignedSleepHandlerRecovered && b.AlignedSleepHandlerRecovered)
            evidence.Add("Instruction-aligned references to keyboard-specific SleepTime UI/resource tokens were recovered in both OEM executables.");
        if (transportCorrespondence)
            evidence.Add("Both OEM executables expose corresponding statically decoded HidD_SetFeature transport call-sites.");
        if (a.ReportBuffer41Recovered && b.ReportBuffer41Recovered)
            evidence.Add("Both OEM SetFeature call-sites reconstruct a 41-byte report argument with bounded report-buffer write evidence.");
        if (bothProven)
            evidence.Add("Both OEM traces explicitly carry a keyboard SleepTime-derived value into an identified report-buffer field before HidD_SetFeature.");

        return new OemKeyboardSleepReportTraceReport(
            1,
            DateTimeOffset.UtcNow,
            verdict,
            "strict static read-only trace from keyboard-specific SleepTime UI/config anchors toward the 41-byte HidD_SetFeature report",
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
            transportCorrespondence,
            reportCorrespondence,
            bothProven,
            evidence.ToArray(),
            [
                "Only keyboard-specific KBSpecialFuncSet/SleepTime anchors are eligible for SleepTime provenance. setting_more.xml and generic power UI are intentionally excluded from proof.",
                "A 41-byte SetFeature report and nearby writes are transport evidence only. PROVEN requires an explicit SleepTime-derived value reaching a concrete report field.",
                "Direct-call graph paths are diagnostic structure; similarity or proximity cannot promote the verdict.",
                "All analysis reads executable/resource bytes only. No OEM code is executed and no HID/device handle is opened."
            ]);
    }

    internal static string KeyboardSleepReportToText(OemKeyboardSleepReportTraceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 HID Research Lab - Keyboard SleepTime -> SetFeature Report Trace");
        sb.AppendLine("Safety: STATIC READ-ONLY; no HID/device open, feature execution/replay, process attach/debug, patching or spoofing.");
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {report.Verdict}");
        sb.AppendLine($"Transport correspondence: {report.TransportCorrespondence}");
        sb.AppendLine($"Report construction correspondence: {report.ReportConstructionCorrespondence}");
        sb.AppendLine($"Both SleepTime -> report proven: {report.BothSleepValueToReportProven}");
        sb.AppendLine();
        AppendKeyboardSleepSide(sb, "A", report.A);
        AppendKeyboardSleepSide(sb, "B", report.B);
        sb.AppendLine("Evidence:");
        foreach (var item in report.Evidence) sb.AppendLine("  - " + item);
        sb.AppendLine();
        foreach (var note in report.Notes) sb.AppendLine("NOTE: " + note);
        return sb.ToString();
    }

    private static void AppendKeyboardSleepSide(StringBuilder sb, string label, OemKeyboardSleepReportSide side)
    {
        sb.AppendLine($"{label}: {side.Executable}");
        sb.AppendLine($"  SHA256={side.Sha256}");
        sb.AppendLine($"  keyboardResource={side.KeyboardSpecificResourceFound}; alignedHandler={side.AlignedSleepHandlerRecovered}; setFeature={side.SetFeatureTransportRecovered}; report41={side.ReportBuffer41Recovered}; sleepValueToReport={side.SleepValueToReportProven}");
        sb.AppendLine("  aligned keyboard sleep xrefs:");
        foreach (var x in side.AlignedXrefs.Take(40))
            sb.AppendLine($"    {x.Token} tokenRVA=0x{x.TokenRva:X8} ins=0x{x.InstructionRva:X8} fn=0x{x.FunctionStartRva:X8}..0x{x.FunctionEndRva:X8} op{x.OperandIndex}:{x.OperandKind} {x.Bytes} {x.Text}");
        sb.AppendLine("  handler -> transport paths:");
        foreach (var path in side.HandlerToTransportPaths.Take(40)) sb.AppendLine("    " + path);
        foreach (var call in side.SetFeatureCalls)
        {
            sb.AppendLine($"  HidD_SetFeature call=0x{call.CallRva:X8}; len41={call.ReportLength41Proven}; len={(call.ReportLength is null ? "?" : call.ReportLength.Value.ToString())}; buffer={call.ReportBufferSignature ?? "unresolved"}");
            foreach (var arg in call.ArgumentSteps) sb.AppendLine("    arg " + arg);
            foreach (var write in call.ReportWrites.Take(100))
                sb.AppendLine($"    report[{write.Offset}] @0x{write.InstructionRva:X8} {write.ValueKind}={write.ValueExpression} sleep={write.SleepValueProven} :: {write.Bytes} {write.Text}");
            foreach (var nested in call.NearbyCalls.Take(30)) sb.AppendLine("    call " + nested);
            foreach (var note in call.Notes) sb.AppendLine("    NOTE: " + note);
        }
        sb.AppendLine($"  fingerprint={side.Fingerprint}");
        foreach (var note in side.Notes) sb.AppendLine("  NOTE: " + note);
        sb.AppendLine();
    }

    private static OemKeyboardSleepReportSide AnalyzeKeyboardSleepSide(string exe)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("OEM executable was not found.", exe);
        var pe = NdevicePe.Parse(exe);
        var text = DecodeRange(pe, pe.TextStart, pe.TextEnd);
        var ui = KeyboardSleepUiTraceAnalyzer.Analyze(exe);
        var tokenRvas = ui.TokenOccurrences
            .Where(x => KeyboardSleepTokens.Contains(x.Token, StringComparer.Ordinal) && x.Rva is not null &&
                        string.Equals(Path.GetFileName(x.RelativePath), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Token, Rva: x.Rva!.Value))
            .Distinct()
            .ToArray();

        var aligned = new List<OemSleepAlignedXref>();
        foreach (var token in tokenRvas)
        {
            var targetVa = pe.ImageBase + token.Rva;
            for (var index = 0; index < text.Count; index++)
            {
                var item = text[index];
                for (var op = 0; op < item.Instruction.OpCount; op++)
                {
                    if (!SleepOperandReferences(item.Instruction, op, targetVa, token.Rva, out var kind)) continue;
                    var bounds = FindSleepFunctionBounds(text, index);
                    aligned.Add(new OemSleepAlignedXref(
                        token.Token,
                        token.Rva,
                        item.Rva,
                        SleepBytes(pe, item),
                        item.Text,
                        op,
                        kind,
                        bounds.Start,
                        bounds.End));
                }
            }
        }

        aligned = aligned
            .GroupBy(x => (x.Token, x.TokenRva, x.InstructionRva, x.OperandIndex))
            .Select(x => x.First())
            .OrderBy(x => x.InstructionRva)
            .Take(96)
            .ToList();

        var setFeatureIndexes = new List<int>();
        for (var i = 0; i < text.Count; i++)
        {
            var ins = text[i].Instruction;
            if (ins.Mnemonic != Mnemonic.Call) continue;
            var symbol = pe.ResolveImport(ins);
            if (symbol is not null && symbol.EndsWith("!HidD_SetFeature", StringComparison.OrdinalIgnoreCase))
                setFeatureIndexes.Add(i);
        }

        var calls = setFeatureIndexes.Select(i => TraceSleepSetFeatureCall(pe, text, i)).ToArray();
        var handlers = aligned.Select(x => x.FunctionStartRva).Distinct().OrderBy(x => x).ToArray();
        var transports = setFeatureIndexes.Select(i => FindSleepFunctionBounds(text, i).Start).Distinct().ToHashSet();
        var paths = BuildSleepCallPaths(pe, text, handlers, transports);
        var sleepValueProven = calls.Any(x => x.ReportWrites.Any(w => w.SleepValueProven));
        var report41 = calls.Any(x => x.ReportLength41Proven && x.ReportBufferSignature is not null && x.ReportWrites.Length > 0);
        var notes = new List<string>();
        if (aligned.Count == 0) notes.Add("No instruction-aligned keyboard-specific sleep token reference was recovered from the selected executable.");
        if (calls.Length == 0) notes.Add("No statically decoded HidD_SetFeature import call was recovered.");
        if (calls.Length > 0 && !report41) notes.Add("SetFeature was found, but a 41-byte report buffer plus bounded writes were not reconstructed conservatively.");
        if (report41 && !sleepValueProven) notes.Add("The 41-byte report construction is visible, but keyboard SleepTime value provenance has not yet reached a concrete report field. Use handler/call-path RVAs to narrow the next helper trace.");

        var fp = string.Join('|', calls.Select(c =>
            $"SET:{c.ReportLength41Proven}:{c.StackBufferBaseDisplacement}:{string.Join(',', c.ReportWrites.Select(w => $"{w.Offset}:{w.ValueKind}:{w.ValueExpression}"))}"));

        return new OemKeyboardSleepReportSide(
            Path.GetFileName(exe),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant(),
            ui.KeyboardSpecificResourceFound,
            aligned.ToArray(),
            handlers,
            calls,
            paths,
            aligned.Count > 0,
            calls.Length > 0,
            report41,
            sleepValueProven,
            fp,
            notes.ToArray());
    }

    private static OemSleepSetFeatureCall TraceSleepSetFeatureCall(NdevicePe pe, List<NdeviceDecoded> text, int callIndex)
    {
        var callItem = text[callIndex];
        var notes = new List<string>();
        var args = new List<string>();
        var nearbyCalls = new List<string>();
        var pushes = new List<(int Index, NdeviceDecoded Item)>();
        for (var i = callIndex - 1; i >= Math.Max(0, callIndex - 28); i--)
        {
            var item = text[i];
            if (item.Instruction.Mnemonic == Mnemonic.Call) break;
            if (item.Instruction.Mnemonic == Mnemonic.Push)
            {
                pushes.Add((i, item));
                if (pushes.Count == 3) break;
            }
        }
        pushes.Reverse();

        uint? reportLength = null;
        int? bufferDisp = null;
        string? bufferSignature = null;
        if (pushes.Count == 3)
        {
            var lengthPush = pushes[0].Item.Instruction;
            if (TryPushImmediate(lengthPush, out var len)) reportLength = len;
            args.Add($"length: 0x{pushes[0].Item.Rva:X8} {pushes[0].Item.Text}");

            var bufferPush = pushes[1];
            args.Add($"buffer: 0x{bufferPush.Item.Rva:X8} {bufferPush.Item.Text}");
            bufferDisp = ResolvePushedStackBuffer(text, bufferPush.Index, out bufferSignature);
            args.Add($"handle: 0x{pushes[2].Item.Rva:X8} {pushes[2].Item.Text}");
        }
        else notes.Add("Could not recover exactly three bounded x86 pushes immediately feeding HidD_SetFeature.");

        var function = FindSleepFunctionBounds(text, callIndex);
        var writes = new List<OemSleepReportWrite>();
        if (bufferDisp is not null)
        {
            var min = bufferDisp.Value;
            var max = min + 41;
            for (var i = function.StartIndex; i < callIndex; i++)
            {
                var item = text[i];
                var ins = item.Instruction;
                if (ins.Mnemonic != Mnemonic.Mov || ins.Op0Kind != OpKind.Memory || Normalize(ins.MemoryBase) != Register.EBP) continue;
                var disp = SignedDisp(ins);
                if (disp < min || disp >= max) continue;
                var offset = checked((int)(disp - min));
                var valueKind = "unresolved";
                var valueExpression = "?";
                if (TryInstructionImmediate(ins, 1, out var immediate))
                {
                    valueKind = "immediate";
                    valueExpression = $"0x{immediate:X}";
                }
                else if (ins.Op1Kind == OpKind.Register)
                {
                    valueKind = "register";
                    valueExpression = Normalize(ins.Op1Register).ToString();
                }
                else if (ins.Op1Kind == OpKind.Memory)
                {
                    valueKind = "memory";
                    valueExpression = item.Text.Split(',', 2).Length == 2 ? item.Text.Split(',', 2)[1] : "memory";
                }
                writes.Add(new OemSleepReportWrite(
                    offset,
                    item.Rva,
                    SleepBytes(pe, item),
                    item.Text,
                    valueKind,
                    valueExpression,
                    false));
            }
        }

        for (var i = Math.Max(function.StartIndex, callIndex - 80); i < callIndex; i++)
        {
            var item = text[i];
            if (item.Instruction.Mnemonic != Mnemonic.Call) continue;
            var symbol = pe.ResolveImport(item.Instruction);
            var target = IsDirectBranch(item.Instruction) ? $"0x{item.Instruction.NearBranchTarget:X8}" : symbol ?? "unresolved";
            nearbyCalls.Add($"0x{item.Rva:X8} {target} {item.Text}");
        }

        if (reportLength != 41) notes.Add("The recovered report-length argument is not the expected 41 bytes, or length provenance is unresolved.");
        if (bufferDisp is null) notes.Add("The report-buffer argument was not resolved to a local stack buffer.");
        if (bufferDisp is not null && writes.Count == 0) notes.Add("A stack report buffer was recovered, but no bounded direct writes were found before the SetFeature call.");

        return new OemSleepSetFeatureCall(
            callItem.Rva,
            pe.ResolveImport(callItem.Instruction) ?? "hid.dll!HidD_SetFeature",
            reportLength == 41,
            reportLength,
            bufferSignature,
            bufferDisp,
            args.ToArray(),
            writes.OrderBy(x => x.Offset).ThenBy(x => x.InstructionRva).ToArray(),
            nearbyCalls.ToArray(),
            notes.ToArray());
    }

    private static int? ResolvePushedStackBuffer(List<NdeviceDecoded> text, int pushIndex, out string? signature)
    {
        signature = null;
        var push = text[pushIndex].Instruction;
        if (push.Op0Kind != OpKind.Register) return null;
        var register = Normalize(push.Op0Register);
        for (var i = pushIndex - 1; i >= Math.Max(0, pushIndex - 20); i--)
        {
            var item = text[i];
            var ins = item.Instruction;
            if (ins.Op0Kind != OpKind.Register || Normalize(ins.Op0Register) != register) continue;
            if (ins.Mnemonic == Mnemonic.Lea && ins.Op1Kind == OpKind.Memory && Normalize(ins.MemoryBase) == Register.EBP)
            {
                var disp = checked((int)SignedDisp(ins));
                signature = $"[EBP{(disp < 0 ? "-" : "+")}0x{Math.Abs(disp):X}]";
                return disp;
            }
            if (ins.Mnemonic == Mnemonic.Mov) break;
        }
        return null;
    }

    private static string[] BuildSleepCallPaths(NdevicePe pe, List<NdeviceDecoded> text, uint[] handlerStarts, HashSet<uint> transportStarts)
    {
        var functions = BuildSleepFunctions(text);
        var functionByRva = new Dictionary<uint, uint>();
        foreach (var fn in functions)
            for (var i = fn.StartIndex; i <= fn.EndIndex && i < text.Count; i++) functionByRva[text[i].Rva] = fn.Start;

        var edges = new Dictionary<uint, HashSet<uint>>();
        foreach (var fn in functions)
        {
            foreach (var item in text.Skip(fn.StartIndex).Take(fn.EndIndex - fn.StartIndex + 1))
            {
                if (item.Instruction.Mnemonic != Mnemonic.Call || !IsDirectBranch(item.Instruction)) continue;
                var target = checked((uint)item.Instruction.NearBranchTarget);
                if (!functionByRva.TryGetValue(target, out var targetFn)) continue;
                if (!edges.TryGetValue(fn.Start, out var set)) edges[fn.Start] = set = [];
                set.Add(targetFn);
            }
        }

        var output = new List<string>();
        foreach (var start in handlerStarts)
        {
            if (transportStarts.Contains(start)) { output.Add($"0x{start:X8} [handler contains HidD_SetFeature]"); continue; }
            var queue = new Queue<(uint Fn, List<uint> Path)>();
            var seen = new HashSet<uint> { start };
            queue.Enqueue((start, [start]));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current.Path.Count > 5) continue;
                if (!edges.TryGetValue(current.Fn, out var next)) continue;
                foreach (var child in next)
                {
                    var path = new List<uint>(current.Path) { child };
                    if (transportStarts.Contains(child))
                    {
                        output.Add(string.Join(" -> ", path.Select(x => $"0x{x:X8}")) + " -> HidD_SetFeature");
                        continue;
                    }
                    if (seen.Add(child)) queue.Enqueue((child, path));
                }
            }
        }
        return output.Distinct().Take(80).ToArray();
    }

    private sealed record SleepFunction(uint Start, uint End, int StartIndex, int EndIndex);

    private static List<SleepFunction> BuildSleepFunctions(List<NdeviceDecoded> text)
    {
        var result = new List<SleepFunction>();
        var startIndex = 0;
        for (var i = 0; i < text.Count; i++)
        {
            if (i > startIndex && LooksFunctionPrologue(text, i))
            {
                result.Add(new SleepFunction(text[startIndex].Rva, text[i - 1].Rva, startIndex, i - 1));
                startIndex = i;
            }
        }
        if (startIndex < text.Count) result.Add(new SleepFunction(text[startIndex].Rva, text[^1].Rva, startIndex, text.Count - 1));
        return result;
    }

    private static (uint Start, uint End, int StartIndex, int EndIndex) FindSleepFunctionBounds(List<NdeviceDecoded> text, int index)
    {
        var start = index;
        for (var i = index; i >= Math.Max(0, index - 1600); i--)
        {
            if (LooksFunctionPrologue(text, i)) { start = i; break; }
            if (i < index && text[i].Instruction.Mnemonic == Mnemonic.Ret) { start = i + 1; break; }
        }
        var end = index;
        for (var i = index; i < Math.Min(text.Count, index + 2400); i++)
        {
            end = i;
            if (text[i].Instruction.Mnemonic == Mnemonic.Ret) break;
        }
        return (text[start].Rva, text[end].Rva, start, end);
    }

    private static bool LooksFunctionPrologue(List<NdeviceDecoded> text, int index)
    {
        if (index < 0 || index + 1 >= text.Count) return false;
        var a = text[index].Instruction;
        var b = text[index + 1].Instruction;
        return a.Mnemonic == Mnemonic.Push && a.Op0Kind == OpKind.Register && Normalize(a.Op0Register) == Register.EBP &&
               b.Mnemonic == Mnemonic.Mov && b.Op0Kind == OpKind.Register && Normalize(b.Op0Register) == Register.EBP &&
               b.Op1Kind == OpKind.Register && Normalize(b.Op1Register) == Register.ESP;
    }

    private static bool SleepOperandReferences(Instruction ins, int op, ulong targetVa, uint targetRva, out string kind)
    {
        kind = string.Empty;
        if (ins.GetOpKind(op) == OpKind.Memory)
        {
            var value = ins.MemoryDisplacement64;
            if (value == targetVa || value == targetRva) { kind = "memory"; return true; }
            return false;
        }
        if (TryInstructionImmediate(ins, op, out var valueImm) && (valueImm == targetVa || valueImm == targetRva))
        {
            kind = "immediate";
            return true;
        }
        return false;
    }

    private static bool TryPushImmediate(Instruction ins, out uint value)
    {
        value = 0;
        if (ins.Mnemonic != Mnemonic.Push) return false;
        return TryInstructionImmediate(ins, 0, out value);
    }

    private static bool TryInstructionImmediate(Instruction ins, int op, out uint value)
    {
        value = 0;
        if (op >= ins.OpCount) return false;
        switch (ins.GetOpKind(op))
        {
            case OpKind.Immediate8: value = ins.Immediate8; return true;
            case OpKind.Immediate16: value = ins.Immediate16; return true;
            case OpKind.Immediate32: value = ins.Immediate32; return true;
            case OpKind.Immediate8to16: value = unchecked((uint)ins.Immediate8to16); return true;
            case OpKind.Immediate8to32: value = unchecked((uint)ins.Immediate8to32); return true;
            default: return false;
        }
    }

    private static string SleepBytes(NdevicePe pe, NdeviceDecoded item)
    {
        var offset = pe.RvaToOffset(item.Rva);
        return Convert.ToHexString(pe.Bytes.AsSpan(offset, item.Instruction.Length)).ToLowerInvariant();
    }
}

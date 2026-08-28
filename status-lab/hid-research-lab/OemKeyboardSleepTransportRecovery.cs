using System.Buffers.Binary;

namespace Vorotex.K15.HidResearchLab;

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    // Owner test #2 for Issue #59 exposed the remaining resolver gap: the previous
    // fallback still iterated the linear Iced instruction list before checking raw
    // bytes. The OEM .text region contains mixed code/data and the linear sweep can
    // lose instruction alignment before the known direct-IAT call. This recovery
    // path therefore scans the raw .text section independently of linear decoding.
    // It accepts only the exact PE32 `FF 15 <absolute HidD_SetFeature IAT VA>` form.
    internal static OemKeyboardSleepReportTraceReport AnalyzeKeyboardSleepReportRecovered(string exeA, string exeB)
    {
        var baseline = AnalyzeKeyboardSleepReport(exeA, exeB);
        var a = RecoverKeyboardSleepTransport(Path.GetFullPath(exeA), baseline.A);
        var b = RecoverKeyboardSleepTransport(Path.GetFullPath(exeB), baseline.B);

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

        var evidence = baseline.Evidence
            .Where(x => !x.Contains("HidD_SetFeature transport", StringComparison.OrdinalIgnoreCase) &&
                        !x.Contains("41-byte report", StringComparison.OrdinalIgnoreCase) &&
                        !x.Contains("SleepTime-derived value", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (transportCorrespondence)
            evidence.Add("Both OEM executables expose corresponding exact PE32 FF 15 [absolute HidD_SetFeature IAT VA] transport call-sites recovered by scanning raw .text bytes independently of linear instruction decoding.");
        if (a.SetFeatureCalls.Any(x => x.ReportLength41Proven) && b.SetFeatureCalls.Any(x => x.ReportLength41Proven))
            evidence.Add("Both OEM direct-IAT call-sites contain the exact bounded x86 ABI prefix `push 0x29; lea report; push report; push handle` before HidD_SetFeature.");
        if (a.ReportBuffer41Recovered && b.ReportBuffer41Recovered)
            evidence.Add("Both recovered OEM SetFeature call-sites reconstruct a 41-byte report argument with bounded report-buffer write evidence.");
        if (bothProven)
            evidence.Add("Both OEM traces explicitly carry a keyboard SleepTime-derived value into an identified report-buffer field before HidD_SetFeature.");

        return baseline with
        {
            Verdict = verdict,
            A = a,
            B = b,
            TransportCorrespondence = transportCorrespondence,
            ReportConstructionCorrespondence = reportCorrespondence,
            BothSleepValueToReportProven = bothProven,
            Evidence = evidence.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static OemKeyboardSleepReportSide RecoverKeyboardSleepTransport(string exe, OemKeyboardSleepReportSide baseline)
    {
        if (baseline.SetFeatureTransportRecovered)
            return baseline;

        var pe = NdevicePe.Parse(exe);
        var text = DecodeRange(pe, pe.TextStart, pe.TextEnd);
        var recovered = RecoverExactPe32SetFeatureRvas(pe);
        if (recovered.Rvas.Length == 0)
        {
            var unresolvedNotes = baseline.Notes
                .Where(x => !x.StartsWith("No statically decoded HidD_SetFeature", StringComparison.Ordinal) &&
                            !x.StartsWith("Parsed HidD_SetFeature IAT VA", StringComparison.Ordinal))
                .Append(recovered.Note)
                .ToArray();
            return baseline with { Notes = unresolvedNotes };
        }

        var calls = new List<OemSleepSetFeatureCall>();
        var transportStarts = new HashSet<uint>();
        foreach (var rva in recovered.Rvas)
        {
            var index = text.FindIndex(x => x.Rva == rva && x.Instruction.Mnemonic == Iced.Intel.Mnemonic.Call);
            if (index >= 0)
            {
                calls.Add(TraceSleepSetFeatureCall(pe, text, index));
                transportStarts.Add(FindSleepFunctionBounds(text, index).Start);
            }
            else
            {
                calls.Add(TraceRawPe32SetFeatureCall(pe, rva, recovered.IatVa));
                transportStarts.Add(FindRawPe32FunctionStart(pe, rva));
            }
        }

        var callArray = calls.ToArray();
        var paths = BuildSleepCallPaths(pe, text, baseline.CandidateHandlerFunctions, transportStarts);
        var sleepValueProven = callArray.Any(x => x.ReportWrites.Any(w => w.SleepValueProven));
        var report41 = callArray.Any(x => x.ReportLength41Proven && x.ReportBufferSignature is not null && x.ReportWrites.Length > 0);
        var fp = string.Join('|', callArray.Select(c =>
            $"SET:{c.CallRva:X}:{c.ReportLength41Proven}:{c.StackBufferBaseDisplacement}:{string.Join(',', c.ReportWrites.Select(w => $"{w.Offset}:{w.ValueKind}:{w.ValueExpression}"))}"));

        var notes = baseline.Notes
            .Where(x => !x.StartsWith("No statically decoded HidD_SetFeature", StringComparison.Ordinal) &&
                        !x.StartsWith("Parsed HidD_SetFeature IAT VA", StringComparison.Ordinal))
            .ToList();
        notes.Add(recovered.Note);
        if (callArray.Any(x => x.ReportLength41Proven) && !report41)
            notes.Add("The exact SetFeature ABI proves a 41-byte report pointer, but bounded report-buffer writes are not yet reconstructed; transport evidence is kept separate from report-construction proof.");
        else if (!report41)
            notes.Add("Exact direct-IAT HidD_SetFeature transport was recovered, but the 41-byte report ABI was not reconstructed conservatively.");
        else if (!sleepValueProven)
            notes.Add("The recovered 41-byte report construction is transport evidence only; keyboard SleepTime provenance has not yet reached a concrete report field.");

        return baseline with
        {
            SetFeatureCalls = callArray,
            HandlerToTransportPaths = paths,
            SetFeatureTransportRecovered = callArray.Length > 0,
            ReportBuffer41Recovered = report41,
            SleepValueToReportProven = sleepValueProven,
            Fingerprint = fp,
            Notes = notes.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private sealed record RawSetFeatureRecovery(uint[] Rvas, uint IatVa, string Note);

    private static RawSetFeatureRecovery RecoverExactPe32SetFeatureRvas(NdevicePe pe)
    {
        if (pe.Pe32Plus)
            return new RawSetFeatureRecovery([], 0, "Raw direct-IAT fallback is intentionally limited to PE32 OEM binaries; PE32+ requires a separately proven RIP-relative resolver.");

        var import = pe.Imports.FirstOrDefault(x =>
            x.Dll.Equals("HID.DLL", StringComparison.OrdinalIgnoreCase) &&
            x.Name.Equals("HidD_SetFeature", StringComparison.OrdinalIgnoreCase));
        if (import is null)
            return new RawSetFeatureRecovery([], 0, "HID.DLL!HidD_SetFeature was not present in the parsed import table.");

        var iatVa64 = pe.ImageBase + import.IatRva;
        if (iatVa64 > uint.MaxValue)
            return new RawSetFeatureRecovery([], 0, "PE32 HidD_SetFeature IAT VA exceeded the 32-bit address space; exact FF 15 recovery was not attempted.");
        var iatVa = (uint)iatVa64;

        var textSection = pe.Sections.FirstOrDefault(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        if (textSection is null)
            return new RawSetFeatureRecovery([], iatVa, "PE image did not contain a .text section for raw SetFeature recovery.");

        var rawStart = checked((int)textSection.RawPointer);
        var rawEnd64 = (long)rawStart + textSection.RawSize;
        var rawEnd = checked((int)Math.Min(pe.Bytes.Length, rawEnd64));
        if (rawStart < 0 || rawStart >= rawEnd)
            return new RawSetFeatureRecovery([], iatVa, ".text raw range was empty or outside the PE image.");

        Span<byte> needle = stackalloc byte[6];
        needle[0] = 0xFF;
        needle[1] = 0x15;
        BinaryPrimitives.WriteUInt32LittleEndian(needle[2..], iatVa);

        var rvas = new List<uint>();
        for (var fileOffset = rawStart; fileOffset + needle.Length <= rawEnd; fileOffset++)
        {
            if (!pe.Bytes.AsSpan(fileOffset, needle.Length).SequenceEqual(needle)) continue;
            var delta = checked((uint)(fileOffset - rawStart));
            rvas.Add(textSection.VirtualAddress + delta);
        }

        var unique = rvas.Distinct().OrderBy(x => x).ToArray();
        var note = unique.Length > 0
            ? $"Recovered {unique.Length} exact PE32 direct-IAT HidD_SetFeature raw .text call(s) via FF 15 [0x{iatVa:X8}] without relying on linear instruction alignment; no OEM code or HID call was executed."
            : $"Parsed HidD_SetFeature IAT VA=0x{iatVa:X8}, but an independent raw .text scan found no exact FF 15 [absolute IAT VA] byte sequence.";
        return new RawSetFeatureRecovery(unique, iatVa, note);
    }

    private static OemSleepSetFeatureCall TraceRawPe32SetFeatureCall(NdevicePe pe, uint callRva, uint iatVa)
    {
        var notes = new List<string>
        {
            "Call-site RVA was recovered from an independent raw .text byte scan because the linear instruction sweep did not retain this instruction boundary."
        };
        var args = new List<string>();
        var reportLength41 = false;
        uint? reportLength = null;
        int? bufferDisp = null;
        string? bufferSignature = null;

        int callOffset;
        try { callOffset = pe.RvaToOffset(callRva); }
        catch
        {
            return new OemSleepSetFeatureCall(callRva, "HID.DLL!HidD_SetFeature", false, null, null, null, [], [], [], ["Recovered raw SetFeature RVA could not be mapped back to a PE file offset."]);
        }

        // Proven OEM PE32 call-site shape from the bounded Vendor Static windows:
        // 6A 29                push 0x29 (41-byte report length)
        // 8D 85 <disp32>       lea eax,[ebp+report]
        // 50                   push eax (report pointer)
        // FF 36                push dword ptr [esi] (handle)
        // FF 15 <iat-va32>     call dword ptr [HidD_SetFeature]
        var abiStart = callOffset - 11;
        if (abiStart >= 0 && callOffset + 6 <= pe.Bytes.Length &&
            pe.Bytes[abiStart] == 0x6A && pe.Bytes[abiStart + 1] == 0x29 &&
            pe.Bytes[abiStart + 2] == 0x8D && pe.Bytes[abiStart + 3] == 0x85 &&
            pe.Bytes[abiStart + 8] == 0x50 &&
            pe.Bytes[abiStart + 9] == 0xFF && pe.Bytes[abiStart + 10] == 0x36 &&
            pe.Bytes[callOffset] == 0xFF && pe.Bytes[callOffset + 1] == 0x15 &&
            BinaryPrimitives.ReadUInt32LittleEndian(pe.Bytes.AsSpan(callOffset + 2, 4)) == iatVa)
        {
            reportLength41 = true;
            reportLength = 41;
            bufferDisp = BinaryPrimitives.ReadInt32LittleEndian(pe.Bytes.AsSpan(abiStart + 4, 4));
            bufferSignature = bufferDisp.Value < 0
                ? $"EBP-0x{Math.Abs((long)bufferDisp.Value):X}"
                : $"EBP+0x{bufferDisp.Value:X}";
            var abiRva = callRva - 11;
            args.Add($"length: 0x{abiRva:X8} push 29h");
            args.Add($"buffer: 0x{abiRva + 2:X8} lea eax,[{bufferSignature}]; push eax");
            args.Add($"handle: 0x{abiRva + 9:X8} push dword ptr [esi]");
            notes.Add("Exact bounded PE32 SetFeature ABI prefix recovered from raw bytes: length=41, EBP-relative report pointer, and handle push.");
        }
        else
        {
            notes.Add("Exact raw direct-IAT call was recovered, but the strict 11-byte OEM SetFeature ABI prefix was not present immediately before it.");
        }

        return new OemSleepSetFeatureCall(
            callRva,
            "HID.DLL!HidD_SetFeature (exact raw PE32 direct-IAT)",
            reportLength41,
            reportLength,
            bufferSignature,
            bufferDisp,
            args.ToArray(),
            [],
            [],
            notes.ToArray());
    }

    private static uint FindRawPe32FunctionStart(NdevicePe pe, uint callRva)
    {
        int callOffset;
        try { callOffset = pe.RvaToOffset(callRva); }
        catch { return callRva; }

        var textSection = pe.Sections.FirstOrDefault(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        if (textSection is null) return callRva;
        var rawStart = checked((int)textSection.RawPointer);
        var lower = Math.Max(rawStart, callOffset - 0x800);
        for (var offset = callOffset - 3; offset >= lower; offset--)
        {
            if (pe.Bytes[offset] != 0x55 || pe.Bytes[offset + 1] != 0x8B || pe.Bytes[offset + 2] != 0xEC) continue;
            return textSection.VirtualAddress + checked((uint)(offset - rawStart));
        }
        return callRva;
    }
}

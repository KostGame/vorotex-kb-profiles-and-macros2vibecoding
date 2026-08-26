using System.Buffers.Binary;

namespace Vorotex.K15.HidResearchLab;

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    // Owner test for Issue #59 exposed a resolver gap: the generic Iced import resolver
    // did not classify the OEM PE32 instruction `FF 15 <absolute IAT VA>` even though
    // the earlier Vendor Static analyzer had already proven the same direct-IAT call.
    // This recovery path stays strictly static/read-only and only accepts an exact
    // PE32 CALL [absolute-IAT-VA] byte match for HID.DLL!HidD_SetFeature.
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
            evidence.Add("Both OEM executables expose corresponding exact PE32 FF 15 [absolute HidD_SetFeature IAT VA] transport call-sites recovered from raw executable bytes.");
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
        var indexes = RecoverExactPe32SetFeatureIndexes(pe, text, out var recoveryNote);
        if (indexes.Length == 0)
        {
            var unresolvedNotes = baseline.Notes
                .Where(x => !x.StartsWith("No statically decoded HidD_SetFeature", StringComparison.Ordinal))
                .Append(recoveryNote)
                .ToArray();
            return baseline with { Notes = unresolvedNotes };
        }

        var calls = indexes.Select(i => TraceSleepSetFeatureCall(pe, text, i)).ToArray();
        var transports = indexes
            .Select(i => FindSleepFunctionBounds(text, i).Start)
            .Distinct()
            .ToHashSet();
        var paths = BuildSleepCallPaths(pe, text, baseline.CandidateHandlerFunctions, transports);
        var sleepValueProven = calls.Any(x => x.ReportWrites.Any(w => w.SleepValueProven));
        var report41 = calls.Any(x => x.ReportLength41Proven && x.ReportBufferSignature is not null && x.ReportWrites.Length > 0);
        var fp = string.Join('|', calls.Select(c =>
            $"SET:{c.ReportLength41Proven}:{c.StackBufferBaseDisplacement}:{string.Join(',', c.ReportWrites.Select(w => $"{w.Offset}:{w.ValueKind}:{w.ValueExpression}"))}"));

        var notes = baseline.Notes
            .Where(x => !x.StartsWith("No statically decoded HidD_SetFeature", StringComparison.Ordinal))
            .ToList();
        notes.Add(recoveryNote);
        if (!report41)
            notes.Add("Exact direct-IAT HidD_SetFeature transport was recovered, but a 41-byte report buffer plus bounded writes were not reconstructed conservatively.");
        else if (!sleepValueProven)
            notes.Add("The recovered 41-byte report construction is transport evidence only; keyboard SleepTime provenance has not yet reached a concrete report field.");

        return baseline with
        {
            SetFeatureCalls = calls,
            HandlerToTransportPaths = paths,
            SetFeatureTransportRecovered = calls.Length > 0,
            ReportBuffer41Recovered = report41,
            SleepValueToReportProven = sleepValueProven,
            Fingerprint = fp,
            Notes = notes.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static int[] RecoverExactPe32SetFeatureIndexes(NdevicePe pe, List<NdeviceDecoded> text, out string note)
    {
        if (pe.Pe32Plus)
        {
            note = "Raw direct-IAT fallback is intentionally limited to PE32 OEM binaries; PE32+ requires a separately proven RIP-relative resolver.";
            return [];
        }

        var import = pe.Imports.FirstOrDefault(x =>
            x.Dll.Equals("HID.DLL", StringComparison.OrdinalIgnoreCase) &&
            x.Name.Equals("HidD_SetFeature", StringComparison.OrdinalIgnoreCase));
        if (import is null)
        {
            note = "HID.DLL!HidD_SetFeature was not present in the parsed import table.";
            return [];
        }

        var iatVa64 = pe.ImageBase + import.IatRva;
        if (iatVa64 > uint.MaxValue)
        {
            note = "PE32 HidD_SetFeature IAT VA exceeded the 32-bit address space; exact FF 15 recovery was not attempted.";
            return [];
        }
        var iatVa = (uint)iatVa64;
        var result = new List<int>();

        for (var i = 0; i < text.Count; i++)
        {
            var item = text[i];
            if (item.Instruction.Mnemonic != Iced.Intel.Mnemonic.Call) continue;

            int offset;
            try { offset = pe.RvaToOffset(item.Rva); }
            catch { continue; }
            if (offset < 0 || offset + 6 > pe.Bytes.Length) continue;
            if (pe.Bytes[offset] != 0xFF || pe.Bytes[offset + 1] != 0x15) continue;

            var encodedVa = BinaryPrimitives.ReadUInt32LittleEndian(pe.Bytes.AsSpan(offset + 2, 4));
            if (encodedVa != iatVa) continue;
            result.Add(i);
        }

        note = result.Count > 0
            ? $"Recovered {result.Count} exact PE32 direct-IAT HidD_SetFeature call(s) via FF 15 [0x{iatVa:X8}] raw-byte match; no OEM code or HID call was executed."
            : $"Parsed HidD_SetFeature IAT VA=0x{iatVa:X8}, but no exact PE32 FF 15 [absolute IAT VA] call was found in .text.";
        return result.Distinct().ToArray();
    }
}

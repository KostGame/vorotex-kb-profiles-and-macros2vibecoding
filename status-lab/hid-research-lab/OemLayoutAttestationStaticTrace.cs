using System.Buffers.Binary;
using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OemLayoutTransportCaller(
    uint CallRva,
    uint FunctionStartRva,
    string[] Context);

internal sealed record OemLayoutTransportFunction(
    string ImportName,
    uint ImportCallRva,
    uint FunctionStartRva,
    string[] Body,
    OemLayoutTransportCaller[] DirectCallers);

internal sealed record OemLayoutInternalFunction(
    uint FunctionRva,
    int ObservedBeforeGetFeatureCount,
    string[] Body,
    OemLayoutTransportCaller[] DirectCallers);

internal sealed record OemLayoutReadOperation(
    uint FunctionRva,
    uint[] GetFeatureCallRvas,
    string[] Body,
    OemLayoutTransportCaller[] DirectCallers);

internal sealed record OemLayoutOperationTypeWrite(
    uint GlobalVa,
    int WidthBits,
    uint Value,
    uint WriteRva,
    uint FunctionStartRva,
    string[] Context);

internal sealed record OemLayoutDispatchCase(
    int OperationType,
    byte JumpIndex,
    uint TargetRva);

internal sealed record OemLayoutAttestationStaticReport(
    int Schema,
    string Verdict,
    string Executable,
    string Sha256,
    OemLayoutTransportFunction[] Transports,
    OemLayoutInternalFunction? RequestBuilder,
    OemLayoutReadOperation[] ReadOperations,
    OemLayoutOperationTypeWrite[] OperationTypeWrites,
    OemLayoutDispatchCase[] OperationDispatchCases,
    OemLayoutInternalFunction? ProfileBindingLoader,
    OemLayoutInternalFunction? BindingValueEncoder,
    string[] Evidence,
    string[] Unresolved);

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    internal static OemLayoutAttestationStaticReport AnalyzeLayoutAttestationStatic(string exePath)
    {
        var fullPath = Path.GetFullPath(exePath);
        var pe = NdevicePe.Parse(fullPath);
        var transports = new List<OemLayoutTransportFunction>();

        foreach (var importName in new[] { "HidD_SetFeature", "HidD_GetFeature" })
        {
            var rvas = RecoverDirectIatCalls(pe, importName);
            foreach (var rva in rvas)
                transports.Add(TraceTransportFunction(pe, importName, rva));
        }

        var requestBuilder = RecoverRequestBuilder(pe, transports);
        var readOperations = RecoverReadOperations(pe, transports);
        var operationTypeWrites = RecoverOperationTypeWrites(pe);
        var operationDispatchCases = RecoverOperationDispatchCases(pe, readOperations);
        var profileBindingLoader = RecoverProfileBindingLoader(pe, operationTypeWrites);
        var bindingValueEncoder = RecoverBindingValueEncoder(pe, operationTypeWrites);
        var evidence = new List<string>();
        var unresolved = new List<string>();
        if (transports.Count(x => x.ImportName == "HidD_SetFeature") == 1)
            evidence.Add("Exactly one direct-IAT HidD_SetFeature call-site was recovered.");
        else
            unresolved.Add("Expected exactly one HidD_SetFeature transport call-site.");

        if (transports.Count(x => x.ImportName == "HidD_GetFeature") == 1)
            evidence.Add("Exactly one direct-IAT HidD_GetFeature call-site was recovered.");
        else
            unresolved.Add("Expected exactly one HidD_GetFeature transport call-site.");
        foreach (var transport in transports)
        {
            evidence.Add($"{transport.ImportName} transport entry 0x{transport.FunctionStartRva:X8} has {transport.DirectCallers.Length} direct E8 caller(s).");
            if (transport.DirectCallers.Length == 0)
                unresolved.Add($"{transport.ImportName} transport has no recovered direct caller.");
        }
        if (requestBuilder is not null)
            evidence.Add($"Common request-builder candidate 0x{requestBuilder.FunctionRva:X8} occurs before {requestBuilder.ObservedBeforeGetFeatureCount} GetFeature call-site(s).");
        else
            unresolved.Add("No common internal request-builder candidate was recovered before GetFeature.");
        evidence.Add($"Recovered {operationTypeWrites.Length} immediate writes to the two OEM operation-type globals.");
        if (operationDispatchCases.Length > 0)
            evidence.Add($"Recovered {operationDispatchCases.Length} operation-dispatch cases from the OEM getter jump table.");
        else
            unresolved.Add("No operation-dispatch cases were recovered from the OEM getter jump table.");
        if (profileBindingLoader is not null)
            evidence.Add($"Profile binding-loader candidate 0x{profileBindingLoader.FunctionRva:X8} was recovered from clustered type-4/type-5 writes.");
        else
            unresolved.Add("No profile binding-loader candidate was recovered from type-4/type-5 writes.");
        if (bindingValueEncoder is not null)
            evidence.Add($"Binding-value encoder candidate 0x{bindingValueEncoder.FunctionRva:X8} was recovered from repeated type-5 profile-load call-sites.");
        else
            unresolved.Add("No common binding-value encoder was recovered from type-5 profile-load call-sites.");

        return new OemLayoutAttestationStaticReport(
            1,
            unresolved.Count == 0 ? "TRANSPORT_CALLERS_RECOVERED" : "TRANSPORT_CALLERS_PARTIAL",
            Path.GetFileName(fullPath),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant(),
            transports.OrderBy(x => x.ImportName, StringComparer.Ordinal).ToArray(),
            requestBuilder,
            readOperations,
            operationTypeWrites,
            operationDispatchCases,
            profileBindingLoader,
            bindingValueEncoder,
            evidence.ToArray(),
            unresolved.ToArray());
    }

    internal static string LayoutAttestationStaticToText(OemLayoutAttestationStaticReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 layout-attestation static transport trace");
        sb.AppendLine($"EXE={report.Executable}");
        sb.AppendLine($"SHA256={report.Sha256}");
        sb.AppendLine($"VERDICT={report.Verdict}");
        sb.AppendLine("SAFETY=STATIC_READ_ONLY");
        foreach (var transport in report.Transports)
        {
            sb.AppendLine();
            sb.AppendLine($"{transport.ImportName}: importCall=0x{transport.ImportCallRva:X8}; function=0x{transport.FunctionStartRva:X8}");
            sb.AppendLine("  body:");
            foreach (var line in transport.Body) sb.AppendLine("    " + line);
            sb.AppendLine($"  direct callers: {transport.DirectCallers.Length}");
            foreach (var caller in transport.DirectCallers)
            {
                sb.AppendLine($"    call=0x{caller.CallRva:X8}; callerFunction=0x{caller.FunctionStartRva:X8}");
                foreach (var line in caller.Context) sb.AppendLine("      " + line);
            }
        }

        if (report.RequestBuilder is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"request-builder: function=0x{report.RequestBuilder.FunctionRva:X8}; observedBeforeGetFeature={report.RequestBuilder.ObservedBeforeGetFeatureCount}");
            sb.AppendLine("  body:");
            foreach (var line in report.RequestBuilder.Body) sb.AppendLine("    " + line);
            sb.AppendLine($"  direct callers: {report.RequestBuilder.DirectCallers.Length}");
            foreach (var caller in report.RequestBuilder.DirectCallers)
            {
                sb.AppendLine($"    call=0x{caller.CallRva:X8}; callerFunction=0x{caller.FunctionStartRva:X8}");
                foreach (var line in caller.Context) sb.AppendLine("      " + line);
            }
        }

        sb.AppendLine();
        sb.AppendLine($"operation dispatch cases: {report.OperationDispatchCases.Length}");
        foreach (var item in report.OperationDispatchCases)
            sb.AppendLine($"  type={item.OperationType}; jumpIndex={item.JumpIndex}; target=0x{item.TargetRva:X8}");

        sb.AppendLine();
        sb.AppendLine($"operation-type writes: {report.OperationTypeWrites.Length}");
        foreach (var write in report.OperationTypeWrites)
        {
            sb.AppendLine($"  global=0x{write.GlobalVa:X8}; width={write.WidthBits}; value=0x{write.Value:X}; write=0x{write.WriteRva:X8}; function=0x{write.FunctionStartRva:X8}");
            foreach (var line in write.Context) sb.AppendLine("    " + line);
        }

        if (report.ProfileBindingLoader is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"profile binding-loader: 0x{report.ProfileBindingLoader.FunctionRva:X8}; type4/type5 writes={report.ProfileBindingLoader.ObservedBeforeGetFeatureCount}");
            foreach (var line in report.ProfileBindingLoader.Body) sb.AppendLine("  " + line);
        }

        if (report.BindingValueEncoder is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"binding-value encoder: 0x{report.BindingValueEncoder.FunctionRva:X8}; observations={report.BindingValueEncoder.ObservedBeforeGetFeatureCount}");
            foreach (var line in report.BindingValueEncoder.Body) sb.AppendLine("  " + line);
        }

        sb.AppendLine();
        foreach (var item in report.Evidence) sb.AppendLine("EVIDENCE: " + item);
        foreach (var item in report.Unresolved) sb.AppendLine("UNRESOLVED: " + item);
        return sb.ToString();
    }

    private static OemLayoutDispatchCase[] RecoverOperationDispatchCases(
        NdevicePe pe,
        IReadOnlyList<OemLayoutReadOperation> readOperations)
    {
        if (pe.Pe32Plus) return [];
        var dispatcher = readOperations.FirstOrDefault(x =>
            x.Body.Any(line => line.Contains("530720h", StringComparison.OrdinalIgnoreCase)) &&
            x.Body.Any(line => line.Contains("52F040h", StringComparison.OrdinalIgnoreCase)));
        if (dispatcher is null) return [];

        var head = DecodeRange(pe, dispatcher.FunctionRva, Math.Min(pe.TextEnd, dispatcher.FunctionRva + 0x100u));
        var indexLoad = head.FirstOrDefault(x =>
            x.Instruction.Mnemonic == Mnemonic.Movzx &&
            x.Instruction.Op1Kind == OpKind.Memory &&
            Normalize(x.Instruction.MemoryBase) == Register.EAX &&
            x.Instruction.MemoryDisplacement64 >= pe.ImageBase);
        var jump = head.FirstOrDefault(x =>
            x.Instruction.Mnemonic == Mnemonic.Jmp &&
            x.Instruction.Op0Kind == OpKind.Memory &&
            Normalize(x.Instruction.MemoryIndex) == Register.EAX &&
            x.Instruction.MemoryIndexScale == 4 &&
            x.Instruction.MemoryDisplacement64 >= pe.ImageBase);
        if (indexLoad is null || jump is null) return [];

        var indexTableRva64 = indexLoad.Instruction.MemoryDisplacement64 - pe.ImageBase;
        var jumpTableRva64 = jump.Instruction.MemoryDisplacement64 - pe.ImageBase;
        if (indexTableRva64 > uint.MaxValue || jumpTableRva64 > uint.MaxValue) return [];
        var indexTableOffset = pe.RvaToOffset((uint)indexTableRva64);
        var jumpTableOffset = pe.RvaToOffset((uint)jumpTableRva64);
        var result = new List<OemLayoutDispatchCase>();
        for (var operationType = 1; operationType <= 0x71; operationType++)
        {
            var jumpIndex = pe.Bytes[indexTableOffset + operationType - 1];
            var targetOffset = jumpTableOffset + jumpIndex * 4;
            if (targetOffset < 0 || targetOffset + 4 > pe.Bytes.Length) continue;
            var targetVa = U32(pe.Bytes, targetOffset);
            if ((ulong)targetVa < pe.ImageBase) continue;
            var targetRva64 = (ulong)targetVa - pe.ImageBase;
            if (targetRva64 > uint.MaxValue) continue;
            var targetRva = (uint)targetRva64;
            if (targetRva < pe.TextStart || targetRva >= pe.TextEnd) continue;
            result.Add(new OemLayoutDispatchCase(operationType, jumpIndex, targetRva));
        }
        return result.ToArray();
    }

    private static OemLayoutOperationTypeWrite[] RecoverOperationTypeWrites(NdevicePe pe)
    {
        if (pe.Pe32Plus) return [];
        var section = pe.Sections.First(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        var rawStart = checked((int)section.RawPointer);
        var rawEnd = checked((int)Math.Min(pe.Bytes.Length, (long)rawStart + section.RawSize));
        var result = new List<OemLayoutOperationTypeWrite>();
        foreach (var globalVa in new uint[] { 0x0052F040, 0x00530720 })
        {
            var address = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(address, globalVa);
            for (var offset = rawStart; offset + 10 <= rawEnd; offset++)
            {
                int widthBits;
                uint value;
                int length;
                if (pe.Bytes[offset] == 0xC6 && pe.Bytes[offset + 1] == 0x05 && pe.Bytes.AsSpan(offset + 2, 4).SequenceEqual(address))
                {
                    widthBits = 8;
                    value = pe.Bytes[offset + 6];
                    length = 7;
                }
                else if (pe.Bytes[offset] == 0x66 && pe.Bytes[offset + 1] == 0xC7 && pe.Bytes[offset + 2] == 0x05 && pe.Bytes.AsSpan(offset + 3, 4).SequenceEqual(address))
                {
                    widthBits = 16;
                    value = BinaryPrimitives.ReadUInt16LittleEndian(pe.Bytes.AsSpan(offset + 7, 2));
                    length = 9;
                }
                else if (pe.Bytes[offset] == 0xC7 && pe.Bytes[offset + 1] == 0x05 && pe.Bytes.AsSpan(offset + 2, 4).SequenceEqual(address))
                {
                    widthBits = 32;
                    value = BinaryPrimitives.ReadUInt32LittleEndian(pe.Bytes.AsSpan(offset + 6, 4));
                    length = 10;
                }
                else continue;

                var writeRva = section.VirtualAddress + checked((uint)(offset - rawStart));
                var start = FindWidePe32FunctionStart(pe, writeRva);
                var decoded = DecodeRange(pe, start, Math.Min(pe.TextEnd, writeRva + 0x100u));
                var index = decoded.FindIndex(x => x.Rva == writeRva);
                string[] context;
                if (index < 0)
                {
                    context = [$"0x{writeRva:X8}: immediate global write ({length} bytes) not retained by bounded decode."];
                }
                else
                {
                    var from = Math.Max(0, index - 16);
                    var to = Math.Min(decoded.Count, index + 24);
                    context = decoded.Skip(from).Take(to - from).Select(FormatDecoded).ToArray();
                }
                result.Add(new OemLayoutOperationTypeWrite(globalVa, widthBits, value, writeRva, start, context));
            }
        }
        return result.OrderBy(x => x.WriteRva).ThenBy(x => x.GlobalVa).ToArray();
    }

    private static OemLayoutInternalFunction? RecoverProfileBindingLoader(
        NdevicePe pe,
        IReadOnlyList<OemLayoutOperationTypeWrite> operationTypeWrites)
    {
        var group = operationTypeWrites
            .Where(x => (x.Value & 0xFF) is 4 or 5)
            .GroupBy(x => x.FunctionStartRva)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key)
            .FirstOrDefault();
        if (group is null || group.Count() < 2) return null;

        var start = group.Key;
        var body = DecodeRange(pe, start, FindFunctionEndByPadding(pe, start))
            .Select(FormatDecoded)
            .ToArray();
        var callers = FindDirectRelativeCallers(pe, start)
            .Select(call => TraceCallerContext(pe, call))
            .ToArray();
        return new OemLayoutInternalFunction(start, group.Count(), body, callers);
    }

    private static OemLayoutInternalFunction? RecoverBindingValueEncoder(
        NdevicePe pe,
        IReadOnlyList<OemLayoutOperationTypeWrite> operationTypeWrites)
    {
        var nearbyTargets = new List<uint>();
        foreach (var write in operationTypeWrites.Where(x => (x.Value & 0xFF) == 5))
        {
            var start = FindWidePe32FunctionStart(pe, write.WriteRva);
            var decoded = DecodeRange(pe, start, Math.Min(pe.TextEnd, write.WriteRva + 0x100u));
            var index = decoded.FindIndex(x => x.Rva == write.WriteRva);
            if (index < 0) continue;
            for (var i = index + 1; i < decoded.Count && decoded[i].Rva <= write.WriteRva + 0x80u; i++)
            {
                var ins = decoded[i].Instruction;
                if (ins.Mnemonic != Mnemonic.Call || !IsDirectBranch(ins)) continue;
                var target64 = ins.NearBranchTarget;
                if (target64 > uint.MaxValue) continue;
                var target = (uint)target64;
                if (target < pe.TextStart || target >= pe.TextEnd) continue;
                nearbyTargets.Add(target);
                break;
            }
        }
        if (nearbyTargets.Count == 0) return null;
        var winner = nearbyTargets
            .GroupBy(x => x)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key)
            .First();
        if (winner.Count() < 2) return null;
        var targetRva = winner.Key;
        var body = DecodeRange(pe, targetRva, FindFunctionEndByPadding(pe, targetRva))
            .Select(FormatDecoded)
            .ToArray();
        var callers = FindDirectRelativeCallers(pe, targetRva)
            .Select(call => TraceCallerContext(pe, call))
            .ToArray();
        return new OemLayoutInternalFunction(targetRva, winner.Count(), body, callers);
    }

    private static OemLayoutReadOperation[] RecoverReadOperations(
        NdevicePe pe,
        IReadOnlyList<OemLayoutTransportFunction> transports)
    {
        var get = transports.SingleOrDefault(x => x.ImportName == "HidD_GetFeature");
        if (get is null) return [];

        return get.DirectCallers
            .GroupBy(x => x.FunctionStartRva)
            .OrderBy(x => x.Key)
            .Select(group =>
            {
                var start = group.Key;
                var decoded = DecodeRange(pe, start, FindFunctionEndByPadding(pe, start));
                var body = decoded.Select(FormatDecoded).ToArray();
                var callers = FindDirectRelativeCallers(pe, start)
                    .Select(call => TraceCallerContext(pe, call))
                    .ToArray();
                return new OemLayoutReadOperation(
                    start,
                    group.Select(x => x.CallRva).Distinct().OrderBy(x => x).ToArray(),
                    body,
                    callers);
            })
            .ToArray();
    }

    private static OemLayoutInternalFunction? RecoverRequestBuilder(
        NdevicePe pe,
        IReadOnlyList<OemLayoutTransportFunction> transports)
    {
        var get = transports.SingleOrDefault(x => x.ImportName == "HidD_GetFeature");
        if (get is null) return null;

        var counts = new Dictionary<uint, int>();
        foreach (var caller in get.DirectCallers)
        {
            foreach (var line in caller.Context)
            {
                var marker = " call ";
                var at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) continue;
                var targetText = line[(at + marker.Length)..].Trim();
                if (!targetText.EndsWith('h') || targetText.Length != 9) continue;
                if (!uint.TryParse(targetText[..^1], System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var target)) continue;
                if (target < pe.TextStart || target >= pe.TextEnd || target == get.FunctionStartRva) continue;
                counts[target] = counts.TryGetValue(target, out var count) ? count + 1 : 1;
            }
        }

        var best = counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).FirstOrDefault();
        if (best.Value < 2) return null;

        var bodyDecoded = DecodeRange(pe, best.Key, Math.Min(pe.TextEnd, best.Key + 0x800u));
        var retIndex = bodyDecoded.FindIndex(x => x.Instruction.Mnemonic is Mnemonic.Ret or Mnemonic.Retf);
        if (retIndex < 0) retIndex = Math.Min(bodyDecoded.Count - 1, 96);
        var body = bodyDecoded.Take(retIndex + 1).Select(FormatDecoded).ToArray();
        var callers = FindDirectRelativeCallers(pe, best.Key)
            .Select(call => TraceCallerContext(pe, call))
            .ToArray();

        return new OemLayoutInternalFunction(best.Key, best.Value, body, callers);
    }

    private static OemLayoutTransportFunction TraceTransportFunction(NdevicePe pe, string importName, uint importCallRva)
    {
        var start = FindRawPe32FunctionStart(pe, importCallRva);
        var decoded = DecodeRange(pe, start, Math.Min(pe.TextEnd, start + 0x1000u));
        var importIndex = decoded.FindIndex(x => x.Rva == importCallRva);
        var endIndex = importIndex >= 0
            ? decoded.FindIndex(importIndex, x => x.Instruction.Mnemonic is Mnemonic.Ret or Mnemonic.Retf)
            : -1;
        if (endIndex < 0) endIndex = Math.Min(decoded.Count - 1, Math.Max(importIndex + 64, 64));

        var body = decoded.Take(endIndex + 1)
            .Select(FormatDecoded)
            .ToArray();
        var callers = FindDirectRelativeCallers(pe, start)
            .Select(call => TraceCallerContext(pe, call))
            .ToArray();

        return new OemLayoutTransportFunction(importName, importCallRva, start, body, callers);
    }

    private static OemLayoutTransportCaller TraceCallerContext(NdevicePe pe, uint callRva)
    {
        var start = FindWidePe32FunctionStart(pe, callRva);
        var decoded = DecodeRange(pe, start, Math.Min(pe.TextEnd, callRva + 0x80u));
        var callIndex = decoded.FindIndex(x => x.Rva == callRva);
        if (callIndex < 0)
            return new OemLayoutTransportCaller(callRva, start, [$"0x{callRva:X8}: raw E8 caller not retained by bounded decode."]);
        var from = Math.Max(0, callIndex - 48);
        var to = Math.Min(decoded.Count, callIndex + 48);
        var context = decoded.Skip(from).Take(to - from)
            .Select(FormatDecoded)
            .ToArray();
        return new OemLayoutTransportCaller(callRva, start, context);
    }

    private static uint FindFunctionEndByPadding(NdevicePe pe, uint startRva)
    {
        int startOffset;
        try { startOffset = pe.RvaToOffset(startRva); }
        catch { return Math.Min(pe.TextEnd, startRva + 0x6000u); }
        var text = pe.Sections.First(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        var rawStart = checked((int)text.RawPointer);
        var rawEnd = checked((int)Math.Min(pe.Bytes.Length, (long)rawStart + text.RawSize));
        var limit = Math.Min(rawEnd, startOffset + 0x6000);
        for (var offset = startOffset + 1; offset + 4 <= limit; offset++)
        {
            if (pe.Bytes[offset] != 0xCC || pe.Bytes[offset + 1] != 0xCC || pe.Bytes[offset + 2] != 0xCC || pe.Bytes[offset + 3] != 0xCC) continue;
            return text.VirtualAddress + checked((uint)(offset - rawStart));
        }
        return Math.Min(pe.TextEnd, startRva + 0x6000u);
    }

    private static uint FindWidePe32FunctionStart(NdevicePe pe, uint callRva)
    {
        int callOffset;
        try { callOffset = pe.RvaToOffset(callRva); }
        catch { return callRva; }
        var text = pe.Sections.FirstOrDefault(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        if (text is null) return callRva;
        var rawStart = checked((int)text.RawPointer);
        var lower = Math.Max(rawStart, callOffset - 0x3000);
        for (var offset = callOffset - 3; offset >= lower; offset--)
        {
            if (pe.Bytes[offset] != 0x55 || pe.Bytes[offset + 1] != 0x8B || pe.Bytes[offset + 2] != 0xEC) continue;
            return text.VirtualAddress + checked((uint)(offset - rawStart));
        }
        return callRva;
    }

    private static string FormatDecoded(NdeviceDecoded item) =>
        $"0x{item.Rva:X8} {item.Text}";

    private static uint[] RecoverDirectIatCalls(NdevicePe pe, string importName)
    {
        if (pe.Pe32Plus) return [];
        var import = pe.Imports.FirstOrDefault(x =>
            x.Dll.Equals("HID.DLL", StringComparison.OrdinalIgnoreCase) &&
            x.Name.Equals(importName, StringComparison.OrdinalIgnoreCase));
        if (import is null) return [];

        var iatVa64 = pe.ImageBase + import.IatRva;
        if (iatVa64 > uint.MaxValue) return [];
        var iatVa = (uint)iatVa64;
        var section = pe.Sections.First(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        var rawStart = checked((int)section.RawPointer);
        var rawEnd = checked((int)Math.Min(pe.Bytes.Length, (long)rawStart + section.RawSize));
        Span<byte> needle = stackalloc byte[6];
        needle[0] = 0xFF;
        needle[1] = 0x15;
        BinaryPrimitives.WriteUInt32LittleEndian(needle[2..], iatVa);

        var result = new List<uint>();
        for (var offset = rawStart; offset + needle.Length <= rawEnd; offset++)
        {
            if (!pe.Bytes.AsSpan(offset, needle.Length).SequenceEqual(needle)) continue;
            result.Add(section.VirtualAddress + checked((uint)(offset - rawStart)));
        }
        return result.Distinct().OrderBy(x => x).ToArray();
    }

    private static uint[] FindDirectRelativeCallers(NdevicePe pe, uint targetRva)
    {
        var section = pe.Sections.First(x => x.Name.Equals(".text", StringComparison.OrdinalIgnoreCase));
        var rawStart = checked((int)section.RawPointer);
        var rawEnd = checked((int)Math.Min(pe.Bytes.Length, (long)rawStart + section.RawSize));
        var result = new List<uint>();

        for (var offset = rawStart; offset + 5 <= rawEnd; offset++)
        {
            if (pe.Bytes[offset] != 0xE8) continue;
            var callRva = section.VirtualAddress + checked((uint)(offset - rawStart));
            var rel = BinaryPrimitives.ReadInt32LittleEndian(pe.Bytes.AsSpan(offset + 1, 4));
            var target = unchecked((uint)((long)callRva + 5 + rel));
            if (target == targetRva) result.Add(callRva);
        }
        return result.Distinct().OrderBy(x => x).ToArray();
    }
}

using System.Text;
using Iced.Intel;

namespace Vorotex.K15.HidResearchLab;

internal static partial class OemNdeviceAggregateCopyAnalyzer
{
    private sealed record NdeviceDecoded(uint Rva, string Text, Instruction Instruction);
    private sealed record NdeviceSection(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
    {
        public bool Contains(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
    }
    private sealed record NdeviceImport(string Dll, string Name, uint IatRva);

    private static bool IsDirectBranch(Instruction ins) => ins.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
    private static bool IsMemoryWrite(Instruction ins) => ins.Mnemonic is Mnemonic.Mov or Mnemonic.Movups or Mnemonic.Movaps or Mnemonic.Movq or Mnemonic.Movdqa or Mnemonic.Movdqu;
    private static long SignedDisp(Instruction ins) => unchecked((int)(uint)ins.MemoryDisplacement64);

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
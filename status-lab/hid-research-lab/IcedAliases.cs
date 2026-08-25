namespace Vorotex.K15.HidResearchLab;

internal sealed class Decoder
{
    private readonly Iced.Intel.Decoder _inner;

    private Decoder(Iced.Intel.Decoder inner)
    {
        _inner = inner;
    }

    public static Decoder Create(int bitness, Iced.Intel.CodeReader reader) =>
        new(Iced.Intel.Decoder.Create(bitness, reader));

    public ulong IP
    {
        get => _inner.IP;
        set => _inner.IP = value;
    }

    public bool CanDecode => true;

    public void Decode(out Iced.Intel.Instruction instruction) =>
        _inner.Decode(out instruction);
}

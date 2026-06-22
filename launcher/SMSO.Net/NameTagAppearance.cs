namespace SMSO.Net;

public struct NameTagAppearance
{
    public byte TextTopR;
    public byte TextTopG;
    public byte TextTopB;
    public byte TextBottomR;
    public byte TextBottomG;
    public byte TextBottomB;
    public byte OutlineR;
    public byte OutlineG;
    public byte OutlineB;
    public byte Flags;

    public const byte FlagGradient = 1 << 0;
    public const byte FlagValid = 1 << 7;

    public bool GradientEnabled => (Flags & FlagGradient) != 0;
    public bool IsValid => (Flags & FlagValid) != 0;

    public static NameTagAppearance CreateDefault()
    {
        return new NameTagAppearance
        {
            TextTopR = 255,
            TextTopG = 255,
            TextTopB = 255,
            TextBottomR = 136,
            TextBottomG = 136,
            TextBottomB = 136,
            OutlineR = 0,
            OutlineG = 0,
            OutlineB = 0,
            Flags = FlagValid,
        };
    }
}

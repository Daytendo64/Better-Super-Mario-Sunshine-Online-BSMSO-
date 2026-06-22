namespace SMSO.Net;

public static class NameTagColorCodec
{
    public const int NameTextBytes = 15;
    public const int NameTextBytesWithOutline = 15;
    public const int NameTextBytesWithGradient = 15;

    public const byte TextOnlyMarker = 0x7F;
    public const byte ExtendedMarker = 0x7D;
    public const byte GradientMarker = 0x7B;

    public static void SetNameTagAppearance(byte[] name, byte textTopR, byte textTopG, byte textTopB,
        byte textBottomR, byte textBottomG, byte textBottomB, byte outlineR, byte outlineG, byte outlineB,
        bool gradientEnabled)
    {
        name ??= new byte[16];
        if (name.Length < 16)
            return;

        if (gradientEnabled) {
            name[5] = textBottomR;
            name[6] = textBottomG;
            name[7] = textBottomB;
            name[15] = GradientMarker;
        }
        else
        {
            name[15] = ExtendedMarker;
        }

        name[8] = outlineR;
        name[9] = outlineG;
        name[10] = outlineB;
        name[11] = 0;
        name[12] = textTopR;
        name[13] = textTopG;
        name[14] = textTopB;
    }

    public static bool HasAppearanceMarker(byte marker) =>
        marker == TextOnlyMarker || marker == ExtendedMarker || marker == GradientMarker;

    public static bool TryReadTextTopColor(byte[]? name, out byte r, out byte g, out byte b)
    {
        r = g = b = 255;
        if (name == null || name.Length < 16 || !HasAppearanceMarker(name[15]))
            return false;

        r = name[12];
        g = name[13];
        b = name[14];
        return true;
    }

    public static bool TryReadTextBottomColor(byte[]? name, out byte r, out byte g, out byte b)
    {
        r = g = b = 255;
        if (name == null || name.Length < 16 || name[15] != GradientMarker)
            return false;

        r = name[5];
        g = name[6];
        b = name[7];
        return true;
    }

    public static bool TryReadOutlineColor(byte[]? name, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (name == null || name.Length < 16)
            return false;

        if (name[15] != ExtendedMarker && name[15] != GradientMarker)
            return false;

        r = name[8];
        g = name[9];
        b = name[10];
        return true;
    }

    public static bool IsGradientEnabled(byte[]? name) =>
        name != null && name.Length >= 16 && name[15] == GradientMarker;

    public static int GetNameTextByteLimit(byte marker) => marker switch
    {
        GradientMarker => NameTextBytesWithGradient,
        ExtendedMarker => NameTextBytesWithOutline,
        TextOnlyMarker => NameTextBytes,
        _ => 16,
    };

    public static bool TryDecodeAppearance(byte[]? wire, out NameTagAppearance appearance)
    {
        appearance = default;
        if (wire == null || wire.Length < 16)
            return false;

        var marker = wire[15];
        if (!HasAppearanceMarker(marker))
            return false;

        if (!TryReadTextTopColor(wire, out var tr, out var tg, out var tb))
            return false;

        appearance.TextTopR = tr;
        appearance.TextTopG = tg;
        appearance.TextTopB = tb;
        appearance.OutlineR = 0;
        appearance.OutlineG = 0;
        appearance.OutlineB = 0;
        appearance.TextBottomR = tr;
        appearance.TextBottomG = tg;
        appearance.TextBottomB = tb;
        appearance.Flags = NameTagAppearance.FlagValid;

        if (TryReadOutlineColor(wire, out var or, out var og, out var ob))
        {
            appearance.OutlineR = or;
            appearance.OutlineG = og;
            appearance.OutlineB = ob;
        }

        if (TryReadTextBottomColor(wire, out var br, out var bg, out var bb))
        {
            appearance.TextBottomR = br;
            appearance.TextBottomG = bg;
            appearance.TextBottomB = bb;
            appearance.Flags |= NameTagAppearance.FlagGradient;
        }

        return true;
    }

    public static void WritePureName(byte[]? target, string? value)
    {
        target ??= new byte[16];
        Array.Clear(target, 0, target.Length);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        Array.Copy(bytes, target, Math.Min(bytes.Length, target.Length));
    }

    public static NameTagAppearance ToAppearance(byte textTopR, byte textTopG, byte textTopB,
        byte textBottomR, byte textBottomG, byte textBottomB, byte outlineR, byte outlineG, byte outlineB,
        bool gradientEnabled)
    {
        var appearance = NameTagAppearance.CreateDefault();
        appearance.TextTopR = textTopR;
        appearance.TextTopG = textTopG;
        appearance.TextTopB = textTopB;
        appearance.TextBottomR = textBottomR;
        appearance.TextBottomG = textBottomG;
        appearance.TextBottomB = textBottomB;
        appearance.OutlineR = outlineR;
        appearance.OutlineG = outlineG;
        appearance.OutlineB = outlineB;
        appearance.Flags = NameTagAppearance.FlagValid;
        if (gradientEnabled)
            appearance.Flags |= NameTagAppearance.FlagGradient;
        return appearance;
    }
}

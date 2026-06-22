using System.Runtime.InteropServices;

namespace SMSO.Net;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vec3
{
    public float X;
    public float Y;
    public float Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = ProtocolConstants.PlayerSnapshotSize)]
public struct PlayerSnapshot
{
    public Vec3 Position;
    public Vec3 Velocity;
    public float RotationY;
    public ushort AnimId;
    public byte NozzleId;
    public byte Water;
    public byte Health;
    public byte StageId;
    public byte EpisodeId;
    public byte MovementState;
    public ushort ActionId;
    public ushort VfxFlags;
    public byte Connected;
    public byte Slot;
    public ushort PingMs;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] Name;

    public ushort AnimFrame;
    public ushort ActionIdHi;

    public string GetName()
    {
        if (Name == null || Name.Length == 0) return string.Empty;
        var marker = Name.Length >= 16 ? Name[15] : (byte)0;
        if (NameTagColorCodec.HasAppearanceMarker(marker))
            return GetPureName();

        int len = Array.IndexOf(Name, (byte)0);
        if (len < 0) len = Name.Length;
        return System.Text.Encoding.UTF8.GetString(Name, 0, len);
    }

    public string GetPureName()
    {
        if (Name == null || Name.Length == 0) return string.Empty;
        int len = Array.IndexOf(Name, (byte)0);
        if (len < 0) len = Name.Length;
        return System.Text.Encoding.UTF8.GetString(Name, 0, len);
    }

    public void SetName(string value)
    {
        Name ??= new byte[16];
        NameTagColorCodec.WritePureName(Name, value);
    }

    public void SetNameTagAppearance(byte textTopR, byte textTopG, byte textTopB, byte textBottomR,
        byte textBottomG, byte textBottomB, byte outlineR, byte outlineG, byte outlineB, bool gradientEnabled)
    {
        Name ??= new byte[16];
        if (Name.Length < 16)
            return;

        NameTagColorCodec.SetNameTagAppearance(Name, textTopR, textTopG, textTopB, textBottomR, textBottomG,
            textBottomB, outlineR, outlineG, outlineB, gradientEnabled);
    }
}

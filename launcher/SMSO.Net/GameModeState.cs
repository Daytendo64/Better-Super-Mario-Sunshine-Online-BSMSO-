using System.Runtime.InteropServices;

namespace SMSO.Net;

public enum GameMode : byte
{
    Normal = 0,
    HideSeek = 1,
}

public enum HideSeekRole : byte
{
    Hider = 0,
    Seeker = 1,
}

[Flags]
public enum GameModeFlags : byte
{
    None = 0,
    TagActive = 1 << 0,
    RoundComplete = 1 << 1,
    TimerReset = 1 << 2,
    RoundFanfare = 1 << 3,
    /// <summary>
    /// Start Tag hide grace: seekers frozen, blue wash, proximity tags suppressed.
    /// Server-authoritative; cleared when GraceRemainingMs hits 0.
    /// </summary>
    GraceActive = 1 << 4,
}

public sealed class GameModeStatePacket
{
    public GameMode GameMode { get; set; }
    public GameModeFlags Flags { get; set; }
    public ushort Seq { get; set; }
    public uint RoundStartMs { get; set; }
    public byte TagEventId { get; set; }
    public HideSeekRole[] Roles { get; } = new HideSeekRole[ProtocolConstants.MaxPlayers];
    public byte LastTaggedSlot { get; set; } = 0xFF;
    /// <summary>Milliseconds left in Start Tag grace (0 when inactive).</summary>
    public ushort GraceRemainingMs { get; set; }

    public bool TagActive => (Flags & GameModeFlags.TagActive) != 0;
    public bool RoundComplete => (Flags & GameModeFlags.RoundComplete) != 0;
    public bool GraceActive => (Flags & GameModeFlags.GraceActive) != 0;

    public static GameModeStatePacket CreateDefault()
    {
        var state = new GameModeStatePacket();
        for (int i = 0; i < state.Roles.Length; i++)
            state.Roles[i] = HideSeekRole.Hider;
        return state;
    }

    public GameModeStatePacket Clone()
    {
        var copy = new GameModeStatePacket
        {
            GameMode = GameMode,
            Flags = Flags,
            Seq = Seq,
            RoundStartMs = RoundStartMs,
            TagEventId = TagEventId,
            LastTaggedSlot = LastTaggedSlot,
            GraceRemainingMs = GraceRemainingMs,
        };
        Array.Copy(Roles, copy.Roles, Roles.Length);
        return copy;
    }

    public byte GetRole(byte slot)
    {
        if (slot >= Roles.Length)
            return (byte)HideSeekRole.Hider;
        return (byte)Roles[slot];
    }

    public void SetRole(byte slot, HideSeekRole role)
    {
        if (slot < Roles.Length)
            Roles[slot] = role;
    }

    public int CountRole(HideSeekRole role)
    {
        int count = 0;
        foreach (var r in Roles)
        {
            if (r == role)
                ++count;
        }
        return count;
    }

    public static CommGameModeState ToCommGameMode(byte localSlot, in GameModeStatePacket packet)
    {
        var state = CommGameModeState.CreateDefault();
        state.Mode = (byte)packet.GameMode;
        state.Flags = (byte)packet.Flags;
        state.LocalRole = packet.GetRole(localSlot);
        state.LastTaggedSlot = packet.LastTaggedSlot;
        state.TagEventId = packet.TagEventId;
        state.RoundStartMs = packet.RoundStartMs;
        state.GraceRemainingMs = packet.GraceRemainingMs;
        for (int i = 0; i < ProtocolConstants.MaxPlayers; i++)
            state.RoleBySlot[i] = packet.GetRole((byte)i);
        return state;
    }

    public static NameTagAppearance HideSeekAppearance(HideSeekRole role)
    {
        return role switch
        {
            HideSeekRole.Seeker => NameTagColorCodec.ToAppearance(255, 59, 59, 255, 59, 59, 0, 0, 0, false),
            _ => NameTagColorCodec.ToAppearance(46, 134, 255, 46, 134, 255, 0, 0, 0, false),
        };
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CommGameModeState
{
    public byte Mode;
    public byte Flags;
    public byte LocalRole;
    public byte LastTaggedSlot;
    public byte TagEventId;
    public uint RoundStartMs;

    [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = ProtocolConstants.MaxPlayers)]
    public byte[] RoleBySlot;

    public ushort GraceRemainingMs;

    public static CommGameModeState CreateDefault()
    {
        return new CommGameModeState
        {
            RoleBySlot = new byte[ProtocolConstants.MaxPlayers],
            LastTaggedSlot = 0xFF,
            GraceRemainingMs = 0,
        };
    }
}

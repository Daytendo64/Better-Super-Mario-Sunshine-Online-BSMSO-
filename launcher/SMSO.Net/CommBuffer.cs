using System.Runtime.InteropServices;

namespace SMSO.Net;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = ProtocolConstants.CommBufferSize)]
public struct CommBuffer
{
    public uint Magic;
    public ushort Version;
    public BridgeFlags BridgeFlags;
    public byte LocalSlot;
    public DolphinState DolphinState;
    public byte PlayerCount;
    public byte WarpTargetSlot;
    public byte WarpCourseId;
    public byte WarpEpisodeId;
    public float WarpPosX;
    public float WarpPosY;
    public float WarpPosZ;
    public float WarpFacingY;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] LocalPlayerName;

    public PlayerSnapshot LocalSnapshot;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public PlayerSnapshot[] RemoteSnapshots;

    public NameTagAppearance LocalNameTagAppearance;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public NameTagAppearance[] RemoteNameTagAppearances;

    public MarioVoiceEvent LocalMarioVoiceEvent;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public MarioVoiceEvent[] RemoteMarioVoiceEvents;

    public CommGameModeState GameModeState;

    public CommWorldSyncState WorldSync;

    public CommRosterHudSync RosterHud;

    public static CommBuffer CreateDefault()
    {
        return new CommBuffer
        {
            Magic = ProtocolConstants.Magic,
            Version = ProtocolConstants.CommVersion,
            WarpTargetSlot = ProtocolConstants.WarpNoTarget,
            LocalPlayerName = new byte[16],
            LocalSnapshot = new PlayerSnapshot { Name = new byte[16] },
            RemoteSnapshots = CreateRemoteArray(),
            LocalNameTagAppearance = NameTagAppearance.CreateDefault(),
            RemoteNameTagAppearances = CreateRemoteAppearanceArray(),
            RemoteMarioVoiceEvents = CreateRemoteMarioVoiceEventArray(),
            GameModeState = CommGameModeState.CreateDefault(),
            RosterHud = CommRosterHudSync.CreateDefault(),
        };
    }

    public static NameTagAppearance[] CreateRemoteAppearanceArray()
    {
        var arr = new NameTagAppearance[9];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = NameTagAppearance.CreateDefault();
        return arr;
    }

    public static PlayerSnapshot[] CreateRemoteArray()
    {
        var arr = new PlayerSnapshot[9];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = new PlayerSnapshot { Name = new byte[16] };
        return arr;
    }

    public static MarioVoiceEvent[] CreateRemoteMarioVoiceEventArray()
        => new MarioVoiceEvent[9];

    public string GetLocalPlayerName()
    {
        if (LocalPlayerName == null) return string.Empty;
        int len = Array.IndexOf(LocalPlayerName, (byte)0);
        if (len < 0) len = LocalPlayerName.Length;
        return System.Text.Encoding.UTF8.GetString(LocalPlayerName, 0, len);
    }

    public void SetLocalPlayerName(string name)
    {
        LocalPlayerName ??= new byte[16];
        Array.Clear(LocalPlayerName, 0, LocalPlayerName.Length);
        var bytes = System.Text.Encoding.UTF8.GetBytes(name ?? string.Empty);
        Array.Copy(bytes, LocalPlayerName, Math.Min(bytes.Length, 15));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = ProtocolConstants.CommRosterHudEventSize)]
public struct CommRosterHudEvent
{
    public ushort Sequence;
    public RosterHudEventKind Kind;
    public byte Slot;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] Name;

    public string GetPlayerName()
    {
        if (Name == null)
            return string.Empty;
        int len = Array.IndexOf(Name, (byte)0);
        if (len < 0)
            len = Name.Length;
        return System.Text.Encoding.UTF8.GetString(Name, 0, len);
    }

    public void SetPlayerName(string name)
    {
        Name ??= new byte[16];
        Array.Clear(Name, 0, Name.Length);
        var bytes = System.Text.Encoding.UTF8.GetBytes(name ?? string.Empty);
        Array.Copy(bytes, Name, Math.Min(bytes.Length, 15));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = ProtocolConstants.CommRosterHudSyncSize)]
public struct CommRosterHudSync
{
    public ushort LatestSequence;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProtocolConstants.CommRosterHudRingSlots)]
    public CommRosterHudEvent[] Events;

    public static CommRosterHudSync CreateDefault()
    {
        var events = new CommRosterHudEvent[ProtocolConstants.CommRosterHudRingSlots];
        for (int i = 0; i < events.Length; i++)
            events[i].Name = new byte[16];
        return new CommRosterHudSync { Events = events };
    }
}

public static class CommBufferMarshal
{
    public static byte[] ToBytes(CommBuffer buffer)
    {
        int size = Marshal.SizeOf<CommBuffer>();
        var bytes = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(buffer, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return bytes;
    }

    public static CommBuffer FromBytes(byte[] data)
    {
        if (data.Length < ProtocolConstants.CommBufferSize)
            return CommBuffer.CreateDefault();

        var ptr = Marshal.AllocHGlobal(ProtocolConstants.CommBufferSize);
        try
        {
            Marshal.Copy(data, 0, ptr, ProtocolConstants.CommBufferSize);
            var buffer = Marshal.PtrToStructure<CommBuffer>(ptr);
            buffer.RemoteSnapshots ??= CommBuffer.CreateRemoteArray();
            buffer.RemoteNameTagAppearances ??= CommBuffer.CreateRemoteAppearanceArray();
            buffer.RemoteMarioVoiceEvents ??= CommBuffer.CreateRemoteMarioVoiceEventArray();
            buffer.RosterHud.Events ??= CommRosterHudSync.CreateDefault().Events;
            buffer.LocalPlayerName ??= new byte[16];
            buffer.LocalSnapshot.Name ??= new byte[16];
            for (int i = 0; i < buffer.RemoteSnapshots.Length; i++)
                buffer.RemoteSnapshots[i].Name ??= new byte[16];
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static int Size => Marshal.SizeOf<CommBuffer>();
}

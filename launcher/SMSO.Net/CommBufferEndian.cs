using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SMSO.Net;

/// <summary>
/// GameCube MEM1 is big-endian; Dolphin exposes raw bytes in that order.
/// </summary>
public static class CommBufferEndian
{
    public static CommBuffer FromDolphinBytes(byte[] data)
    {
        if (data.Length < ProtocolConstants.CommBufferSize)
            return CommBuffer.CreateDefault();

        var buf = CommBuffer.CreateDefault();
        int o = 0;
        buf.Magic = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4)); o += 4;
        buf.Version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        buf.BridgeFlags = (BridgeFlags)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4)); o += 4;
        buf.LocalSlot = data[o++];
        buf.DolphinState = (DolphinState)data[o++];
        buf.PlayerCount = data[o++];
        buf.WarpTargetSlot = data[o++];
        buf.WarpCourseId = data[o++];
        buf.WarpEpisodeId = data[o++];
        buf.WarpPosX = ReadF32(data, ref o);
        buf.WarpPosY = ReadF32(data, ref o);
        buf.WarpPosZ = ReadF32(data, ref o);
        buf.WarpFacingY = ReadF32(data, ref o);
        Array.Copy(data, o, buf.LocalPlayerName, 0, 16); o += 16;
        buf.LocalSnapshot = ReadSnapshot(data, ref o);
        for (int i = 0; i < buf.RemoteSnapshots.Length; i++)
            buf.RemoteSnapshots[i] = ReadSnapshot(data, ref o);
        buf.LocalNameTagAppearance = ReadAppearance(data, ref o);
        buf.RemoteNameTagAppearances ??= CommBuffer.CreateRemoteAppearanceArray();
        for (int i = 0; i < buf.RemoteNameTagAppearances.Length; i++)
            buf.RemoteNameTagAppearances[i] = ReadAppearance(data, ref o);
        buf.LocalMarioVoiceEvent = ReadMarioVoiceEvent(data, ref o);
        buf.RemoteMarioVoiceEvents ??= CommBuffer.CreateRemoteMarioVoiceEventArray();
        for (int i = 0; i < buf.RemoteMarioVoiceEvents.Length; i++)
            buf.RemoteMarioVoiceEvents[i] = ReadMarioVoiceEvent(data, ref o);
        buf.GameModeState = ReadGameModeState(data, ref o);
        return buf;
    }

    public static byte[] ToRemoteSnapshotsDolphinBytes(PlayerSnapshot[] remotes)
    {
        var data = new byte[ProtocolConstants.CommRemoteSnapshotsSize];
        WriteRemoteSnapshotsInto(data, remotes);
        return data;
    }

    public static void WriteRemoteSnapshotsInto(Span<byte> dest, PlayerSnapshot[] remotes)
    {
        if (dest.Length < ProtocolConstants.CommRemoteSnapshotsSize)
            throw new ArgumentException("Remote snapshot buffer is too small.", nameof(dest));

        int o = 0;
        var slots = remotes ?? CommBuffer.CreateRemoteArray();
        for (int i = 0; i < ProtocolConstants.MaxRemoteSlots; i++)
            WriteSnapshot(dest, ref o, i < slots.Length ? slots[i] : new PlayerSnapshot { Name = new byte[16] });
    }

    public static void ApplyWarpIntentToControlSpan(
        Span<byte> control,
        byte targetSlot,
        byte courseId,
        byte episodeId,
        bool setHostFlag,
        bool setWarpPending = true,
        bool setWarpAll = false,
        bool setWarpToPoint = false,
        float warpPosX = 0f,
        float warpPosY = 0f,
        float warpPosZ = 0f,
        float warpFacingY = 0f)
    {
        if (control.Length < ProtocolConstants.CommBridgeControlSize)
            throw new ArgumentException("Control span too small", nameof(control));

        var flags = BinaryPrimitives.ReadUInt32BigEndian(control);
        if (setWarpPending)
            flags |= (uint)BridgeFlags.WarpPending;
        if (setWarpToPoint)
            flags |= (uint)BridgeFlags.WarpToPoint;
        if (setHostFlag)
            flags |= (uint)BridgeFlags.Host;
        if (setWarpAll)
            flags |= (uint)BridgeFlags.WarpAll;
        else
            flags &= ~(uint)BridgeFlags.WarpAll;
        BinaryPrimitives.WriteUInt32BigEndian(control, flags);
        control[7] = targetSlot;
        control[8] = courseId;
        control[9] = episodeId;
        WriteF32At(control, 10, warpPosX);
        WriteF32At(control, 14, warpPosY);
        WriteF32At(control, 18, warpPosZ);
        WriteF32At(control, 22, warpFacingY);
    }

    private static void WriteF32At(Span<byte> span, int offset, float value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), BitConverter.SingleToUInt32Bits(value));
    }

    public static byte[] ToDolphinBytes(CommBuffer buffer)
    {
        var data = new byte[ProtocolConstants.CommBufferSize];
        int o = 0;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(o, 4), buffer.Magic); o += 4;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(o, 2), buffer.Version); o += 2;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(o, 4), (uint)buffer.BridgeFlags); o += 4;
        data[o++] = buffer.LocalSlot;
        data[o++] = (byte)buffer.DolphinState;
        data[o++] = buffer.PlayerCount;
        data[o++] = buffer.WarpTargetSlot;
        data[o++] = buffer.WarpCourseId;
        data[o++] = buffer.WarpEpisodeId;
        WriteF32(data, ref o, buffer.WarpPosX);
        WriteF32(data, ref o, buffer.WarpPosY);
        WriteF32(data, ref o, buffer.WarpPosZ);
        WriteF32(data, ref o, buffer.WarpFacingY);
        Array.Copy(buffer.LocalPlayerName ?? new byte[16], 0, data, o, 16); o += 16;
        WriteSnapshot(data, ref o, buffer.LocalSnapshot);
        foreach (var snap in buffer.RemoteSnapshots ?? CommBuffer.CreateRemoteArray())
            WriteSnapshot(data, ref o, snap);
        WriteAppearance(data, ref o, buffer.LocalNameTagAppearance);
        foreach (var appearance in buffer.RemoteNameTagAppearances ?? CommBuffer.CreateRemoteAppearanceArray())
            WriteAppearance(data, ref o, appearance);
        WriteMarioVoiceEvent(data, ref o, buffer.LocalMarioVoiceEvent);
        foreach (var voiceEvent in buffer.RemoteMarioVoiceEvents ?? CommBuffer.CreateRemoteMarioVoiceEventArray())
            WriteMarioVoiceEvent(data, ref o, voiceEvent);
        WriteGameModeState(data, ref o, buffer.GameModeState);
        return data;
    }

    public static byte[] ToGameModeStateDolphinBytes(in CommGameModeState state)
    {
        var data = new byte[ProtocolConstants.CommGameModeStateSize];
        WriteGameModeStateInto(data, state);
        return data;
    }

    public static void WriteGameModeStateInto(Span<byte> dest, in CommGameModeState state)
    {
        if (dest.Length < ProtocolConstants.CommGameModeStateSize)
            throw new ArgumentException("Game mode buffer is too small.", nameof(dest));

        int o = 0;
        WriteGameModeState(dest, ref o, state);
    }

    public static byte[] ToNameTagAppearancesDolphinBytes(NameTagAppearance local, NameTagAppearance[] remotes)
    {
        var data = new byte[ProtocolConstants.CommNameTagAppearancesSize];
        WriteNameTagAppearancesInto(data, local, remotes);
        return data;
    }

    public static void WriteNameTagAppearancesInto(Span<byte> dest, NameTagAppearance local, NameTagAppearance[] remotes)
    {
        if (dest.Length < ProtocolConstants.CommNameTagAppearancesSize)
            throw new ArgumentException("Name tag buffer is too small.", nameof(dest));

        int o = 0;
        WriteAppearance(dest, ref o, local);
        var slots = remotes ?? CommBuffer.CreateRemoteAppearanceArray();
        for (int i = 0; i < ProtocolConstants.MaxRemoteSlots; i++)
            WriteAppearance(dest, ref o, i < slots.Length ? slots[i] : NameTagAppearance.CreateDefault());
    }

    public static byte[] ToRemoteMarioVoiceEventsDolphinBytes(MarioVoiceEvent[] remotes)
    {
        var data = new byte[ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots];
        WriteRemoteMarioVoiceEventsInto(data, remotes);
        return data;
    }

    public static void WriteRemoteMarioVoiceEventsInto(Span<byte> dest, MarioVoiceEvent[] remotes)
    {
        if (dest.Length < ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots)
            throw new ArgumentException("Remote voice buffer is too small.", nameof(dest));

        int o = 0;
        var slots = remotes ?? CommBuffer.CreateRemoteMarioVoiceEventArray();
        for (int i = 0; i < ProtocolConstants.MaxRemoteSlots; i++)
            WriteMarioVoiceEvent(dest, ref o, i < slots.Length ? slots[i] : default);
    }

    private static NameTagAppearance ReadAppearance(byte[] data, ref int o)
    {
        return new NameTagAppearance
        {
            TextTopR = data[o++],
            TextTopG = data[o++],
            TextTopB = data[o++],
            TextBottomR = data[o++],
            TextBottomG = data[o++],
            TextBottomB = data[o++],
            OutlineR = data[o++],
            OutlineG = data[o++],
            OutlineB = data[o++],
            Flags = data[o++],
        };
    }

    private static void WriteAppearance(Span<byte> data, ref int o, NameTagAppearance appearance)
    {
        data[o++] = appearance.TextTopR;
        data[o++] = appearance.TextTopG;
        data[o++] = appearance.TextTopB;
        data[o++] = appearance.TextBottomR;
        data[o++] = appearance.TextBottomG;
        data[o++] = appearance.TextBottomB;
        data[o++] = appearance.OutlineR;
        data[o++] = appearance.OutlineG;
        data[o++] = appearance.OutlineB;
        data[o++] = appearance.Flags;
    }

    private static void WriteAppearance(byte[] data, ref int o, NameTagAppearance appearance) =>
        WriteAppearance(data.AsSpan(), ref o, appearance);

    private static MarioVoiceEvent ReadMarioVoiceEvent(byte[] data, ref int o)
    {
        var voiceEvent = new MarioVoiceEvent
        {
            SoundId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4)),
            Sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o + 4, 2)),
            Flags = data[o + 6],
            Health = data[o + 7],
            StageId = data[o + 8],
            EpisodeId = data[o + 9],
            Reserved0 = data[o + 10],
            Reserved1 = data[o + 11],
        };
        o += ProtocolConstants.MarioVoiceEventSize;
        return voiceEvent;
    }

    private static void WriteMarioVoiceEvent(Span<byte> data, ref int o, MarioVoiceEvent voiceEvent)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(o, 4), voiceEvent.SoundId);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o + 4, 2), voiceEvent.Sequence);
        data[o + 6] = voiceEvent.Flags;
        data[o + 7] = voiceEvent.Health;
        data[o + 8] = voiceEvent.StageId;
        data[o + 9] = voiceEvent.EpisodeId;
        data[o + 10] = voiceEvent.Reserved0;
        data[o + 11] = voiceEvent.Reserved1;
        o += ProtocolConstants.MarioVoiceEventSize;
    }

    private static void WriteMarioVoiceEvent(byte[] data, ref int o, MarioVoiceEvent voiceEvent) =>
        WriteMarioVoiceEvent(data.AsSpan(), ref o, voiceEvent);

    private static CommGameModeState ReadGameModeState(byte[] data, ref int o)
    {
        var state = new CommGameModeState
        {
            Mode = data[o],
            Flags = data[o + 1],
            LocalRole = data[o + 2],
            LastTaggedSlot = data[o + 3],
            TagEventId = data[o + 4],
            RoundStartMs = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o + 5, 4)),
            RoleBySlot = new byte[ProtocolConstants.StableMaxPlayers],
        };
        Array.Copy(data, o + 9, state.RoleBySlot, 0, ProtocolConstants.StableMaxPlayers);
        o += ProtocolConstants.CommGameModeStateSize;
        return state;
    }

    private static void WriteGameModeState(Span<byte> data, ref int o, in CommGameModeState state)
    {
        data[o++] = state.Mode;
        data[o++] = state.Flags;
        data[o++] = state.LocalRole;
        data[o++] = state.LastTaggedSlot;
        data[o++] = state.TagEventId;
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(o, 4), state.RoundStartMs);
        o += 4;
        var roles = state.RoleBySlot ?? new byte[ProtocolConstants.StableMaxPlayers];
        for (int i = 0; i < ProtocolConstants.StableMaxPlayers; i++)
            data[o++] = i < roles.Length ? roles[i] : (byte)HideSeekRole.Hider;
    }

    private static void WriteGameModeState(byte[] data, ref int o, in CommGameModeState state) =>
        WriteGameModeState(data.AsSpan(), ref o, state);

    private static PlayerSnapshot ReadSnapshot(byte[] data, ref int o)
    {
        var snap = new PlayerSnapshot { Name = new byte[16] };
        snap.Position = ReadVec3(data, ref o);
        snap.Velocity = ReadVec3(data, ref o);
        snap.RotationY = ReadF32(data, ref o);
        snap.AnimId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        snap.NozzleId = data[o++];
        snap.Water = data[o++];
        snap.Health = data[o++];
        snap.StageId = data[o++];
        snap.EpisodeId = data[o++];
        snap.MovementState = data[o++];
        snap.ActionId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        snap.VfxFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        snap.Connected = data[o++];
        snap.Slot = data[o++];
        snap.PingMs = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        Array.Copy(data, o, snap.Name, 0, 16); o += 16;
        snap.AnimFrame = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        snap.ActionIdHi = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        return snap;
    }

    private static void WriteSnapshot(Span<byte> data, ref int o, PlayerSnapshot snap)
    {
        WriteVec3(data, ref o, snap.Position);
        WriteVec3(data, ref o, snap.Velocity);
        WriteF32(data, ref o, snap.RotationY);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), snap.AnimId); o += 2;
        data[o++] = snap.NozzleId;
        data[o++] = snap.Water;
        data[o++] = snap.Health;
        data[o++] = snap.StageId;
        data[o++] = snap.EpisodeId;
        data[o++] = snap.MovementState;
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), snap.ActionId); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), snap.VfxFlags); o += 2;
        data[o++] = snap.Connected;
        data[o++] = snap.Slot;
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), snap.PingMs); o += 2;
        var name = snap.Name ?? new byte[16];
        name.AsSpan(0, Math.Min(16, name.Length)).CopyTo(data.Slice(o, 16));
        o += 16;
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), snap.AnimFrame); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), snap.ActionIdHi); o += 2;
    }

    private static void WriteSnapshot(byte[] data, ref int o, PlayerSnapshot snap) =>
        WriteSnapshot(data.AsSpan(), ref o, snap);

    private static Vec3 ReadVec3(byte[] data, ref int o)
    {
        return new Vec3
        {
            X = ReadF32(data, ref o),
            Y = ReadF32(data, ref o),
            Z = ReadF32(data, ref o),
        };
    }

    private static void WriteVec3(Span<byte> data, ref int o, Vec3 v)
    {
        WriteF32(data, ref o, v.X);
        WriteF32(data, ref o, v.Y);
        WriteF32(data, ref o, v.Z);
    }

    private static void WriteVec3(byte[] data, ref int o, Vec3 v) =>
        WriteVec3(data.AsSpan(), ref o, v);

    private static float ReadF32(byte[] data, ref int o)
    {
        var bits = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4));
        o += 4;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    private static void WriteF32(Span<byte> data, ref int o, float value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(o, 4), BitConverter.SingleToUInt32Bits(value));
        o += 4;
    }

    private static void WriteF32(byte[] data, ref int o, float value) =>
        WriteF32(data.AsSpan(), ref o, value);
}

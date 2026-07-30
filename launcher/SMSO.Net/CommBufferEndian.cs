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
        ReadSnapshotInto(data, ref o, ref buf.LocalSnapshot);
        for (int i = 0; i < buf.RemoteSnapshots.Length; i++)
            ReadSnapshotInto(data, ref o, ref buf.RemoteSnapshots[i]);
        buf.LocalNameTagAppearance = ReadAppearance(data, ref o);
        buf.RemoteNameTagAppearances ??= CommBuffer.CreateRemoteAppearanceArray();
        for (int i = 0; i < buf.RemoteNameTagAppearances.Length; i++)
            buf.RemoteNameTagAppearances[i] = ReadAppearance(data, ref o);
        buf.LocalMarioVoiceEvent = ReadMarioVoiceEvent(data, ref o);
        buf.RemoteMarioVoiceEvents ??= CommBuffer.CreateRemoteMarioVoiceEventArray();
        for (int i = 0; i < buf.RemoteMarioVoiceEvents.Length; i++)
            buf.RemoteMarioVoiceEvents[i] = ReadMarioVoiceEvent(data, ref o);
        ReadGameModeStateInto(data, ref o, ref buf.GameModeState);
        buf.WorldSync = ReadWorldSyncState(data, ref o);
        ReadRosterHudSyncInto(data, ref o, ref buf.RosterHud);
        buf.LocalMarioModelId ??= new byte[ProtocolConstants.MarioModelIdSize];
        Array.Copy(data, o, buf.LocalMarioModelId, 0, ProtocolConstants.MarioModelIdSize);
        o += ProtocolConstants.MarioModelIdSize;
        buf.RemoteMarioModelIds ??=
            new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
        Array.Copy(data, o, buf.RemoteMarioModelIds, 0, buf.RemoteMarioModelIds.Length);
        o += buf.RemoteMarioModelIds.Length;
        buf.ProgressSnapshotHostSeq = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4)); o += 4;
        buf.ProgressSnapshotModuleAppliedSeq = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4)); o += 4;
        buf.ProgressSnapshotPayloadLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2)); o += 2;
        buf.ProgressSnapshotFlags = data[o++];
        buf.ProgressSnapshotReserved = data[o++];
        buf.ProgressSnapshotPayload ??= new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        Array.Copy(data, o, buf.ProgressSnapshotPayload, 0, ProtocolConstants.CommProgressSnapshotMaxPayload);
        o += ProtocolConstants.CommProgressSnapshotMaxPayload;
        buf.MusicVolume = o < data.Length ? data[o] : ProtocolConstants.CommMusicVolumeDefault;
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
        WriteWorldSyncState(data, ref o, buffer.WorldSync);
        WriteRosterHudSync(data, ref o, buffer.RosterHud);
        var localModel = buffer.LocalMarioModelId ?? new byte[ProtocolConstants.MarioModelIdSize];
        localModel.AsSpan(0, ProtocolConstants.MarioModelIdSize).CopyTo(data.AsSpan(o, ProtocolConstants.MarioModelIdSize));
        o += ProtocolConstants.MarioModelIdSize;
        var remoteModels = buffer.RemoteMarioModelIds ??
            new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
        remoteModels.AsSpan(0, ProtocolConstants.CommMarioModelIdsSize - ProtocolConstants.MarioModelIdSize)
            .CopyTo(data.AsSpan(o, ProtocolConstants.CommMarioModelIdsSize - ProtocolConstants.MarioModelIdSize));
        o += ProtocolConstants.CommMarioModelIdsSize - ProtocolConstants.MarioModelIdSize;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(o, 4), buffer.ProgressSnapshotHostSeq); o += 4;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(o, 4), buffer.ProgressSnapshotModuleAppliedSeq); o += 4;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(o, 2), buffer.ProgressSnapshotPayloadLen); o += 2;
        data[o++] = buffer.ProgressSnapshotFlags;
        data[o++] = buffer.ProgressSnapshotReserved;
        var snapPayload = buffer.ProgressSnapshotPayload ??
            new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        var copyLen = Math.Min(snapPayload.Length, ProtocolConstants.CommProgressSnapshotMaxPayload);
        snapPayload.AsSpan(0, copyLen).CopyTo(data.AsSpan(o, copyLen));
        o += ProtocolConstants.CommProgressSnapshotMaxPayload;
        data[o] = buffer.MusicVolume;
        return data;
    }

    public static void WriteProgressSnapshotInto(Span<byte> dest, uint hostSeq, ushort payloadLen,
        ReadOnlySpan<byte> payload, byte flags = 0)
    {
        if (dest.Length < ProtocolConstants.CommProgressSnapshotSize)
            throw new ArgumentException("Progress snapshot buffer is too small.", nameof(dest));
        if (payloadLen > ProtocolConstants.CommProgressSnapshotMaxPayload)
            throw new ArgumentOutOfRangeException(nameof(payloadLen));

        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(0, 4), hostSeq);
        // Preserve moduleAppliedSeq — caller passes existing value via dest or we leave bytes 4-7.
        // Callers that want a full overwrite should clear moduleAppliedSeq separately.
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(8, 2), payloadLen);
        dest[10] = flags;
        dest[11] = 0;
        dest.Slice(12, ProtocolConstants.CommProgressSnapshotMaxPayload).Clear();
        if (payloadLen > 0)
            payload.Slice(0, payloadLen).CopyTo(dest.Slice(12, payloadLen));
    }

    public static byte[] ToRosterHudSyncDolphinBytes(in CommRosterHudSync sync)
    {
        var data = new byte[ProtocolConstants.CommRosterHudSyncSize];
        int o = 0;
        WriteRosterHudSync(data, ref o, sync);
        return data;
    }

    public static void WriteRosterHudSyncInto(Span<byte> dest, in CommRosterHudSync sync)
    {
        if (dest.Length < ProtocolConstants.CommRosterHudSyncSize)
            throw new ArgumentException("Roster HUD buffer is too small.", nameof(dest));

        int o = 0;
        WriteRosterHudSync(dest, ref o, sync);
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

    public static byte[] ToMarioModelIdsDolphinBytes(byte[] localModelId, byte[] remoteModelIds)
    {
        var data = new byte[ProtocolConstants.CommMarioModelIdsSize];
        WriteMarioModelIdsInto(data, localModelId, remoteModelIds);
        return data;
    }

    public static void WriteMarioModelIdsInto(Span<byte> dest, byte[] localModelId, byte[] remoteModelIds)
    {
        if (dest.Length < ProtocolConstants.CommMarioModelIdsSize)
            throw new ArgumentException("Mario model id buffer is too small.", nameof(dest));

        dest.Clear();
        var local = localModelId ?? new byte[ProtocolConstants.MarioModelIdSize];
        local.AsSpan(0, Math.Min(ProtocolConstants.MarioModelIdSize, local.Length))
            .CopyTo(dest.Slice(0, ProtocolConstants.MarioModelIdSize));
        var remotes = remoteModelIds ??
            new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
        remotes.AsSpan(0, Math.Min(remotes.Length, ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots))
            .CopyTo(dest.Slice(ProtocolConstants.MarioModelIdSize));
    }

    public static byte ClampMusicVolumePercent(int percent) =>
        (byte)Math.Clamp(percent, 0, 100);

    public static void WriteMusicVolumeInto(Span<byte> dest, byte percent)
    {
        if (dest.Length < ProtocolConstants.CommMusicVolumeSize)
            throw new ArgumentException("Music volume buffer is too small.", nameof(dest));
        dest[0] = ClampMusicVolumePercent(percent);
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

    public static byte[] ToIncomingWorldEventDolphinBytes(in CommWorldEvent incoming)
    {
        var data = new byte[ProtocolConstants.CommWorldEventSize];
        WriteIncomingWorldEventInto(data, incoming);
        return data;
    }

    public static void WriteIncomingWorldEventInto(Span<byte> dest, in CommWorldEvent incoming)
    {
        if (dest.Length < ProtocolConstants.CommWorldEventSize)
            throw new ArgumentException("Incoming world event buffer is too small.", nameof(dest));

        int o = 0;
        WriteWorldEvent(dest, ref o, incoming);
    }

    /// <summary>Decode a single big-endian CommWorldEvent (e.g. localPending re-read).</summary>
    public static CommWorldEvent ReadWorldEventFromDolphinBytes(byte[] data)
    {
        if (data is null || data.Length < ProtocolConstants.CommWorldEventSize)
            throw new ArgumentException("World-event buffer is too small.", nameof(data));

        int o = 0;
        return ReadWorldEvent(data, ref o);
    }

    private static CommWorldSyncState ReadWorldSyncState(byte[] data, ref int o)
    {
        var state = new CommWorldSyncState
        {
            LocalPendingOwnership = ReadWorldEvent(data, ref o),
            LocalPendingMission = ReadWorldEvent(data, ref o),
            IncomingOwnership = ReadWorldEvent(data, ref o),
            Incoming = ReadWorldEvent(data, ref o),
            LastAppliedEventId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4)),
        };
        o += 4;
        return state;
    }

    private static void WriteWorldSyncState(Span<byte> data, ref int o, in CommWorldSyncState state)
    {
        WriteWorldEvent(data, ref o, state.LocalPendingOwnership);
        WriteWorldEvent(data, ref o, state.LocalPendingMission);
        WriteWorldEvent(data, ref o, state.IncomingOwnership);
        WriteWorldEvent(data, ref o, state.Incoming);
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(o, 4), state.LastAppliedEventId);
        o += 4;
    }

    private static void ReadRosterHudSyncInto(byte[] data, ref int o, ref CommRosterHudSync sync)
    {
        sync.Events ??= CommRosterHudSync.CreateDefault().Events;
        sync.LatestSequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2));
        o += 2;
        for (int i = 0; i < sync.Events.Length; i++)
            ReadRosterHudEventInto(data, ref o, ref sync.Events[i]);
    }

    private static void WriteRosterHudSync(Span<byte> data, ref int o, in CommRosterHudSync sync)
    {
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), sync.LatestSequence);
        o += 2;
        var events = sync.Events ?? CommRosterHudSync.CreateDefault().Events;
        for (int i = 0; i < ProtocolConstants.CommRosterHudRingSlots; i++)
            WriteRosterHudEvent(data, ref o, i < events.Length ? events[i] : default);
    }

    private static void ReadRosterHudEventInto(byte[] data, ref int o, ref CommRosterHudEvent ev)
    {
        ev.Name ??= new byte[16];
        ev.Sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o, 2));
        o += 2;
        ev.Kind = (RosterHudEventKind)data[o++];
        ev.Slot = data[o++];
        Array.Copy(data, o, ev.Name, 0, 16);
        o += 16;
    }

    private static void WriteRosterHudEvent(Span<byte> data, ref int o, in CommRosterHudEvent ev)
    {
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), ev.Sequence);
        o += 2;
        data[o++] = (byte)ev.Kind;
        data[o++] = ev.Slot;
        var name = ev.Name ?? new byte[16];
        name.AsSpan(0, 16).CopyTo(data.Slice(o, 16));
        o += 16;
    }

    private static CommWorldEvent ReadWorldEvent(byte[] data, ref int o)
    {
        var worldEvent = new CommWorldEvent
        {
            EventId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o, 4)),
            Sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o + 4, 2)),
            Type = (WorldEventType)data[o + 6],
            CourseId = data[o + 7],
            EpisodeId = data[o + 8],
            Payload0 = data[o + 9],
            Reserved = data[o + 10],
            Payload1 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o + 11, 4)),
            Payload2 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o + 15, 4)),
        };
        o += ProtocolConstants.CommWorldEventSize;
        return worldEvent;
    }

    private static void WriteWorldEvent(Span<byte> data, ref int o, in CommWorldEvent worldEvent)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(o, 4), worldEvent.EventId);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o + 4, 2), worldEvent.Sequence);
        data[o + 6] = (byte)worldEvent.Type;
        data[o + 7] = worldEvent.CourseId;
        data[o + 8] = worldEvent.EpisodeId;
        data[o + 9] = worldEvent.Payload0;
        data[o + 10] = worldEvent.Reserved;
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(o + 11, 4), worldEvent.Payload1);
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(o + 15, 4), worldEvent.Payload2);
        o += ProtocolConstants.CommWorldEventSize;
    }

    private static void ReadGameModeStateInto(byte[] data, ref int o, ref CommGameModeState state)
    {
        state.RoleBySlot ??= new byte[ProtocolConstants.StableMaxPlayers];
        state.Mode = data[o];
        state.Flags = data[o + 1];
        state.LocalRole = data[o + 2];
        state.LastTaggedSlot = data[o + 3];
        state.TagEventId = data[o + 4];
        state.RoundStartMs = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o + 5, 4));
        Array.Copy(data, o + 9, state.RoleBySlot, 0, ProtocolConstants.StableMaxPlayers);
        state.GraceRemainingMs = BinaryPrimitives.ReadUInt16BigEndian(
            data.AsSpan(o + 9 + ProtocolConstants.StableMaxPlayers, 2));
        o += ProtocolConstants.CommGameModeStateSize;
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
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(o, 2), state.GraceRemainingMs);
        o += 2;
    }

    private static void WriteGameModeState(byte[] data, ref int o, in CommGameModeState state) =>
        WriteGameModeState(data.AsSpan(), ref o, state);

    private static void ReadSnapshotInto(byte[] data, ref int o, ref PlayerSnapshot snap)
    {
        snap.Name ??= new byte[16];
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

using System.Buffers.Binary;

namespace SMSO.Net;

public static class PacketSerializer
{
    private const int TcpHeaderSize = 9;
    private const int TcpCrcSize = 4;

    public static byte[] WrapTcp(TcpPacketId id, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ProtocolConstants.MaxTcpPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(payload), "TCP payload is too large.");

        var header = new byte[TcpHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), ProtocolConstants.ProtocolVersion);
        header[6] = (byte)id;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(7, 2), (ushort)payload.Length);
        payload.CopyTo(header.AsSpan(TcpHeaderSize));
        var crc = Crc32.Compute(header);
        var frame = new byte[header.Length + TcpCrcSize];
        header.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(header.Length, TcpCrcSize), crc);
        return frame;
    }

    public static bool TryUnwrapTcp(byte[] frame, out TcpPacketId id, out byte[] payload)
        => TryUnwrapTcp(frame.AsSpan(), out id, out payload);

    public static bool TryUnwrapTcp(ReadOnlySpan<byte> frame, out TcpPacketId id, out byte[] payload)
    {
        id = 0;
        payload = Array.Empty<byte>();
        if (frame.Length < TcpHeaderSize + TcpCrcSize) return false;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(0, 4));
        var version = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2));
        if (magic != ProtocolConstants.Magic || version != ProtocolConstants.ProtocolVersion)
            return false;

        id = (TcpPacketId)frame[6];
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(7, 2));
        if (len > ProtocolConstants.MaxTcpPayloadSize)
            return false;

        var total = TcpHeaderSize + len + TcpCrcSize;
        if (frame.Length != total) return false;

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(TcpHeaderSize + len, TcpCrcSize));
        if (Crc32.Compute(frame.Slice(0, TcpHeaderSize + len)) != expectedCrc)
            return false;

        payload = frame.Slice(TcpHeaderSize, len).ToArray();
        return true;
    }

    public static bool TryGetTcpFrameLength(ReadOnlySpan<byte> header, out int frameLength)
    {
        frameLength = 0;
        if (header.Length < TcpHeaderSize + TcpCrcSize)
            return false;

        if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4)) != ProtocolConstants.Magic)
            return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2)) != ProtocolConstants.ProtocolVersion)
            return false;

        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(7, 2));
        if (payloadLength > ProtocolConstants.MaxTcpPayloadSize)
            return false;

        frameLength = TcpHeaderSize + payloadLength + TcpCrcSize;
        return true;
    }

    public static byte[] BuildHandshake(Guid clientId, ushort? modBuildId = null)
    {
        var payload = new byte[ProtocolConstants.HandshakePayloadSize];
        clientId.ToByteArray().CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(16, 2),
            modBuildId ?? ProtocolConstants.ModBuildId);
        return WrapTcp(TcpPacketId.Handshake, payload);
    }

    public static bool TryReadHandshakeModBuildId(ReadOnlySpan<byte> payload, out ushort modBuildId)
    {
        modBuildId = 0;
        if (payload.Length < ProtocolConstants.HandshakePayloadSize)
            return false;
        modBuildId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(16, 2));
        return true;
    }

    public static byte[] BuildHandshakeAck(byte slot, ushort? modBuildId = null)
    {
        var ack = new byte[ProtocolConstants.HandshakeAckPayloadSize];
        ack[16] = slot;
        BinaryPrimitives.WriteUInt16LittleEndian(
            ack.AsSpan(17, 2),
            modBuildId ?? ProtocolConstants.ModBuildId);
        return WrapTcp(TcpPacketId.HandshakeAck, ack);
    }

    public static bool TryReadHandshakeAck(
        ReadOnlySpan<byte> payload,
        out byte slot,
        out ushort? serverModBuildId)
    {
        slot = 0;
        serverModBuildId = null;
        if (payload.Length < 17)
            return false;
        slot = payload[16];
        if (payload.Length >= ProtocolConstants.HandshakeAckPayloadSize)
            serverModBuildId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(17, 2));
        return true;
    }

    public static byte[] BuildJoinRequest(
        string username,
        string? marioModelId = null,
        ushort? modBuildId = null,
        ushort? gameProfileId = null)
    {
        var payload = new byte[ProtocolConstants.JoinRequestSize];
        var bytes = System.Text.Encoding.UTF8.GetBytes(username ?? string.Empty);
        Array.Copy(bytes, payload, Math.Min(bytes.Length, 15));
        var model = MarioPack.CharacterPack.EncodeModelId(marioModelId);
        model.CopyTo(payload, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(16 + ProtocolConstants.MarioModelIdSize, 2),
            modBuildId ?? ProtocolConstants.ModBuildId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(16 + ProtocolConstants.MarioModelIdSize + 2, 2),
            gameProfileId ?? ProtocolConstants.CurrentGameProfileId);
        return WrapTcp(TcpPacketId.JoinRequest, payload);
    }

    public static bool TryReadJoinRequest(
        ReadOnlySpan<byte> payload,
        out string username,
        out string marioModelId,
        out ushort modBuildId,
        out ushort gameProfileId)
    {
        username = string.Empty;
        marioModelId = string.Empty;
        modBuildId = 0;
        gameProfileId = 0;
        if (payload.Length < 16)
            return false;

        username = System.Text.Encoding.UTF8.GetString(payload.Slice(0, 16)).TrimEnd('\0');
        var modelEnd = 16 + ProtocolConstants.MarioModelIdSize;
        if (payload.Length >= modelEnd)
            marioModelId = MarioPack.CharacterPack.DecodeModelId(payload.Slice(16, ProtocolConstants.MarioModelIdSize));
        // Old clients omit ModBuildId; treat as 0 so the server rejects VersionMismatch.
        if (payload.Length >= modelEnd + 2)
            modBuildId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(modelEnd, 2));
        // Pre-profile clients omit the field; 0 fails the server profile gate.
        if (payload.Length >= modelEnd + 4)
            gameProfileId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(modelEnd + 2, 2));
        return true;
    }

    public static bool TryReadJoinRequest(
        ReadOnlySpan<byte> payload,
        out string username,
        out string marioModelId,
        out ushort modBuildId)
        => TryReadJoinRequest(payload, out username, out marioModelId, out modBuildId, out _);

    public static bool TryReadJoinRequest(ReadOnlySpan<byte> payload, out string username, out string marioModelId)
        => TryReadJoinRequest(payload, out username, out marioModelId, out _);

    public static byte[] BuildWarpRequest(byte targetSlot, byte courseId, byte episodeId)
    {
        var payload = new byte[] { targetSlot, courseId, episodeId };
        return WrapTcp(TcpPacketId.WarpRequest, payload);
    }

    public static byte[] BuildSyncSettings(bool syncFlags, bool syncObjects, bool syncProgress)
    {
        return WrapTcp(TcpPacketId.SyncSettings, new byte[]
        {
            (byte)(syncFlags ? 1 : 0),
            (byte)(syncObjects ? 1 : 0),
            (byte)(syncProgress ? 1 : 0),
        });
    }

    public static byte[] BuildClientTeleportSettings(bool allowClientTeleport)
        => WrapTcp(TcpPacketId.ClientTeleportSettings, new[] { (byte)(allowClientTeleport ? 1 : 0) });

    public static byte[] BuildGameModeState(in GameModeStatePacket state)
    {
        // mode(1)+flags(1)+seq(2)+roundStartMs(4)+tagEventId(1)+roles[N]+lastTaggedSlot(1)+graceRemainingMs(2)
        var payload = new byte[9 + ProtocolConstants.MaxPlayers + 1 + 2];
        payload[0] = (byte)state.GameMode;
        payload[1] = (byte)state.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), state.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), state.RoundStartMs);
        payload[8] = state.TagEventId;
        for (int i = 0; i < ProtocolConstants.MaxPlayers; i++)
            payload[9 + i] = state.GetRole((byte)i);
        payload[9 + ProtocolConstants.MaxPlayers] = state.LastTaggedSlot;
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(10 + ProtocolConstants.MaxPlayers, 2), state.GraceRemainingMs);
        return WrapTcp(TcpPacketId.GameModeState, payload);
    }

    public static bool TryReadGameModeState(ReadOnlySpan<byte> payload, out GameModeStatePacket state)
    {
        state = GameModeStatePacket.CreateDefault();
        if (payload.Length < 9 + ProtocolConstants.MaxPlayers + 1)
            return false;

        state.GameMode = (GameMode)payload[0];
        state.Flags = (GameModeFlags)payload[1];
        state.Seq = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        state.RoundStartMs = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        state.TagEventId = payload[8];
        for (int i = 0; i < ProtocolConstants.MaxPlayers; i++)
            state.SetRole((byte)i, (HideSeekRole)payload[9 + i]);
        state.LastTaggedSlot = payload[9 + ProtocolConstants.MaxPlayers];
        // GraceRemainingMs added with hide grace; older payloads omit it (treat as 0).
        if (payload.Length >= 11 + ProtocolConstants.MaxPlayers)
            state.GraceRemainingMs = BinaryPrimitives.ReadUInt16LittleEndian(
                payload.Slice(10 + ProtocolConstants.MaxPlayers, 2));
        return true;
    }

    public static byte[] BuildDisconnect(DisconnectReason reason)
        => WrapTcp(TcpPacketId.Disconnect, new[] { (byte)reason });

    public static byte[] BuildHeartbeat(long timestamp)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, timestamp);
        return WrapTcp(TcpPacketId.Heartbeat, buf);
    }

    public static byte[] BuildHeartbeat(ReadOnlySpan<byte> payload)
        => WrapTcp(TcpPacketId.Heartbeat, payload);

    public static byte[] BuildMarioModelIntent(string? marioModelId, uint sequence = 0)
    {
        var payload = new byte[ProtocolConstants.MarioModelIntentSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sequence);
        MarioPack.CharacterPack.EncodeModelId(marioModelId).CopyTo(payload, 4);
        return WrapTcp(TcpPacketId.MarioModelIntent, payload);
    }

    public static bool TryReadMarioModelIntent(ReadOnlySpan<byte> payload, out string marioModelId)
        => TryReadMarioModelIntent(payload, out _, out marioModelId);

    public static bool TryReadMarioModelIntent(
        ReadOnlySpan<byte> payload, out uint sequence, out string marioModelId)
    {
        sequence = 0;
        marioModelId = string.Empty;
        // Accept the original id-only payload from clients built before intent
        // sequencing. New clients include a monotonic sequence so stale queued
        // sends cannot roll the roster back after a rapid selection.
        if (payload.Length == ProtocolConstants.MarioModelIdSize)
        {
            marioModelId = MarioPack.CharacterPack.DecodeModelId(payload);
            return true;
        }
        if (payload.Length != ProtocolConstants.MarioModelIntentSize)
            return false;
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        marioModelId = MarioPack.CharacterPack.DecodeModelId(
            payload.Slice(4, ProtocolConstants.MarioModelIdSize));
        return true;
    }

    public static byte[] BuildMarioVoiceEvent(byte slot, in MarioVoiceEvent voiceEvent)
    {
        var payload = new byte[11];
        payload[0] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), voiceEvent.SoundId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5, 2), voiceEvent.Sequence);
        payload[7] = voiceEvent.Flags;
        payload[8] = voiceEvent.Health;
        payload[9] = voiceEvent.StageId;
        payload[10] = voiceEvent.EpisodeId;
        return WrapTcp(TcpPacketId.MarioVoiceEvent, payload);
    }

    public static bool TryReadMarioVoiceEvent(ReadOnlySpan<byte> payload, out byte slot, out MarioVoiceEvent voiceEvent)
    {
        slot = 0;
        voiceEvent = default;
        if (payload.Length < 11)
            return false;

        slot = payload[0];
        voiceEvent = new MarioVoiceEvent
        {
            SoundId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4)),
            Sequence = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(5, 2)),
            Flags = payload[7],
            Health = payload[8],
            StageId = payload[9],
            EpisodeId = payload[10],
        };
        return true;
    }

    public static byte[] BuildWorldProgressRequest()
        => BuildWorldProgressRequest(0);

    /// <summary>
    /// Client asks for a progress heal. When <paramref name="clientProgressSeq"/> matches
    /// the server's current seq, the server replies with an unchanged compact snapshot
    /// instead of re-shipping all ownership bits.
    /// </summary>
    public static byte[] BuildWorldProgressRequest(uint clientProgressSeq)
    {
        var payload = new byte[ProtocolConstants.WorldProgressRequestClientSeqSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, clientProgressSeq);
        return WrapTcp(TcpPacketId.WorldProgressRequest, payload);
    }

    public static bool TryReadWorldProgressRequestClientSeq(ReadOnlySpan<byte> payload,
        out uint clientProgressSeq)
    {
        clientProgressSeq = 0;
        if (payload.Length < ProtocolConstants.WorldProgressRequestClientSeqSize)
            return payload.Length == 0; // legacy empty request
        clientProgressSeq = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(0, ProtocolConstants.WorldProgressRequestClientSeqSize));
        return true;
    }

    public static byte[] BuildWorldProgressSnapshot(WorldProgressSnapshot snapshot)
        => WrapTcp(TcpPacketId.WorldProgressSnapshot, BuildWorldProgressSnapshotPayload(snapshot));

    /// <summary>
    /// LE payload body (no TCP wrapper) for the Dolphin progress-snapshot mailbox lane.
    /// </summary>
    public static byte[] BuildWorldProgressSnapshotPayload(WorldProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Worst case still far under MaxTcpPayloadSize; size exactly then copy.
        var estimated =
            1 + 1 + 4 + // version, flags, progressSeq
            WorldProgressSnapshot.ShineBitsByteCount + // shine bits (v2: 32)
            1 + snapshot.BlueCourses.Count * (1 + 8) +
            2 + snapshot.StoryFlags.Count * (4 + 1) +
            2 + snapshot.TriggerFlags.Count * (1 + 1 + 4 + 1) +
            2 + snapshot.SecretFlags.Count * (4 + 1) +
            2 + snapshot.RedStages.Count * (1 + 1 + 1 + 8 * 4) +
            2 + snapshot.NpcCleanStages.Count * (1 + 1 + 2);
        if (estimated > ProtocolConstants.MaxTcpPayloadSize)
            throw new InvalidOperationException(
                $"WorldProgressSnapshot estimated size {estimated} exceeds MaxTcpPayloadSize");

        var payload = new byte[estimated];
        var offset = 0;
        payload[offset++] = WorldProgressSnapshot.FormatVersion;
        payload[offset++] = snapshot.Unchanged ? WorldProgressSnapshot.FlagUnchanged : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), snapshot.ProgressSeq);
        offset += 4;

        if (snapshot.Unchanged)
        {
            Array.Resize(ref payload, offset);
            return payload;
        }

        if (snapshot.ShineBits.Length != WorldProgressSnapshot.ShineBitsByteCount)
            throw new ArgumentException(
                $"ShineBits must be {WorldProgressSnapshot.ShineBitsByteCount} bytes",
                nameof(snapshot));
        Buffer.BlockCopy(snapshot.ShineBits, 0, payload, offset,
            WorldProgressSnapshot.ShineBitsByteCount);
        offset += WorldProgressSnapshot.ShineBitsByteCount;

        payload[offset++] = (byte)Math.Min(byte.MaxValue, snapshot.BlueCourses.Count);
        var blueCount = payload[offset - 1];
        for (var i = 0; i < blueCount; i++)
        {
            var (courseId, mask) = snapshot.BlueCourses[i];
            payload[offset++] = courseId;
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(offset, 8), mask);
            offset += 8;
        }

        void WriteU16Count(int count)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2),
                (ushort)Math.Min(ushort.MaxValue, count));
            offset += 2;
        }

        WriteU16Count(snapshot.StoryFlags.Count);
        var storyCount = Math.Min(ushort.MaxValue, snapshot.StoryFlags.Count);
        for (var i = 0; i < storyCount; i++)
        {
            var (flagId, value) = snapshot.StoryFlags[i];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), flagId);
            offset += 4;
            payload[offset++] = value;
        }

        WriteU16Count(snapshot.TriggerFlags.Count);
        var triggerCount = Math.Min(ushort.MaxValue, snapshot.TriggerFlags.Count);
        for (var i = 0; i < triggerCount; i++)
        {
            var (courseId, episodeId, flagId, value) = snapshot.TriggerFlags[i];
            payload[offset++] = courseId;
            payload[offset++] = episodeId;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), flagId);
            offset += 4;
            payload[offset++] = value;
        }

        WriteU16Count(snapshot.SecretFlags.Count);
        var secretCount = Math.Min(ushort.MaxValue, snapshot.SecretFlags.Count);
        for (var i = 0; i < secretCount; i++)
        {
            var (flagId, value) = snapshot.SecretFlags[i];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), flagId);
            offset += 4;
            payload[offset++] = value;
        }

        WriteU16Count(snapshot.RedStages.Count);
        var redCount = Math.Min(ushort.MaxValue, snapshot.RedStages.Count);
        for (var i = 0; i < redCount; i++)
        {
            var stage = snapshot.RedStages[i];
            payload[offset++] = stage.CourseId;
            payload[offset++] = stage.EpisodeId;
            payload[offset++] = stage.Mask;
            for (var p = 0; p < 8; p++)
            {
                var packed = p < stage.PackedPos.Length ? stage.PackedPos[p] : 0u;
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), packed);
                offset += 4;
            }
        }

        WriteU16Count(snapshot.NpcCleanStages.Count);
        var npcCount = Math.Min(ushort.MaxValue, snapshot.NpcCleanStages.Count);
        for (var i = 0; i < npcCount; i++)
        {
            var (courseId, episodeId, mask) = snapshot.NpcCleanStages[i];
            payload[offset++] = courseId;
            payload[offset++] = episodeId;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), mask);
            offset += 2;
        }

        if (offset != payload.Length)
            Array.Resize(ref payload, offset);
        return payload;
    }

    public static bool TryReadWorldProgressSnapshot(ReadOnlySpan<byte> payload,
        out WorldProgressSnapshot snapshot)
    {
        snapshot = WorldProgressSnapshot.CreateUnchanged(0);
        if (payload.Length < 6)
            return false;

        var offset = 0;
        var version = payload[offset++];
        if (version != WorldProgressSnapshot.FormatVersion)
            return false;

        var flags = payload[offset++];
        var progressSeq = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        var unchanged = (flags & WorldProgressSnapshot.FlagUnchanged) != 0;
        if (unchanged)
        {
            snapshot = WorldProgressSnapshot.CreateUnchanged(progressSeq);
            return true;
        }

        if (payload.Length < offset + WorldProgressSnapshot.ShineBitsByteCount)
            return false;

        var shineBits = payload.Slice(offset, WorldProgressSnapshot.ShineBitsByteCount).ToArray();
        offset += WorldProgressSnapshot.ShineBitsByteCount;

        if (payload.Length < offset + 1)
            return false;
        var blueCount = payload[offset++];
        var blues = new List<(byte, ulong)>(blueCount);
        for (var i = 0; i < blueCount; i++)
        {
            if (payload.Length < offset + 1 + 8)
                return false;
            var courseId = payload[offset++];
            var mask = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8));
            offset += 8;
            blues.Add((courseId, mask));
        }

        if (!TryReadU16(payload, ref offset, out var storyCount))
            return false;
        var stories = new List<(uint, byte)>(storyCount);
        for (var i = 0; i < storyCount; i++)
        {
            if (payload.Length < offset + 5)
                return false;
            var flagId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var value = payload[offset++];
            stories.Add((flagId, value));
        }

        if (!TryReadU16(payload, ref offset, out var triggerCount))
            return false;
        var triggers = new List<(byte, byte, uint, byte)>(triggerCount);
        for (var i = 0; i < triggerCount; i++)
        {
            if (payload.Length < offset + 7)
                return false;
            var courseId = payload[offset++];
            var episodeId = payload[offset++];
            var flagId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var value = payload[offset++];
            triggers.Add((courseId, episodeId, flagId, value));
        }

        if (!TryReadU16(payload, ref offset, out var secretCount))
            return false;
        var secrets = new List<(uint, byte)>(secretCount);
        for (var i = 0; i < secretCount; i++)
        {
            if (payload.Length < offset + 5)
                return false;
            var flagId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var value = payload[offset++];
            secrets.Add((flagId, value));
        }

        if (!TryReadU16(payload, ref offset, out var redCount))
            return false;
        var reds = new List<WorldProgressSnapshot.RedStageMask>(redCount);
        for (var i = 0; i < redCount; i++)
        {
            if (payload.Length < offset + 3 + 32)
                return false;
            var courseId = payload[offset++];
            var episodeId = payload[offset++];
            var mask = payload[offset++];
            var packed = new uint[8];
            for (var p = 0; p < 8; p++)
            {
                packed[p] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
                offset += 4;
            }

            reds.Add(new WorldProgressSnapshot.RedStageMask(courseId, episodeId, mask, packed));
        }

        if (!TryReadU16(payload, ref offset, out var npcCount))
            return false;
        var npcs = new List<(byte, byte, ushort)>(npcCount);
        for (var i = 0; i < npcCount; i++)
        {
            if (payload.Length < offset + 4)
                return false;
            var courseId = payload[offset++];
            var episodeId = payload[offset++];
            var mask = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
            offset += 2;
            npcs.Add((courseId, episodeId, mask));
        }

        if (offset != payload.Length)
            return false;

        snapshot = new WorldProgressSnapshot
        {
            ProgressSeq = progressSeq,
            Unchanged = false,
            ShineBits = shineBits,
            BlueCourses = blues,
            StoryFlags = stories,
            TriggerFlags = triggers,
            SecretFlags = secrets,
            RedStages = reds,
            NpcCleanStages = npcs,
        };
        return true;
    }

    private static bool TryReadU16(ReadOnlySpan<byte> payload, ref int offset, out ushort value)
    {
        value = 0;
        if (payload.Length < offset + 2)
            return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
        offset += 2;
        return true;
    }

    public static byte[] BuildWorldEventRequest(in WorldEventRequest request)
    {
        var payload = new byte[ProtocolConstants.WorldEventClientPayloadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), request.Sequence);
        payload[2] = (byte)request.Type;
        payload[3] = request.CourseId;
        payload[4] = request.EpisodeId;
        payload[5] = request.Payload0;
        payload[6] = request.Reserved;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7, 4), request.Payload1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(11, 4), request.Payload2);
        return WrapTcp(TcpPacketId.WorldEvent, payload);
    }

    public static bool TryReadWorldEventRequest(ReadOnlySpan<byte> payload, out WorldEventRequest request)
    {
        request = default;
        if (payload.Length < ProtocolConstants.WorldEventClientPayloadSize)
            return false;

        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        var type = (WorldEventType)payload[2];
        if (sequence == 0 || type == 0)
            return false;

        request = new WorldEventRequest(
            sequence,
            type,
            payload[3],
            payload[4],
            payload[5],
            payload[6],
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(7, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(11, 4)));
        return true;
    }

    public static byte[] BuildWorldEventBroadcast(in WorldEventPacket packet)
    {
        var payload = new byte[ProtocolConstants.WorldEventBroadcastPayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), packet.EventId);
        payload[4] = (byte)packet.Type;
        payload[5] = packet.CourseId;
        payload[6] = packet.EpisodeId;
        payload[7] = packet.Payload0;
        payload[8] = packet.Reserved;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(9, 4), packet.Payload1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(13, 4), packet.Payload2);
        return WrapTcp(TcpPacketId.WorldEvent, payload);
    }

    public static bool TryReadWorldEventBroadcast(ReadOnlySpan<byte> payload, out WorldEventPacket packet)
    {
        packet = default;
        if (payload.Length < ProtocolConstants.WorldEventBroadcastPayloadSize)
            return false;

        var eventId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        var type = (WorldEventType)payload[4];
        if (eventId == 0 || type == 0)
            return false;

        var payload1 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(9, 4));
        var payload2 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(13, 4));
        packet = new WorldEventPacket(
            eventId,
            type,
            payload[5],
            payload[6],
            payload[7],
            payload[8],
            payload1,
            payload2);
        return true;
    }

    public static bool TryReadWorldStateReplay(ReadOnlySpan<byte> payload, out WorldEventPacket[] events)
    {
        events = Array.Empty<WorldEventPacket>();
        if (payload.Length < 2)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        var expected = 2 + count * ProtocolConstants.WorldEventBroadcastPayloadSize;
        if (payload.Length != expected)
            return false;

        if (count == 0)
        {
            events = Array.Empty<WorldEventPacket>();
            return true;
        }

        var parsed = new WorldEventPacket[count];
        var offset = 2;
        for (var i = 0; i < count; ++i)
        {
            if (!TryReadWorldEventBroadcast(payload.Slice(offset, ProtocolConstants.WorldEventBroadcastPayloadSize),
                    out parsed[i]))
            {
                return false;
            }

            offset += ProtocolConstants.WorldEventBroadcastPayloadSize;
        }

        events = parsed;
        return true;
    }

    public static byte[] BuildUdpRegister(ushort udpPort)
    {
        var buf = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, udpPort);
        return WrapTcp(TcpPacketId.UdpRegister, buf);
    }

    public static byte[] BuildUdpSnapshot(byte slot, uint seq, in PlayerSnapshot snap)
    {
        var bytes = new byte[ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.PlayerSnapshotSize];
        WriteUdpSnapshotInto(bytes, slot, seq, snap);
        return bytes;
    }

    /// <summary>
    /// Write a 74-byte UDP snapshot frame into a caller-provided buffer to avoid allocating a
    /// new array on every 60 Hz send. The buffer must be at least
    /// <see cref="ProtocolConstants.UdpSnapshotPayloadOffset"/> + <see cref="ProtocolConstants.PlayerSnapshotSize"/> bytes.
    /// </summary>
    public static void WriteUdpSnapshotInto(Span<byte> dest, byte slot, uint seq, in PlayerSnapshot snap)
    {
        if (dest.Length < ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.PlayerSnapshotSize)
            throw new ArgumentException("UDP snapshot buffer is too small.", nameof(dest));

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(0, 4), ProtocolConstants.Magic);
        dest[4] = (byte)UdpPacketId.PlayerSnapshot;
        dest[5] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(6, 4), seq);
        SnapshotToBytes(snap, dest.Slice(ProtocolConstants.UdpSnapshotPayloadOffset, ProtocolConstants.PlayerSnapshotSize));
    }

    public static void WriteUdpSnapshotBatchHeader(Span<byte> dest, byte count)
    {
        if (count > ProtocolConstants.StableMaxPlayers ||
            dest.Length < ProtocolConstants.UdpSnapshotBatchHeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(0, 4), ProtocolConstants.Magic);
        dest[4] = (byte)UdpPacketId.SnapshotBatch;
        dest[5] = count;
    }

    public static void WriteUdpSnapshotBatchEntry(
        Span<byte> dest, int index, byte slot, uint seq, in PlayerSnapshot snap)
    {
        if ((uint)index >= ProtocolConstants.StableMaxPlayers)
            throw new ArgumentOutOfRangeException(nameof(index));

        var offset = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                     index * ProtocolConstants.UdpSnapshotBatchEntrySize;
        if (dest.Length < offset + ProtocolConstants.UdpSnapshotBatchEntrySize)
            throw new ArgumentException("UDP snapshot batch buffer is too small.", nameof(dest));

        dest[offset] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(offset + 1, 4), seq);
        SnapshotToBytes(
            snap,
            dest.Slice(offset + 5, ProtocolConstants.PlayerSnapshotSize));
    }

    public static bool TryReadUdpSnapshotBatchEntry(
        ReadOnlySpan<byte> packet,
        int index,
        byte[] nameBuffer,
        out byte slot,
        out uint seq,
        out PlayerSnapshot snapshot)
    {
        slot = 0;
        seq = 0;
        snapshot = default;
        if (packet.Length < ProtocolConstants.UdpSnapshotBatchHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(0, 4)) != ProtocolConstants.Magic ||
            (UdpPacketId)packet[4] != UdpPacketId.SnapshotBatch)
        {
            return false;
        }

        var count = packet[5];
        if (count > ProtocolConstants.StableMaxPlayers || index < 0 || index >= count)
            return false;

        var required = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                       count * ProtocolConstants.UdpSnapshotBatchEntrySize;
        var offset = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                     index * ProtocolConstants.UdpSnapshotBatchEntrySize;
        if (packet.Length < required)
            return false;

        slot = packet[offset];
        seq = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset + 1, 4));
        snapshot = SnapshotFromBytes(
            packet.Slice(offset + 5, ProtocolConstants.PlayerSnapshotSize),
            nameBuffer);
        return true;
    }

    /// <summary>
    /// Write a UDP Ping frame (magic + Ping id + slot + zero seq + 8-byte LE timestamp) for RTT
    /// measurement on the UDP path. The server echoes the timestamp back as a Pong.
    /// </summary>
    public static void WriteUdpPingInto(Span<byte> dest, byte slot, long timestampMs)
    {
        if (dest.Length < ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.UdpPingPayloadSize)
            throw new ArgumentException("UDP ping buffer is too small.", nameof(dest));

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(0, 4), ProtocolConstants.Magic);
        dest[4] = (byte)UdpPacketId.Ping;
        dest[5] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(6, 4), 0u);
        BinaryPrimitives.WriteInt64LittleEndian(
            dest.Slice(ProtocolConstants.UdpSnapshotPayloadOffset, ProtocolConstants.UdpPingPayloadSize),
            timestampMs);
    }

    public static byte[] SnapshotToBytes(PlayerSnapshot snap)
    {
        var bytes = new byte[ProtocolConstants.PlayerSnapshotSize];
        SnapshotToBytes(snap, bytes);
        return bytes;
    }

    public static void SnapshotToBytes(in PlayerSnapshot snap, Span<byte> data)
    {
        if (data.Length < ProtocolConstants.PlayerSnapshotSize)
            throw new ArgumentException("Snapshot buffer is too small.", nameof(data));

        WriteSingle(data, 0, snap.Position.X);
        WriteSingle(data, 4, snap.Position.Y);
        WriteSingle(data, 8, snap.Position.Z);
        WriteSingle(data, 12, snap.Velocity.X);
        WriteSingle(data, 16, snap.Velocity.Y);
        WriteSingle(data, 20, snap.Velocity.Z);
        WriteSingle(data, 24, snap.RotationY);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(28, 2), snap.AnimId);
        data[30] = snap.NozzleId;
        data[31] = snap.Water;
        data[32] = snap.Health;
        data[33] = snap.StageId;
        data[34] = snap.EpisodeId;
        data[35] = snap.MovementState;
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(36, 2), snap.ActionId);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(38, 2), snap.VfxFlags);
        data[40] = snap.Connected;
        data[41] = snap.Slot;
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(42, 2), snap.PingMs);

        data.Slice(44, 16).Clear();
        if (snap.Name != null)
            snap.Name.AsSpan(0, Math.Min(16, snap.Name.Length)).CopyTo(data.Slice(44, 16));

        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(60, 2), snap.AnimFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(62, 2), snap.ActionIdHi);
    }

    public static PlayerSnapshot SnapshotFromBytes(byte[] data) => SnapshotFromBytes(data.AsSpan());

    public static PlayerSnapshot SnapshotFromBytes(ReadOnlySpan<byte> data)
        => SnapshotFromBytes(data, new byte[16]);

    public static PlayerSnapshot SnapshotFromBytes(ReadOnlySpan<byte> data, byte[] nameBuffer)
    {
        ArgumentNullException.ThrowIfNull(nameBuffer);
        if (nameBuffer.Length < 16)
            throw new ArgumentException("Snapshot name buffer is too small.", nameof(nameBuffer));

        if (data.Length < ProtocolConstants.PlayerSnapshotSize)
        {
            Array.Clear(nameBuffer, 0, 16);
            return new PlayerSnapshot { Name = nameBuffer };
        }

        data.Slice(44, 16).CopyTo(nameBuffer);

        return new PlayerSnapshot
        {
            Position = new Vec3
            {
                X = ReadSingle(data, 0),
                Y = ReadSingle(data, 4),
                Z = ReadSingle(data, 8),
            },
            Velocity = new Vec3
            {
                X = ReadSingle(data, 12),
                Y = ReadSingle(data, 16),
                Z = ReadSingle(data, 20),
            },
            RotationY = ReadSingle(data, 24),
            AnimId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)),
            NozzleId = data[30],
            Water = data[31],
            Health = data[32],
            StageId = data[33],
            EpisodeId = data[34],
            MovementState = data[35],
            ActionId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(36, 2)),
            VfxFlags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(38, 2)),
            Connected = data[40],
            Slot = data[41],
            PingMs = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(42, 2)),
            Name = nameBuffer,
            AnimFrame = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(60, 2)),
            ActionIdHi = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(62, 2)),
        };
    }

    private static void WriteSingle(Span<byte> data, int offset, float value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, 4), BitConverter.SingleToUInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset)
        => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
}

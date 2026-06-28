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

    public static byte[] BuildHandshake(Guid clientId) => WrapTcp(TcpPacketId.Handshake, clientId.ToByteArray());

    public static byte[] BuildJoinRequest(string username)
    {
        var name = new byte[16];
        var bytes = System.Text.Encoding.UTF8.GetBytes(username);
        Array.Copy(bytes, name, Math.Min(bytes.Length, 15));
        return WrapTcp(TcpPacketId.JoinRequest, name);
    }

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
        var payload = new byte[14];
        payload[0] = (byte)state.GameMode;
        payload[1] = (byte)state.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), state.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), state.RoundStartMs);
        payload[8] = state.TagEventId;
        for (int i = 0; i < ProtocolConstants.StableMaxPlayers; i++)
            payload[9 + i] = state.GetRole((byte)i);
        payload[13] = state.LastTaggedSlot;
        return WrapTcp(TcpPacketId.GameModeState, payload);
    }

    public static bool TryReadGameModeState(ReadOnlySpan<byte> payload, out GameModeStatePacket state)
    {
        state = GameModeStatePacket.CreateDefault();
        if (payload.Length < 14)
            return false;

        state.GameMode = (GameMode)payload[0];
        state.Flags = (GameModeFlags)payload[1];
        state.Seq = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        state.RoundStartMs = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        state.TagEventId = payload[8];
        for (int i = 0; i < ProtocolConstants.StableMaxPlayers; i++)
            state.SetRole((byte)i, (HideSeekRole)payload[9 + i]);
        state.LastTaggedSlot = payload[13];
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
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(7, 4)));
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
        packet = new WorldEventPacket(
            eventId,
            type,
            payload[5],
            payload[6],
            payload[7],
            payload[8],
            payload1);
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
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), ProtocolConstants.Magic);
        bytes[4] = (byte)UdpPacketId.PlayerSnapshot;
        bytes[5] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6, 4), seq);
        SnapshotToBytes(snap, bytes.AsSpan(ProtocolConstants.UdpSnapshotPayloadOffset, ProtocolConstants.PlayerSnapshotSize));
        return bytes;
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
    {
        if (data.Length < ProtocolConstants.PlayerSnapshotSize)
            return new PlayerSnapshot { Name = new byte[16] };

        var name = new byte[16];
        data.Slice(44, 16).CopyTo(name);

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
            Name = name,
            AnimFrame = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(60, 2)),
            ActionIdHi = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(62, 2)),
        };
    }

    private static void WriteSingle(Span<byte> data, int offset, float value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, 4), BitConverter.SingleToUInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset)
        => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
}

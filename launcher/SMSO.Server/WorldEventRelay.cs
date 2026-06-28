using System.Buffers.Binary;
using SMSO.Net;

namespace SMSO.Server;

public sealed class WorldEventRelay
{
    private uint _nextEventId = 1;
    private readonly List<WorldEventPacket> _history = new();

    public IReadOnlyList<WorldEventPacket> History => _history;

    public byte[] CreateWorldEvent(WorldEventType type, byte courseId, byte episodeId, byte payload0,
        byte reserved, uint payload1)
    {
        var id = _nextEventId++;
        var packet = new WorldEventPacket(id, type, courseId, episodeId, payload0, reserved, payload1);
        _history.Add(packet);
        return PacketSerializer.BuildWorldEventBroadcast(packet);
    }

    public byte[] BuildWorldStateReplay()
    {
        var payload = new byte[2 + _history.Count * ProtocolConstants.WorldEventBroadcastPayloadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)_history.Count);

        var offset = 2;
        foreach (var packet in _history)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), packet.EventId);
            payload[offset + 4] = (byte)packet.Type;
            payload[offset + 5] = packet.CourseId;
            payload[offset + 6] = packet.EpisodeId;
            payload[offset + 7] = packet.Payload0;
            payload[offset + 8] = packet.Reserved;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 9, 4), packet.Payload1);
            offset += ProtocolConstants.WorldEventBroadcastPayloadSize;
        }

        return PacketSerializer.WrapTcp(TcpPacketId.WorldStateReplay, payload);
    }
}

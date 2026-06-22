using SMSO.Net;

namespace SMSO.Server;

public sealed class WorldEventRelay
{
    private uint _nextEventId = 1;

    public byte[] CreateWorldEvent(WorldEventType type, byte courseId, byte episodeId, byte payload0, uint payload1)
    {
        var payload = new byte[12];
        var id = _nextEventId++;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), id);
        payload[4] = (byte)type;
        payload[5] = courseId;
        payload[6] = episodeId;
        payload[7] = payload0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), payload1);
        return PacketSerializer.WrapTcp(TcpPacketId.WorldEvent, payload);
    }
}

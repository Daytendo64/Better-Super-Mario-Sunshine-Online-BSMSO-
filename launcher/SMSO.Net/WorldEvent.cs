using System.Runtime.InteropServices;

namespace SMSO.Net;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = ProtocolConstants.CommWorldEventSize)]
public struct CommWorldEvent
{
    public uint EventId;
    public ushort Sequence;
    public WorldEventType Type;
    public byte CourseId;
    public byte EpisodeId;
    public byte Payload0;
    public byte Reserved;
    public uint Payload1;
    public uint Payload2;

    public bool IsEmpty => Sequence == 0 || Type == 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = ProtocolConstants.CommWorldSyncSize)]
public struct CommWorldSyncState
{
    public CommWorldEvent LocalPending;
    public CommWorldEvent Incoming;
    public uint LastAppliedEventId;
}

public readonly struct WorldEventRequest
{
    public WorldEventRequest(ushort sequence, WorldEventType type, byte courseId, byte episodeId,
        byte payload0, byte reserved, uint payload1, uint payload2 = 0)
    {
        Sequence = sequence;
        Type = type;
        CourseId = courseId;
        EpisodeId = episodeId;
        Payload0 = payload0;
        Reserved = reserved;
        Payload1 = payload1;
        Payload2 = payload2;
    }

    public ushort Sequence { get; }
    public WorldEventType Type { get; }
    public byte CourseId { get; }
    public byte EpisodeId { get; }
    public byte Payload0 { get; }
    public byte Reserved { get; }
    public uint Payload1 { get; }
    public uint Payload2 { get; }

    public bool IsEmpty => Sequence == 0 || Type == 0;
}

public readonly struct WorldEventPacket
{
    public WorldEventPacket(uint eventId, WorldEventType type, byte courseId, byte episodeId,
        byte payload0, byte reserved, uint payload1, uint payload2 = 0)
    {
        EventId = eventId;
        Type = type;
        CourseId = courseId;
        EpisodeId = episodeId;
        Payload0 = payload0;
        Reserved = reserved;
        Payload1 = payload1;
        Payload2 = payload2;
    }

    public uint EventId { get; }
    public WorldEventType Type { get; }
    public byte CourseId { get; }
    public byte EpisodeId { get; }
    public byte Payload0 { get; }
    public byte Reserved { get; }
    public uint Payload1 { get; }
    public uint Payload2 { get; }

    public CommWorldEvent ToIncomingEvent()
        => new()
        {
            EventId = EventId,
            Sequence = 0,
            Type = Type,
            CourseId = CourseId,
            EpisodeId = EpisodeId,
            Payload0 = Payload0,
            Reserved = Reserved,
            Payload1 = Payload1,
            Payload2 = Payload2,
        };
}

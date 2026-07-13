using System.Buffers.Binary;
using System.Linq;
using SMSO.Net;

namespace SMSO.Server;

public sealed class WorldEventRelay
{
    private uint _nextEventId = 1;
    private readonly List<WorldEventPacket> _durableHistory = new();
    private readonly object _gate = new();

    public IReadOnlyList<WorldEventPacket> History
    {
        get { lock (_gate) return _durableHistory.ToArray(); }
    }

    /// <summary>
    /// Ephemeral object/VFX events must not enter durable history. A 10-player session
    /// generates thousands of fruit/NPC events; WorldStateReplay is capped by TCP payload
    /// size and previously threw once history exceeded ~240 events — permanently breaking
    /// late-join / reconnect flag sync mid-run.
    /// </summary>
    public static bool IsDurable(WorldEventType type) => type is
        WorldEventType.ShineCollected or
        WorldEventType.BlueCoinCollected or
        WorldEventType.RedCoinCollected or
        WorldEventType.NpcCleaned or
        WorldEventType.EpisodeComplete or
        WorldEventType.StoryFlag or
        WorldEventType.TriggerFlag or
        WorldEventType.SecretComplete;

    public byte[] CreateWorldEvent(WorldEventType type, byte courseId, byte episodeId, byte payload0,
        byte reserved, uint payload1, uint payload2 = 0)
    {
        lock (_gate)
        {
            var id = _nextEventId++;
            var packet = new WorldEventPacket(id, type, courseId, episodeId, payload0, reserved, payload1, payload2);
            if (IsDurable(type))
                _durableHistory.Add(packet);
            return PacketSerializer.BuildWorldEventBroadcast(packet);
        }
    }

    public byte[] BuildWorldStateReplay()
    {
        lock (_gate)
            return BuildWorldStateReplay(_durableHistory);
    }

    public static byte[] BuildWorldStateReplay(IReadOnlyList<WorldEventPacket> events)
    {
        // Leave headroom under MaxTcpPayloadSize so WrapTcp never throws mid-run.
        var maxEvents = (ProtocolConstants.MaxTcpPayloadSize - 2) /
                        ProtocolConstants.WorldEventBroadcastPayloadSize;
        var count = Math.Min(events.Count, Math.Min(ushort.MaxValue, maxEvents));
        var payload = new byte[2 + count * ProtocolConstants.WorldEventBroadcastPayloadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)count);

        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            var packet = events[i];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), packet.EventId);
            payload[offset + 4] = (byte)packet.Type;
            payload[offset + 5] = packet.CourseId;
            payload[offset + 6] = packet.EpisodeId;
            payload[offset + 7] = packet.Payload0;
            payload[offset + 8] = packet.Reserved;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 9, 4), packet.Payload1);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 13, 4), packet.Payload2);
            offset += ProtocolConstants.WorldEventBroadcastPayloadSize;
        }

        return PacketSerializer.WrapTcp(TcpPacketId.WorldStateReplay, payload);
    }

    /// <summary>
    /// Rebuild a complete durable collectible + story-flag snapshot from authoritative state.
    /// Used for late join, periodic rebroadcast, and explicit client resync so a lost
    /// one-shot event can never leave a client permanently desynced.
    /// </summary>
    public byte[] BuildAuthoritySnapshotReplay(
        ShineAuthority shines,
        BlueCoinAuthority blueCoins,
        RedCoinAuthority redCoins,
        NpcCleanAuthority npcCleans,
        StoryFlagAuthority storyFlags)
    {
        lock (_gate)
        {
        var events = new List<WorldEventPacket>(64);
        var nextId = _nextEventId;

        foreach (var shineId in shines.Collected.OrderBy(id => id))
        {
            events.Add(new WorldEventPacket(nextId++, WorldEventType.ShineCollected, 0, 0, shineId, 0, 0));
        }

        foreach (var (courseId, mask) in blueCoins.AllCourses.OrderBy(pair => pair.Key))
        {
            for (byte index = 0; index < BlueCoinAuthority.MaxIndexExclusive; index++)
            {
                if ((mask & (1ul << index)) == 0)
                    continue;
                events.Add(new WorldEventPacket(
                    nextId++, WorldEventType.BlueCoinCollected, courseId, 0, index, 0, 0));
            }
        }

        foreach (var ((courseId, episodeId), mask) in redCoins.AllStages.OrderBy(pair => pair.Key))
        {
            var count = System.Numerics.BitOperations.PopCount(mask);
            for (byte index = 0; index < 8; index++)
            {
                if ((mask & (1 << index)) == 0)
                    continue;
                var payload0 = (byte)((count << 4) | index);
                events.Add(new WorldEventPacket(
                    nextId++, WorldEventType.RedCoinCollected, courseId, episodeId, payload0, index, 0));
            }
        }

        foreach (var ((courseId, episodeId), mask) in npcCleans.AllStages.OrderBy(pair => pair.Key))
        {
            var count = System.Numerics.BitOperations.PopCount(mask);
            for (byte index = 0; index < 16; index++)
            {
                if ((mask & (1 << index)) == 0)
                    continue;
                var payload0 = (byte)((count << 4) | index);
                events.Add(new WorldEventPacket(
                    nextId++, WorldEventType.NpcCleaned, courseId, episodeId, payload0, index, 0));
            }
        }

        foreach (var (flagId, value) in storyFlags.StoryFlags.OrderBy(pair => pair.Key))
        {
            events.Add(new WorldEventPacket(
                nextId++, WorldEventType.StoryFlag, 0, 0, value, 0, flagId));
        }

        foreach (var (key, value) in storyFlags.TriggerFlags
                     .OrderBy(pair => pair.Key.CourseId)
                     .ThenBy(pair => pair.Key.EpisodeId)
                     .ThenBy(pair => pair.Key.FlagId))
        {
            events.Add(new WorldEventPacket(
                nextId++, WorldEventType.TriggerFlag,
                key.CourseId, key.EpisodeId, value, 0, key.FlagId));
        }

        foreach (var (flagId, value) in storyFlags.SecretFlags.OrderBy(pair => pair.Key))
        {
            events.Add(new WorldEventPacket(
                nextId++, WorldEventType.SecretComplete, 0, 0, value, 0, flagId));
        }

        // Advance the live counter past synthetic snapshot ids so a later real event cannot
        // collide with a resync packet the client already applied.
        if (events.Count > 0)
            _nextEventId = nextId;

        return BuildWorldStateReplay(events);
        }
    }
}

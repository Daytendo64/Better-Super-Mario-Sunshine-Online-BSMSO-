using System;
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
        // GraffitiCleaned intentionally excluded — goop sync permanently disabled.
        WorldEventType.EpisodeComplete or
        WorldEventType.StoryFlag or
        WorldEventType.TriggerFlag or
        WorldEventType.SecretComplete;

    /// <summary>
    /// Card ownership only — diagnostic history no longer retains red/NPC (authorities heal).
    /// </summary>
    public static bool IsOwnershipDurable(WorldEventType type) => type is
        WorldEventType.ShineCollected or
        WorldEventType.BlueCoinCollected or
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
            // Authorities are the ONLY durable heal source. Tiny ownership-only diagnostic
            // ring for host Reset / debugging — never grow with playtime.
            if (IsOwnershipDurable(type))
            {
                _durableHistory.Add(packet);
                const int maxDiagnosticHistory = 4;
                if (_durableHistory.Count > maxDiagnosticHistory)
                    _durableHistory.RemoveRange(0, _durableHistory.Count - maxDiagnosticHistory);
            }
            return PacketSerializer.BuildWorldEventBroadcast(packet);
        }
    }

    /// <summary>
    /// Drops durable RedCoinCollected history for one stage after a solo mission reset
    /// so a full-history replay cannot resurrect cleared coins.
    /// </summary>
    public void RemoveRedCoinHistory(byte courseId, byte episodeId)
    {
        var key = RedCoinAuthority.NormalizeStage(courseId, episodeId);
        lock (_gate)
        {
            _durableHistory.RemoveAll(e =>
                e.Type == WorldEventType.RedCoinCollected &&
                RedCoinAuthority.NormalizeStage(e.CourseId, e.EpisodeId) == key);
        }
    }

    /// <summary>Drops durable red-coin history for every episode of a course (plaza hub empty).</summary>
    public void RemoveCourseRedCoinHistory(byte courseId)
    {
        lock (_gate)
        {
            _durableHistory.RemoveAll(e =>
                e.Type == WorldEventType.RedCoinCollected && e.CourseId == courseId);
        }
    }

    /// <summary>
    /// Drops durable shine/blue ownership history after a host Reset Flags so
    /// heal / late-join replay cannot resurrect cleared collectibles.
    /// </summary>
    public void RemoveShineBlueHistory()
    {
        lock (_gate)
        {
            _durableHistory.RemoveAll(e =>
                e.Type is WorldEventType.ShineCollected or WorldEventType.BlueCoinCollected);
        }
    }

    /// <summary>
    /// Drops all durable world-event history after a host session progress reset.
    /// </summary>
    public void ClearDurableHistory()
    {
        lock (_gate)
            _durableHistory.Clear();
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
    /// Legacy event-list heal. Prefer <see cref="BuildAuthorityProgressSnapshot"/>.
    /// Synthetic heal ids no longer advance <see cref="_nextEventId"/> (that inflation
    /// was a primary mid-run desync amplifier under lobby-wide resync).
    /// </summary>
    /// <param name="includeRedCoinStage">
    /// Optional filter for red-coin stages. True solo (never co-op this occupancy window)
    /// stages should be excluded so death-reset progress is never rebroadcast; stages that
    /// hit occupancy 2+ persist in heals even after a peer leaves (occupancy 1) so
    /// force-full / sticky co-op revive can restore authority bits.
    /// </param>
    public byte[] BuildAuthoritySnapshotReplay(
        ShineAuthority shines,
        BlueCoinAuthority blueCoins,
        RedCoinAuthority redCoins,
        NpcCleanAuthority npcCleans,
        StoryFlagAuthority storyFlags,
        Func<byte, byte, bool>? includeRedCoinStage = null)
    {
        var snapshot = BuildAuthorityProgressSnapshot(
            shines, blueCoins, redCoins, npcCleans, storyFlags, progressSeq: 0,
            includeRedCoinStage);
        return BuildWorldStateReplay(snapshot.ExpandToWorldEvents());
    }

    /// <summary>
    /// Compact authority heal from bitsets / sparse flag sets. Does not touch live event ids.
    /// </summary>
    public WorldProgressSnapshot BuildAuthorityProgressSnapshot(
        ShineAuthority shines,
        BlueCoinAuthority blueCoins,
        RedCoinAuthority redCoins,
        NpcCleanAuthority npcCleans,
        StoryFlagAuthority storyFlags,
        uint progressSeq,
        Func<byte, byte, bool>? includeRedCoinStage = null,
        bool unchanged = false)
    {
        if (unchanged)
            return WorldProgressSnapshot.CreateUnchanged(progressSeq);

        lock (_gate)
        {
            var shineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount];
            foreach (var shineId in shines.Collected)
                WorldProgressSnapshot.SetShineBit(shineBits, shineId);

            var blues = blueCoins.AllCourses
                .OrderBy(pair => pair.Key)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();

            var stories = storyFlags.StoryFlags
                .OrderBy(pair => pair.Key)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();

            var triggers = storyFlags.TriggerFlags
                .OrderBy(pair => pair.Key.CourseId)
                .ThenBy(pair => pair.Key.EpisodeId)
                .ThenBy(pair => pair.Key.FlagId)
                .Select(pair => (pair.Key.CourseId, pair.Key.EpisodeId, pair.Key.FlagId, pair.Value))
                .ToList();

            var secrets = storyFlags.SecretFlags
                .OrderBy(pair => pair.Key)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();

            var redStages = new List<WorldProgressSnapshot.RedStageMask>();
            foreach (var ((courseId, episodeId), mask) in redCoins.AllStages.OrderBy(pair => pair.Key))
            {
                if (includeRedCoinStage != null && !includeRedCoinStage(courseId, episodeId))
                    continue;

                var packed = new uint[8];
                for (byte index = 0; index < 8; index++)
                {
                    if ((mask & (1 << index)) == 0)
                        continue;
                    packed[index] = redCoins.PackedPos(courseId, episodeId, index);
                }

                redStages.Add(new WorldProgressSnapshot.RedStageMask(
                    courseId, episodeId, mask, packed));
            }

            var npcStages = npcCleans.AllStages
                .OrderBy(pair => pair.Key)
                .Select(pair => (pair.Key.CourseId, pair.Key.EpisodeId, pair.Value))
                .ToList();

            return new WorldProgressSnapshot
            {
                ProgressSeq = progressSeq,
                Unchanged = false,
                ShineBits = shineBits,
                BlueCourses = blues,
                StoryFlags = stories,
                TriggerFlags = triggers,
                SecretFlags = secrets,
                RedStages = redStages,
                NpcCleanStages = npcStages,
            };
        }
    }

    public byte[] BuildAuthorityProgressSnapshotFrame(
        ShineAuthority shines,
        BlueCoinAuthority blueCoins,
        RedCoinAuthority redCoins,
        NpcCleanAuthority npcCleans,
        StoryFlagAuthority storyFlags,
        uint progressSeq,
        Func<byte, byte, bool>? includeRedCoinStage = null,
        bool unchanged = false)
    {
        var snapshot = BuildAuthorityProgressSnapshot(
            shines, blueCoins, redCoins, npcCleans, storyFlags, progressSeq,
            includeRedCoinStage, unchanged);
        return PacketSerializer.BuildWorldProgressSnapshot(snapshot);
    }
}

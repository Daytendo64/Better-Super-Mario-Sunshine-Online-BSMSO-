using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SMSO.Net;

/// <summary>
/// Compact authoritative progress heal payload. Prefer this over exploding bitsets into
/// per-collectible <see cref="WorldStateReplay"/> events — heal cost stays O(progress bytes)
/// instead of O(N mailbox drains).
/// </summary>
public sealed class WorldProgressSnapshot
{
    /// <summary>
    /// v2: 256-bit shine ownership bitset (32 bytes). v1 (128-bit / 16 bytes) is a hard cut —
    /// mismatched peers are rejected via <see cref="ProtocolConstants.ModBuildId"/>.
    /// </summary>
    public const byte FormatVersion = 2;

    public const int ShineBitCapacity = ProtocolConstants.ShineBitCapacity;
    public const int ShineBitsByteCount = ProtocolConstants.ShineBitsByteCount;

    /// <summary>
    /// Synthetic heal event ids live in this high range so they never collide with live
    /// server <c>_nextEventId</c> sequencing and never bump the live counter.
    /// </summary>
    public const uint HealEventIdBase = 0x70000000u;

    public const byte FlagUnchanged = 1 << 0;

    public uint ProgressSeq { get; init; }
    public bool Unchanged { get; init; }

    /// <summary>
    /// Shine ownership bitset (shine id 0..<see cref="ShineBitCapacity"/>-1).
    /// Length must be <see cref="ShineBitsByteCount"/>.
    /// </summary>
    public byte[] ShineBits { get; init; } = new byte[ShineBitsByteCount];

    public IReadOnlyList<(byte CourseId, ulong Mask)> BlueCourses { get; init; } =
        Array.Empty<(byte, ulong)>();

    public IReadOnlyList<(uint FlagId, byte Value)> StoryFlags { get; init; } =
        Array.Empty<(uint, byte)>();

    public IReadOnlyList<(byte CourseId, byte EpisodeId, uint FlagId, byte Value)> TriggerFlags
    {
        get;
        init;
    } = Array.Empty<(byte, byte, uint, byte)>();

    public IReadOnlyList<(uint FlagId, byte Value)> SecretFlags { get; init; } =
        Array.Empty<(uint, byte)>();

    public IReadOnlyList<RedStageMask> RedStages { get; init; } = Array.Empty<RedStageMask>();

    public IReadOnlyList<(byte CourseId, byte EpisodeId, ushort Mask)> NpcCleanStages
    {
        get;
        init;
    } = Array.Empty<(byte, byte, ushort)>();

    public readonly struct RedStageMask
    {
        public RedStageMask(byte courseId, byte episodeId, byte mask, uint[] packedPos)
        {
            CourseId = courseId;
            EpisodeId = episodeId;
            Mask = mask;
            PackedPos = packedPos ?? new uint[8];
        }

        public byte CourseId { get; }
        public byte EpisodeId { get; }
        public byte Mask { get; }
        public uint[] PackedPos { get; }
    }

    public static WorldProgressSnapshot CreateUnchanged(uint progressSeq) => new()
    {
        ProgressSeq = progressSeq,
        Unchanged = true,
    };

    public int OwnershipEventCount
    {
        get
        {
            var count = PopCountBits(ShineBits);
            foreach (var (_, mask) in BlueCourses)
                count += BitOperations.PopCount(mask);
            count += StoryFlags.Count + TriggerFlags.Count + SecretFlags.Count;
            return count;
        }
    }

    public int MissionEventCount
    {
        get
        {
            var count = 0;
            foreach (var stage in RedStages)
                count += BitOperations.PopCount(stage.Mask);
            foreach (var (_, _, mask) in NpcCleanStages)
                count += BitOperations.PopCount(mask);
            return count;
        }
    }

    /// <summary>
    /// Mailbox heals only need ownership + the local stage's mission bits. Off-stage
    /// red/NPC masks are recovered on stage-enter when that stage becomes current.
    /// </summary>
    public WorldProgressSnapshot WithMissionFilteredToStage(byte stageId, byte episodeId,
        bool hasStage)
    {
        if (Unchanged)
            return this;

        IReadOnlyList<RedStageMask> reds = Array.Empty<RedStageMask>();
        IReadOnlyList<(byte, byte, ushort)> npcs = Array.Empty<(byte, byte, ushort)>();
        if (hasStage)
        {
            reds = RedStages
                .Where(s => s.CourseId == stageId && EpisodesMatch(stageId, s.EpisodeId, episodeId))
                .ToList();
            npcs = NpcCleanStages
                .Where(s => s.CourseId == stageId && EpisodesMatch(stageId, s.EpisodeId, episodeId))
                .ToList();
        }

        return new WorldProgressSnapshot
        {
            ProgressSeq = ProgressSeq,
            Unchanged = false,
            ShineBits = ShineBits,
            BlueCourses = BlueCourses,
            StoryFlags = StoryFlags,
            TriggerFlags = TriggerFlags,
            SecretFlags = SecretFlags,
            RedStages = reds,
            NpcCleanStages = npcs,
        };
    }

    private static bool EpisodesMatch(byte stageId, byte episodeA, byte episodeB)
        => LevelCatalog.EpisodesEquivalent(stageId, episodeA, episodeB);

    /// <summary>
    /// Expand into world events for the legacy single-slot mailbox apply path.
    /// Ownership first, then mission. Event ids are synthetic heal ids.
    /// </summary>
    public WorldEventPacket[] ExpandToWorldEvents()
    {
        if (Unchanged)
            return Array.Empty<WorldEventPacket>();

        var list = new List<WorldEventPacket>(OwnershipEventCount + MissionEventCount);
        uint nextId = HealEventIdBase;

        // Use int — byte would wrap at 255 and infinite-loop if capacity is 256.
        for (var shineId = 0; shineId < ShineBitCapacity; shineId++)
        {
            if (!TestBit(ShineBits, shineId))
                continue;
            list.Add(new WorldEventPacket(nextId++, WorldEventType.ShineCollected, 0, 0,
                (byte)shineId, 0, 0));
        }

        foreach (var (courseId, mask) in BlueCourses)
        {
            for (byte index = 0; index < 50; index++)
            {
                if ((mask & (1ul << index)) == 0)
                    continue;
                list.Add(new WorldEventPacket(
                    nextId++, WorldEventType.BlueCoinCollected, courseId, 0, index, 0, 0));
            }
        }

        foreach (var (flagId, value) in StoryFlags)
        {
            list.Add(new WorldEventPacket(
                nextId++, WorldEventType.StoryFlag, 0, 0, value, 0, flagId));
        }

        foreach (var (courseId, episodeId, flagId, value) in TriggerFlags)
        {
            list.Add(new WorldEventPacket(
                nextId++, WorldEventType.TriggerFlag, courseId, episodeId, value, 0, flagId));
        }

        foreach (var (flagId, value) in SecretFlags)
        {
            list.Add(new WorldEventPacket(
                nextId++, WorldEventType.SecretComplete, 0, 0, value, 0, flagId));
        }

        foreach (var stage in RedStages)
        {
            var count = BitOperations.PopCount(stage.Mask);
            for (byte index = 0; index < 8; index++)
            {
                if ((stage.Mask & (1 << index)) == 0)
                    continue;
                var payload0 = (byte)((count << 4) | index);
                var packed = index < stage.PackedPos.Length ? stage.PackedPos[index] : 0u;
                list.Add(new WorldEventPacket(
                    nextId++, WorldEventType.RedCoinCollected, stage.CourseId, stage.EpisodeId,
                    payload0, index, stage.Mask, packed));
            }
        }

        foreach (var (courseId, episodeId, mask) in NpcCleanStages)
        {
            var count = BitOperations.PopCount(mask);
            for (byte index = 0; index < 16; index++)
            {
                if ((mask & (1 << index)) == 0)
                    continue;
                var payload0 = (byte)((count << 4) | index);
                list.Add(new WorldEventPacket(
                    nextId++, WorldEventType.NpcCleaned, courseId, episodeId, payload0, index, 0));
            }
        }

        return list.ToArray();
    }

    public static void SetShineBit(byte[] bits, int shineId)
    {
        if (shineId < 0 || shineId >= ShineBitCapacity || bits.Length < ShineBitsByteCount)
            return;
        bits[shineId >> 3] |= (byte)(1 << (shineId & 7));
    }

    public static bool TestBit(byte[] bits, int shineId)
    {
        if (shineId < 0 || shineId >= ShineBitCapacity || bits.Length < ShineBitsByteCount)
            return false;
        return (bits[shineId >> 3] & (1 << (shineId & 7))) != 0;
    }

    private static int PopCountBits(byte[] bits)
    {
        var count = 0;
        foreach (var b in bits)
            count += BitOperations.PopCount(b);
        return count;
    }
}

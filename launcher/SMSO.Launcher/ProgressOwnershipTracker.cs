using System.Collections.Generic;
using SMSO.Net;

namespace SMSO.Launcher;

/// <summary>
/// Tracks ownership / mission bits already applied so compact progress heals only
/// enqueue deltas instead of re-flooding the single Dolphin mailbox.
/// Mission masks are replaced from authority snapshots (not grown from infinite live notes).
/// </summary>
internal sealed class ProgressOwnershipTracker
{
    private readonly HashSet<byte> _shines = new();
    private readonly Dictionary<byte, ulong> _blues = new();
    private readonly HashSet<uint> _storyFlags = new();
    private readonly HashSet<(byte CourseId, byte EpisodeId, uint FlagId)> _triggerFlags = new();
    private readonly HashSet<uint> _secretFlags = new();
    private readonly Dictionary<(byte CourseId, byte EpisodeId), byte> _redMasks = new();
    private readonly Dictionary<(byte CourseId, byte EpisodeId), ushort> _npcMasks = new();
    private readonly object _gate = new();

    public void Clear()
    {
        lock (_gate)
        {
            _shines.Clear();
            _blues.Clear();
            _storyFlags.Clear();
            _triggerFlags.Clear();
            _secretFlags.Clear();
            _redMasks.Clear();
            _npcMasks.Clear();
        }
    }

    public void NoteLiveEvent(in WorldEventPacket worldEvent)
    {
        lock (_gate)
            NoteUnlocked(worldEvent);
    }

    /// <summary>
    /// After a compact heal, rebuild mission notes from the mailbox snapshot so stale
    /// stage masks cannot accumulate forever while authorities remain source of truth.
    /// Ownership bits are unioned (grow-only).
    /// </summary>
    public void NoteSnapshotEvents(IEnumerable<WorldEventPacket> events, bool replaceMission = true)
    {
        lock (_gate)
        {
            if (replaceMission)
            {
                _redMasks.Clear();
                _npcMasks.Clear();
            }

            foreach (var worldEvent in events)
                NoteUnlocked(worldEvent);
        }
    }

    /// <summary>
    /// Drop off-stage mission masks. Red/NPC authority heals the current stage on enter;
    /// keeping every visited stage's mask forever grows with playtime for no benefit.
    /// </summary>
    public void PruneMissionToStage(byte stageId, byte episodeId)
    {
        lock (_gate)
        {
            PruneStageDict(_redMasks, stageId, episodeId);
            PruneStageDict(_npcMasks, stageId, episodeId);
        }
    }

    private static void PruneStageDict<T>(Dictionary<(byte CourseId, byte EpisodeId), T> dict,
        byte stageId, byte episodeId)
    {
        if (dict.Count == 0)
            return;

        List<(byte, byte)>? remove = null;
        foreach (var key in dict.Keys)
        {
            if (key.CourseId == stageId &&
                LevelCatalog.EpisodesEquivalent(stageId, key.EpisodeId, episodeId))
                continue;
            remove ??= new List<(byte, byte)>();
            remove.Add(key);
        }

        if (remove == null)
            return;
        foreach (var key in remove)
            dict.Remove(key);
    }

    public List<WorldEventPacket> FilterNewEvents(IReadOnlyList<WorldEventPacket> events,
        bool filterOwnership = true)
    {
        lock (_gate)
        {
            var delta = new List<WorldEventPacket>(events.Count);
            var redStageHandled = new HashSet<(byte, byte)>();
            var npcStageHandled = new HashSet<(byte, byte)>();

            foreach (var worldEvent in events)
            {
                switch (worldEvent.Type)
                {
                    case WorldEventType.ShineCollected:
                        if (filterOwnership && _shines.Contains(worldEvent.Payload0))
                            continue;
                        break;

                    case WorldEventType.BlueCoinCollected:
                        if (filterOwnership &&
                            _blues.TryGetValue(worldEvent.CourseId, out var blueMask) &&
                            (blueMask & (1ul << worldEvent.Payload0)) != 0)
                            continue;
                        break;

                    case WorldEventType.StoryFlag:
                        if (filterOwnership && _storyFlags.Contains(worldEvent.Payload1))
                            continue;
                        break;

                    case WorldEventType.TriggerFlag:
                        if (filterOwnership && _triggerFlags.Contains(
                                (worldEvent.CourseId, worldEvent.EpisodeId, worldEvent.Payload1)))
                            continue;
                        break;

                    case WorldEventType.SecretComplete:
                        if (filterOwnership && _secretFlags.Contains(worldEvent.Payload1))
                            continue;
                        break;

                    case WorldEventType.RedCoinCollected:
                    {
                        var key = (worldEvent.CourseId, worldEvent.EpisodeId);
                        var snapshotMask = (byte)worldEvent.Payload1;
                        _redMasks.TryGetValue(key, out var knownMask);

                        // First event for this stage in the batch: adopt shrinks without enqueue.
                        if (redStageHandled.Add(key) && knownMask != snapshotMask &&
                            (snapshotMask & ~knownMask) == 0)
                        {
                            _redMasks[key] = snapshotMask;
                        }

                        _redMasks.TryGetValue(key, out knownMask);
                        var bit = (byte)(1 << worldEvent.Reserved);
                        if (filterOwnership && (knownMask & bit) != 0)
                            continue;
                        break;
                    }

                    case WorldEventType.NpcCleaned:
                    {
                        var key = (worldEvent.CourseId, worldEvent.EpisodeId);
                        var bit = (ushort)(1 << worldEvent.Reserved);
                        _npcMasks.TryGetValue(key, out var knownMask);

                        // Shrink detection needs the full stage mask; NpcCleaned events only
                        // carry one bit each. Track via OR of known — if bit already set, skip.
                        if (filterOwnership && (knownMask & bit) != 0)
                            continue;

                        npcStageHandled.Add(key);
                        break;
                    }

                    default:
                        break;
                }

                delta.Add(worldEvent);
            }

            return delta;
        }
    }

    private void NoteUnlocked(in WorldEventPacket worldEvent)
    {
        switch (worldEvent.Type)
        {
            case WorldEventType.ShineCollected:
                _shines.Add(worldEvent.Payload0);
                break;
            case WorldEventType.BlueCoinCollected:
            {
                _blues.TryGetValue(worldEvent.CourseId, out var mask);
                _blues[worldEvent.CourseId] = mask | (1ul << worldEvent.Payload0);
                break;
            }
            case WorldEventType.StoryFlag:
                _storyFlags.Add(worldEvent.Payload1);
                break;
            case WorldEventType.TriggerFlag:
                _triggerFlags.Add((worldEvent.CourseId, worldEvent.EpisodeId, worldEvent.Payload1));
                break;
            case WorldEventType.SecretComplete:
                _secretFlags.Add(worldEvent.Payload1);
                break;
            case WorldEventType.RedCoinCollected:
                _redMasks[(worldEvent.CourseId, worldEvent.EpisodeId)] = (byte)worldEvent.Payload1;
                break;
            case WorldEventType.NpcCleaned:
            {
                var key = (worldEvent.CourseId, worldEvent.EpisodeId);
                _npcMasks.TryGetValue(key, out var mask);
                _npcMasks[key] = (ushort)(mask | (1 << worldEvent.Reserved));
                break;
            }
        }
    }
}

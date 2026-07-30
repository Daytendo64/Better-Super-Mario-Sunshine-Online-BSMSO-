using System.Collections.Generic;
using System.Numerics;
using SMSO.Net;

namespace SMSO.Server;

/// <summary>
/// Authoritative pollution-NPC cleaned state keyed by course/episode (Pianta Village
/// Ep. 6 "Piantas in Need", etc.). Rejects duplicate stable-index cleans so late join
/// / resync cannot double-count the mission HUD.
/// </summary>
public sealed class NpcCleanAuthority
{
    private readonly Dictionary<(byte CourseId, byte EpisodeId), StageState> _stages = new();
    private readonly object _gate = new();

    public void Reset()
    {
        lock (_gate)
            _stages.Clear();
    }

    /// <summary>
    /// Clears cleaned-NPC state for one course/episode when the stage empties so a
    /// re-entry can sync cleans again (matches SMS episode reset).
    /// </summary>
    public void ResetStage(byte courseId, byte episodeId)
    {
        lock (_gate)
            _stages.Remove(NormalizeStage(courseId, episodeId));
    }

    /// <summary>Clears every episode bucket for one course (plaza hub empty).</summary>
    public void ResetCourse(byte courseId)
    {
        lock (_gate)
        {
            var keys = new List<(byte CourseId, byte EpisodeId)>();
            foreach (var key in _stages.Keys)
            {
                if (key.CourseId == courseId)
                    keys.Add(key);
            }

            foreach (var key in keys)
                _stages.Remove(key);
        }
    }

    /// <summary>
    /// Coalesce Sirena casino/hotel mission ids onto catalog episodes so cleans
    /// share one key with roster occupancy and late-join snapshots.
    /// </summary>
    public static (byte CourseId, byte EpisodeId) NormalizeStage(byte courseId, byte episodeId)
        => (courseId, LevelCatalog.NormalizeEpisodeFromGame(courseId, episodeId));

    public bool TryAcceptCleaned(in WorldEventRequest request, out byte payload0, out byte reserved,
        out uint payload1)
    {
        payload0 = 0;
        reserved = 0;
        payload1 = 0;

        lock (_gate)
        {
            var stableIndex = ResolveStableIndex(request);
            if (stableIndex >= 16)
                return false;

            var stage = GetStage(request.CourseId, request.EpisodeId);
            if ((stage.CleanedMask & (1 << stableIndex)) != 0)
                return false;

            stage.CleanedMask |= (ushort)(1 << stableIndex);
            var authoritativeCount = BitOperations.PopCount(stage.CleanedMask);

            reserved = stableIndex;
            payload0 = (byte)((stableIndex & 0xF) | (authoritativeCount << 4));
            payload1 = request.Payload1;
            return true;
        }
    }

    internal ushort CleanedMask(byte courseId, byte episodeId)
    {
        lock (_gate)
        {
            var key = NormalizeStage(courseId, episodeId);
            return _stages.TryGetValue(key, out var stage) ? stage.CleanedMask : (ushort)0;
        }
    }

    public IReadOnlyDictionary<(byte CourseId, byte EpisodeId), ushort> AllStages
    {
        get
        {
            lock (_gate)
            {
                var copy = new Dictionary<(byte CourseId, byte EpisodeId), ushort>(_stages.Count);
                foreach (var pair in _stages)
                    copy[pair.Key] = pair.Value.CleanedMask;
                return copy;
            }
        }
    }

    private StageState GetStage(byte courseId, byte episodeId)
    {
        var key = NormalizeStage(courseId, episodeId);
        if (!_stages.TryGetValue(key, out var stage))
        {
            stage = new StageState();
            _stages[key] = stage;
        }

        return stage;
    }

    private static byte ResolveStableIndex(in WorldEventRequest request)
    {
        if (request.Reserved < 16)
            return request.Reserved;

        return 0xFF;
    }

    private sealed class StageState
    {
        public ushort CleanedMask;
    }
}

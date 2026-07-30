using System.Collections.Generic;
using System.Numerics;
using SMSO.Net;

namespace SMSO.Server;

/// <summary>
/// Authoritative red-coin state keyed by course/episode. Rejects duplicate stable-index
/// collections so two clients cannot increment the counter for the same coin.
/// Forwards optional packed initialPos (payload2) so remotes hide by position fingerprint.
/// Sirena casino/hotel mission ids are normalized to catalog episodes so authority,
/// occupancy, and late-join snapshots share one key (mission 3/4 ↔ catalog 0/1, etc.).
/// </summary>
public sealed class RedCoinAuthority
{
    private readonly Dictionary<(byte CourseId, byte EpisodeId), StageState> _stages = new();
    private readonly object _gate = new();

    /// <summary>
    /// Coalesce game/director mission ids onto the same catalog episode used by roster
    /// occupancy (see <see cref="LevelCatalog.NormalizeEpisodeFromGame"/>).
    /// </summary>
    public static (byte CourseId, byte EpisodeId) NormalizeStage(byte courseId, byte episodeId)
        => (courseId, LevelCatalog.NormalizeEpisodeFromGame(courseId, episodeId));

    public void Reset()
    {
        lock (_gate)
            _stages.Clear();
    }

    /// <summary>
    /// Clears collected-coin state for one course/episode. Called when no players remain in
    /// that stage so a re-entry can collect and sync red coins again (matches SMS episode reset).
    /// Also called when a solo player reloads the stage (death) so durable progress does not
    /// resurrect after vanilla clears the mission.
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
    /// Sentinel <see cref="WorldEventRequest.Reserved"/> for a solo mission reset
    /// (module stage-enter with no same-stage peer). Not a collectible index.
    /// </summary>
    public const byte MissionResetReserved = 0xFF;

    public static bool IsMissionResetRequest(in WorldEventRequest request) =>
        request.Type == WorldEventType.RedCoinCollected &&
        request.Reserved == MissionResetReserved;

    public bool TryAcceptCollected(in WorldEventRequest request, out byte payload0, out byte reserved,
        out uint payload1, out uint payload2)
    {
        payload0 = 0;
        reserved = 0;
        payload1 = 0;
        payload2 = 0;

        lock (_gate)
        {
            var stableIndex = ResolveStableIndex(request);
            if (stableIndex >= 8)
                return false;
            if (request.Reserved == MissionResetReserved)
                return false;

            var stage = GetStage(request.CourseId, request.EpisodeId);
            if ((stage.CollectedMask & (1 << stableIndex)) != 0)
                return false;

            stage.CollectedMask |= (byte)(1 << stableIndex);
            if (request.Payload2 != 0)
                stage.PackedPos[stableIndex] = request.Payload2;

            var authoritativeCount = BitOperations.PopCount(stage.CollectedMask);

            reserved = stableIndex;
            var hudSlot = request.Payload0 == 255 ? stableIndex : (byte)(request.Payload0 & 0xF);
            payload0 = (byte)(hudSlot | (authoritativeCount << 4));
            // Authoritative collected bitmask (low 8 bits).
            payload1 = stage.CollectedMask;
            // Position fingerprint for remote hide (0 when unknown / legacy clients).
            payload2 = stage.PackedPos[stableIndex];
            return true;
        }
    }

    internal byte CollectedMask(byte courseId, byte episodeId)
    {
        lock (_gate)
        {
            var key = NormalizeStage(courseId, episodeId);
            return _stages.TryGetValue(key, out var stage) ? stage.CollectedMask : (byte)0;
        }
    }

    internal uint PackedPos(byte courseId, byte episodeId, byte index)
    {
        lock (_gate)
        {
            var key = NormalizeStage(courseId, episodeId);
            if (!_stages.TryGetValue(key, out var stage) || index >= 8)
                return 0;
            return stage.PackedPos[index];
        }
    }

    public IReadOnlyDictionary<(byte CourseId, byte EpisodeId), byte> AllStages
    {
        get
        {
            lock (_gate)
            {
                var copy = new Dictionary<(byte CourseId, byte EpisodeId), byte>(_stages.Count);
                foreach (var pair in _stages)
                    copy[pair.Key] = pair.Value.CollectedMask;
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
        if (request.Reserved < 8)
            return request.Reserved;

        return 0xFF;
    }

    private sealed class StageState
    {
        public byte CollectedMask;
        public readonly uint[] PackedPos = new uint[8];
    }
}

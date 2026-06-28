using System.Collections.Generic;
using System.Numerics;
using SMSO.Net;

namespace SMSO.Server;

/// <summary>
/// Authoritative red-coin state keyed by course/episode. Rejects duplicate stable-index
/// collections so two clients cannot increment the counter for the same coin.
/// </summary>
public sealed class RedCoinAuthority
{
    private readonly Dictionary<(byte CourseId, byte EpisodeId), StageState> _stages = new();

    public void Reset()
    {
        _stages.Clear();
    }

    public bool TryAcceptCollected(in WorldEventRequest request, out byte payload0, out byte reserved,
        out uint payload1)
    {
        payload0 = 0;
        reserved = 0;
        payload1 = 0;

        var stableIndex = ResolveStableIndex(request);
        if (stableIndex >= 8)
            return false;

        var stage = GetStage(request.CourseId, request.EpisodeId);
        if ((stage.CollectedMask & (1 << stableIndex)) != 0)
            return false;

        stage.CollectedMask |= (byte)(1 << stableIndex);
        var authoritativeCount = BitOperations.PopCount(stage.CollectedMask);

        reserved = stableIndex;
        var hudSlot = request.Payload0 == 255 ? stableIndex : (byte)(request.Payload0 & 0xF);
        payload0 = (byte)(hudSlot | (authoritativeCount << 4));
        payload1 = request.Payload1;
        return true;
    }

    internal byte CollectedMask(byte courseId, byte episodeId)
        => _stages.TryGetValue((courseId, episodeId), out var stage) ? stage.CollectedMask : (byte)0;

    private StageState GetStage(byte courseId, byte episodeId)
    {
        var key = (courseId, episodeId);
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
    }
}

using System.Collections.Generic;
using SMSO.Net;

namespace SMSO.Server;

/// <summary>
/// Authoritative graffiti / pollution-clean stamps keyed by course/episode.
/// Grow-only 32u <b>XYZ</b> grid cells (deduped). Late join / ~45s / stage-enter
/// snapshots replay accepted stamps so remotes catch up without bitmap sync.
/// Delfino Plaza coalesces all episodes to <see cref="StoryFlagAuthority.PlazaHubEpisode"/>
/// so decideNextScenario does not split authority across hub episode buckets.
/// </summary>
public sealed class GraffitiCleanAuthority
{
    public const float CellSize = 32f;
    public const int MaxCellsPerStage = 384;

    /// <summary>Bit 30 marks a valid 10|10|10 cell pack in payload2.</summary>
    public const uint CellPackValidBit = 1u << 30;
    private const uint CellPackAxisMask = 0x3FFu;

    /// <summary>Original clean was wall splash (mCleanSize*32). Remotes may multi-stamp.</summary>
    public const byte ReservedWall = 0x01;
    /// <summary>Debounced finishing force-clear; may re-broadcast a known cell live.</summary>
    public const byte ReservedFinishing = 0x02;
    private const byte ReservedMask = ReservedWall | ReservedFinishing;

    private readonly Dictionary<(byte CourseId, byte EpisodeId), StageState> _stages = new();
    private readonly object _gate = new();

    public void Reset()
    {
        lock (_gate)
            _stages.Clear();
    }

    /// <summary>
    /// Clears graffiti stamps when a stage empties so a re-entry can sync cleans again
    /// (matches SMS episode pollution reload). Plaza resets the hub-global bucket.
    /// </summary>
    public void ResetStage(byte courseId, byte episodeId)
    {
        lock (_gate)
            _stages.Remove(NormalizeStage(courseId, episodeId));
    }

    public bool TryAcceptCleaned(in WorldEventRequest request, out byte payload0, out byte reserved,
        out uint payload1, out uint payload2)
    {
        payload0 = 0;
        reserved = 0;
        payload1 = 0;
        payload2 = 0;

        lock (_gate)
        {
            if (!TryResolveCell(request, out var cellX, out var cellY, out var cellZ, out var packedPos,
                    out var sizeQuant))
                return false;

            var flags = (byte)(request.Reserved & ReservedMask);
            var isFinishing = (flags & ReservedFinishing) != 0;

            var stage = GetStage(request.CourseId, request.EpisodeId);
            var key = (cellX, cellY, cellZ);
            if (stage.Cells.ContainsKey(key))
            {
                // Finishing catch-up: relay live without growing the grow-only set.
                if (!isFinishing)
                    return false;

                reserved = flags;
                payload0 = sizeQuant;
                payload1 = packedPos;
                payload2 = PackCell(cellX, cellY, cellZ);
                return true;
            }

            if (stage.Cells.Count >= MaxCellsPerStage)
                return false;

            stage.Cells[key] = new CellStamp(packedPos, sizeQuant);
            reserved = flags;
            payload0 = sizeQuant;
            payload1 = packedPos;
            payload2 = PackCell(cellX, cellY, cellZ);
            return true;
        }
    }

    /// <summary>
    /// Plaza hub episode for wire / occupancy. Non-plaza episodes pass through
    /// <see cref="LevelCatalog.NormalizeEpisodeFromGame"/> so Sirena casino/hotel
    /// mission ids share one authority bucket with roster occupancy.
    /// </summary>
    public static byte NormalizeEpisode(byte courseId, byte episodeId)
    {
        if (courseId == StoryFlagAuthority.PlazaAreaId)
            return StoryFlagAuthority.PlazaHubEpisode;
        return LevelCatalog.NormalizeEpisodeFromGame(courseId, episodeId);
    }

    public static (byte CourseId, byte EpisodeId) NormalizeStage(byte courseId, byte episodeId)
        => (courseId, NormalizeEpisode(courseId, episodeId));

    public IReadOnlyDictionary<(byte CourseId, byte EpisodeId), IReadOnlyList<GraffitiCell>> AllStages
    {
        get
        {
            lock (_gate)
            {
                var copy = new Dictionary<(byte CourseId, byte EpisodeId), IReadOnlyList<GraffitiCell>>(_stages.Count);
                foreach (var pair in _stages)
                {
                    var list = new List<GraffitiCell>(pair.Value.Cells.Count);
                    foreach (var cell in pair.Value.Cells)
                    {
                        list.Add(new GraffitiCell(cell.Key.CellX, cell.Key.CellY, cell.Key.CellZ,
                            cell.Value.PackedPos, cell.Value.SizeQuant));
                    }

                    copy[pair.Key] = list;
                }

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

    private static bool TryResolveCell(in WorldEventRequest request, out short cellX, out short cellY,
        out short cellZ, out uint packedPos, out byte sizeQuant)
    {
        cellX = 0;
        cellY = 0;
        cellZ = 0;
        packedPos = request.Payload1;
        sizeQuant = request.Payload0 == 0 ? (byte)8 : request.Payload0;

        if (TryUnpackCell(request.Payload2, out cellX, out cellY, out cellZ))
            return true;

        // Fallback: derive cell from packed world position (10-bit XYZ pack).
        if (request.Payload1 == 0 || request.Payload1 == 0x3FFFFFFFu)
            return false;

        const float scale = 16f;
        const float bias = 256f;
        var ex = request.Payload1 & 0x3FFu;
        var ey = (request.Payload1 >> 10) & 0x3FFu;
        var ez = (request.Payload1 >> 20) & 0x3FFu;
        var x = (ex - bias) * scale;
        var y = (ey - bias) * scale;
        var z = (ez - bias) * scale;
        cellX = (short)Math.Floor(x / CellSize);
        cellY = (short)Math.Floor(y / CellSize);
        cellZ = (short)Math.Floor(z / CellSize);
        return true;
    }

    public static uint PackCell(short cellX, short cellY, short cellZ) =>
        (uint)(cellX & (int)CellPackAxisMask) |
        ((uint)(cellY & (int)CellPackAxisMask) << 10) |
        ((uint)(cellZ & (int)CellPackAxisMask) << 20) |
        CellPackValidBit;

    public static bool TryUnpackCell(uint packed, out short cellX, out short cellY, out short cellZ)
    {
        cellX = 0;
        cellY = 0;
        cellZ = 0;
        if ((packed & CellPackValidBit) == 0)
            return false;

        cellX = SignExtend10(packed);
        cellY = SignExtend10(packed >> 10);
        cellZ = SignExtend10(packed >> 20);
        return true;
    }

    private static short SignExtend10(uint bits)
    {
        bits &= CellPackAxisMask;
        if ((bits & 0x200u) != 0)
            return (short)((int)bits - 0x400);
        return (short)bits;
    }

    public readonly record struct GraffitiCell(short CellX, short CellY, short CellZ, uint PackedPos,
        byte SizeQuant);

    private sealed class StageState
    {
        public Dictionary<(short CellX, short CellY, short CellZ), CellStamp> Cells { get; } = new();
    }

    private readonly record struct CellStamp(uint PackedPos, byte SizeQuant);
}

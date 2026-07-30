using System.Collections.Generic;
using SMSO.Net;

namespace SMSO.Server;

/// <summary>
/// LEGACY stub — graffiti/goop sync permanently disabled (2026-07-19).
/// Kept so older tests / references compile. <see cref="TryAcceptCleaned"/> always
/// returns false; GameServer ignores <see cref="WorldEventType.GraffitiCleaned"/>.
/// </summary>
public sealed class GraffitiCleanAuthority
{
    public const float CellSize = 32f;
    public const int MaxCellsPerStage = 384;
    public const uint CellPackValidBit = 1u << 30;
    private const uint CellPackAxisMask = 0x3FFu;
    public const byte ReservedWall = 0x01;
    public const byte ReservedFinishing = 0x02;

    private static readonly Dictionary<(byte CourseId, byte EpisodeId), IReadOnlyList<GraffitiCell>>
        EmptyStages = new();

    public void Reset() { }

    public void ResetStage(byte courseId, byte episodeId) { }

    public bool TryAcceptCleaned(in WorldEventRequest request, out byte payload0, out byte reserved,
        out uint payload1, out uint payload2)
    {
        payload0 = 0;
        reserved = 0;
        payload1 = 0;
        payload2 = 0;
        return false;
    }

    public IReadOnlyDictionary<(byte CourseId, byte EpisodeId), IReadOnlyList<GraffitiCell>> AllStages =>
        EmptyStages;

    public static byte NormalizeEpisode(byte courseId, byte episodeId) =>
        courseId == StoryFlagAuthority.PlazaAreaId
            ? StoryFlagAuthority.PlazaHubEpisode
            : episodeId;

    public static uint PackCell(short cellX, short cellY, short cellZ)
    {
        static uint Enc(short v) => (uint)(v + 512) & CellPackAxisMask;
        return Enc(cellX) | (Enc(cellY) << 10) | (Enc(cellZ) << 20) | CellPackValidBit;
    }

    public static bool TryUnpackCell(uint packed, out short cellX, out short cellY, out short cellZ)
    {
        cellX = cellY = cellZ = 0;
        if ((packed & CellPackValidBit) == 0)
            return false;
        cellX = (short)((int)(packed & CellPackAxisMask) - 512);
        cellY = (short)((int)((packed >> 10) & CellPackAxisMask) - 512);
        cellZ = (short)((int)((packed >> 20) & CellPackAxisMask) - 512);
        return true;
    }

    public readonly record struct GraffitiCell(short CellX, short CellY, short CellZ, byte SizeQuant,
        uint PackedPos);
}

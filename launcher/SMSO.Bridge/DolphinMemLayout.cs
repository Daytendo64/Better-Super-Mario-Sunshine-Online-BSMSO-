namespace SMSO.Bridge;

/// <summary>
/// Dolphin fastmem layout (Source/Core/Core/HW/Memmap.cpp).
/// Arena layout: 2 GiB guard | 4 GiB physical view | 2 GiB guard | 4 GiB logical view | 2 GiB guard.
/// </summary>
internal static class DolphinMemLayout
{
    public const ulong GuardSize = 0x80000000UL;           // 2 GiB
    public const ulong PpcViewSize = 0x100000000UL;        // 4 GiB
    public const ulong PhysicalBaseOffset = GuardSize;     // arena + 2 GiB
    public const ulong LogicalBaseOffset = PpcViewSize + GuardSize * 2; // arena + 8 GiB (0x200000000)

    /// <summary>Minimum arena size to hold logical-view anchor at 0x817FC000 (≈ 8.1 GiB from arena base).</summary>
    public const ulong MinFastmemArenaSize = 0x202000000UL;

    /// <summary>Offsets from the fastmem arena base to the logical/physical PPC views (guest 0x80000000).</summary>
    public static readonly ulong[] Mem1ViewBaseOffsets =
    {
        LogicalBaseOffset,   // DR on — what retail SMS uses in-game
        PhysicalBaseOffset,    // DR off
        0x000000000UL,         // non-fastmem / legacy direct map
        0x100000000UL,         // older 64-bit builds (4 GiB logical)
        0x280000000UL,         // BAT mirror views (legacy memmap)
        0x2C0000000UL,
    };
}

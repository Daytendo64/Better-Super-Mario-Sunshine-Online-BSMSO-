namespace SMSO.Net;

/// <summary>
/// Mirrors module/include/comm_buffer.hpp Yoshi snapshot field repurposing rules.
/// </summary>
public static class YoshiSnapshotCodec
{
    // TWaterGun::Yoshi (NTSC-U doldecomp).
    public const byte YoshiNozzleId = 4;
    private const ushort YoshiFruitEncShift = 11;

    public static bool SnapshotHostOnYoshi(byte packedNozzle, ushort vfxFlags) =>
        (vfxFlags & (ushort)VfxFlags.NoFludd) != 0 &&
        (packedNozzle & 0x0F) == YoshiNozzleId;

    public static bool YoshiTongueIsActive(byte packedHealth) =>
        ((packedHealth >> 2) & 0x07) != 0;

    public static bool SnapshotYoshiFruitMouthActive(in PlayerSnapshot snap) =>
        SnapshotHostOnYoshi(snap.NozzleId, snap.VfxFlags) &&
        (snap.VfxFlags & (ushort)VfxFlags.YoshiFruitMouth) != 0;

    public static byte UnpackFruitEnc(ushort vfxFlags)
    {
        if ((vfxFlags & (ushort)VfxFlags.YoshiFruitMouth) == 0)
            return 0;
        return (byte)((vfxFlags >> YoshiFruitEncShift) & 0x07);
    }

    public static byte LogicalStageId(in PlayerSnapshot snap, byte fallbackStageId) => snap.StageId;

    public static byte LogicalEpisodeId(in PlayerSnapshot snap, byte fallbackEpisodeId) => snap.EpisodeId;
}

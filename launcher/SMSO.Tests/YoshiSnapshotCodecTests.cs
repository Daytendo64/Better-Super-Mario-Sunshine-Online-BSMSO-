using SMSO.Net;

namespace SMSO.Tests;

public class YoshiSnapshotCodecTests
{
    [Fact]
    public void LogicalEpisodeId_UsesFallbackWhenFruitMouthFlagSet()
    {
        var snap = new PlayerSnapshot
        {
            NozzleId = YoshiSnapshotCodec.YoshiNozzleId,
            VfxFlags = (ushort)((ushort)VfxFlags.NoFludd | (ushort)VfxFlags.YoshiFruitMouth | (3 << 11)),
            EpisodeId = 9,
        };

        Assert.Equal(9, YoshiSnapshotCodec.LogicalEpisodeId(snap, 0));
        Assert.Equal(3, YoshiSnapshotCodec.UnpackFruitEnc(snap.VfxFlags));
    }

    [Fact]
    public void LogicalEpisodeId_PassesThroughWhenNotFruitMouth()
    {
        var snap = new PlayerSnapshot { EpisodeId = 5 };
        Assert.Equal(5, YoshiSnapshotCodec.LogicalEpisodeId(snap, 9));
    }

    [Fact]
    public void YoshiTongueIsActive_DetectsPackedState()
    {
        const byte packed = (byte)((2 << 2) | 1);
        Assert.True(YoshiSnapshotCodec.YoshiTongueIsActive(packed));
        Assert.False(YoshiSnapshotCodec.YoshiTongueIsActive(0x03));
    }
}

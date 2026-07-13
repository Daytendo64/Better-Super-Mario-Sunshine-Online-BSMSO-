using SMSO.Bridge;
using SMSO.Net;

namespace SMSO.Tests;

public sealed class DolphinBridgeCacheTests
{
    [Fact]
    public void RemoteSyncPayloadMatches_DetectsMailboxRegionWipe()
    {
        var snapshotAndNameSize =
            ProtocolConstants.CommRemoteSnapshotsSize + ProtocolConstants.CommNameTagAppearancesSize;
        var voiceAndModeSize =
            ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots +
            ProtocolConstants.CommGameModeStateSize;
        var voiceAndModeOffset =
            ProtocolConstants.CommMarioVoiceEventsOffset + ProtocolConstants.MarioVoiceEventSize;
        var expectedSnapshotsAndNames = Enumerable.Range(1, snapshotAndNameSize)
            .Select(i => (byte)(i % 251 + 1))
            .ToArray();
        var expectedVoicesAndMode = Enumerable.Range(1, voiceAndModeSize)
            .Select(i => (byte)(i % 239 + 1))
            .ToArray();
        var live = new byte[ProtocolConstants.CommBufferSize];
        expectedSnapshotsAndNames.CopyTo(live, ProtocolConstants.CommRemoteSnapshotsOffset);
        expectedVoicesAndMode.CopyTo(live, voiceAndModeOffset);

        Assert.True(DolphinBridge.RemoteSyncPayloadMatches(
            live, expectedSnapshotsAndNames, expectedVoicesAndMode));

        live.AsSpan(
            ProtocolConstants.CommRemoteSnapshotsOffset,
            ProtocolConstants.CommRemoteSnapshotsSize).Clear();
        Assert.False(DolphinBridge.RemoteSyncPayloadMatches(
            live, expectedSnapshotsAndNames, expectedVoicesAndMode));

        expectedSnapshotsAndNames.CopyTo(live, ProtocolConstants.CommRemoteSnapshotsOffset);
        live.AsSpan(voiceAndModeOffset, voiceAndModeSize).Clear();
        Assert.False(DolphinBridge.RemoteSyncPayloadMatches(
            live, expectedSnapshotsAndNames, expectedVoicesAndMode));
    }

    [Fact]
    public void PrepareForRelink_AdvancesWriteCacheEpoch()
    {
        using var bridge = new DolphinBridge();
        var before = bridge.WriteCacheEpoch;

        bridge.PrepareForRelink();

        Assert.NotEqual(before, bridge.WriteCacheEpoch);
    }

    [Fact]
    public void ModelIdScratch_IsNotRebuiltWhenDesiredMapIsUnchanged()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.ApplyLocalMarioModelId("luigi");
        var afterFirstBuild = worker.DebugModelIdScratchBuildCount;

        worker.ApplyLocalMarioModelId("luigi");

        Assert.Equal(afterFirstBuild, worker.DebugModelIdScratchBuildCount);
        worker.SetRemoteMarioModelId(2, "shadow");
        Assert.Equal(afterFirstBuild + 1, worker.DebugModelIdScratchBuildCount);
    }
}

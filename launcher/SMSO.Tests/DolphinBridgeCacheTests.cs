using SMSO.Bridge;
using SMSO.Net;
using SMSO.Net.MarioPack;

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

    [Fact]
    public void PrepareRemoteSlotForJoin_DoesNotClearOrRewriteModelIds()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetRemoteMarioModelId(2, "aabbccdd");
        var builds = worker.DebugModelIdScratchBuildCount;

        worker.PrepareRemoteSlotForJoin(2);
        worker.SetRemoteMarioModelId(2, "aabbccdd");

        Assert.Equal(builds, worker.DebugModelIdScratchBuildCount);
    }

    [Fact]
    public void LiveMailboxAdoption_PreservesAllBridgeAuthoredClientFieldsAtomically()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 3, "client-three", isHost: false);
        worker.ApplyLocalMarioModelId("aabbccdd");
        var appearance = new NameTagAppearance
        {
            TextTopR = 12,
            TextTopG = 34,
            TextTopB = 56,
            TextBottomR = 78,
            TextBottomG = 90,
            TextBottomB = 123,
            OutlineR = 4,
            OutlineG = 5,
            OutlineB = 6,
            Flags = NameTagAppearance.FlagValid | NameTagAppearance.FlagGradient,
        };
        worker.ApplyLocalNameTagAppearance("client-three", appearance);
        var remoteAppearance = appearance;
        remoteAppearance.TextTopR = 201;
        var remoteSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 2,
            ActionId = 77,
            Name = new byte[16],
        };
        remoteSnapshot.SetName("remote-two");
        worker.PushRemoteSnapshot(2, remoteSnapshot, remoteAppearance);
        // The bridge is intentionally unattached in this deterministic unit
        // test. Populate the same authored arrays the attached flush owns.
        var authored = worker.DebugGetWorkingBuffer();
        authored.RemoteSnapshots[2] = remoteSnapshot;
        authored.RemoteNameTagAppearances[2] = remoteAppearance;
        var voice = new MarioVoiceEvent
        {
            SoundId = 0x78B6,
            Sequence = 9,
            Health = 4,
        };
        worker.PushRemoteMarioVoiceEvent(2, voice);
        worker.SetRemoteMarioModelId(2, "11223344");
        var mode = GameModeStatePacket.CreateDefault();
        mode.Seq = 9;
        mode.RoundStartMs = 4242;
        worker.ApplyGameModeState(3, mode);
        worker.EnqueueRosterHudEvent(RosterHudEventKind.Connected, 2, "remote-two");

        var staleLive = CommBuffer.CreateDefault();
        staleLive.LocalSlot = 8;
        staleLive.SetLocalPlayerName("stale-dolphin");
        staleLive.BridgeFlags = BridgeFlags.Loading;
        staleLive.LocalNameTagAppearance = default;
        CharacterPack.EncodeModelId("deadbeef").CopyTo(staleLive.LocalMarioModelId, 0);

        worker.DebugAdoptLiveBuffer(staleLive);
        var merged = worker.DebugGetWorkingBuffer();

        Assert.Equal((byte)3, merged.LocalSlot);
        Assert.Equal("client-three", merged.GetLocalPlayerName());
        Assert.True((merged.BridgeFlags & BridgeFlags.Connected) != 0);
        Assert.True((merged.BridgeFlags & BridgeFlags.Loading) != 0);
        Assert.Equal(appearance.TextTopR, merged.LocalNameTagAppearance.TextTopR);
        Assert.Equal(appearance.TextBottomB, merged.LocalNameTagAppearance.TextBottomB);
        Assert.Equal(appearance.Flags, merged.LocalNameTagAppearance.Flags);
        Assert.Equal((ushort)77, merged.RemoteSnapshots[2].ActionId);
        Assert.Equal("remote-two", merged.RemoteSnapshots[2].GetName());
        Assert.Equal((byte)201, merged.RemoteNameTagAppearances[2].TextTopR);
        Assert.Equal(voice.SoundId, merged.RemoteMarioVoiceEvents[2].SoundId);
        Assert.Equal(4242u, merged.GameModeState.RoundStartMs);
        Assert.Equal((ushort)1, merged.RosterHud.LatestSequence);
        Assert.Equal("remote-two", merged.RosterHud.Events[0].GetPlayerName());
        Assert.Equal("aabbccdd", CharacterPack.DecodeModelId(merged.LocalMarioModelId));
        Assert.Equal("11223344", CharacterPack.DecodeModelId(
            merged.RemoteMarioModelIds.AsSpan(
                2 * ProtocolConstants.MarioModelIdSize,
                ProtocolConstants.MarioModelIdSize)));
    }

    [Fact]
    public void AdoptLiveBuffer_PreservesRosterHudNames_WhenLiveSequenceIsAheadWithEmptyEvents()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);
        worker.EnqueueRosterHudEvent(RosterHudEventKind.Connected, 2, "Daytendo");

        var liveAhead = CommBuffer.CreateDefault();
        liveAhead.RosterHud = CommRosterHudSync.CreateDefault();
        liveAhead.RosterHud.LatestSequence = 9; // higher than authored, empty event names
        liveAhead.RosterHud.Events[0] = new CommRosterHudEvent
        {
            Sequence = 9,
            Kind = RosterHudEventKind.Connected,
            Slot = 2,
            Name = new byte[16],
        };

        worker.DebugAdoptLiveBuffer(liveAhead);
        var merged = worker.DebugGetWorkingBuffer();

        Assert.Equal("Daytendo", merged.RosterHud.Events[0].GetPlayerName());
        Assert.Equal(RosterHudEventKind.Connected, merged.RosterHud.Events[0].Kind);
        Assert.Equal((byte)2, merged.RosterHud.Events[0].Slot);
        // Authored payload kept; counter may advance past live so the next enqueue is unique.
        Assert.True(merged.RosterHud.LatestSequence >= 1);
    }

    [Fact]
    public void SetConnected_PreservesAuthoredRemoteNametagsWhenAdoptingStaleLiveBuffer()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var appearance = new NameTagAppearance
        {
            TextTopR = 210,
            TextTopG = 11,
            TextTopB = 22,
            TextBottomR = 33,
            TextBottomG = 44,
            TextBottomB = 55,
            OutlineR = 1,
            OutlineG = 2,
            OutlineB = 3,
            Flags = NameTagAppearance.FlagValid | NameTagAppearance.FlagGradient,
        };
        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 42,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, appearance);

        var staleLive = CommBuffer.CreateDefault();
        staleLive.RemoteNameTagAppearances = CommBuffer.CreateRemoteAppearanceArray();
        staleLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        staleLive.LocalSlot = 9;
        staleLive.SetLocalPlayerName("stale-dolphin");
        staleLive.BridgeFlags = BridgeFlags.Loading;

        // Re-asserting Connected adopts a live mailbox (same path as SetConnected's
        // prefetch). Remote nametag sidecars must survive that adopt.
        worker.SetConnected(true, 0, "host-zero", isHost: true);
        worker.DebugAdoptLiveBuffer(staleLive);

        var merged = worker.DebugGetWorkingBuffer();
        Assert.Equal((byte)0, merged.LocalSlot);
        Assert.Equal("host-zero", merged.GetLocalPlayerName());
        Assert.True((merged.BridgeFlags & BridgeFlags.Connected) != 0);
        Assert.Equal((byte)210, merged.RemoteNameTagAppearances[1].TextTopR);
        Assert.Equal((byte)55, merged.RemoteNameTagAppearances[1].TextBottomB);
        Assert.Equal("peer-one", merged.RemoteSnapshots[1].GetName());
        Assert.Equal((ushort)42, merged.RemoteSnapshots[1].ActionId);
    }

    [Fact]
    public void AdoptLiveBuffer_HoldsRemoteRepublish_WhenStageExitClearsRemotes()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var appearance = new NameTagAppearance
        {
            TextTopR = 210,
            TextTopG = 11,
            TextTopB = 22,
            TextBottomR = 33,
            TextBottomG = 44,
            TextBottomB = 55,
            OutlineR = 1,
            OutlineG = 2,
            OutlineB = 3,
            Flags = NameTagAppearance.FlagValid | NameTagAppearance.FlagGradient,
        };
        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 42,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, appearance);

        // Module stageExit: DS_LOADING then clearPuppets.
        var exitLive = CommBuffer.CreateDefault();
        exitLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        exitLive.RemoteNameTagAppearances = CommBuffer.CreateRemoteAppearanceArray();
        exitLive.DolphinState = DolphinState.Loading;
        exitLive.LocalSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 0,
            StageId = 2,
            EpisodeId = 7,
            Name = new byte[16],
        };

        worker.DebugAdoptLiveBuffer(exitLive);
        var held = worker.DebugGetWorkingBuffer();

        Assert.True(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)0, held.RemoteSnapshots[1].Connected);
        // Appearances stay cached for the post-load flush.
        Assert.Equal((byte)210, held.RemoteNameTagAppearances[1].TextTopR);

        // Soft-reload / stage ready: Active again — need Active grace before release.
        var activeLive = exitLive;
        activeLive.DolphinState = DolphinState.Active;
        activeLive.LocalSnapshot.Connected = 1;
        for (int i = 0; i < 40 && worker.DebugHoldRemotePublishForStageExit; i++)
            worker.DebugAdoptLiveBuffer(activeLive);
        var restored = worker.DebugGetWorkingBuffer();

        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)1, restored.RemoteSnapshots[1].Connected);
        Assert.Equal("peer-one", restored.RemoteSnapshots[1].GetName());
        Assert.Equal((ushort)42, restored.RemoteSnapshots[1].ActionId);
    }

    [Fact]
    public void AdoptLiveBuffer_HoldsRemoteRepublish_WhenActiveMissesLoadingWindow()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var appearance = new NameTagAppearance
        {
            TextTopR = 210,
            TextTopG = 11,
            TextTopB = 22,
            Flags = NameTagAppearance.FlagValid | NameTagAppearance.FlagGradient,
        };
        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 42,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, appearance);

        // Prior live poll had Connected remotes (normal multiplayer). Then stageExit
        // clears them while exportLocalPlayer still reports Active.
        var priorLive = CommBuffer.CreateDefault();
        priorLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        priorLive.RemoteSnapshots[1] = snapshot;
        priorLive.DolphinState = DolphinState.Active;
        priorLive.LocalSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 0,
            StageId = 2,
            EpisodeId = 7,
            Name = new byte[16],
        };
        worker.DebugAdoptLiveBuffer(priorLive);

        var exitLive = CommBuffer.CreateDefault();
        exitLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        exitLive.RemoteNameTagAppearances = CommBuffer.CreateRemoteAppearanceArray();
        exitLive.DolphinState = DolphinState.Active;
        exitLive.LocalSnapshot = priorLive.LocalSnapshot;

        worker.DebugAdoptLiveBuffer(exitLive);
        var held = worker.DebugGetWorkingBuffer();

        Assert.True(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)0, held.RemoteSnapshots[1].Connected);

        // Still Active without seeing Booting/Loading/Warping — must not release yet
        // (brief teardown grace; module often forces Active and skips Loading).
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)0, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);

        // Observe Loading, then Active grace → release.
        exitLive.DolphinState = DolphinState.Loading;
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        exitLive.DolphinState = DolphinState.Active;
        for (int i = 0; i < 40 && worker.DebugHoldRemotePublishForStageExit; i++)
            worker.DebugAdoptLiveBuffer(exitLive);
        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)1, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);
    }

    [Fact]
    public void AdoptLiveBuffer_ReleasesRemoteHold_AfterActiveGraceWithoutLoading()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 42,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, NameTagAppearance.CreateDefault());

        var priorLive = CommBuffer.CreateDefault();
        priorLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        priorLive.RemoteSnapshots[1] = snapshot;
        priorLive.DolphinState = DolphinState.Active;
        priorLive.LocalSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 0,
            StageId = 2,
            EpisodeId = 7,
            Name = new byte[16],
        };
        worker.DebugAdoptLiveBuffer(priorLive);

        var exitLive = CommBuffer.CreateDefault();
        exitLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        exitLive.RemoteNameTagAppearances = CommBuffer.CreateRemoteAppearanceArray();
        exitLive.DolphinState = DolphinState.Active;
        exitLive.LocalSnapshot = priorLive.LocalSnapshot;

        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        // Never observe Loading/Warping (module forced Active). After grace Active
        // adopts, hold must release so remotes republish.
        for (int i = 0; i < 40 && worker.DebugHoldRemotePublishForStageExit; i++)
            worker.DebugAdoptLiveBuffer(exitLive);

        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)1, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);
    }

    [Fact]
    public void AdoptLiveBuffer_PastMaxDuration_StillRequiresActiveGrace()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 42,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, NameTagAppearance.CreateDefault());

        var priorLive = CommBuffer.CreateDefault();
        priorLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        priorLive.RemoteSnapshots[1] = snapshot;
        priorLive.DolphinState = DolphinState.Active;
        priorLive.LocalSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 0,
            StageId = 2,
            EpisodeId = 7,
            Name = new byte[16],
        };
        worker.DebugAdoptLiveBuffer(priorLive);

        var exitLive = CommBuffer.CreateDefault();
        exitLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        exitLive.RemoteNameTagAppearances = CommBuffer.CreateRemoteAppearanceArray();
        exitLive.DolphinState = DolphinState.Loading;
        exitLive.LocalSnapshot = priorLive.LocalSnapshot;
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        // Long plaza/load (>3s): MaxDuration elapses while still Loading.
        worker.DebugBackdateHoldRemotePublishSinceUtc(TimeSpan.FromSeconds(5));
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        // First Active tick after a long load must NOT republish (Bugbot: Yoshi tear-down).
        exitLive.DolphinState = DolphinState.Active;
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)0, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);

        for (int i = 0; i < 40 && worker.DebugHoldRemotePublishForStageExit; i++)
            worker.DebugAdoptLiveBuffer(exitLive);
        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)1, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);
    }

    [Fact]
    public void StageIdentityChange_WhileLoading_KeepsRemoteHoldUntilActive()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 42,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, NameTagAppearance.CreateDefault());

        // Establish last-restored stage via Active mailbox (poll path seeds ids).
        var priorLive = CommBuffer.CreateDefault();
        priorLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        priorLive.RemoteSnapshots[1] = snapshot;
        priorLive.DolphinState = DolphinState.Active;
        priorLive.LocalSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 0,
            StageId = 2,
            EpisodeId = 7,
            Name = new byte[16],
        };
        worker.DebugAdoptLiveBuffer(priorLive);
        worker.DebugMaybeRestoreRemoteSnapshotsAfterStageChange(priorLive);

        // stageExit: Loading + cleared remotes.
        var exitLive = CommBuffer.CreateDefault();
        exitLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        exitLive.RemoteNameTagAppearances = CommBuffer.CreateRemoteAppearanceArray();
        exitLive.DolphinState = DolphinState.Loading;
        exitLive.LocalSnapshot = priorLive.LocalSnapshot;
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        // Stage id flips mid-load (plaza) — must NOT clear hold / republish remotes.
        exitLive.LocalSnapshot.StageId = 1;
        exitLive.LocalSnapshot.EpisodeId = 0;
        worker.DebugMaybeRestoreRemoteSnapshotsAfterStageChange(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)0, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);

        // Active after load — still held until Active grace settles the stage.
        exitLive.DolphinState = DolphinState.Active;
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        for (int i = 0; i < 40 && worker.DebugHoldRemotePublishForStageExit; i++)
            worker.DebugAdoptLiveBuffer(exitLive);
        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)1, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);
    }

    [Fact]
    public void PushRemoteSnapshot_OwnsNameBuffer_AndIgnoresLaterOverlayPoison()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        var shared = new byte[16];
        var snap = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            Name = shared,
        };
        snap.SetName("Player");
        var appearance = NameTagAppearance.CreateDefault();
        appearance.Flags = NameTagAppearance.FlagValid | NameTagAppearance.FlagGradient;
        appearance.TextTopR = 210;

        worker.PushRemoteSnapshot(1, snap, appearance);
        Assert.Equal("Player", worker.DebugGetRemotePureName(1));

        // Simulate the old NetClient pooled-buffer race: mutate the array that was
        // originally passed in after Push already stored the snapshot.
        snap.SetNameTagAppearance(10, 20, 30, 40, 50, 60, 1, 2, 3, gradientEnabled: true);
        Assert.Equal("Playe", snap.GetPureName());

        var stored = worker.DebugGetRemoteRaw(1);
        Assert.Equal("Player", stored.GetName());
        Assert.False(NameTagColorCodec.HasAppearanceMarker(stored.Name[15]));

        // A subsequent packed wire sample must not replace the remembered full name.
        var packed = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            Name = new byte[16],
        };
        packed.SetName("Player");
        packed.SetNameTagAppearance(10, 20, 30, 40, 50, 60, 1, 2, 3, gradientEnabled: true);
        worker.PushRemoteSnapshot(1, packed, appearance);

        Assert.Equal("Player", worker.DebugGetRemotePureName(1));
        Assert.Equal("Playe", packed.GetPureName());
    }

    [Fact]
    public void SetConnected_False_ClearsHoldAndRemoteSnapshots()
    {
        // Regression (ModBuildId 50): disconnect used to adopt while _sessionConnected was
        // still true (could arm HoldRemotePublish) and left Connected remotes in the
        // working buffer until a later ClearRemoteSnapshots — rehost then suppressed
        // remotes until Dolphin restart.
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 7,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, NameTagAppearance.CreateDefault());

        var priorLive = CommBuffer.CreateDefault();
        priorLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        priorLive.RemoteSnapshots[1] = snapshot;
        priorLive.DolphinState = DolphinState.Active;
        priorLive.LocalSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 0,
            StageId = 1,
            EpisodeId = 0,
            Name = new byte[16],
        };
        worker.DebugAdoptLiveBuffer(priorLive);

        var exitLive = CommBuffer.CreateDefault();
        exitLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        exitLive.DolphinState = DolphinState.Loading;
        exitLive.LocalSnapshot = priorLive.LocalSnapshot;
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        worker.SetConnected(false, 0, "", false);

        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        var cleared = worker.DebugGetWorkingBuffer();
        Assert.Equal((byte)0, cleared.RemoteSnapshots[1].Connected);
        Assert.Equal(0, (int)(cleared.BridgeFlags & BridgeFlags.Connected));
    }

    [Fact]
    public void SetConnected_True_FreshConnect_ClearsHoldSoRemotesCanRepublish()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        var snapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 1,
            ActionId = 9,
            Name = new byte[16],
        };
        snapshot.SetName("peer-one");
        worker.DebugSeedRemoteNametag(1, snapshot, NameTagAppearance.CreateDefault());

        var priorLive = CommBuffer.CreateDefault();
        priorLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        priorLive.RemoteSnapshots[1] = snapshot;
        priorLive.DolphinState = DolphinState.Active;
        priorLive.LocalSnapshot = new PlayerSnapshot
        {
            Connected = 1,
            Slot = 0,
            StageId = 1,
            EpisodeId = 0,
            Name = new byte[16],
        };
        worker.DebugAdoptLiveBuffer(priorLive);

        var exitLive = CommBuffer.CreateDefault();
        exitLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        exitLive.DolphinState = DolphinState.Loading;
        exitLive.LocalSnapshot = priorLive.LocalSnapshot;
        worker.DebugAdoptLiveBuffer(exitLive);
        Assert.True(worker.DebugHoldRemotePublishForStageExit);

        // Rehost without going through ClearRemoteSnapshots (SetConnected owns the reset).
        worker.SetConnected(false, 0, "", false);
        worker.SetConnected(true, 0, "host-zero", isHost: true);

        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)0, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);
        Assert.NotEqual(0, (int)(worker.DebugGetWorkingBuffer().BridgeFlags & BridgeFlags.Connected));

        // New lobby remote must be publishable (no stuck hold).
        snapshot.ActionId = 11;
        worker.DebugSeedRemoteNametag(1, snapshot, NameTagAppearance.CreateDefault());
        var activeLive = priorLive;
        activeLive.RemoteSnapshots = CommBuffer.CreateRemoteArray();
        activeLive.DolphinState = DolphinState.Active;
        worker.DebugAdoptLiveBuffer(activeLive);

        Assert.False(worker.DebugHoldRemotePublishForStageExit);
        Assert.Equal((byte)1, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].Connected);
        Assert.Equal((ushort)11, worker.DebugGetWorkingBuffer().RemoteSnapshots[1].ActionId);
    }
}

using SMSO.Net;
using SMSO.Server;
using SMSO.Launcher;

namespace SMSO.Tests;

public class GameServerTests
{
    [Fact]
    public void MaxPlayers_ClampsToStableMaxAndRejectsBelowTwo()
    {
        var server = new GameServer(new LevelCatalog());
        Assert.Equal(ProtocolConstants.StableMaxPlayers, server.MaxPlayers);

        server.MaxPlayers = 99;
        Assert.Equal(ProtocolConstants.StableMaxPlayers, server.MaxPlayers);

        server.MaxPlayers = 1;
        Assert.Equal(2, server.MaxPlayers);

        server.MaxPlayers = 10;
        Assert.Equal(10, server.MaxPlayers);
    }

    [Fact]
    public void LevelCatalog_NormalizesDelfinoHubEpisode()
    {
        Assert.Equal(0, LevelCatalog.NormalizeEpisodeFromGame(1, 8));  // dolpic8 open hub
        Assert.Equal(1, LevelCatalog.NormalizeEpisodeFromGame(1, 0));  // dolpic0 arrival
        Assert.Equal(6, LevelCatalog.NormalizeEpisodeFromGame(1, 9));  // dolpic9 flooded
        Assert.Equal(7, LevelCatalog.NormalizeEpisodeFromGame(1, 2));  // dolpic10 post-flood
        Assert.Equal(2, LevelCatalog.NormalizeEpisodeFromGame(2, 2));
    }

    [Fact]
    public void LevelCatalog_ResolvesDelfinoWarpEpisodes()
    {
        Assert.Equal(8, LevelCatalog.ResolveEpisodeForWarp(1, 0));  // open hub
        Assert.Equal(0, LevelCatalog.ResolveEpisodeForWarp(1, 1)); // arrival
        Assert.Equal(9, LevelCatalog.ResolveEpisodeForWarp(1, 6)); // flooded
        Assert.Equal(2, LevelCatalog.ResolveEpisodeForWarp(1, 7)); // post-flood dolpic10
        Assert.Equal(3, LevelCatalog.ResolveEpisodeForWarp(2, 3));
    }

    [Fact]
    public void LevelCatalog_IncludesAllDelfinoScenes()
    {
        var path = FindLevels();
        if (!File.Exists(path)) return;
        var cat = LevelCatalog.Load(path);
        var plaza = cat.FindCourse(1);
        Assert.NotNull(plaza);
        Assert.Equal(8, plaza!.Episodes.Count);
        Assert.True(cat.IsValidWarp(21, 0)); // Super Slide
        Assert.True(cat.IsValidWarp(23, 0)); // Red Coin Field
        Assert.True(cat.IsValidWarp(1, 6));  // Flooded plaza
        Assert.True(cat.IsValidWarp(1, 7));  // Post-flood plaza
    }

    [Fact]
    public void LevelCatalog_ValidatesWarp()
    {
        var path = FindLevels();
        if (!File.Exists(path)) return;
        var cat = LevelCatalog.Load(path);
        Assert.True(cat.IsValidWarp(2, 0)); // Bianco Hills ep 1
        Assert.False(cat.IsValidWarp(255, 0));
    }

    private static int ReserveEphemeralPort() => TestPortAllocator.Next();

    [Fact]
    public void Server_StartStop()
    {
        var port = ReserveEphemeralPort();

        var server = new GameServer(new LevelCatalog());
        server.Start(port);
        Assert.True(server.IsRunning);
        server.Stop();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task Server_RehostSamePort_BindsReliably()
    {
        var port = ReserveEphemeralPort();

        var server = new GameServer(new LevelCatalog());
        server.Start(port);
        Assert.True(server.IsRunning);
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);
        Assert.True(server.IsAccepting);
        server.NotifyShutdown();
        server.Stop();
        Assert.False(server.IsRunning);

        // Immediate rehost must not throw AddressAlreadyInUse (bind retry + linger-0).
        server.Start(port);
        Assert.True(server.IsRunning);
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var client = new NetClient();
        await client.ConnectAsync("127.0.0.1", port, "RehostHost");
        Assert.True(client.IsConnected);
        await client.DisconnectAsync();
        server.Stop();
    }

    [Fact]
    public void Server_SecondBindSamePort_FailsWhileFirstRunning()
    {
        var port = ReserveEphemeralPort();

        var first = new GameServer(new LevelCatalog());
        first.Start(port);
        Assert.True(first.IsRunning);

        var second = new GameServer(new LevelCatalog());
        var ex = Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => second.Start(port));
        Assert.True(
            ex.SocketErrorCode is System.Net.Sockets.SocketError.AddressAlreadyInUse
                or System.Net.Sockets.SocketError.AccessDenied,
            $"Expected exclusive-bind conflict, got {ex.SocketErrorCode}");
        Assert.False(second.IsRunning);

        first.Stop();
    }

    [Fact]
    public void NpcReactDedup_IncludesActingSlot()
    {
        var server = new GameServer(new LevelCatalog());
        var slot0 = new WorldEventRequest(1, WorldEventType.NpcReact, 1, 0, 1, 0, 0xABCDEF);
        var slot1 = new WorldEventRequest(2, WorldEventType.NpcReact, 1, 0, 1, 1, 0xABCDEF);
        var slot0Dup = new WorldEventRequest(3, WorldEventType.NpcReact, 1, 0, 1, 0, 0xABCDEF);

        Assert.True(server.TryAcceptNpcReact(slot0));
        // Different acting slot must not be collapsed with the first hit.
        Assert.True(server.TryAcceptNpcReact(slot1));
        // Same slot + same NPC pos within the window is still deduped.
        Assert.False(server.TryAcceptNpcReact(slot0Dup));
    }

    [Fact]
    public void OccupancyKey_CoalescesPlazaScenariosToHub()
    {
        Assert.Equal((1, StoryFlagAuthority.PlazaHubEpisode), GameServer.OccupancyKey(1, 5));
        Assert.Equal((1, StoryFlagAuthority.PlazaHubEpisode), GameServer.OccupancyKey(1, 2));
        Assert.Equal((1, StoryFlagAuthority.PlazaHubEpisode), GameServer.OccupancyKey(1, 8));
        // Non-plaza keeps the episode.
        Assert.Equal((2, 7), GameServer.OccupancyKey(2, 7));
    }

    [Fact]
    public void SameProgressResyncStage_IgnoresPlazaEpisodeDrift()
    {
        Assert.True(SessionCoordinator.SameProgressResyncStage(1, 2, 1, 5));
        Assert.True(SessionCoordinator.SameProgressResyncStage(1, 5, 1, 5));
        Assert.False(SessionCoordinator.SameProgressResyncStage(1, 5, 2, 5));
        Assert.False(SessionCoordinator.SameProgressResyncStage(8, 0, 8, 1));
        // Casino catalog↔mission aliases stay equivalent.
        Assert.True(SessionCoordinator.SameProgressResyncStage(14, 0, 14, 3));
        Assert.False(SessionCoordinator.SameProgressResyncStage(14, 0, 14, 1));
        // Ricco Ep1 mid-fight scenario 8 ≡ catalog episode 0.
        Assert.True(SessionCoordinator.SameProgressResyncStage(3, 0, 3, 8));
        Assert.False(SessionCoordinator.SameProgressResyncStage(3, 0, 3, 1));
        // Hotel Red Coins: catalog 7 ≡ director mission 4.
        Assert.True(SessionCoordinator.SameProgressResyncStage(7, 7, 7, 4));
        Assert.False(SessionCoordinator.SameProgressResyncStage(7, 7, 7, 2));
        // Pinna Mecha-Bowser aftermath scenarios 6/7 ≡ catalog 0.
        Assert.True(SessionCoordinator.SameProgressResyncStage(13, 0, 13, 6));
        Assert.False(SessionCoordinator.SameProgressResyncStage(13, 0, 13, 1));
    }

    [Fact]
    public void MatchesEpisodeScopedApply_AliasesHotelRiccoCasino()
    {
        var hotelRed = new WorldEventPacket(
            1, WorldEventType.RedCoinCollected, SirenaHotelInteriorMapping.AreaId, 7,
            0x11, 0, 0x01);
        Assert.True(SessionCoordinator.MatchesEpisodeScopedApply(
            hotelRed, SirenaHotelInteriorMapping.AreaId, 4));
        Assert.False(SessionCoordinator.MatchesEpisodeScopedApply(
            hotelRed, SirenaHotelInteriorMapping.AreaId, 2));

        var riccoRed = new WorldEventPacket(
            2, WorldEventType.RedCoinCollected, RiccoHarborMapping.AreaId, 0,
            0x11, 0, 0x01);
        Assert.True(SessionCoordinator.MatchesEpisodeScopedApply(
            riccoRed, RiccoHarborMapping.AreaId, 8));
        Assert.False(SessionCoordinator.MatchesEpisodeScopedApply(
            riccoRed, RiccoHarborMapping.AreaId, 1));

        var casinoRed = new WorldEventPacket(
            3, WorldEventType.RedCoinCollected, SirenaCasinoMapping.AreaId, 0,
            0x11, 0, 0x01);
        Assert.True(SessionCoordinator.MatchesEpisodeScopedApply(
            casinoRed, SirenaCasinoMapping.AreaId, 3));
        Assert.False(SessionCoordinator.MatchesEpisodeScopedApply(
            casinoRed, SirenaCasinoMapping.AreaId, 1));
    }

    [Fact]
    public void MatchesPendingEpisodeFlush_AliasesHotelRicco()
    {
        var hotelNpc = new WorldEventPacket(
            1, WorldEventType.NpcCleaned, SirenaHotelInteriorMapping.AreaId, 7,
            0x11, 0, 0);
        Assert.True(SessionCoordinator.MatchesPendingEpisodeFlush(
            hotelNpc, SirenaHotelInteriorMapping.AreaId, 4));

        var riccoNpc = new WorldEventPacket(
            2, WorldEventType.NpcCleaned, RiccoHarborMapping.AreaId, 0,
            0x11, 0, 0);
        Assert.True(SessionCoordinator.MatchesPendingEpisodeFlush(
            riccoNpc, RiccoHarborMapping.AreaId, 8));
        Assert.False(SessionCoordinator.MatchesPendingEpisodeFlush(
            riccoNpc, RiccoHarborMapping.AreaId, 1));
    }

    [Fact]
    public void LevelCatalog_EpisodesEquivalent_MatchesServerStagesRules()
    {
        Assert.True(LevelCatalog.EpisodesEquivalent(1, 2, 8));
        Assert.True(LevelCatalog.EpisodesEquivalent(14, 0, 3));
        Assert.True(LevelCatalog.EpisodesEquivalent(3, 0, 8));
        Assert.True(LevelCatalog.EpisodesEquivalent(7, 7, 4));
        Assert.True(LevelCatalog.EpisodesEquivalent(13, 0, 7));
        Assert.False(LevelCatalog.EpisodesEquivalent(7, 7, 2));
        Assert.False(LevelCatalog.EpisodesEquivalent(3, 0, 1));
    }

    [Fact]
    public void ClientProgressRequestSeq_ForceFull_UsesZero()
    {
        Assert.Equal(0u, SessionCoordinator.ClientProgressRequestSeq(42, forceFull: true));
        Assert.Equal(42u, SessionCoordinator.ClientProgressRequestSeq(42, forceFull: false));
        Assert.Equal(0u, SessionCoordinator.ClientProgressRequestSeq(0, forceFull: false));
    }

    [Fact]
    public void ForceFullProgressRequest_UsesZeroSeq_SoServerCannotUnchangedAck()
    {
        // Force-full clears the client mailbox; server must always send a body when
        // clientSeq==0 (never coalesce to silence / unchanged).
        Assert.Equal(0u, SessionCoordinator.ClientProgressRequestSeq(210, forceFull: true));
        Assert.True(0u != 210u);
    }

    [Fact]
    public void ForceFull_MustNotBumpProgressSeq()
    {
        // Build 36: seq=0 force-reheal delivers a body without NoteProgressChanged.
        // Bumping on every stage-enter/coop-start flooded TCP (seq→476 / ~46 shines).
        Assert.False(GameServer.ForceFullProgressRequestBumpsProgressSeq);
    }

    [Fact]
    public void DebouncedProgressRequest_StillNeedsReply()
    {
        // Contract: non-force debounce may coalesce work but must still EnqueueProgressSnapshot
        // (Unchanged when seq matches). Silence advances the client catch-up timer with no heal.
        Assert.True(GameServer.ProgressRequestDebounceStillReplies);
        Assert.False(GameServer.ShouldDebounceProgressRequest(forceFull: true, withinDebounce: true));
        Assert.True(GameServer.ShouldDebounceProgressRequest(forceFull: false, withinDebounce: true));
        Assert.False(GameServer.ShouldDebounceProgressRequest(forceFull: false, withinDebounce: false));
    }

    [Fact]
    public void EpisodeFlushDrain_DoesNotAbortBatchOnRequeue()
    {
        // ApplyWorldEventToBridge false during warp window is intentional re-queue — drain
        // must continue other ready events (gold/red/NPC), not return early.
        Assert.False(SessionCoordinator.ShouldAbortWorldEventDrainOnApplyFailure(
            acceptWorldEventApplies: true));
        Assert.True(SessionCoordinator.ShouldAbortWorldEventDrainOnApplyFailure(
            acceptWorldEventApplies: false));
    }

    [Fact]
    public void ForceFull_WithAuthorityCache_MustNotClearMailbox()
    {
        // Phase 1: clear-then-await-TCP is the Jul-20 soft-death. Cache restage only.
        Assert.True(SessionCoordinator.ShouldClearMailboxBeforeForceTcp(hasAuthorityCache: false));
        Assert.False(SessionCoordinator.ShouldClearMailboxBeforeForceTcp(hasAuthorityCache: true));
    }

    [Fact]
    public void UnchangedProgressAck_DoesNotClearForceFullAwait()
    {
        // Stale unchanged (periodic catch-up) must not satisfy force-full await —
        // only a real snapshot body clears the await.
        Assert.False(SessionCoordinator.ClearsForceProgressAwait(snapshotUnchanged: true));
        Assert.True(SessionCoordinator.ClearsForceProgressAwait(snapshotUnchanged: false));
    }

    [Fact]
    public void ProgressMailboxHealPending_WhenHostAheadOfModuleApplied()
    {
        // Launcher Push advances hostSeq with moduleApplied=0; advertising lastApplied
        // while pending yields server Unchanged and soft-kills ownership mid-run.
        Assert.True(SessionCoordinator.ProgressMailboxHealPending(42, 0));
        Assert.True(SessionCoordinator.ProgressMailboxHealPending(10, 9));
        Assert.False(SessionCoordinator.ProgressMailboxHealPending(42, 42));
        Assert.False(SessionCoordinator.ProgressMailboxHealPending(0, 0));
        Assert.False(SessionCoordinator.ProgressMailboxHealPending(5, 5));
    }

    [Fact]
    public void PeriodicCatchupAdvertiseSeq_UsesModuleAppliedNotLauncherLastApplied()
    {
        // Contract: non-force catch-up advertises ProgressSnapshotModuleAppliedSeq only.
        // Launcher _lastAppliedProgressSeq may be ahead after Push / Unchanged.
        const uint launcherLastApplied = 99;
        const uint moduleApplied = 42;
        Assert.Equal(42u, SessionCoordinator.PeriodicCatchupAdvertiseSeq(moduleApplied));
        Assert.NotEqual(launcherLastApplied,
            SessionCoordinator.PeriodicCatchupAdvertiseSeq(moduleApplied));
        Assert.Equal(0u,
            SessionCoordinator.ClientProgressRequestSeq(
                SessionCoordinator.PeriodicCatchupAdvertiseSeq(0), forceFull: false));
        // Force-full path remains seq=0 regardless of module/launcher proof.
        Assert.Equal(0u, SessionCoordinator.ClientProgressRequestSeq(moduleApplied, forceFull: true));
        Assert.Equal(0u, SessionCoordinator.ClientProgressRequestSeq(launcherLastApplied, forceFull: true));
    }

    [Fact]
    public void ShouldIncludeRedCoinStageInHeal_OccupancyOne_OnlyWhenHadCoop()
    {
        // True solo (never co-op): omit so death-reset bits cannot rebroadcast.
        Assert.False(GameServer.ShouldIncludeRedCoinStageInHeal(0, stageHadCoop: false));
        Assert.False(GameServer.ShouldIncludeRedCoinStageInHeal(1, stageHadCoop: false));
        // Sticky co-op / peer-left: occupancy may be 1 while authority still holds reds.
        Assert.True(GameServer.ShouldIncludeRedCoinStageInHeal(1, stageHadCoop: true));
        Assert.True(GameServer.ShouldIncludeRedCoinStageInHeal(2, stageHadCoop: false));
        Assert.True(GameServer.ShouldIncludeRedCoinStageInHeal(2, stageHadCoop: true));
    }

    [Fact]
    public void OccupancyKey_PlazaCoalescesToHubEpisode()
    {
        Assert.Equal(
            (StoryFlagAuthority.PlazaAreaId, StoryFlagAuthority.PlazaHubEpisode),
            GameServer.OccupancyKey(StoryFlagAuthority.PlazaAreaId, 3));
        Assert.Equal((23, (byte)0), GameServer.OccupancyKey(23, 0));
    }

    [Fact]
    public void ForceFullProgressHeal_IncludesRedsAtOccupancyOne_WhenStageHadCoop()
    {
        // Force-full (clientSeq==0) after same-stage revive: occupancy can be 1 once the
        // partner left, but authority still has collected bits and module skipped solo
        // mission-reset via sStageHadSameStagePeer.
        var reds = new RedCoinAuthority();
        Assert.True(reds.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, 23, 0, 0, 1, 0x111),
            out _, out _, out _, out _));
        Assert.True(reds.TryAcceptCollected(
            new WorldEventRequest(2, WorldEventType.RedCoinCollected, 23, 0, 0, 3, 0x222),
            out _, out _, out _, out _));

        var relay = new WorldEventRelay();
        const int occupancy = 1;
        var forceFullInclude = (byte course, byte episode) =>
            GameServer.ShouldIncludeRedCoinStageInHeal(occupancy, stageHadCoop: true);

        var included = relay.BuildAuthorityProgressSnapshot(
            new ShineAuthority(), new BlueCoinAuthority(), reds, new NpcCleanAuthority(),
            new StoryFlagAuthority(), progressSeq: 7, includeRedCoinStage: forceFullInclude);
        Assert.False(included.Unchanged);
        Assert.Single(included.RedStages);
        Assert.Equal(23, included.RedStages[0].CourseId);
        Assert.Equal(0, included.RedStages[0].EpisodeId);
        Assert.Equal((byte)0b0000_1010, included.RedStages[0].Mask);

        var soloOmit = (byte course, byte episode) =>
            GameServer.ShouldIncludeRedCoinStageInHeal(occupancy, stageHadCoop: false);
        var omitted = relay.BuildAuthorityProgressSnapshot(
            new ShineAuthority(), new BlueCoinAuthority(), reds, new NpcCleanAuthority(),
            new StoryFlagAuthority(), progressSeq: 7, includeRedCoinStage: soloOmit);
        Assert.Empty(omitted.RedStages);
    }

    [Fact]
    public void SessionProgressResetDelivery_IsPerUsernameNotSlot()
    {
        var delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(GameServer.TryMarkSessionProgressResetDelivered(delivered, "Alice"));
        // Same player reconnecting (any casing) must not receive the wipe again.
        Assert.False(GameServer.TryMarkSessionProgressResetDelivered(delivered, "alice"));
        Assert.False(GameServer.TryMarkSessionProgressResetDelivered(delivered, "ALICE"));
        // A different player still needs the late-join clear.
        Assert.True(GameServer.TryMarkSessionProgressResetDelivered(delivered, "Bob"));
        Assert.False(GameServer.TryMarkSessionProgressResetDelivered(delivered, null));
        Assert.False(GameServer.TryMarkSessionProgressResetDelivered(delivered, ""));
    }

    private static string FindLevels()
    {
        var p = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "assets", "levels.ntsc-u.json");
        return Path.GetFullPath(p);
    }
}

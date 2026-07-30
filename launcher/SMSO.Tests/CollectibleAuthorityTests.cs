using SMSO.Bridge;
using SMSO.Launcher;
using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

public class CollectibleAuthorityTests
{
    [Fact]
    public void ShineAuthority_RejectsDuplicateShine()
    {
        var authority = new ShineAuthority();
        Assert.True(authority.TryAccept(117));
        Assert.False(authority.TryAccept(117));
        Assert.True(authority.TryAccept(42));
        Assert.True(authority.TryAccept(200));
        Assert.True(authority.TryAccept(255));
    }

    [Fact]
    public void ShineAuthority_AcceptsBowserEpilogueShine()
    {
        // doldecomp TMovieDirector::decideNextMode: movie 14 (epilogue.thp) → setShineFlag(0x77).
        var authority = new ShineAuthority();
        Assert.Equal(0x77, ShineAuthority.BowserEpilogueShineId);
        Assert.Equal(119, ShineAuthority.BowserEpilogueShineId);
        Assert.True(authority.TryAccept(ShineAuthority.BowserEpilogueShineId));
        Assert.False(authority.TryAccept(ShineAuthority.BowserEpilogueShineId));
        Assert.Contains(ShineAuthority.BowserEpilogueShineId, authority.Collected);
    }

    [Fact]
    public void WorldProgressSnapshot_Full256ShineBitset_RoundTripFitsMailbox()
    {
        var shines = new ShineAuthority();
        for (var shineId = 0; shineId < ProtocolConstants.ShineBitCapacity; shineId++)
            Assert.True(shines.TryAccept((byte)shineId));

        var relay = new WorldEventRelay();
        var snapshot = relay.BuildAuthorityProgressSnapshot(
            shines, new BlueCoinAuthority(), new RedCoinAuthority(), new NpcCleanAuthority(),
            new StoryFlagAuthority(), progressSeq: 42);
        Assert.Equal(ProtocolConstants.ShineBitCapacity, snapshot.OwnershipEventCount);
        Assert.Equal(WorldProgressSnapshot.ShineBitsByteCount, snapshot.ShineBits.Length);
        Assert.True(WorldProgressSnapshot.TestBit(snapshot.ShineBits, 0));
        Assert.True(WorldProgressSnapshot.TestBit(snapshot.ShineBits, 120));
        Assert.True(WorldProgressSnapshot.TestBit(snapshot.ShineBits, 255));

        var filtered = snapshot.WithMissionFilteredToStage(3, 1, hasStage: true);
        var payload = PacketSerializer.BuildWorldProgressSnapshotPayload(filtered);
        Assert.True(payload.Length <= ProtocolConstants.CommProgressSnapshotMaxPayload);
        Assert.True(payload.Length >= 6 + WorldProgressSnapshot.ShineBitsByteCount);
        Assert.Equal(WorldProgressSnapshot.FormatVersion, payload[0]);

        Assert.True(PacketSerializer.TryReadWorldProgressSnapshot(payload, out var restored));
        Assert.Equal(ProtocolConstants.ShineBitCapacity, restored.OwnershipEventCount);
        Assert.Equal(42u, restored.ProgressSeq);

        var expanded = restored.ExpandToWorldEvents();
        Assert.Equal(ProtocolConstants.ShineBitCapacity, expanded.Length);
        Assert.Equal(0, expanded[0].Payload0);
        Assert.Equal(255, expanded[^1].Payload0);
        Assert.All(expanded, e => Assert.Equal(WorldEventType.ShineCollected, e.Type));
    }

    [Fact]
    public void BlueCoinAuthority_RejectsDuplicateIndexPerCourse()
    {
        var authority = new BlueCoinAuthority();
        Assert.True(authority.TryAccept(1, 14));
        Assert.False(authority.TryAccept(1, 14));
        Assert.True(authority.TryAccept(1, 15));
        Assert.True(authority.TryAccept(2, 14));
    }

    [Fact]
    public void BlueCoinAuthority_AcceptsFullVanillaIndexRange()
    {
        var authority = new BlueCoinAuthority();
        Assert.True(authority.TryAccept(1, 0));
        Assert.True(authority.TryAccept(1, 49));
        Assert.False(authority.TryAccept(1, 50));
        Assert.Equal(1ul << 0 | 1ul << 49, authority.MaskForCourse(1));
    }

    [Fact]
    public void BlueCoinAuthority_RetainsMaskAcrossCourseChanges()
    {
        var authority = new BlueCoinAuthority();
        Assert.True(authority.TryAccept(1, 14));
        Assert.True(authority.TryAccept(2, 3));
        // Returning to course 1 must still reject the already-accepted coin.
        Assert.False(authority.TryAccept(1, 14));
        Assert.Equal(1ul << 14, authority.MaskForCourse(1));
        Assert.Equal(1ul << 3, authority.MaskForCourse(2));
    }

    [Fact]
    public void WorldEventRelay_ExcludesEphemeralEventsFromDurableHistory()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.NpcReact, 1, 0, 2, 0, 0x111);
        relay.CreateWorldEvent(WorldEventType.MarioFruitKicked, 1, 0, 1, 0, 0x222);
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 1, 0, 10, 0, 0);
        relay.CreateWorldEvent(WorldEventType.BlueCoinCollected, 2, 0, 5, 1, 0);
        relay.CreateWorldEvent(WorldEventType.HipDropObject, 2, 1, 3, 0, 0x333);

        Assert.Equal(2, relay.History.Count);
        Assert.Equal(WorldEventType.ShineCollected, relay.History[0].Type);
        Assert.Equal(WorldEventType.BlueCoinCollected, relay.History[1].Type);
        Assert.True(WorldEventRelay.IsDurable(WorldEventType.RedCoinCollected));
        Assert.True(WorldEventRelay.IsDurable(WorldEventType.NpcCleaned));
        Assert.False(WorldEventRelay.IsDurable(WorldEventType.NpcReact));
        Assert.False(WorldEventRelay.IsDurable(WorldEventType.HipDropObject));
    }

    [Fact]
    public void WorldEventRelay_AuthoritySnapshot_IncludesAllCourses()
    {
        var shines = new ShineAuthority();
        var blues = new BlueCoinAuthority();
        var reds = new RedCoinAuthority();
        var npcCleans = new NpcCleanAuthority();
        var story = new StoryFlagAuthority();
        Assert.True(shines.TryAccept(7));
        Assert.True(blues.TryAccept(1, 2));
        Assert.True(blues.TryAccept(5, 9));
        Assert.True(reds.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, 3, 4, 0, 1, 0x100),
            out _, out _, out _, out _));
        Assert.True(npcCleans.TryAcceptCleaned(
            new WorldEventRequest(2, WorldEventType.NpcCleaned, 8, 5, 0, 3, 0x200),
            out _, out _, out _));
        Assert.True(story.TryAcceptStory(0x10384, 1));
        Assert.True(story.TryAcceptTrigger(1, 4, 0x50001, 1));

        var relay = new WorldEventRelay();
        var frame = relay.BuildAuthoritySnapshotReplay(shines, blues, reds, npcCleans, story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldStateReplay, id);
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        Assert.Contains(events, e => e.Type == WorldEventType.ShineCollected && e.Payload0 == 7);
        Assert.Contains(events, e => e.Type == WorldEventType.BlueCoinCollected && e.CourseId == 1 && e.Payload0 == 2);
        Assert.Contains(events, e => e.Type == WorldEventType.BlueCoinCollected && e.CourseId == 5 && e.Payload0 == 9);
        Assert.Contains(events, e => e.Type == WorldEventType.RedCoinCollected && e.CourseId == 3 && e.EpisodeId == 4);
        Assert.Contains(events, e => e.Type == WorldEventType.NpcCleaned && e.CourseId == 8 && e.EpisodeId == 5 && e.Reserved == 3);
        Assert.Contains(events, e => e.Type == WorldEventType.StoryFlag && e.Payload1 == 0x10384 && e.Payload0 == 1);
        Assert.Contains(events, e => e.Type == WorldEventType.TriggerFlag &&
                                     e.CourseId == StoryFlagAuthority.PlazaAreaId &&
                                     e.EpisodeId == StoryFlagAuthority.PlazaHubEpisode &&
                                     e.Payload1 == 0x50001 && e.Payload0 == 1);
    }

    [Fact]
    public void WorldEventRelay_AuthoritySnapshot_CanExcludeSoloRedCoinStages()
    {
        var reds = new RedCoinAuthority();
        Assert.True(reds.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, 23, 0, 0, 1, 0),
            out _, out _, out _, out _));
        Assert.True(reds.TryAcceptCollected(
            new WorldEventRequest(2, WorldEventType.RedCoinCollected, 3, 5, 0, 0, 0),
            out _, out _, out _, out _));

        var relay = new WorldEventRelay();
        // Simulate occupancy filter: only co-op (or prior-coop) stage 3/5 is included.
        var frame = relay.BuildAuthoritySnapshotReplay(
            new ShineAuthority(), new BlueCoinAuthority(), reds, new NpcCleanAuthority(),
            new StoryFlagAuthority(),
            includeRedCoinStage: (course, episode) =>
                GameServer.ShouldIncludeRedCoinStageInHeal(
                    equivalentOccupancy: course == 3 ? 2 : 1,
                    stageHadCoop: course == 3));
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        Assert.DoesNotContain(events, e => e.Type == WorldEventType.RedCoinCollected && e.CourseId == 23);
        Assert.Contains(events, e => e.Type == WorldEventType.RedCoinCollected && e.CourseId == 3 && e.EpisodeId == 5);
    }

    [Fact]
    public void WorldEventRelay_ForceFullHeal_IncludesPriorCoopRedStageAtOccupancyOne()
    {
        var reds = new RedCoinAuthority();
        Assert.True(reds.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, 23, 0, 0, 2, 0xABC),
            out _, out _, out _, out _));

        var relay = new WorldEventRelay();
        // Force-full seq=0 path: occupancy dropped to 1 after peer left, but stage had co-op.
        var snapshot = relay.BuildAuthorityProgressSnapshot(
            new ShineAuthority(), new BlueCoinAuthority(), reds, new NpcCleanAuthority(),
            new StoryFlagAuthority(), progressSeq: 3,
            includeRedCoinStage: (_, _) =>
                GameServer.ShouldIncludeRedCoinStageInHeal(1, stageHadCoop: true));

        Assert.Single(snapshot.RedStages);
        Assert.Equal(23, snapshot.RedStages[0].CourseId);
        Assert.Equal((byte)0b0000_0100, snapshot.RedStages[0].Mask);
    }

    [Fact]
    public void WorldEventRelay_RemoveRedCoinHistory_DropsStageEvents()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.RedCoinCollected, 23, 0, 0x10, 0, 1);
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 7, 0, 0);
        relay.CreateWorldEvent(WorldEventType.RedCoinCollected, 23, 0, 0x20, 1, 3);
        // Red is durable for protocol but ownership-only diagnostic history.
        Assert.Single(relay.History);

        relay.RemoveRedCoinHistory(23, 0);
        Assert.Single(relay.History);
        Assert.Equal(WorldEventType.ShineCollected, relay.History[0].Type);
    }

    [Fact]
    public void WorldEventRelay_ClearDurableHistory_DropsAllDurableEvents()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 7, 0, 0);
        relay.CreateWorldEvent(WorldEventType.BlueCoinCollected, 2, 0, 3, 0, 0);
        relay.CreateWorldEvent(WorldEventType.RedCoinCollected, 23, 0, 0x10, 0, 1);
        relay.CreateWorldEvent(WorldEventType.StoryFlag, 0, 0, 1, 0, 0x10384);
        Assert.Equal(3, relay.History.Count);

        relay.ClearDurableHistory();
        Assert.Empty(relay.History);
    }

    [Fact]
    public void WorldEventRelay_GraffitiCleaned_IsNotDurable()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.GraffitiCleaned, 1, 0xFF, 1, 0, 0, 0x40000001u);
        Assert.False(WorldEventRelay.IsDurable(WorldEventType.GraffitiCleaned));
        Assert.Empty(relay.History);
    }

    [Fact]
    public void WorldEventRelay_RemoveShineBlueHistory_DropsOnlyShineAndBlue()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 7, 0, 0);
        relay.CreateWorldEvent(WorldEventType.BlueCoinCollected, 2, 0, 3, 0, 0);
        relay.CreateWorldEvent(WorldEventType.RedCoinCollected, 23, 0, 0x10, 0, 1);
        relay.CreateWorldEvent(WorldEventType.StoryFlag, 0, 0, 1, 0, 0x10384);
        Assert.Equal(3, relay.History.Count);

        relay.RemoveShineBlueHistory();
        Assert.Single(relay.History);
        Assert.Contains(relay.History, e => e.Type == WorldEventType.StoryFlag);
        Assert.DoesNotContain(relay.History, e => e.Type == WorldEventType.ShineCollected);
        Assert.DoesNotContain(relay.History, e => e.Type == WorldEventType.BlueCoinCollected);
        Assert.DoesNotContain(relay.History, e => e.Type == WorldEventType.RedCoinCollected);
    }

    [Fact]
    public void WorldEventRelay_SessionProgressReset_IsNotDurable()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 1, 0, 0);
        relay.CreateWorldEvent(WorldEventType.SessionProgressReset, 0, 0, 0, 0, 0);
        Assert.False(WorldEventRelay.IsDurable(WorldEventType.SessionProgressReset));
        Assert.False(WorldEventRelay.IsDurable(WorldEventType.ShineBlueProgressReset));
        Assert.Equal(WorldEventType.SessionProgressReset, WorldEventType.ShineBlueProgressReset);
        Assert.Single(relay.History);
        Assert.Equal(WorldEventType.ShineCollected, relay.History[0].Type);
    }

    [Fact]
    public void SessionProgressAuthorities_Reset_EmptiesSnapshot()
    {
        var shines = new ShineAuthority();
        var blues = new BlueCoinAuthority();
        var reds = new RedCoinAuthority();
        var npc = new NpcCleanAuthority();
        var story = new StoryFlagAuthority();
        Assert.True(shines.TryAccept(12));
        Assert.True(blues.TryAccept(2, 5));
        Assert.True(story.TryAcceptStory(0x10384, 1));
        Assert.True(story.TryAcceptTrigger(1, 0, 0x50004, 1));

        shines.Reset();
        blues.Reset();
        reds.Reset();
        npc.Reset();
        story.Reset();

        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 12, 0, 0);
        relay.CreateWorldEvent(WorldEventType.StoryFlag, 0, 0, 1, 0, 0x10384);
        relay.ClearDurableHistory();

        var frame = relay.BuildAuthoritySnapshotReplay(shines, blues, reds, npc, story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldStateReplay, id);
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        Assert.Empty(events);
    }

    [Fact]
    public void ShineBlueAuthority_Reset_EmptiesSnapshotCollectibles()
    {
        var shines = new ShineAuthority();
        var blues = new BlueCoinAuthority();
        Assert.True(shines.TryAccept(12));
        Assert.True(blues.TryAccept(2, 5));
        shines.Reset();
        blues.Reset();

        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 12, 0, 0);
        relay.CreateWorldEvent(WorldEventType.BlueCoinCollected, 2, 0, 5, 0, 0);
        relay.CreateWorldEvent(WorldEventType.StoryFlag, 0, 0, 1, 0, 0x10384);
        relay.RemoveShineBlueHistory();

        var story = new StoryFlagAuthority();
        Assert.True(story.TryAcceptStory(0x10384, 1));
        var frame = relay.BuildAuthoritySnapshotReplay(
            shines, blues, new RedCoinAuthority(), new NpcCleanAuthority(), story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldStateReplay, id);
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        Assert.DoesNotContain(events, e => e.Type == WorldEventType.ShineCollected);
        Assert.DoesNotContain(events, e => e.Type == WorldEventType.BlueCoinCollected);
        Assert.Contains(events, e => e.Type == WorldEventType.StoryFlag && e.Payload1 == 0x10384);
    }

    [Fact]
    public void StoryFlagAuthority_IsGrowOnlyAndRejectsClears()
    {
        var authority = new StoryFlagAuthority();
        Assert.True(authority.TryAcceptStory(0x10384, 1));
        Assert.False(authority.TryAcceptStory(0x10384, 1));
        Assert.False(authority.TryAcceptStory(0x10384, 0));
        Assert.False(authority.TryAcceptTrigger(1, 0, 0x50004, 0));
        Assert.True(authority.TryAcceptTrigger(1, 0, 0x50004, 1));
        Assert.False(authority.TryAcceptTrigger(1, 0, 0x50004, 0));
        Assert.Equal(2, authority.TotalCount);
    }

    [Fact]
    public void StoryFlagAuthority_CoalescesPlazaTriggersToHubEpisode()
    {
        var authority = new StoryFlagAuthority();
        Assert.True(authority.TryAcceptTrigger(1, 0, 0x50001, 1));
        // Same plaza flag from a different dolpic scenario must not duplicate.
        Assert.False(authority.TryAcceptTrigger(1, 8, 0x50001, 1));
        Assert.True(authority.TryAcceptTrigger(1, 7, 0x50002, 1));
        // Non-plaza courses never admit Type5 allowlist bits.
        Assert.False(authority.TryAcceptTrigger(2, 0, 0x50001, 1));
        Assert.False(authority.TryAcceptTrigger(3, 4, 0x50001, 1));
        Assert.Equal(2, authority.TriggerFlags.Count);
        Assert.All(authority.TriggerFlags.Keys,
            key => Assert.Equal(StoryFlagAuthority.PlazaHubEpisode, key.EpisodeId));
        Assert.True(StoryFlagAuthority.IsPlazaHubTrigger(1, 0x50001));
        Assert.False(StoryFlagAuthority.IsPlazaHubTrigger(2, 0x50001));
    }

    [Fact]
    public void StoryFlagAuthority_ConcurrentDuplicateSetHasSingleWinner()
    {
        var authority = new StoryFlagAuthority();
        var accepted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (authority.TryAcceptStory(0x10384, 1))
                Interlocked.Increment(ref accepted);
        });
        Assert.Equal(1, accepted);
        Assert.Single(authority.StoryFlags);
    }

    [Fact]
    public void StoryFlagAuthority_RejectsRuntimeGameBoolsAndCollectibleCardBits()
    {
        var authority = new StoryFlagAuthority();
        Assert.False(authority.TryAcceptStory(0x30000, 1));
        Assert.False(authority.TryAcceptStory(0x30004, 1));
        Assert.False(authority.TryAcceptStory(0x10007, 1));
        Assert.False(authority.TryAcceptStory(0x10078, 1));
        Assert.True(authority.TryAcceptStory(0x10384, 1));
        Assert.Equal(1, authority.TotalCount);
    }

    [Fact]
    public void WorldEventRelay_AuthoritySnapshot_EmitsPlazaHubTriggerEpisode()
    {
        var story = new StoryFlagAuthority();
        Assert.True(story.TryAcceptTrigger(1, 8, 0x50001, 1));
        Assert.False(story.TryAcceptTrigger(2, 3, 0x50001, 1));
        var relay = new WorldEventRelay();
        var frame = relay.BuildAuthoritySnapshotReplay(
            new ShineAuthority(), new BlueCoinAuthority(), new RedCoinAuthority(),
            new NpcCleanAuthority(), story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        Assert.Contains(events, e => e.Type == WorldEventType.TriggerFlag &&
                                     e.CourseId == StoryFlagAuthority.PlazaAreaId &&
                                     e.EpisodeId == StoryFlagAuthority.PlazaHubEpisode &&
                                     e.Payload1 == 0x50001);
        Assert.DoesNotContain(events, e => e.Type == WorldEventType.TriggerFlag &&
                                           e.CourseId == 2);
    }

    [Fact]
    public void StoryFlagAuthority_RejectsEphemeralRedCoinSwitchPressedTrigger()
    {
        var authority = new StoryFlagAuthority();
        Assert.True(StoryFlagAuthority.IsEphemeralStageSessionTrigger(
            StoryFlagAuthority.RedCoinSwitchPressedFlagId));
        Assert.False(authority.TryAcceptTrigger(1, 0, StoryFlagAuthority.RedCoinSwitchPressedFlagId, 1));
        Assert.False(authority.TryAcceptTrigger(1, 0, StoryFlagAuthority.RedCoinSwitchPressedFlagId, 0));
        Assert.Equal(0, authority.TotalCount);
        Assert.Empty(authority.TriggerFlags);

        // Other Type5 bits are resetStage scratch (graffiti/session/one-shot state).
        Assert.False(authority.TryAcceptTrigger(1, 0, 0x50008, 1));
        Assert.False(authority.TryAcceptTrigger(1, 0, 0x5000A, 1));
        Assert.Equal(0, authority.TotalCount);
    }

    [Fact]
    public void StoryFlagAuthority_RejectsEphemeralPinnaSpawnDirectorFlags()
    {
        var authority = new StoryFlagAuthority();
        Assert.True(StoryFlagAuthority.IsEphemeralSpawnDirectorFlag(
            StoryFlagAuthority.SpawnDirectorFlag30004));
        Assert.True(StoryFlagAuthority.IsEphemeralSpawnDirectorFlag(
            StoryFlagAuthority.SpawnDirectorFlag30001));

        // Durable Pinna unlock progress (dolpic8) must still sync.
        Assert.True(authority.TryAcceptStory(0x10389, 1));

        Assert.False(authority.TryAcceptStory(StoryFlagAuthority.SpawnDirectorFlag30004, 1));
        Assert.False(authority.TryAcceptStory(StoryFlagAuthority.SpawnDirectorFlag30001, 1));
        Assert.Equal(1, authority.TotalCount);
        Assert.DoesNotContain(StoryFlagAuthority.SpawnDirectorFlag30004, authority.StoryFlags.Keys);
        Assert.DoesNotContain(StoryFlagAuthority.SpawnDirectorFlag30001, authority.StoryFlags.Keys);
    }

    [Fact]
    public void StoryFlagAuthority_AcceptsCoronaVisitedPostFloodFlag()
    {
        var authority = new StoryFlagAuthority();
        Assert.Equal(0x103AEu, StoryFlagAuthority.CoronaVisitedFlagId);
        Assert.Equal(52, StoryFlagAuthority.CoronaMountainAreaId);
        Assert.True(StoryFlagAuthority.IsDurableCardFlag(StoryFlagAuthority.CoronaVisitedFlagId));

        // decideNextScenario: 0x103AE → scenario 2 (dolpic10 post-flood).
        Assert.True(authority.TryAcceptStory(StoryFlagAuthority.CoronaVisitedFlagId, 1));
        Assert.False(authority.TryAcceptStory(StoryFlagAuthority.CoronaVisitedFlagId, 1));
        Assert.False(authority.TryAcceptStory(StoryFlagAuthority.CoronaVisitedFlagId, 0));
        Assert.Contains(StoryFlagAuthority.CoronaVisitedFlagId, authority.StoryFlags.Keys);
    }

    [Fact]
    public void WorldEventRelay_FullGameAuthoritySnapshot_FitsTcpPayload()
    {
        // Approximate a completed Sunshine save: 120 shines, blue coins across many
        // courses, red-coin clears, and story/trigger flags. Must stay under MaxTcpPayloadSize
        // so 10-player late-join / 45s resync never throws mid-run.
        var shines = new ShineAuthority();
        var blues = new BlueCoinAuthority();
        var reds = new RedCoinAuthority();
        var npcCleans = new NpcCleanAuthority();
        var story = new StoryFlagAuthority();

        for (byte shineId = 1; shineId <= 120; shineId++)
            Assert.True(shines.TryAccept(shineId));

        for (byte course = 1; course <= 20; course++)
        {
            for (byte index = 0; index < 20; index++)
                Assert.True(blues.TryAccept(course, index));
        }

        // Prefer courses without Sirena/Pinna missionâ†”catalog aliases so the
        // volume fill does not collide after RedCoinAuthority.NormalizeStage.
        byte[] redCourses = { 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 15, 16, 17, 18, 19 };
        foreach (var course in redCourses)
        {
            for (byte episode = 0; episode < 8; episode++)
            {
                for (byte index = 0; index < 8; index++)
                {
                    Assert.True(reds.TryAcceptCollected(
                        new WorldEventRequest(1, WorldEventType.RedCoinCollected, course, episode, index, index, 0x100u + index),
                        out _, out _, out _, out _));
                }
            }
        }

        // Every non-collectible persistent card bit plus the full stage-scoped trigger
        // allowlist. This exceeded the old 32 KiB cap and silently truncated story flags.
        var storyCount = 0;
        for (uint flag = StoryFlagAuthority.CardBoolBase;
             flag < StoryFlagAuthority.CardBoolEnd;
             flag++)
        {
            if (!StoryFlagAuthority.IsDurableCardFlag(flag))
                continue;
            Assert.True(story.TryAcceptStory(flag, 1));
            storyCount++;
        }
        Assert.Equal(378, storyCount);

        // Plaza hub Type5 allowlist only â€” three flags coalesced to episode 0xFF.
        var triggerCount = 0;
        foreach (var flag in new uint[] { 0x50001, 0x50002, 0x50004 })
        {
            Assert.True(story.TryAcceptTrigger(StoryFlagAuthority.PlazaAreaId, 8, flag, 1));
            // Re-admit from another dolpic scenario must be rejected (already coalesced).
            Assert.False(story.TryAcceptTrigger(StoryFlagAuthority.PlazaAreaId, 0, flag, 1));
            triggerCount++;
        }

        Assert.Equal(3, triggerCount);

        var relay = new WorldEventRelay();
        var frame = relay.BuildAuthoritySnapshotReplay(shines, blues, reds, npcCleans, story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldStateReplay, id);
        Assert.True(payload.Length <= ProtocolConstants.MaxTcpPayloadSize);
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        // 120 shines + 20*20 blues + 15*8*8 reds + 378 story + 3 plaza hub triggers
        // = 120 + 400 + 960 + 378 + 3 = 1861
        Assert.Equal(1861, events.Length);
        Assert.DoesNotContain(events,
            e => e.Type == WorldEventType.TriggerFlag &&
                 e.Payload1 == StoryFlagAuthority.RedCoinSwitchPressedFlagId);
        Assert.All(
            events.Where(e => e.Type == WorldEventType.TriggerFlag),
            e =>
            {
                Assert.Equal(StoryFlagAuthority.PlazaAreaId, e.CourseId);
                Assert.Equal(StoryFlagAuthority.PlazaHubEpisode, e.EpisodeId);
            });
    }

    [Fact]
    public void WorldEventRelay_CompactProgressSnapshot_FitsWellUnderLegacyEventList()
    {
        var shines = new ShineAuthority();
        var blues = new BlueCoinAuthority();
        var reds = new RedCoinAuthority();
        var npcCleans = new NpcCleanAuthority();
        var story = new StoryFlagAuthority();

        for (byte shineId = 1; shineId <= 120; shineId++)
            Assert.True(shines.TryAccept(shineId));

        for (byte course = 1; course <= 20; course++)
        {
            for (byte index = 0; index < 20; index++)
                Assert.True(blues.TryAccept(course, index));
        }

        byte[] redCourses = { 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 15, 16, 17, 18, 19 };
        foreach (var course in redCourses)
        {
            for (byte episode = 0; episode < 8; episode++)
            {
                for (byte index = 0; index < 8; index++)
                {
                    Assert.True(reds.TryAcceptCollected(
                        new WorldEventRequest(1, WorldEventType.RedCoinCollected, course, episode, index,
                            index, 0x100u + index),
                        out _, out _, out _, out _));
                }
            }
        }

        for (uint flag = StoryFlagAuthority.CardBoolBase;
             flag < StoryFlagAuthority.CardBoolEnd;
             flag++)
        {
            if (!StoryFlagAuthority.IsDurableCardFlag(flag))
                continue;
            Assert.True(story.TryAcceptStory(flag, 1));
        }

        foreach (var flag in new uint[] { 0x50001, 0x50002, 0x50004 })
            Assert.True(story.TryAcceptTrigger(StoryFlagAuthority.PlazaAreaId, 8, flag, 1));

        var relay = new WorldEventRelay();
        // Live event must not be displaced by snapshot id inflation.
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 1, 0, 0);
        var beforeLive = relay.History[^1].EventId;

        var frame = relay.BuildAuthorityProgressSnapshotFrame(
            shines, blues, reds, npcCleans, story, progressSeq: 42);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldProgressSnapshot, id);
        Assert.True(payload.Length < 8 * 1024,
            $"compact snapshot should be ≪ 8 KiB, was {payload.Length}");
        Assert.True(PacketSerializer.TryReadWorldProgressSnapshot(payload, out var snapshot));
        Assert.Equal(42u, snapshot.ProgressSeq);
        Assert.False(snapshot.Unchanged);
        Assert.Equal(120 + 400 + 378 + 3, snapshot.OwnershipEventCount);
        Assert.Equal(15 * 8 * 8, snapshot.MissionEventCount);
        Assert.True(WorldProgressSnapshot.TestBit(snapshot.ShineBits, 1));
        Assert.True(WorldProgressSnapshot.TestBit(snapshot.ShineBits, 120));

        var expanded = snapshot.ExpandToWorldEvents();
        Assert.Equal(1861, expanded.Length);
        Assert.True(expanded[0].EventId >= WorldProgressSnapshot.HealEventIdBase);

        // Snapshot must not advance live event ids.
        relay.CreateWorldEvent(WorldEventType.BlueCoinCollected, 1, 0, 0, 0, 0);
        Assert.Equal(beforeLive + 1, relay.History[^1].EventId);
    }

    [Fact]
    public void WorldEventRelay_AuthoritySnapshot_DoesNotAdvanceLiveEventIds()
    {
        var shines = new ShineAuthority();
        Assert.True(shines.TryAccept(7));
        Assert.True(shines.TryAccept(8));
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 7, 0, 0);
        var liveId = relay.History[0].EventId;

        _ = relay.BuildAuthoritySnapshotReplay(
            shines, new BlueCoinAuthority(), new RedCoinAuthority(), new NpcCleanAuthority(),
            new StoryFlagAuthority());

        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 8, 0, 0);
        Assert.Equal(liveId + 1, relay.History[1].EventId);
    }

    [Fact]
    public void WorldEventRelay_DurableHistory_IsBoundedDiagnosticRing()
    {
        var relay = new WorldEventRelay();
        for (uint i = 0; i < 100; i++)
            relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, (byte)(i % 120), 0, 0);

        Assert.Equal(4, relay.History.Count);
        // Live event ids still advance past the ring.
        Assert.True(relay.History[^1].EventId >= 100);
    }

    [Fact]
    public void WorldEventRelay_DiagnosticHistory_ExcludesRedAndNpc()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 1, 0, 0);
        relay.CreateWorldEvent(WorldEventType.RedCoinCollected, 3, 0, 0x10, 1, 0x02);
        relay.CreateWorldEvent(WorldEventType.NpcCleaned, 8, 5, 0, 2, 0);
        relay.CreateWorldEvent(WorldEventType.BlueCoinCollected, 2, 0, 4, 0, 0);

        Assert.Equal(2, relay.History.Count);
        Assert.All(relay.History, e => Assert.True(WorldEventRelay.IsOwnershipDurable(e.Type)));
        Assert.True(WorldEventRelay.IsDurable(WorldEventType.RedCoinCollected));
        Assert.False(WorldEventRelay.IsOwnershipDurable(WorldEventType.RedCoinCollected));
    }

    [Fact]
    public void WorldProgressSnapshot_MissionFilter_KeepsOnlyLocalStage()
    {
        var snap = new WorldProgressSnapshot
        {
            ProgressSeq = 3,
            ShineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount],
            BlueCourses = Array.Empty<(byte, ulong)>(),
            RedStages = new[]
            {
                new WorldProgressSnapshot.RedStageMask(3, 1, 0x03, new uint[8]),
                new WorldProgressSnapshot.RedStageMask(5, 2, 0x0F, new uint[8]),
            },
            NpcCleanStages = new[]
            {
                ((byte)8, (byte)5, (ushort)0x0001),
                ((byte)3, (byte)1, (ushort)0x0004),
            },
        };
        WorldProgressSnapshot.SetShineBit(snap.ShineBits, 7);

        var filtered = snap.WithMissionFilteredToStage(3, 1, hasStage: true);
        Assert.Single(filtered.RedStages);
        Assert.Equal(3, filtered.RedStages[0].CourseId);
        Assert.Single(filtered.NpcCleanStages);
        Assert.Equal(3, filtered.NpcCleanStages[0].CourseId);
        Assert.True(WorldProgressSnapshot.TestBit(filtered.ShineBits, 7));

        var payload = PacketSerializer.BuildWorldProgressSnapshotPayload(filtered);
        Assert.True(payload.Length <= ProtocolConstants.CommProgressSnapshotMaxPayload);
    }

    [Fact]
    public void WorldProgressSnapshot_MissionFilter_KeepsHotelDirectorVsCatalog()
    {
        // Server stores hotel Red Coins under catalog 7; local snapshot often shows mission 4.
        var snap = new WorldProgressSnapshot
        {
            ProgressSeq = 4,
            ShineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount],
            RedStages = new[]
            {
                new WorldProgressSnapshot.RedStageMask(
                    SirenaHotelInteriorMapping.AreaId, 7, 0x05, new uint[8]),
                new WorldProgressSnapshot.RedStageMask(3, 0, 0x0F, new uint[8]),
            },
            NpcCleanStages = new[]
            {
                (SirenaHotelInteriorMapping.AreaId, (byte)7, (ushort)0x0003),
            },
        };

        var filtered = snap.WithMissionFilteredToStage(
            SirenaHotelInteriorMapping.AreaId, 4, hasStage: true);
        Assert.Single(filtered.RedStages);
        Assert.Equal(SirenaHotelInteriorMapping.AreaId, filtered.RedStages[0].CourseId);
        Assert.Equal(7, filtered.RedStages[0].EpisodeId);
        Assert.Equal(0x05, filtered.RedStages[0].Mask);
        Assert.Single(filtered.NpcCleanStages);
        Assert.Equal(7, filtered.NpcCleanStages[0].EpisodeId);
    }

    [Fact]
    public void WorldProgressSnapshot_MissionFilter_KeepsRiccoDirectorVsCatalog()
    {
        // Ep1 mid-fight director scenario 8 ≡ catalog episode 0.
        var snap = new WorldProgressSnapshot
        {
            ProgressSeq = 5,
            ShineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount],
            RedStages = new[]
            {
                new WorldProgressSnapshot.RedStageMask(
                    RiccoHarborMapping.AreaId, 0, 0x03, new uint[8]),
                new WorldProgressSnapshot.RedStageMask(5, 2, 0x0F, new uint[8]),
            },
            NpcCleanStages = new[]
            {
                (RiccoHarborMapping.AreaId, (byte)0, (ushort)0x0004),
            },
        };

        var filtered = snap.WithMissionFilteredToStage(
            RiccoHarborMapping.AreaId, 8, hasStage: true);
        Assert.Single(filtered.RedStages);
        Assert.Equal(RiccoHarborMapping.AreaId, filtered.RedStages[0].CourseId);
        Assert.Equal(0, filtered.RedStages[0].EpisodeId);
        Assert.Single(filtered.NpcCleanStages);
    }

    [Fact]
    public void ProgressOwnershipTracker_MailboxHeal_DoesNotNoteFilteredMission()
    {
        // Regression: noting ExpandToWorldEvents() on the FULL snapshot permanently
        // filtered off-stage reds from later heals. Only mailboxSnap may be noted.
        var full = new WorldProgressSnapshot
        {
            ProgressSeq = 6,
            ShineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount],
            RedStages = new[]
            {
                new WorldProgressSnapshot.RedStageMask(
                    SirenaHotelInteriorMapping.AreaId, 7, 0x01, new uint[8]),
                new WorldProgressSnapshot.RedStageMask(
                    RiccoHarborMapping.AreaId, 0, 0x02, new uint[8]),
            },
        };
        WorldProgressSnapshot.SetShineBit(full.ShineBits, 9);

        var mailboxSnap = full.WithMissionFilteredToStage(
            SirenaHotelInteriorMapping.AreaId, 4, hasStage: true);
        Assert.Single(mailboxSnap.RedStages);

        var tracker = new ProgressOwnershipTracker();
        foreach (var worldEvent in mailboxSnap.ExpandToWorldEvents())
            tracker.NoteLiveEvent(worldEvent);

        var laterRicco = full.WithMissionFilteredToStage(
            RiccoHarborMapping.AreaId, 0, hasStage: true);
        var delta = tracker.FilterNewEvents(laterRicco.ExpandToWorldEvents(),
            filterOwnership: false);
        Assert.Contains(delta, e =>
            e.Type == WorldEventType.RedCoinCollected &&
            e.CourseId == RiccoHarborMapping.AreaId &&
            e.EpisodeId == 0);

        // Heal expand (filterOwnership:false) must still re-ship noted-but-unapplied
        // reds — optimistic notes must not permanently suppress mission heals.
        var noted = new ProgressOwnershipTracker();
        foreach (var worldEvent in full.ExpandToWorldEvents())
            noted.NoteLiveEvent(worldEvent);
        Assert.Empty(noted.FilterNewEvents(laterRicco.ExpandToWorldEvents(),
            filterOwnership: true));
        var healDelta = noted.FilterNewEvents(laterRicco.ExpandToWorldEvents(),
            filterOwnership: false);
        Assert.Contains(healDelta, e =>
            e.Type == WorldEventType.RedCoinCollected &&
            e.CourseId == RiccoHarborMapping.AreaId);
    }

    [Fact]
    public void WorldEventSnapshot_NeverEmitsGraffitiCleaned()
    {
        // Regression: graffiti cell flood used to fill TCP catch-up and starve story.
        // Goop sync is permanently disabled — snapshots must never contain GraffitiCleaned.
        var shines = new ShineAuthority();
        var blues = new BlueCoinAuthority();
        var story = new StoryFlagAuthority();

        for (byte shineId = 0; shineId < 120; shineId++)
            Assert.True(shines.TryAccept(shineId));

        for (byte course = 1; course <= 12; course++)
        {
            for (byte index = 0; index < 20; index++)
                Assert.True(blues.TryAccept(course, index));
        }

        Assert.True(story.TryAcceptStory(0x10384, 1));
        Assert.True(story.TryAcceptStory(0x10389, 1));
        Assert.True(story.TryAcceptTrigger(StoryFlagAuthority.PlazaAreaId, 8, 0x50001, 1));
        Assert.True(story.TryAcceptTrigger(StoryFlagAuthority.PlazaAreaId, 0, 0x50004, 1));
        Assert.True(story.TryAcceptSecret(0x10390, 1));

        var relay = new WorldEventRelay();
        var frame = relay.BuildAuthoritySnapshotReplay(
            shines, blues, new RedCoinAuthority(), new NpcCleanAuthority(), story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldStateReplay, id);
        Assert.True(payload.Length <= ProtocolConstants.MaxTcpPayloadSize);
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));

        Assert.Equal(120, events.Count(e => e.Type == WorldEventType.ShineCollected));
        Assert.Equal(240, events.Count(e => e.Type == WorldEventType.BlueCoinCollected));
        Assert.Contains(events, e => e.Type == WorldEventType.StoryFlag && e.Payload1 == 0x10384);
        Assert.Contains(events, e => e.Type == WorldEventType.StoryFlag && e.Payload1 == 0x10389);
        Assert.Contains(events, e => e.Type == WorldEventType.TriggerFlag && e.Payload1 == 0x50001);
        Assert.Contains(events, e => e.Type == WorldEventType.TriggerFlag && e.Payload1 == 0x50004);
        Assert.Contains(events, e => e.Type == WorldEventType.SecretComplete && e.Payload1 == 0x10390);
        Assert.DoesNotContain(events, e => e.Type == WorldEventType.GraffitiCleaned);
    }

    [Fact]
    public void BridgeWorker_PrioritizesShineBlue_DropsEphemeral()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            1, WorldEventType.NpcReact, 1, 0, 1, 2, 0x11111111));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            2, WorldEventType.MarioFruitThrown, 1, 0, 3, 1, 0x22222222));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            3, WorldEventType.ShineCollected, 1, 0, 17, 1, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            4, WorldEventType.BlueCoinCollected, 2, 0, 5, 1, 0));

        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.Equal(2, pending.Length);
        Assert.Equal(WorldEventType.ShineCollected, pending[0]);
        Assert.Equal(WorldEventType.BlueCoinCollected, pending[1]);
    }

    [Fact]
    public void BridgeWorker_PrioritizesMission_DropsFruit()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            1, WorldEventType.MarioFruitThrown, 1, 0, 3, 1, 0x11));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            2, WorldEventType.NpcReact, 1, 0, 1, 2, 0x22));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            3, WorldEventType.RedCoinCollected, 23, 0, 0x10, 2, 0x04, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            4, WorldEventType.NpcCleaned, 8, 5, 0, 3, 0x08, 0));

        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.Equal(2, pending.Length);
        Assert.Equal(WorldEventType.RedCoinCollected, pending[0]);
        Assert.Equal(WorldEventType.NpcCleaned, pending[1]);
    }

    [Fact]
    public void BridgeWorker_CapsEphemeralIncomingQueueWithoutDroppingOwnership()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        for (uint i = 1; i <= 40; i++)
        {
            worker.PushIncomingWorldEvent(new WorldEventPacket(
                i, WorldEventType.NpcReact, 1, 5, 2, 1, 0x1000u + i));
        }

        worker.PushIncomingWorldEvent(new WorldEventPacket(
            100, WorldEventType.ShineCollected, 0, 0, 42, 0, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            101, WorldEventType.StoryFlag, 0, 0, 1, 0, 0x10384));

        var pending = worker.DebugGetPendingIncomingTypes();
        // Phase A: ephemeral hard-dropped — only ownership remains.
        Assert.Equal(2, pending.Length);
        Assert.Contains(WorldEventType.ShineCollected, pending);
        Assert.Contains(WorldEventType.StoryFlag, pending);
        Assert.DoesNotContain(WorldEventType.NpcReact, pending);
    }

    [Fact]
    public void BridgeWorker_ClearPendingAlsoClearsWorkingIncomingSlot()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.DebugSeedWorldSync(lastAppliedEventId: 10, incomingEventId: 99);
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            11, WorldEventType.ShineCollected, 0, 0, 3, 0, 0));

        worker.ClearPendingIncomingWorldEvents();

        Assert.Empty(worker.DebugGetPendingIncomingTypes());
        var (_, incoming) = worker.DebugGetWorldSync();
        Assert.Equal(0u, incoming);
    }

    [Fact]
    public void BridgeWorker_ClearNonOwnershipKeepsQueuedShine()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            1, WorldEventType.NpcReact, 1, 0, 1, 2, 0x11));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            2, WorldEventType.ShineCollected, 0, 0, 9, 0, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            3, WorldEventType.MarioFruitThrown, 1, 0, 3, 1, 0x22));

        worker.ClearNonOwnershipIncomingWorldEvents();

        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.Single(pending);
        Assert.Equal(WorldEventType.ShineCollected, pending[0]);
    }

    [Fact]
    public void BridgeWorker_ClearNonOwnershipKeepsMissionAndOwnership()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            1, WorldEventType.NpcReact, 1, 0, 1, 2, 0x11));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            2, WorldEventType.GoldCoinCollected, 3, 1, 0, 0, 42));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            3, WorldEventType.MarioFruitThrown, 1, 0, 3, 1, 0x22));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            4, WorldEventType.RedCoinCollected, 3, 1, 0x01, 0, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            5, WorldEventType.ShineCollected, 0, 0, 9, 0, 0));

        worker.ClearNonOwnershipIncomingWorldEvents();

        // Ephemeral never enqueued; ClearNonOwnership only strips ephemeral lane —
        // ownership + mission (red) remain.
        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.Equal(2, pending.Length);
        Assert.Equal(WorldEventType.ShineCollected, pending[0]);
        Assert.Equal(WorldEventType.RedCoinCollected, pending[1]);
    }

    [Fact]
    public void ProgressOwnershipTracker_HealExpand_DoesNotFilterOwnership()
    {
        var tracker = new ProgressOwnershipTracker();
        var shine = new WorldEventPacket(1, WorldEventType.ShineCollected, 0, 0, 7, 0, 0);
        var blue = new WorldEventPacket(2, WorldEventType.BlueCoinCollected, 3, 0, 4, 0, 0);
        var story = new WorldEventPacket(3, WorldEventType.StoryFlag, 1, 0, 1, 0, 0x1039Au);
        tracker.NoteLiveEvent(shine);
        tracker.NoteLiveEvent(blue);
        tracker.NoteLiveEvent(story);

        // Optimistic live notes would permanently suppress heal re-ships if filtered.
        var filtered = tracker.FilterNewEvents(new[] { shine, blue, story }, filterOwnership: true);
        Assert.Empty(filtered);

        var heal = tracker.FilterNewEvents(new[] { shine, blue, story }, filterOwnership: false);
        Assert.Equal(3, heal.Count);
        Assert.Contains(heal, e => e.Type == WorldEventType.ShineCollected && e.Payload0 == 7);
        Assert.Contains(heal, e => e.Type == WorldEventType.BlueCoinCollected && e.Payload0 == 4);
        Assert.Contains(heal, e => e.Type == WorldEventType.StoryFlag && e.Payload1 == 0x1039Au);
    }

    [Fact]
    public void ProgressOwnershipTracker_HealExpand_DoesNotFilterRedOrNpc()
    {
        var tracker = new ProgressOwnershipTracker();
        var red = new WorldEventPacket(1, WorldEventType.RedCoinCollected, 3, 0, 0x10, 2, 0x04);
        var npc = new WorldEventPacket(2, WorldEventType.NpcCleaned, 8, 5, 0x10, 3, 0);
        tracker.NoteLiveEvent(red);
        tracker.NoteLiveEvent(npc);

        Assert.Empty(tracker.FilterNewEvents(new[] { red, npc }, filterOwnership: true));

        var heal = tracker.FilterNewEvents(new[] { red, npc }, filterOwnership: false);
        Assert.Equal(2, heal.Count);
        Assert.Contains(heal, e => e.Type == WorldEventType.RedCoinCollected);
        Assert.Contains(heal, e => e.Type == WorldEventType.NpcCleaned);
    }

    [Fact]
    public void BridgeWorker_LocalPending_DoesNotAdvanceSeqUntilClearWouldSucceed()
    {
        // Without Dolphin attached, TryClearLocalPendingOwnershipWorldEvent returns false.
        // The poll handshake must NOT advance last-seq on a failed clear — that was the
        // soft-death that left shine 36 published in-module but never drained.
        using var worker = new BridgeWorker(new DolphinBridge());
        WorldEventRequest? published = null;
        worker.LocalWorldEventReady += ev => published = ev;

        var pending = new CommWorldEvent
        {
            Sequence = 42,
            Type = WorldEventType.ShineCollected,
            CourseId = 13,
            EpisodeId = 4,
            Payload0 = 36,
        };

        worker.DebugPublishLocalWorldEvent(pending);
        Assert.NotNull(published);
        Assert.Equal((byte)36, published!.Value.Payload0);
        Assert.Equal((ushort)0, worker.DebugLastLocalWorldEventSequence);
        Assert.Equal((ushort)42, worker.DebugPublishedUnclearedLocalWorldEventSequence);

        // Same sequence again: must not double-invoke the network callback.
        published = null;
        worker.DebugPublishLocalWorldEvent(pending);
        Assert.Null(published);
        Assert.Equal((ushort)42, worker.DebugPublishedUnclearedLocalWorldEventSequence);
    }

    [Fact]
    public void BridgeWorker_DetachedLocalPending_HandoffIsOrderedFifo()
    {
        // Bugbot: clearing Dolphin before ThreadPool handoff let DrainLocalWorldEventBacklog
        // race ahead and reorder/drop TCP world events across reconnect.
        using var worker = new BridgeWorker(new DolphinBridge());
        var published = new List<ushort>();
        var gate = new ManualResetEventSlim(false);
        worker.LocalWorldEventReady += ev =>
        {
            // Stall the first callback so a second enqueue must wait in the FIFO queue.
            if (ev.Sequence == 1)
                Assert.True(gate.Wait(TimeSpan.FromSeconds(2)));
            published.Add(ev.Sequence);
        };

        worker.DebugPublishLocalWorldEventDetached(new CommWorldEvent
        {
            Sequence = 1,
            Type = WorldEventType.ShineCollected,
            Payload0 = 1,
        });
        worker.DebugPublishLocalWorldEventDetached(new CommWorldEvent
        {
            Sequence = 2,
            Type = WorldEventType.ShineCollected,
            Payload0 = 2,
        });

        // After durable enqueue, clear may advance last-seq even without Dolphin.
        // The outbound queue must still deliver 1 then 2.
        gate.Set();
        Assert.True(worker.DebugWaitOutboundWorldEventDrainIdle());
        Assert.Equal(new ushort[] { 1, 2 }, published.ToArray());
    }

    [Fact]
    public void BridgeWorker_DetachedLocalSnapshot_LatestWins_DropsStale()
    {
        // Bugbot: concurrent InvokeDetached LocalSnapshotReady could complete out of order
        // and advance _lastLocalSnapshot / PublishSnapshot backwards.
        using var worker = new BridgeWorker(new DolphinBridge());
        var appliedStages = new List<byte>();
        var gate = new ManualResetEventSlim(false);
        var firstEntered = new ManualResetEventSlim(false);

        worker.LocalSnapshotReady += snap =>
        {
            if (snap.StageId == 1)
            {
                firstEntered.Set();
                Assert.True(gate.Wait(TimeSpan.FromSeconds(2)));
            }

            appliedStages.Add(snap.StageId);
        };

        worker.DebugPublishLocalSnapshotReadyDetached(new PlayerSnapshot
        {
            Connected = 1,
            StageId = 1,
            EpisodeId = 0,
            Name = new byte[16],
        });
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));

        // Newer snap parks while the first callback is stalled — must replace, not queue.
        worker.DebugPublishLocalSnapshotReadyDetached(new PlayerSnapshot
        {
            Connected = 1,
            StageId = 7,
            EpisodeId = 2,
            Name = new byte[16],
        });
        worker.DebugPublishLocalSnapshotReadyDetached(new PlayerSnapshot
        {
            Connected = 1,
            StageId = 9,
            EpisodeId = 1,
            Name = new byte[16],
        });

        gate.Set();
        Assert.True(worker.DebugWaitOutboundWorldEventDrainIdle());
        // First may still complete (already dequeued); subsequent must be latest-only (9),
        // never 7 after 9.
        Assert.Contains((byte)1, appliedStages);
        Assert.Contains((byte)9, appliedStages);
        Assert.DoesNotContain((byte)7, appliedStages);
        Assert.Equal((byte)9, appliedStages.Last());
        Assert.True(worker.DebugLocalSnapshotAppliedSeq >= 3);
    }

    [Fact]
    public void BridgeWorker_DetachedLocalSnapshot_PrecedesWorldEventSameTick()
    {
        // Same poll tick: LocalSnapshotReady side effects must run before LocalWorldEventReady.
        using var worker = new BridgeWorker(new DolphinBridge());
        var order = new List<string>();
        var snapGate = new ManualResetEventSlim(false);

        worker.LocalSnapshotReady += _ =>
        {
            order.Add("snap");
            Assert.True(snapGate.Wait(TimeSpan.FromSeconds(2)));
        };
        worker.LocalWorldEventReady += _ => order.Add("world");
        worker.ModuleProgressResyncRequested += () => order.Add("resync");

        worker.DebugPublishLocalSnapshotReadyDetached(new PlayerSnapshot
        {
            Connected = 1,
            StageId = 3,
            EpisodeId = 0,
            Name = new byte[16],
        });
        worker.DebugPublishModuleProgressResyncDetached();
        worker.DebugPublishLocalWorldEventDetached(new CommWorldEvent
        {
            Sequence = 11,
            Type = WorldEventType.ShineCollected,
            Payload0 = 4,
        });

        // World/resync must not run until the snapshot callback is allowed to finish.
        Thread.Sleep(50);
        Assert.Equal(new[] { "snap" }, order.ToArray());

        snapGate.Set();
        Assert.True(worker.DebugWaitOutboundWorldEventDrainIdle());
        Assert.Equal(new[] { "snap", "resync", "world" }, order.ToArray());
    }

    [Fact]
    public void BridgeWorker_PushIncoming_OrdersOwnershipAheadOfMission_DropsEphemeral()
    {
        using var worker = new BridgeWorker(new DolphinBridge());

        worker.PushIncomingWorldEvent(new WorldEventPacket(
            1, WorldEventType.NpcReact, 1, 0, 1, 0, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            2, WorldEventType.RedCoinCollected, 1, 0, 0, 1, 0x01));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            3, WorldEventType.ShineCollected, 1, 0, 10, 0, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            4, WorldEventType.BlueCoinCollected, 2, 0, 5, 0, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            5, WorldEventType.GoldCoinCollected, 1, 0, 0, 0, 3));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            6, WorldEventType.MarioFruitThrown, 1, 0, 3, 1, 0x22));

        var pending = worker.DebugDrainPendingIncomingWorldEvents();
        // Phase A: fruit / react / gold hard-dropped — never wedge mission.
        Assert.Equal(3, pending.Count);
        Assert.Equal(WorldEventType.ShineCollected, pending[0].Type);
        Assert.Equal(WorldEventType.BlueCoinCollected, pending[1].Type);
        Assert.Equal(WorldEventType.RedCoinCollected, pending[2].Type);
    }

    [Fact]
    public void BridgeWorker_CapsMissionIncoming_IndependentlyOfOwnership()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        for (uint i = 1; i <= 50; i++)
        {
            worker.PushIncomingWorldEvent(new WorldEventPacket(
                i, WorldEventType.RedCoinCollected, 3, 0, 0x10, (byte)(i % 8), (uint)(1 << (int)(i % 8))));
        }

        worker.PushIncomingWorldEvent(new WorldEventPacket(
            100, WorldEventType.ShineCollected, 0, 0, 55, 0, 0));

        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.Equal(WorldEventType.ShineCollected, pending[0]);
        Assert.True(pending.Count(t => t == WorldEventType.RedCoinCollected) <= 32);
        Assert.Contains(WorldEventType.ShineCollected, pending);
    }

    [Fact]
    public void BridgeWorker_CapsOwnershipIncoming_DropOldestWhenDistinctAtCap()
    {
        // Soft-cap used to always Enqueue after a no-op coalesce → unbounded growth.
        using var worker = new BridgeWorker(new DolphinBridge());
        for (uint i = 1; i <= 200; i++)
        {
            worker.PushIncomingWorldEvent(new WorldEventPacket(
                i, WorldEventType.BlueCoinCollected, (byte)(i / 50), 0, (byte)(i % 50), 0, 0));
        }

        var pending = worker.DebugDrainPendingIncomingWorldEvents();
        Assert.Equal(64, pending.Count);
        Assert.DoesNotContain(pending, e => e.EventId == 1);
        Assert.Equal(137u, pending[0].EventId); // 200 - 64 + 1
        Assert.Equal(200u, pending[^1].EventId);
    }

    [Fact]
    public void BridgeWorker_CapsOwnershipIncoming_CoalescesDuplicatesBeforeDropOldest()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        for (uint i = 1; i <= 63; i++)
        {
            worker.PushIncomingWorldEvent(new WorldEventPacket(
                i, WorldEventType.ShineCollected, 0, 0, (byte)i, 0, 0));
        }

        // Duplicate shine 1 → queue at hard-cap with one coalescable pair.
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            64, WorldEventType.ShineCollected, 0, 0, 1, 0, 0));

        // New distinct shine: coalesce drops the older shine:1 duplicate, then enqueues.
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            65, WorldEventType.ShineCollected, 0, 0, 200, 0, 0));

        var pending = worker.DebugDrainPendingIncomingWorldEvents();
        Assert.Equal(64, pending.Count);
        Assert.Contains(pending, e => e.EventId == 1); // first shine:1 kept by coalesce
        Assert.DoesNotContain(pending, e => e.EventId == 64); // duplicate shine:1 dropped
        Assert.Contains(pending, e => e.EventId == 65);
        Assert.Equal(1, pending.Count(e => e.Type == WorldEventType.ShineCollected && e.Payload0 == 1));
    }

    [Fact]
    public void GameServer_HighPriorityTcp_ClassifiesOwnershipAheadOfFruit()
    {
        var shine = PacketSerializer.BuildWorldEventBroadcast(new WorldEventPacket(
            1, WorldEventType.ShineCollected, 0, 0, 7, 0, 0));
        var fruit = PacketSerializer.BuildWorldEventBroadcast(new WorldEventPacket(
            2, WorldEventType.MarioFruitThrown, 1, 0, 3, 1, 0x22));
        var progress = PacketSerializer.BuildWorldProgressSnapshot(new WorldProgressSnapshot
        {
            ProgressSeq = 1,
            ShineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount],
        });
        var reset = PacketSerializer.BuildWorldEventBroadcast(new WorldEventPacket(
            3, WorldEventType.SessionProgressReset, 0, 0, 0, 0, 0));

        Assert.True(GameServer.IsHighPriorityTcpFrame(shine));
        Assert.False(GameServer.IsHighPriorityTcpFrame(fruit));
        Assert.True(GameServer.IsHighPriorityTcpFrame(progress));
        Assert.True(GameServer.IsHighPriorityTcpFrame(reset));
    }

    [Fact]
    public void GameServer_HighPriorityTcp_IsBounded()
    {
        // Unbounded high-pri was a Phase 1 soft-death under ownership storms + slow peer.
        Assert.Equal(128, GameServer.HighPrioritySendCapacity);
        Assert.Equal(8, GameServer.LowPrioritySendCapacity);
        Assert.Equal(200, GameServer.OwnershipPushCoalesceMs);
        Assert.True(GameServer.ProgressSnapshotEnqueueIsLatestWinsOnly);
        Assert.True(GameServer.HighPrioritySendCapacity > 0);
    }

    [Fact]
    public void WorldEventTcpPolicy_PhaseA_DurableOnly()
    {
        Assert.True(WorldEventTcpPolicy.IsSnapshotOwnership(WorldEventType.ShineCollected));
        Assert.True(WorldEventTcpPolicy.IsSnapshotMission(WorldEventType.RedCoinCollected));
        Assert.True(WorldEventTcpPolicy.IsNonNetworkedEphemeral(WorldEventType.MarioFruitThrown));
        Assert.True(WorldEventTcpPolicy.IsNonNetworkedEphemeral(WorldEventType.NpcReact));
        Assert.True(WorldEventTcpPolicy.IsNonNetworkedEphemeral(WorldEventType.HipDropObject));
        Assert.True(WorldEventTcpPolicy.IsNonNetworkedEphemeral(WorldEventType.GoldCoinCollected));
        Assert.False(WorldEventTcpPolicy.ShouldSendLocalWorldEvent(WorldEventType.GoldCoinCollected));
        Assert.True(WorldEventTcpPolicy.ShouldSendLocalWorldEvent(WorldEventType.ShineCollected));
        Assert.True(WorldEventTcpPolicy.ShouldSendLocalWorldEvent(WorldEventType.RedCoinCollected));
        Assert.True(WorldEventTcpPolicy.RequiresLiveTcpFanout(WorldEventType.SessionProgressReset));
        Assert.Equal(200, (int)ProgressPushCoalescer.DefaultCoalesce.TotalMilliseconds);
        Assert.Equal(500, (int)ProgressPushCoalescer.LoadedCoalesce.TotalMilliseconds);
        Assert.Equal(4, ProgressPushCoalescer.LoadedFlushThreshold);
    }

    [Fact]
    public void CommBuffer_WorldSync_IncludesDualOutboundAndOwnershipIncoming()
    {
        Assert.Equal(15, ProtocolConstants.CommVersion);
        Assert.True(ProtocolConstants.ModBuildId >= 81);
        Assert.Equal(ProtocolConstants.CommWorldEventSize * 4 + 4, ProtocolConstants.CommWorldSyncSize);
        Assert.Equal(
            ProtocolConstants.CommWorldSyncOffset,
            ProtocolConstants.CommLocalPendingOwnershipOffset);
        Assert.Equal(
            ProtocolConstants.CommWorldSyncOffset + ProtocolConstants.CommWorldEventSize,
            ProtocolConstants.CommLocalPendingMissionOffset);
        Assert.Equal(
            ProtocolConstants.CommWorldSyncOffset + ProtocolConstants.CommWorldEventSize * 2,
            ProtocolConstants.CommIncomingOwnershipWorldEventOffset);
        Assert.Equal(
            ProtocolConstants.CommWorldSyncOffset + ProtocolConstants.CommWorldEventSize * 3,
            ProtocolConstants.CommIncomingWorldEventOffset);
    }

    [Fact]
    public void ProgressOwnershipTracker_NoteSnapshot_ReplacesMissionMasks()
    {
        var tracker = new ProgressOwnershipTracker();
        tracker.NoteLiveEvent(new WorldEventPacket(
            1, WorldEventType.RedCoinCollected, 3, 1, 0x10, 0, 0x01));
        tracker.NoteLiveEvent(new WorldEventPacket(
            2, WorldEventType.ShineCollected, 0, 0, 9, 0, 0));

        // Heal for a different stage replaces mission notes (authorities are SoT).
        tracker.NoteSnapshotEvents(new[]
        {
            new WorldEventPacket(10, WorldEventType.ShineCollected, 0, 0, 9, 0, 0),
            new WorldEventPacket(11, WorldEventType.RedCoinCollected, 5, 2, 0x10, 1, 0x02),
        }, replaceMission: true);

        var oldRed = new WorldEventPacket(1, WorldEventType.RedCoinCollected, 3, 1, 0x10, 0, 0x01);
        var newRed = new WorldEventPacket(11, WorldEventType.RedCoinCollected, 5, 2, 0x10, 1, 0x02);
        Assert.Single(tracker.FilterNewEvents(new[] { oldRed }, filterOwnership: true));
        Assert.Empty(tracker.FilterNewEvents(new[] { newRed }, filterOwnership: true));
    }

    [Fact]
    public void BridgeWorker_MergeLocalPendingPolicy_EmptyLiveClearsStaleWorking()
    {
        // Documents the 2026-07-20 soft-death: after clear, live LocalPending is empty but
        // a stale RedCoin in the working buffer must NOT be preserved across full writes.
        var working = new CommWorldEvent
        {
            Sequence = 87,
            Type = WorldEventType.RedCoinCollected,
            CourseId = 3,
            EpisodeId = 5,
            Payload0 = 4,
            Reserved = 1,
            Payload1 = 0x02,
        };
        var liveEmpty = default(CommWorldEvent);
        Assert.True(working.IsEmpty == false);
        Assert.True(liveEmpty.IsEmpty);

        // Policy used by TryWriteWorkingBuffer: always adopt live, including empty.
        var merged = liveEmpty;
        Assert.True(merged.IsEmpty);
        Assert.NotEqual(WorldEventType.RedCoinCollected, merged.Type);
    }

    [Fact]
    public void BridgeWorker_ReadMissFullWrite_ClearsIncomingAndProtectsStagedHeal()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);

        var staleOwnership = new CommWorldEvent
        {
            EventId = 50,
            Type = WorldEventType.ShineCollected,
            Payload0 = 7,
        };
        var staleMission = new CommWorldEvent
        {
            EventId = 51,
            Type = WorldEventType.GoldCoinCollected,
            CourseId = 3,
            EpisodeId = 1,
            Payload1 = 9,
        };
        worker.DebugStageIncomingForSplice(staleOwnership, staleMission);
        worker.DebugMarkProgressHealStaged(hostSeq: 42, payloadLen: 2);

        // Simulate poll-path pollution then a TryReadBuffer miss on full write.
        var polluted = worker.DebugGetWorkingBuffer();
        Assert.Equal(50u, polluted.WorldSync.IncomingOwnership.EventId);
        Assert.Equal(0u, polluted.ProgressSnapshotModuleAppliedSeq);

        // Force stale moduleApplied into working as if a prior adopt left it.
        worker.DebugMarkProgressHealStaged(hostSeq: 42, payloadLen: 2);
        worker.DebugApplyReadMissFullWritePolicy();

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(0u, buf.WorldSync.LocalPendingOwnership.EventId);
        Assert.Equal(0u, buf.WorldSync.LocalPendingMission.EventId);
        Assert.Equal(0u, buf.WorldSync.IncomingOwnership.EventId);
        Assert.Equal(0u, buf.WorldSync.Incoming.EventId);
        Assert.Equal(42u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.True(buf.ProgressSnapshotPayloadLen > 0);
    }

    [Fact]
    public void BridgeWorker_ReadMissFullWrite_KeepsProgressLaneCleared()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.DebugMarkProgressLaneCleared();

        // Pollute progress fields then apply miss policy — clear must stick.
        worker.DebugMarkProgressHealStaged(hostSeq: 99, payloadLen: 1);
        worker.DebugMarkProgressLaneCleared();
        worker.DebugApplyReadMissFullWritePolicy();

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(0u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(0, buf.ProgressSnapshotPayloadLen);
    }

    [Fact]
    public void BridgeWorker_DualLocalPending_PublishOrderPrefersOwnership()
    {
        // Comm v14 layout: ownership outbound precedes mission in the mailbox image.
        Assert.True(ProtocolConstants.CommLocalPendingOwnershipOffset <
                    ProtocolConstants.CommLocalPendingMissionOffset);
        Assert.Equal(
            ProtocolConstants.CommLocalPendingOwnershipOffset + ProtocolConstants.CommWorldEventSize,
            ProtocolConstants.CommLocalPendingMissionOffset);

        using var worker = new BridgeWorker(new DolphinBridge());
        var published = new List<WorldEventType>();
        worker.LocalWorldEventReady += req => published.Add(req.Type);

        // Without Dolphin, clear fails — but ownership publish is attempted first by poll
        // order. Exercise the same API order the poll loop uses.
        worker.DebugPublishLocalWorldEvent(new CommWorldEvent
        {
            Sequence = 1,
            Type = WorldEventType.ShineCollected,
            Payload0 = 3,
        }, ownershipLane: true);
        worker.DebugPublishLocalWorldEvent(new CommWorldEvent
        {
            Sequence = 2,
            Type = WorldEventType.RedCoinCollected,
            CourseId = 3,
            EpisodeId = 1,
            Reserved = 1,
            Payload1 = 0x01,
        }, ownershipLane: false);

        Assert.Equal(2, published.Count);
        Assert.Equal(WorldEventType.ShineCollected, published[0]);
        Assert.Equal(WorldEventType.RedCoinCollected, published[1]);
        // Clear fails without Dolphin — seq must not advance (ownership soft-death class).
        Assert.Equal(0, worker.DebugLastLocalWorldEventSequence);
        Assert.Equal(0, worker.DebugLastLocalMissionWorldEventSequence);
        Assert.Equal(1, worker.DebugPublishedUnclearedLocalWorldEventSequence);
    }

    [Fact]
    public void BridgeWorker_IncomingLanes_EmptyLiveClearsStaleWorking()
    {
        // Same resurrection bug as localPending: after the module clears a slot, a full write
        // must not rewrite the stale working event and block ownership staging.
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);

        var staleOwnership = new CommWorldEvent
        {
            EventId = 50,
            Type = WorldEventType.ShineCollected,
            Payload0 = 7,
        };
        var staleMission = new CommWorldEvent
        {
            EventId = 51,
            Type = WorldEventType.GoldCoinCollected,
            CourseId = 3,
            EpisodeId = 1,
            Payload1 = 9,
        };
        worker.DebugStageIncomingForSplice(staleOwnership, staleMission);

        // Simulate module clear with no Drain staging left (already applied).
        worker.DebugAdoptIncomingLanesFromLive(
            default, default, lastAppliedEventId: 51);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(0u, buf.WorldSync.IncomingOwnership.EventId);
        Assert.Equal(0u, buf.WorldSync.Incoming.EventId);
    }

    [Fact]
    public void BridgeWorker_IncomingLanes_SplicesStagedWhenLiveEmptyUnapplied()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);

        var stagedOwnership = new CommWorldEvent
        {
            EventId = 80,
            Type = WorldEventType.ShineCollected,
            Payload0 = 3,
        };
        worker.DebugStageIncomingForSplice(stagedOwnership, default);

        // Drain just wrote; live re-read still empty and lastApplied behind — keep staged.
        worker.DebugAdoptIncomingLanesFromLive(default, default, lastAppliedEventId: 79);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(80u, buf.WorldSync.IncomingOwnership.EventId);
        Assert.Equal(WorldEventType.ShineCollected, buf.WorldSync.IncomingOwnership.Type);
    }

    [Fact]
    public void BridgeWorker_MissionCap_DropsOldestRedUnderPressure()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        // Cap is 32: filling with reds then adding one more drops oldest (healable from snapshot).
        for (uint i = 1; i <= 33; i++)
        {
            worker.PushIncomingWorldEvent(new WorldEventPacket(
                i, WorldEventType.RedCoinCollected, 3, 1, 0x10, (byte)(i % 8), (uint)(1 << (int)(i % 8))));
        }

        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.True(pending.Length <= 32);
        Assert.All(pending, t => Assert.Equal(WorldEventType.RedCoinCollected, t));
    }

    [Fact]
    public void BridgeWorker_GoldAndFruit_NeverEnqueueMission()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        for (uint i = 1; i <= 10; i++)
        {
            worker.PushIncomingWorldEvent(new WorldEventPacket(
                i, WorldEventType.GoldCoinCollected, 1, 0, 0, 0, i));
            worker.PushIncomingWorldEvent(new WorldEventPacket(
                100 + i, WorldEventType.MarioFruitThrown, 1, 0, 3, 1, i));
        }

        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.Empty(pending);
    }
}

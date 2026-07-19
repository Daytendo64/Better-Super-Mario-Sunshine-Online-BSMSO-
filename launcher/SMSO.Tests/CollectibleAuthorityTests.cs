using SMSO.Bridge;
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
        var frame = relay.BuildAuthoritySnapshotReplay(shines, blues, reds, npcCleans,
            new GraffitiCleanAuthority(), story);
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
        // Simulate occupancy filter: only co-op stage 3/5 is included.
        var frame = relay.BuildAuthoritySnapshotReplay(
            new ShineAuthority(), new BlueCoinAuthority(), reds, new NpcCleanAuthority(),
            new GraffitiCleanAuthority(), new StoryFlagAuthority(),
            includeRedCoinStage: (course, episode) => course == 3 && episode == 5);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        Assert.DoesNotContain(events, e => e.Type == WorldEventType.RedCoinCollected && e.CourseId == 23);
        Assert.Contains(events, e => e.Type == WorldEventType.RedCoinCollected && e.CourseId == 3 && e.EpisodeId == 5);
    }

    [Fact]
    public void WorldEventRelay_RemoveRedCoinHistory_DropsStageEvents()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.RedCoinCollected, 23, 0, 0x10, 0, 1);
        relay.CreateWorldEvent(WorldEventType.ShineCollected, 0, 0, 7, 0, 0);
        relay.CreateWorldEvent(WorldEventType.RedCoinCollected, 23, 0, 0x20, 1, 3);
        Assert.Equal(3, relay.History.Count);

        relay.RemoveRedCoinHistory(23, 0);
        Assert.Single(relay.History);
        Assert.Equal(WorldEventType.ShineCollected, relay.History[0].Type);
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
            new NpcCleanAuthority(), new GraffitiCleanAuthority(), story);
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

        // Prefer courses without Sirena/Pinna mission↔catalog aliases so the
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

        // Plaza hub Type5 allowlist only — three flags coalesced to episode 0xFF.
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
        var frame = relay.BuildAuthoritySnapshotReplay(shines, blues, reds, npcCleans,
            new GraffitiCleanAuthority(), story);
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
    public void WorldEventRelay_GraffitiHeavySnapshot_PreservesStoryFlags()
    {
        // Regression: graffiti was serialized before story flags. With MaxCellsPerStage
        // across several stages the TCP payload filled and silently truncated story
        // catch-up — late join / 45s resync could soft-lock plaza gates and secrets.
        var shines = new ShineAuthority();
        var blues = new BlueCoinAuthority();
        var story = new StoryFlagAuthority();
        var graffiti = new GraffitiCleanAuthority();

        for (byte shineId = 0; shineId < 120; shineId++)
            Assert.True(shines.TryAccept(shineId));

        // 240 blues: 12 courses × 20 indices (covers full-clear coin ownership volume).
        for (byte course = 1; course <= 12; course++)
        {
            for (byte index = 0; index < 20; index++)
                Assert.True(blues.TryAccept(course, index));
        }

        Assert.True(story.TryAcceptStory(0x10384, 1)); // Bianco king gate
        Assert.True(story.TryAcceptStory(0x10389, 1)); // Pinna unlock
        Assert.True(story.TryAcceptTrigger(StoryFlagAuthority.PlazaAreaId, 8, 0x50001, 1));
        Assert.True(story.TryAcceptTrigger(StoryFlagAuthority.PlazaAreaId, 0, 0x50004, 1));
        Assert.True(story.TryAcceptSecret(0x10390, 1));

        // Saturate graffiti across many stages past the TCP event budget.
        for (byte course = 1; course <= 10; course++)
        {
            for (short i = 0; i < GraffitiCleanAuthority.MaxCellsPerStage; i++)
            {
                Assert.True(graffiti.TryAcceptCleaned(
                    new WorldEventRequest((ushort)(i + 1), WorldEventType.GraffitiCleaned, course, 0, 8, 0,
                        1u, GraffitiCleanAuthority.PackCell(i, 0, (short)course)),
                    out _, out _, out _, out _));
            }
        }

        var maxEvents = (ProtocolConstants.MaxTcpPayloadSize - 2) /
                        ProtocolConstants.WorldEventBroadcastPayloadSize;
        // 10 × 384 graffiti alone exceeds the TCP event budget.
        Assert.True(10 * GraffitiCleanAuthority.MaxCellsPerStage > maxEvents);

        var relay = new WorldEventRelay();
        var frame = relay.BuildAuthoritySnapshotReplay(
            shines, blues, new RedCoinAuthority(), new NpcCleanAuthority(), graffiti, story);
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

        // Graffiti may be truncated, but must never displace ownership/story events.
        var graffitiCount = events.Count(e => e.Type == WorldEventType.GraffitiCleaned);
        Assert.True(graffitiCount < 10 * GraffitiCleanAuthority.MaxCellsPerStage);
        Assert.True(events.Length <= maxEvents);
    }

    [Fact]
    public void BridgeWorker_PrioritizesShineBlueAheadOfEphemeralQueue()
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
        Assert.Equal(4, pending.Length);
        Assert.Equal(WorldEventType.ShineCollected, pending[0]);
        Assert.Equal(WorldEventType.BlueCoinCollected, pending[1]);
        Assert.Equal(WorldEventType.NpcReact, pending[2]);
        Assert.Equal(WorldEventType.MarioFruitThrown, pending[3]);
    }

    [Fact]
    public void BridgeWorker_PrioritizesRedCoinAheadOfGraffitiQueue()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            1, WorldEventType.GraffitiCleaned, 1, 255, 0, 0, 0, 0x11));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            2, WorldEventType.GraffitiCleaned, 1, 255, 0, 0, 0, 0x22));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            3, WorldEventType.RedCoinCollected, 23, 0, 0x10, 2, 0x04, 0));
        worker.PushIncomingWorldEvent(new WorldEventPacket(
            4, WorldEventType.NpcCleaned, 8, 5, 0, 3, 0x08, 0));

        var pending = worker.DebugGetPendingIncomingTypes();
        Assert.Equal(4, pending.Length);
        Assert.Equal(WorldEventType.RedCoinCollected, pending[0]);
        Assert.Equal(WorldEventType.NpcCleaned, pending[1]);
        Assert.Equal(WorldEventType.GraffitiCleaned, pending[2]);
        Assert.Equal(WorldEventType.GraffitiCleaned, pending[3]);
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
}

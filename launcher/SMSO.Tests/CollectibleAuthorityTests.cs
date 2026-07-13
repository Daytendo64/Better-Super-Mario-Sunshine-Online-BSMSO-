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
            out _, out _, out _));
        Assert.True(npcCleans.TryAcceptCleaned(
            new WorldEventRequest(2, WorldEventType.NpcCleaned, 8, 5, 0, 3, 0x200),
            out _, out _, out _));
        Assert.True(story.TryAcceptStory(0x10384, 1));
        Assert.True(story.TryAcceptTrigger(3, 4, 0x50001, 1));

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
                                     e.CourseId == 3 && e.EpisodeId == 4 &&
                                     e.Payload1 == 0x50001 && e.Payload0 == 1);
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
    public void StoryFlagAuthority_ScopesStageFlagsByCourseAndEpisode()
    {
        var authority = new StoryFlagAuthority();
        Assert.True(authority.TryAcceptTrigger(1, 0, 0x50001, 1));
        Assert.False(authority.TryAcceptTrigger(1, 0, 0x50001, 1));
        Assert.True(authority.TryAcceptTrigger(1, 1, 0x50001, 1));
        Assert.True(authority.TryAcceptTrigger(2, 0, 0x50001, 1));
        Assert.Equal(3, authority.TriggerFlags.Count);
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
    public void WorldEventRelay_AuthoritySnapshot_PreservesTriggerStageScope()
    {
        var story = new StoryFlagAuthority();
        Assert.True(story.TryAcceptTrigger(1, 0, 0x50001, 1));
        Assert.True(story.TryAcceptTrigger(2, 3, 0x50001, 1));
        var relay = new WorldEventRelay();
        var frame = relay.BuildAuthoritySnapshotReplay(
            new ShineAuthority(), new BlueCoinAuthority(), new RedCoinAuthority(),
            new NpcCleanAuthority(), story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        Assert.Contains(events, e => e.Type == WorldEventType.TriggerFlag &&
                                     e.CourseId == 1 && e.EpisodeId == 0);
        Assert.Contains(events, e => e.Type == WorldEventType.TriggerFlag &&
                                     e.CourseId == 2 && e.EpisodeId == 3);
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

        for (byte course = 1; course <= 15; course++)
        {
            for (byte episode = 0; episode < 8; episode++)
            {
                for (byte index = 0; index < 8; index++)
                {
                    Assert.True(reds.TryAcceptCollected(
                        new WorldEventRequest(1, WorldEventType.RedCoinCollected, course, episode, index, index, 0x100u + index),
                        out _, out _, out _));
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

        var triggerCount = 0;
        for (byte course = 1; course <= 15; course++)
        {
            for (byte episode = 0; episode < 8; episode++)
            {
                foreach (var flag in new uint[] { 0x50001, 0x50002, 0x50004 })
                {
                    Assert.True(story.TryAcceptTrigger(course, episode, flag, 1));
                    triggerCount++;
                }
            }
        }

        Assert.Equal(360, triggerCount);

        var relay = new WorldEventRelay();
        var frame = relay.BuildAuthoritySnapshotReplay(shines, blues, reds, npcCleans, story);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldStateReplay, id);
        Assert.True(payload.Length <= ProtocolConstants.MaxTcpPayloadSize);
        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var events));
        // 120 shines + 20*20 blues + 15*8*8 reds + 378 story + 360 triggers
        // = 120 + 400 + 960 + 378 + 360 = 2218
        Assert.Equal(2218, events.Length);
        Assert.DoesNotContain(events,
            e => e.Type == WorldEventType.TriggerFlag &&
                 e.Payload1 == StoryFlagAuthority.RedCoinSwitchPressedFlagId);
    }
}

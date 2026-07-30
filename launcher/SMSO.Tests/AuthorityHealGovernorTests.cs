using System;
using SMSO.Launcher;
using SMSO.Net;
using Xunit;

namespace SMSO.Tests;

public sealed class AuthorityHealGovernorTests
{
    private static WorldProgressSnapshot SampleSnapshot(uint seq = 10) => new()
    {
        ProgressSeq = seq,
        Unchanged = false,
        ShineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount],
    };

    [Fact]
    public void BeginForce_WithoutCache_ClearsAndRequestsTcp()
    {
        var gov = new AuthorityHealGovernor();
        var plan = gov.BeginForce(DateTime.UtcNow);
        Assert.Equal(ForceHealPlan.Kind.ClearAndRequestTcp, plan.Action);
        Assert.True(plan.RequestTcpRefresh);
        Assert.True(AuthorityHealGovernor.ShouldClearMailboxBeforeForceTcp(hasAuthorityCache: false));
        Assert.True(gov.IsAwaitingForce);
    }

    [Fact]
    public void BeginForce_WithCache_RestagesWithRealProgressSeq_WithoutArmingAwait()
    {
        var gov = new AuthorityHealGovernor();
        var now = DateTime.UtcNow;
        gov.NoteAuthoritySnapshot(SampleSnapshot(42), now);

        var plan = gov.BeginForce(now.AddSeconds(1));
        Assert.Equal(ForceHealPlan.Kind.RestageFromCache, plan.Action);
        Assert.NotNull(plan.Snapshot);
        // Build 26: real ProgressSeq — never synthetic 0x60000000 band.
        Assert.Equal(42u, plan.HostSeq);
        Assert.False(AuthorityHealGovernor.IsCacheHealHostSeq(plan.HostSeq));
        Assert.False(plan.RequestTcpRefresh);
        Assert.False(AuthorityHealGovernor.ShouldClearMailboxBeforeForceTcp(hasAuthorityCache: true));
        // Build 33: cache restage is the heal — silent TCP must not arm the 2s watchdog storm.
        Assert.False(gov.IsAwaitingForce);
        Assert.False(gov.ForceReplyTimedOut(now.AddSeconds(1) + AuthorityHealGovernor.ForceReplyTimeout));
    }

    [Fact]
    public void ForceTimeout_WithCache_RestagesAndClearsAwait()
    {
        var gov = new AuthorityHealGovernor();
        var t0 = DateTime.UtcNow;
        // No cache at BeginForce → await TCP; then a snapshot arrives into the cache.
        _ = gov.BeginForce(t0);
        Assert.True(gov.IsAwaitingForce);
        gov.NoteAuthoritySnapshot(SampleSnapshot(7), t0.AddMilliseconds(50));

        var d = gov.OnForceTimeout(t0 + AuthorityHealGovernor.ForceReplyTimeout);
        Assert.Equal(ForceTimeoutDecision.Kind.RestageFromCacheAndClearAwait, d.Action);
        Assert.Equal(7u, d.HostSeq);
        Assert.False(d.RequestTcpRefresh);
        // Kind name is literal — await must clear so TCP silence cannot restage forever.
        Assert.False(gov.IsAwaitingForce);
        Assert.Equal(AuthorityHealState.Idle, gov.State);
        Assert.False(gov.ForceReplyTimedOut(t0 + AuthorityHealGovernor.ForceReplyTimeout * 2));
    }

    [Fact]
    public void ForceTimeout_WithoutCache_RetriesTcp()
    {
        var gov = new AuthorityHealGovernor();
        var t0 = DateTime.UtcNow;
        _ = gov.BeginForce(t0);
        Assert.True(gov.IsAwaitingForce);
        Assert.True(gov.ForceReplyTimedOut(t0 + AuthorityHealGovernor.ForceReplyTimeout));
        var decision = gov.OnForceTimeout(t0 + AuthorityHealGovernor.ForceReplyTimeout);
        Assert.Equal(ForceTimeoutDecision.Kind.RetryTcp, decision.Action);
        Assert.True(gov.IsAwaitingForce);
        Assert.Equal(2, decision.Attempt);
    }

    [Fact]
    public void ForceTimeout_WithoutCache_RetriesThenOpensCircuit()
    {
        var gov = new AuthorityHealGovernor();
        var t0 = DateTime.UtcNow;
        _ = gov.BeginForce(t0);

        for (var i = 1; i < AuthorityHealGovernor.MaxTcpForceAttempts; i++)
        {
            var d = gov.OnForceTimeout(t0.AddSeconds(2 * i));
            Assert.Equal(ForceTimeoutDecision.Kind.RetryTcp, d.Action);
            Assert.Equal(i + 1, d.Attempt);
            Assert.True(gov.IsAwaitingForce);
        }

        var open = gov.OnForceTimeout(t0.AddSeconds(2 * AuthorityHealGovernor.MaxTcpForceAttempts));
        Assert.Equal(ForceTimeoutDecision.Kind.OpenCircuit, open.Action);
        Assert.False(gov.IsAwaitingForce);
        Assert.Equal(AuthorityHealState.CircuitOpen, gov.State);

        var blocked = gov.BeginForce(t0.AddSeconds(2 * AuthorityHealGovernor.MaxTcpForceAttempts + 1));
        Assert.Equal(ForceHealPlan.Kind.CircuitBlocked, blocked.Action);

        var after = gov.BeginForce(t0.AddSeconds(2 * AuthorityHealGovernor.MaxTcpForceAttempts) +
                                   AuthorityHealGovernor.CircuitCooldown + TimeSpan.FromMilliseconds(1));
        Assert.Equal(ForceHealPlan.Kind.ClearAndRequestTcp, after.Action);
    }

    [Fact]
    public void NoteAuthoritySnapshot_CachesChangedBodies()
    {
        var gov = new AuthorityHealGovernor();
        var t0 = DateTime.UtcNow;
        _ = gov.BeginForce(t0);
        gov.NoteAuthoritySnapshot(SampleSnapshot(99), t0.AddMilliseconds(10));
        Assert.True(gov.HasAuthorityCache());
        Assert.Equal(99u, gov.PeekAuthorityCache()!.ProgressSeq);
        // Note does not clear await — only NoteForceSatisfied after mailbox write.
        Assert.True(gov.IsAwaitingForce);
        gov.NoteForceSatisfied();
        Assert.False(gov.IsAwaitingForce);
    }

    [Fact]
    public void StaleCache_StillRestagesInsteadOfClear()
    {
        var gov = new AuthorityHealGovernor();
        var t0 = DateTime.UtcNow;
        gov.NoteAuthoritySnapshot(SampleSnapshot(5), t0);
        var plan = gov.BeginForce(
            t0 + AuthorityHealGovernor.AuthorityCacheMaxAge + TimeSpan.FromSeconds(1));
        Assert.Equal(ForceHealPlan.Kind.RestageFromCache, plan.Action);
        Assert.False(AuthorityHealGovernor.ShouldClearMailboxBeforeForceTcp(gov.HasAuthorityCache()));
        Assert.Equal(5u, plan.HostSeq);
    }

    [Fact]
    public void Unchanged_RefreshesStamp_DoesNotReplaceBody()
    {
        var gov = new AuthorityHealGovernor();
        var t0 = DateTime.UtcNow;
        gov.NoteAuthoritySnapshot(SampleSnapshot(3), t0);
        Assert.True(gov.HasAuthorityCache());
        gov.NoteAuthoritySnapshot(WorldProgressSnapshot.CreateUnchanged(3),
            t0 + AuthorityHealGovernor.AuthorityCacheMaxAge + TimeSpan.FromSeconds(1));
        Assert.True(gov.HasAuthorityCache());
        Assert.False(gov.PeekAuthorityCache()!.Unchanged);
        Assert.Equal(3u, gov.PeekAuthorityCache()!.ProgressSeq);
    }

    [Fact]
    public void IsCacheHealHostSeq_DetectsLegacySyntheticBand()
    {
        Assert.True(AuthorityHealGovernor.IsCacheHealHostSeq(0x60000001u));
        Assert.True(AuthorityHealGovernor.IsCacheHealHostSeq(1610612814u));
        Assert.False(AuthorityHealGovernor.IsCacheHealHostSeq(335u));
        Assert.False(AuthorityHealGovernor.IsCacheHealHostSeq(0u));
    }

    [Fact]
    public void PeriodicCatchupAdvertiseSeq_ClampsLegacyCacheHealBand()
    {
        Assert.Equal(335u,
            SessionCoordinator.PeriodicCatchupAdvertiseSeq(1610612814u, lastRealProgressSeq: 335));
        Assert.Equal(0u,
            SessionCoordinator.PeriodicCatchupAdvertiseSeq(0x60000010u, lastRealProgressSeq: 0));
        Assert.Equal(42u, SessionCoordinator.PeriodicCatchupAdvertiseSeq(42u, 99u));
    }

    [Fact]
    public void ForceTimeoutRestage_AndSerializeFailure_RequireExpandFallback()
    {
        // Build 27 Bugbot: HandleForceProgressTimeout RestageFromCacheAndClearAwait and
        // OnWorldProgressSnapshotReceived serialize catch must expand like
        // RequestWorldProgressResync — never leave force-await / ownership unhealed.
        Assert.True(SessionCoordinator.ForceTimeoutRestageExpandsOnFailure);
        Assert.True(SessionCoordinator.ProgressSnapshotSerializeFailureExpands);
    }
}

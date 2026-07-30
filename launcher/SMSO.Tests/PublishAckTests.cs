using SMSO.Bridge;
using SMSO.Net;

namespace SMSO.Tests;

/// <summary>
/// Durable publish reliability: the Dolphin localPending lane and the module's
/// "already published" caches must not advance until the server actually received the
/// mutation. The server's authorities heal *from* their own state, so anything they never
/// received is unrecoverable for the rest of the session.
/// </summary>
public sealed class PublishAckTests
{
    private static CommWorldEvent Shine(ushort sequence, byte shineId) => new()
    {
        Sequence = sequence,
        Type = WorldEventType.ShineCollected,
        CourseId = 13,
        EpisodeId = 4,
        Payload0 = shineId,
    };

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void FailedSend_RequeuesAtFrontAndPreservesFifoOrder()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        var sent = new List<byte>();
        var firstAttempts = 0;

        worker.LocalWorldEventSendAsync = request =>
        {
            // First shine fails once — the retry must still be delivered before the
            // shine queued behind it.
            if (request.Payload0 == 1 && Interlocked.Increment(ref firstAttempts) == 1)
                return Task.FromResult(false);

            lock (sent)
                sent.Add(request.Payload0);
            return Task.FromResult(true);
        };

        worker.DebugPublishLocalWorldEventDetached(Shine(1, 1));
        worker.DebugPublishLocalWorldEventDetached(Shine(2, 2));

        Assert.True(worker.DebugWaitOutboundWorldEventDrainIdle(8000));
        lock (sent)
            Assert.Equal(new byte[] { 1, 2 }, sent.ToArray());
        Assert.Equal(1, worker.DebugWorldEventSendFailureCount);
    }

    [Fact]
    public void DolphinLane_IsNotClearedUntilTheSendIsAcked()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.LocalWorldEventSendAsync = _ => gate.Task;

        worker.DebugPublishLocalWorldEventDetached(Shine(42, 36));

        // In flight: the lane stays occupied and last-published must not advance.
        Thread.Sleep(80);
        Assert.Equal(0, worker.DebugLocalPendingClearAttempts);
        Assert.Equal((ushort)0, worker.DebugAckedOwnershipSequence);
        Assert.Equal((ushort)42, worker.DebugPublishedUnclearedLocalWorldEventSequence);
        Assert.Equal((ushort)0, worker.DebugLastLocalWorldEventSequence);

        // A poll tick that re-observes the same unacked sequence must not resend or clear.
        worker.DebugPublishLocalWorldEventDetached(Shine(42, 36));
        Assert.Equal(0, worker.DebugLocalPendingClearAttempts);

        gate.SetResult(true);
        Assert.True(WaitFor(() => worker.DebugAckedOwnershipSequence == 42));

        // Now the next poll tick is allowed to clear Dolphin.
        worker.DebugPublishLocalWorldEventDetached(Shine(42, 36));
        Assert.True(worker.DebugLocalPendingClearAttempts > 0);
    }

    [Fact]
    public void RetryExhaustion_RetainsTheEventAndReleasesTheLane()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        var attempts = 0;
        worker.LocalWorldEventSendAsync = _ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(false);
        };

        worker.DebugPublishLocalWorldEventDetached(Shine(7, 77));

        Assert.True(WaitFor(() => worker.DebugRetainedWorldEventCount == 1));
        // 5 bounded attempts (100/200/400/800 ms backoff), never an infinite spin.
        Assert.Equal(5, Volatile.Read(ref attempts));
        Assert.Equal(0, worker.DebugOutboundWorldEventQueueCount);
        // Lane released so the module's ownership queue keeps draining; the event itself
        // is retained rather than dropped.
        Assert.Equal((ushort)7, worker.DebugAckedOwnershipSequence);
        Assert.Contains(worker.DebugRetainedWorldEvents, r => r.Payload0 == 77);
    }

    [Fact]
    public void QueuedEvents_SurviveABriefDisconnectAndReplayOnReconnect()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<byte>();

        worker.LocalWorldEventSendAsync = request =>
        {
            if (request.Payload0 == 10)
                return hold.Task;

            lock (sent)
                sent.Add(request.Payload0);
            return Task.FromResult(true);
        };

        // First event parks inside the sender, so the second stays queued behind it.
        worker.DebugPublishLocalWorldEventDetached(Shine(1, 10));
        worker.DebugPublishLocalWorldEventDetached(Shine(2, 11));
        Assert.True(WaitFor(() => worker.DebugOutboundWorldEventQueueCount == 1));

        // Session drops: the queue must be retained, not wiped.
        worker.DebugRetainOutboundForReconnect();
        Assert.Equal(0, worker.DebugOutboundWorldEventQueueCount);
        Assert.Equal(1, worker.DebugRetainedWorldEventCount);
        Assert.Contains(worker.DebugRetainedWorldEvents, r => r.Payload0 == 11);

        hold.SetResult(true);
        worker.DebugFlushRetainedWorldEvents();

        Assert.True(WaitFor(() =>
        {
            lock (sent)
                return sent.Contains((byte)11);
        }));
        Assert.Equal(0, worker.DebugRetainedWorldEventCount);
    }

    [Fact]
    public void RetainedEvents_AreKeyedAndBounded()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.LocalWorldEventSendAsync = _ => Task.FromResult(false);

        // Same shine published twice must collapse to one retained entry.
        worker.DebugPublishLocalWorldEventDetached(Shine(1, 5));
        Assert.True(WaitFor(() => worker.DebugRetainedWorldEventCount == 1));
        worker.DebugPublishLocalWorldEventDetached(Shine(2, 5));
        Assert.True(WaitFor(() => worker.DebugWorldEventSendFailureCount >= 10));
        Assert.Equal(1, worker.DebugRetainedWorldEventCount);
    }

    [Fact]
    public void RetainedEvent_IsResentByThePollLoopDrainWithoutAnyReconnect()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        var sent = new List<byte>();
        var failing = true;

        worker.LocalWorldEventSendAsync = request =>
        {
            if (Volatile.Read(ref failing))
                return Task.FromResult(false);

            lock (sent)
                sent.Add(request.Payload0);
            return Task.FromResult(true);
        };

        worker.SetConnected(true, 1, "Host", true);
        worker.Start();
        try
        {
            worker.DebugPublishLocalWorldEventDetached(Shine(3, 44));
            Assert.True(WaitFor(() => worker.DebugRetainedWorldEventCount == 1));

            // The transient fault clears but the session never drops — retention used to
            // drain only from SetConnected(true), losing the shine for the whole session.
            Volatile.Write(ref failing, false);

            Assert.True(WaitFor(() =>
            {
                lock (sent)
                    return sent.Contains((byte)44);
            }, 20000));
            Assert.Equal(0, worker.DebugRetainedWorldEventCount);
        }
        finally
        {
            worker.Stop();
        }
    }

    [Fact]
    public void PeriodicRetentionDrain_SkipsAnEventAlreadyQueuedForSend()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;

        worker.LocalWorldEventSendAsync = request =>
        {
            Interlocked.Increment(ref sends);
            return request.Sequence == 1 ? hold.Task : Task.FromResult(true);
        };

        worker.SetConnected(true, 1, "Host", true);

        // One copy of the shine parks inside the sender, a second queues behind it.
        worker.DebugPublishLocalWorldEventDetached(Shine(1, 9));
        Assert.True(WaitFor(() => Volatile.Read(ref sends) == 1));
        worker.DebugPublishLocalWorldEventDetached(Shine(2, 9));
        Assert.True(WaitFor(() => worker.DebugOutboundWorldEventQueueCount == 1));

        // Park the queued copy in retention, then let the module publish the same key again.
        worker.DebugRetainOutboundForReconnect();
        Assert.Equal(1, worker.DebugRetainedWorldEventCount);
        worker.DebugPublishLocalWorldEventDetached(Shine(3, 9));
        Assert.True(WaitFor(() => worker.DebugOutboundWorldEventQueueCount == 1));

        worker.DebugExpireRetainedRetryCadence();
        worker.DebugRetryRetainedWorldEvents();

        // The retained copy is absorbed by the queued one instead of becoming a second send.
        Assert.Equal(0, worker.DebugRetainedWorldEventCount);
        Assert.Equal(1, worker.DebugOutboundWorldEventQueueCount);

        hold.SetResult(true);
        Assert.True(worker.DebugWaitOutboundWorldEventDrainIdle(8000));
        Assert.Equal(2, Volatile.Read(ref sends));
    }

    [Fact]
    public void PeriodicRetentionDrain_DoesNotRunWhileDisconnected()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.LocalWorldEventSendAsync = _ => Task.FromResult(false);

        worker.DebugPublishLocalWorldEventDetached(Shine(1, 12));
        Assert.True(WaitFor(() => worker.DebugRetainedWorldEventCount == 1));

        worker.DebugExpireRetainedRetryCadence();
        worker.DebugRetryRetainedWorldEvents();
        Assert.Equal(1, worker.DebugRetainedWorldEventCount);
        Assert.Equal(0, worker.DebugOutboundWorldEventQueueCount);
    }

    [Fact]
    public async Task PollLoop_RestartsAfterAFaultOutsideThePerTickGuard()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        using var cts = new CancellationTokenSource();
        var calls = 0;

        worker.PollLoopBodyOverrideForTests = _ =>
        {
            // A PeriodicTimer fault (or any throw outside the per-tick catch) used to end
            // every Dolphin read/write for the rest of the session, silently.
            if (Interlocked.Increment(ref calls) <= 2)
                throw new InvalidOperationException("simulated timer fault");
            return Task.CompletedTask;
        };

        await worker.DebugRunPollLoopSupervisorAsync(cts.Token);

        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal(2, worker.DebugPollLoopRestartCount);
    }

    [Fact]
    public async Task PollLoop_GivesUpAfterTheRestartCapInsteadOfSpinning()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        using var cts = new CancellationTokenSource();
        var calls = 0;

        worker.PollLoopBodyOverrideForTests = _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("permanent fault");
        };

        var run = worker.DebugRunPollLoopSupervisorAsync(cts.Token);
        // Backoff is capped at 5 s; cancel once a few restarts have been observed.
        Assert.True(WaitFor(() => worker.DebugPollLoopRestartCount >= 3, 10000));
        cts.Cancel();
        await run;

        Assert.True(worker.DebugPollLoopRestartCount >= 3);
        Assert.True(worker.DebugPollLoopRestartCount <= 9);
    }

    [Fact]
    public void UdpHealth_IgnoresBriefLossButEscalatesSustainedSilence()
    {
        // Warm-up grace: silence during join must never degrade the session.
        Assert.Equal(NetClient.UdpHealth.Healthy, NetClient.EvaluateUdpHealth(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
        // Brief transient loss stays healthy.
        Assert.Equal(NetClient.UdpHealth.Healthy, NetClient.EvaluateUdpHealth(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), 0));
        // Sustained silence with a live TCP session = firewalled UDP; warn.
        Assert.Equal(NetClient.UdpHealth.Degraded, NetClient.EvaluateUdpHealth(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(8), 0));
        // Long silence: remotes are frozen — drop rather than look connected forever.
        Assert.Equal(NetClient.UdpHealth.Dead, NetClient.EvaluateUdpHealth(
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(25), 0));
        // A locally unusable socket is dead even during the warm-up grace.
        Assert.Equal(NetClient.UdpHealth.Dead, NetClient.EvaluateUdpHealth(
            TimeSpan.FromSeconds(1), TimeSpan.Zero, NetClient.UdpDeadSendFailures));
    }

    [Fact]
    public void TcpResync_ReportsSkippedBytesSoTheStreamBudgetIsBounded()
    {
        var frame = PacketSerializer.BuildHeartbeat(1234);

        // Two bytes of garbage ahead of a valid frame: resync reports what it consumed.
        var pending = new List<byte> { 0xAB, 0xCD };
        pending.AddRange(frame);
        Assert.True(NetClient.TryExtractFrame(pending, out var extracted, out var skipped));
        Assert.Equal(2, skipped);
        Assert.Equal(frame, extracted);

        // Pure garbage: every byte is counted so the caller can disconnect at the budget.
        var garbage = new List<byte>(new byte[64]);
        Assert.False(NetClient.TryExtractFrame(garbage, out _, out var garbageSkipped));
        Assert.Equal(64 - 12, garbageSkipped);
        Assert.True(NetClient.MaxResyncSkippedBytes > 0);
    }
}

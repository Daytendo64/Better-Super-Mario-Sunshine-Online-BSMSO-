using System;
using System.Threading;
using System.Threading.Tasks;

namespace SMSO.Net;

/// <summary>
/// Coalesces rapid ownership mutations into a single progress-snapshot broadcast.
/// Primary heal path for 10p / 120-shine runs: every authority change schedules a push
/// within <see cref="DefaultCoalesce"/> so catch-up is continuous, not stage-enter-only.
/// <para>
/// Build 36: adaptive coalesce — when flush rate is high (warp/red-reset storms), stretch
/// the window toward <see cref="LoadedCoalesce"/> so TCP does not carry a full lobby
/// snapshot every 125 ms. Inspired by snapshot rate limiting (Gaffer / Source tick budgets).
/// </para>
/// </summary>
public sealed class ProgressPushCoalescer
{
    /// <summary>Idle coalesce: shine-clear snappiness vs TCP fanout under 10p.</summary>
    public static readonly TimeSpan DefaultCoalesce = TimeSpan.FromMilliseconds(200);

    /// <summary>Under load: prefer fewer, larger coalesced pushes over a TCP storm.</summary>
    public static readonly TimeSpan LoadedCoalesce = TimeSpan.FromMilliseconds(500);

    /// <summary>Flushes inside this window count toward the loaded threshold.</summary>
    public static readonly TimeSpan LoadWindow = TimeSpan.FromSeconds(2);

    /// <summary>At/above this many flushes in <see cref="LoadWindow"/>, use loaded coalesce.</summary>
    public const int LoadedFlushThreshold = 4;

    private readonly TimeSpan _idleCoalesce;
    private readonly TimeSpan _loadedCoalesce;
    private readonly Action _flush;
    private int _pending;
    private int _scheduled;
    private long _flushCount;
    private long _loadWindowStartTicks;
    private int _flushesInWindow;

    public ProgressPushCoalescer(Action flush, TimeSpan? coalesce = null)
    {
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _idleCoalesce = coalesce ?? DefaultCoalesce;
        _loadedCoalesce = LoadedCoalesce < _idleCoalesce ? _idleCoalesce : LoadedCoalesce;
        _loadWindowStartTicks = DateTime.UtcNow.Ticks;
    }

    public long FlushCount => Interlocked.Read(ref _flushCount);

    /// <summary>Current coalesce delay based on recent flush pressure (for tests/telemetry).</summary>
    public TimeSpan CurrentCoalesce
    {
        get
        {
            RefreshLoadWindow(DateTime.UtcNow);
            return Volatile.Read(ref _flushesInWindow) >= LoadedFlushThreshold
                ? _loadedCoalesce
                : _idleCoalesce;
        }
    }

    /// <summary>Mark that authority changed; at most one flush runs after the coalesce window.</summary>
    public void NoteChanged()
    {
        Interlocked.Exchange(ref _pending, 1);
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
            return;

        _ = RunCoalescedFlushAsync();
    }

    /// <summary>Test / shutdown: flush immediately if anything is pending.</summary>
    public void FlushNow()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0)
            return;
        NoteFlushOccurred(DateTime.UtcNow);
        Interlocked.Increment(ref _flushCount);
        _flush();
    }

    private async Task RunCoalescedFlushAsync()
    {
        try
        {
            do
            {
                var delay = CurrentCoalesce;
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (Interlocked.Exchange(ref _pending, 0) == 0)
                    break;

                NoteFlushOccurred(DateTime.UtcNow);
                Interlocked.Increment(ref _flushCount);
                _flush();
            }
            while (Volatile.Read(ref _pending) == 1);
        }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            // NoteChanged may have set pending after the last Exchange but before
            // scheduled cleared — ensure that mutation still schedules a push.
            if (Volatile.Read(ref _pending) == 1)
                NoteChanged();
        }
    }

    private void NoteFlushOccurred(DateTime utcNow)
    {
        RefreshLoadWindow(utcNow);
        Interlocked.Increment(ref _flushesInWindow);
    }

    private void RefreshLoadWindow(DateTime utcNow)
    {
        var start = Interlocked.Read(ref _loadWindowStartTicks);
        if (utcNow.Ticks - start < LoadWindow.Ticks)
            return;

        // Roll the window — lose precise overlap; good enough for rate limiting.
        if (Interlocked.CompareExchange(ref _loadWindowStartTicks, utcNow.Ticks, start) == start)
            Interlocked.Exchange(ref _flushesInWindow, 0);
    }
}

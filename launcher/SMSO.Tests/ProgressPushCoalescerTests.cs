using System;
using System.Threading;
using System.Threading.Tasks;
using SMSO.Net;
using Xunit;

namespace SMSO.Tests;

public sealed class ProgressPushCoalescerTests
{
    [Fact]
    public async Task NoteChanged_CoalescesRapidMutationsIntoOneFlush()
    {
        var flushes = 0;
        var coalescer = new ProgressPushCoalescer(
            () => Interlocked.Increment(ref flushes),
            TimeSpan.FromMilliseconds(30));

        for (var i = 0; i < 20; i++)
            coalescer.NoteChanged();

        await Task.Delay(120);
        Assert.Equal(1, flushes);
        Assert.Equal(1, coalescer.FlushCount);
    }

    [Fact]
    public async Task NoteChanged_DuringFlush_Reschedules()
    {
        var flushes = 0;
        ProgressPushCoalescer? coalescer = null;
        coalescer = new ProgressPushCoalescer(
            () =>
            {
                var n = Interlocked.Increment(ref flushes);
                if (n == 1)
                    coalescer!.NoteChanged();
            },
            TimeSpan.FromMilliseconds(20));

        coalescer.NoteChanged();
        await Task.Delay(150);
        Assert.True(flushes >= 2, $"expected reschedule, got flushes={flushes}");
    }

    [Fact]
    public void FlushNow_DrainsPendingImmediately()
    {
        var flushes = 0;
        var coalescer = new ProgressPushCoalescer(() => flushes++, TimeSpan.FromSeconds(30));
        coalescer.NoteChanged();
        coalescer.FlushNow();
        Assert.Equal(1, flushes);
    }
}

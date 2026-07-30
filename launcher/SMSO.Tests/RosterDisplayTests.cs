using System.Collections.ObjectModel;
using SMSO.Launcher;
using SMSO.Net;
using Xunit;

namespace SMSO.Tests;

file static class RosterListSync
{
    public static void Sync(
        ObservableCollection<RosterViewModel> items,
        IReadOnlyList<PlayerRosterEntry> entries,
        Action<RosterViewModel, PlayerRosterEntry> apply)
    {
        var wanted = entries.Select(e => e.Slot).ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(items[i].Slot))
                items.RemoveAt(i);
        }

        foreach (var entry in entries.OrderBy(e => e.Slot))
        {
            var row = items.FirstOrDefault(r => r.Slot == entry.Slot);
            if (row == null)
            {
                row = new RosterViewModel { Slot = entry.Slot };
                var insertAt = items.TakeWhile(r => r.Slot < entry.Slot).Count();
                items.Insert(insertAt, row);
            }

            apply(row, entry);
        }

        for (var i = 0; i < items.Count; i++)
            items[i].Ordinal = i + 1;
    }
}

public class RosterDisplayTests
{
    [Fact]
    public void DisplayName_UsesConnectedOrdinal_NotRawNetworkSlot()
    {
        var row = new RosterViewModel { Slot = 2, Ordinal = 2, Username = "Wario" };
        Assert.Equal("2. Wario", row.DisplayName);
    }

    [Fact]
    public void DisplayName_NotifiesWhenUsernameChanges()
    {
        var row = new RosterViewModel { Slot = 0, Ordinal = 1, Username = "A" };
        string? changed = null;
        row.PropertyChanged += (_, e) => changed = e.PropertyName;
        row.Username = "B";
        Assert.Equal(nameof(RosterViewModel.DisplayName), changed);
        Assert.Equal("1. B", row.DisplayName);
    }

    [Fact]
    public void Sync_MiddleDisconnect_RenumbersSurvivorsOneToN()
    {
        var items = new ObservableCollection<RosterViewModel>();
        RosterListSync.Sync(items, new[]
        {
            Entry(0, "Host"),
            Entry(1, "P2"),
            Entry(2, "P3"),
        }, Apply);

        Assert.Equal(new[] { "1. Host", "2. P2", "3. P3" }, items.Select(r => r.DisplayName));

        // Slot 1 leaves — survivors renumber to 1..N (sorted by remaining network slots).
        RosterListSync.Sync(items, new[]
        {
            Entry(0, "Host"),
            Entry(2, "P3"),
        }, Apply);

        Assert.Equal(2, items.Count);
        Assert.Equal(new byte[] { 0, 2 }, items.Select(r => r.Slot).ToArray());
        Assert.Equal(new[] { "1. Host", "2. P3" }, items.Select(r => r.DisplayName));
    }

    [Fact]
    public void Sync_LowerSlotRejoins_InsertsSortedAndRenumbers()
    {
        var items = new ObservableCollection<RosterViewModel>();
        RosterListSync.Sync(items, new[]
        {
            Entry(1, "P2"),
            Entry(2, "P3"),
        }, Apply);

        Assert.Equal(new[] { "1. P2", "2. P3" }, items.Select(r => r.DisplayName));

        RosterListSync.Sync(items, new[]
        {
            Entry(0, "Host"),
            Entry(1, "P2"),
            Entry(2, "P3"),
        }, Apply);

        Assert.Equal(new byte[] { 0, 1, 2 }, items.Select(r => r.Slot).ToArray());
        Assert.Equal(new[] { "1. Host", "2. P2", "3. P3" }, items.Select(r => r.DisplayName));
    }

    [Fact]
    public void Sync_AppendWithoutSortedInsertWouldScramble_ButInsertKeepsOrder()
    {
        // Regression: plain Add() after Host+P3 left would put rejoining slot 1 at the end
        // ("1. Host", "2. P3", "3. P2" visually with old slot labels, or out-of-order rows).
        var items = new ObservableCollection<RosterViewModel>();
        RosterListSync.Sync(items, new[]
        {
            Entry(0, "Host"),
            Entry(2, "P3"),
        }, Apply);

        RosterListSync.Sync(items, new[]
        {
            Entry(0, "Host"),
            Entry(1, "P2"),
            Entry(2, "P3"),
        }, Apply);

        Assert.Equal(new byte[] { 0, 1, 2 }, items.Select(r => r.Slot).ToArray());
        Assert.Equal(new[] { "1. Host", "2. P2", "3. P3" }, items.Select(r => r.DisplayName));
    }

    private static PlayerRosterEntry Entry(byte slot, string name) => new()
    {
        Slot = slot,
        Username = name,
        State = DolphinState.Active,
    };

    private static void Apply(RosterViewModel row, PlayerRosterEntry entry)
    {
        row.Username = entry.Username;
        row.Status = entry.State.ToString();
    }
}

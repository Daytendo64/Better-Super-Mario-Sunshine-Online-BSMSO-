namespace SMSO.Net;

/// <summary>
/// Cooldown tracking for hide-and-seek random seeker picks. Recently chosen players stay
/// exempt for a player-count-scaled number of subsequent random picks.
/// </summary>
public static class HideSeekRandomTagExemption
{
    public static int GetExemptRounds(int connectedPlayerCount) => connectedPlayerCount switch
    {
        <= 4 => 1,
        <= 6 => 2,
        <= 8 => 3,
        _ => 4,
    };

    public static IReadOnlyCollection<byte> GetExemptSlots(IReadOnlyDictionary<byte, int> roundsRemainingBySlot)
    {
        if (roundsRemainingBySlot.Count == 0)
            return Array.Empty<byte>();

        var exempt = new List<byte>(roundsRemainingBySlot.Count);
        foreach (var (slot, rounds) in roundsRemainingBySlot)
        {
            if (rounds > 0)
                exempt.Add(slot);
        }

        return exempt;
    }

    public static void RegisterPick(Dictionary<byte, int> roundsRemainingBySlot, byte chosenSlot, int connectedPlayerCount)
    {
        roundsRemainingBySlot[chosenSlot] = GetExemptRounds(connectedPlayerCount);

        foreach (var slot in roundsRemainingBySlot.Keys.ToList())
        {
            if (slot == chosenSlot)
                continue;

            if (--roundsRemainingBySlot[slot] <= 0)
                roundsRemainingBySlot.Remove(slot);
        }
    }

    public static void PruneDisconnected(Dictionary<byte, int> roundsRemainingBySlot, IEnumerable<byte> activeSlots)
    {
        var active = activeSlots as HashSet<byte> ?? activeSlots.ToHashSet();
        foreach (var slot in roundsRemainingBySlot.Keys.ToList())
        {
            if (!active.Contains(slot))
                roundsRemainingBySlot.Remove(slot);
        }
    }

    public static void Clear(Dictionary<byte, int> roundsRemainingBySlot) => roundsRemainingBySlot.Clear();
}

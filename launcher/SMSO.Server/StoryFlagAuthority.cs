using System.Collections.Generic;
using System.Linq;

namespace SMSO.Server;

/// <summary>
/// Authoritative durable story state. This is a grow-only set: vanilla resets and
/// transient clears are observations about one client, never permission to erase
/// shared session progress.
///
/// Pattern (same family as red-coin MissionBitset):
///   admit → broadcast immediately → clients apply live or hold pending overlay →
///   snapshots are recovery only.
/// </summary>
public sealed class StoryFlagAuthority
{
    public readonly record struct StageFlagKey(byte CourseId, byte EpisodeId, uint FlagId);

    public const uint CardBoolBase = 0x10000;
    public const uint CardBoolEnd = 0x103B4;
    public const uint ShineFlagEnd = 0x10078;
    public const uint BlueCoinFlagBase = 0x10078;
    public const uint BlueCoinFlagEnd = 0x1023A;
    public const uint StageBoolBase = 0x50000;
    public const uint StageBoolEnd = 0x50064;

    /// <summary>Delfino Plaza area id. Allowlist Type5 latches are hub-global here.</summary>
    public const byte PlazaAreaId = 1;

    /// <summary>
    /// Wildcard episode for plaza hub-global Type5 allowlist. dolpic scenarios use
    /// distinct mEpisodeID values (8/7/6/…) while 0x50001/02/04 mean the same thing.
    /// </summary>
    public const byte PlazaHubEpisode = 0xFF;

    /// <summary>
    /// TFlagManager Type5Flag.mRedCoinSwitchPressed (stage bool bit 9). Cleared by
    /// vanilla/BSE resetStage each stage enter; must not be durable-synced or it
    /// re-arms every red-coin switch mission globally.
    /// </summary>
    public const uint RedCoinSwitchPressedFlagId = 0x50009;

    /// <summary>
    /// One-shot plaza spawn directors consumed by decideMarioPosIdx. Pinna unlock FMV
    /// sets 0x30004 for the cannon reveal; durable sync must not re-apply it or every
    /// plaza return spawns at the cannon. Durable Pinna progress is card bool 0x10389.
    /// </summary>
    public const uint SpawnDirectorFlag30001 = 0x30001;
    public const uint SpawnDirectorFlag30004 = 0x30004;

    private readonly Dictionary<uint, byte> _storyFlags = new();
    private readonly Dictionary<StageFlagKey, byte> _triggerFlags = new();
    private readonly Dictionary<uint, byte> _secretFlags = new();
    private readonly object _gate = new();

    public void Reset()
    {
        lock (_gate)
        {
            _storyFlags.Clear();
            _triggerFlags.Clear();
            _secretFlags.Clear();
        }
    }

    public bool TryAcceptStory(uint flagId, byte value)
    {
        if (!IsDurableCardFlag(flagId) || value == 0)
            return false;

        lock (_gate)
            return TryAcceptSet(_storyFlags, flagId);
    }

    public bool TryAcceptTrigger(byte courseId, byte episodeId, uint flagId, byte value)
    {
        if (!IsDurableStageTrigger(flagId) || value == 0)
            return false;

        // Plaza MapEvent / MareGate latches only — Type5 ids reused as scratch on
        // other courses must never enter authority.
        if (courseId != PlazaAreaId)
            return false;

        // Coalesce every dolpic scenario into one hub-global key.
        var key = new StageFlagKey(PlazaAreaId, PlazaHubEpisode, flagId);
        lock (_gate)
            return TryAcceptSet(_triggerFlags, key);
    }

    public static bool IsEphemeralStageSessionTrigger(uint flagId)
        => flagId == RedCoinSwitchPressedFlagId;

    public static bool IsEphemeralSpawnDirectorFlag(uint flagId)
        => flagId == SpawnDirectorFlag30001 || flagId == SpawnDirectorFlag30004;

    public bool TryAcceptSecret(uint flagId, byte value)
    {
        if (!IsDurableCardFlag(flagId) || value == 0)
            return false;
        lock (_gate)
            return TryAcceptSet(_secretFlags, flagId);
    }

    public static bool IsDurableCardFlag(uint flagId)
        => flagId >= CardBoolBase &&
           flagId < CardBoolEnd &&
           flagId >= ShineFlagEnd &&
           (flagId < BlueCoinFlagBase || flagId >= BlueCoinFlagEnd);

    public static bool IsDurableStageTrigger(uint flagId)
        // Type5 is resetStage scratch space. Most IDs are reused by graffiti,
        // timers, switches, and one-shot episode logic; these are the verified
        // plaza MapEvent / MareGate latches with durable shared meaning.
        => flagId is 0x50001 or 0x50002 or 0x50004;

    /// <summary>
    /// True when a TriggerFlag event should apply on the local plaza visit
    /// regardless of dolpic scenario episode id.
    /// </summary>
    public static bool IsPlazaHubTrigger(byte courseId, uint flagId)
        => courseId == PlazaAreaId && IsDurableStageTrigger(flagId);

    public IReadOnlyDictionary<uint, byte> StoryFlags
    {
        get { lock (_gate) return Copy(_storyFlags); }
    }
    public IReadOnlyDictionary<StageFlagKey, byte> TriggerFlags
    {
        get { lock (_gate) return Copy(_triggerFlags); }
    }
    public IReadOnlyDictionary<uint, byte> SecretFlags
    {
        get { lock (_gate) return Copy(_secretFlags); }
    }

    public int TotalCount
    {
        get { lock (_gate) return _storyFlags.Count + _triggerFlags.Count + _secretFlags.Count; }
    }

    private static bool TryAcceptSet<TKey>(Dictionary<TKey, byte> store, TKey key)
        where TKey : notnull
    {
        if (store.ContainsKey(key))
            return false;

        store[key] = 1;
        return true;
    }

    private static IReadOnlyDictionary<TKey, byte> Copy<TKey>(Dictionary<TKey, byte> source)
        where TKey : notnull
        => source.ToDictionary(pair => pair.Key, pair => pair.Value);
}

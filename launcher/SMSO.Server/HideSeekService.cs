using System.Buffers.Binary;
using SMSO.Net;

namespace SMSO.Server;

public sealed class HideSeekService
{
    public const float TagRadius = HideSeekTagConstants.MaxHorizontalReach;
    public const float TagVerticalTolerance = HideSeekTagConstants.MaxVerticalSeparation;
    internal const float TagLagCompensationMaxSeconds = 0.12f;
    // Half a UDP tick for the fresh snapshot; stale peers extrapolate by packet age up to the cap.
    private const float TagFreshExtrapolationSeconds = 1f / (ProtocolConstants.SnapshotRateHz * 2f);
    private const int TagCooldownMs = 500;
    /// <summary>
    /// Full Start Tag hide grace: blue wash + seeker freeze on clients, proximity tags blocked.
    /// Server clock is authoritative via GraceActive / GraceRemainingMs.
    /// </summary>
    public const int DefaultStartTagGraceMs = 30_000;
    public const int MinStartTagGraceMs = 5_000;
    public const int MaxStartTagGraceMs = 60_000;
    internal const int StartTagGraceMs = DefaultStartTagGraceMs;
    /// <summary>
    /// After mid-round warp-all / Start Tag resume, ignore proximity tags briefly so
    /// clustered spawn positions do not mass-promote hiders. Does NOT re-arm the full
    /// 30s freeze/tint. Death promotions during this window are absorbed into baseline
    /// (stale VFX_DEAD) rather than promoting — real deaths after the window still work.
    /// </summary>
    internal const int TagProximityImmunityMs = 8000;

    private readonly GameServer _server;
    private GameModeStatePacket _state = GameModeStatePacket.CreateDefault();
    private readonly Dictionary<(byte seeker, byte hider), long> _tagCooldowns = new();
    private readonly Dictionary<byte, bool> _hiderDeathWasActive = new();
    private readonly HashSet<byte> _assignedRoleSlots = new();
    /// <summary>
    /// Username that last occupied a slot when it was released. A slot reclaimed by a
    /// different player must not inherit the previous occupant's Seeker role; the same
    /// name coming back within the reconnect window keeps it.
    /// </summary>
    private readonly Dictionary<byte, string> _lastOccupantBySlot = new();
    private byte _tagEventId;
    private uint _tagElapsedMs;
    private long _tagSegmentStartTick;
    /// <summary>
    /// True after the first Start Tag until Reset Tag / full Reset. Distinguishes
    /// Resume from a fresh Start even when Stop Tag lands on the same tick as
    /// start (elapsed stays 0) or during opening grace.
    /// </summary>
    private bool _tagRoundStarted;
    private long _startTagGraceUntilTick;
    private long _proximityTagImmunityUntilTick;
    private ushort _lastBroadcastGraceSec = ushort.MaxValue;
    private int _startTagGraceMs = DefaultStartTagGraceMs;

    public HideSeekService(GameServer server) => _server = server;

    /// <summary>
    /// Start Tag hide-grace duration in milliseconds (clamped). Applies to the next
    /// Start Tag: an in-flight grace countdown is never re-armed or truncated, so the
    /// host cannot accidentally extend or cut a round that is already hiding.
    /// </summary>
    public int StartTagGraceDurationMs
    {
        get => _startTagGraceMs;
        set => _startTagGraceMs = ClampGraceMs(value);
    }

    public static int ClampGraceMs(int ms) =>
        Math.Clamp(ms, MinStartTagGraceMs, MaxStartTagGraceMs);

    public GameModeStatePacket CurrentState
    {
        get
        {
            SyncGraceFieldsFromClock();
            return _state.Clone();
        }
    }

    /// <summary>
    /// Allocation-free reads for the per-snapshot hot path — <see cref="CurrentState"/>
    /// clones the packet, which at 10 players ran ~600 times a second.
    /// </summary>
    internal bool IsTagActive => _state.TagActive && _state.GameMode == GameMode.HideSeek;

    internal byte GetRoleForSlot(byte slot) => _state.GetRole(slot);

    public void Reset()
    {
        _state = GameModeStatePacket.CreateDefault();
        ClearRoundState();
    }

    /// <summary>
    /// Drop every trace of the current round: roles, timers, grace, tag ids and slot
    /// bookkeeping. Shared by <see cref="Reset"/> and <see cref="SetGameMode"/>(Normal)
    /// so leaving Hide &amp; Seek can never leave a half-started round behind (a stale
    /// <c>_tagRoundStarted</c> made the next Start Tag skip the hide grace entirely).
    /// Does not touch <see cref="GameModeStatePacket.Seq"/> — clients ignore rollbacks.
    /// </summary>
    private void ClearRoundState()
    {
        _state.Flags = GameModeFlags.None;
        _state.RoundStartMs = 0;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _state.GraceRemainingMs = 0;
        for (int i = 0; i < _state.Roles.Length; i++)
            _state.Roles[i] = HideSeekRole.Hider;
        _tagCooldowns.Clear();
        _hiderDeathWasActive.Clear();
        _assignedRoleSlots.Clear();
        _lastOccupantBySlot.Clear();
        _tagEventId = 0;
        _tagElapsedMs = 0;
        _tagSegmentStartTick = 0;
        _tagRoundStarted = false;
        _startTagGraceUntilTick = 0;
        _proximityTagImmunityUntilTick = 0;
        _lastBroadcastGraceSec = ushort.MaxValue;
    }

    private void AccumulateTagElapsed()
    {
        if (!_state.TagActive || _tagSegmentStartTick == 0)
            return;

        _tagElapsedMs += (uint)(Environment.TickCount64 - _tagSegmentStartTick);
        _tagSegmentStartTick = 0;
    }

    private void BeginTagSegment()
    {
        _tagSegmentStartTick = Environment.TickCount64;
    }

    private void ArmStartTagGrace()
    {
        _startTagGraceUntilTick = Environment.TickCount64 + _startTagGraceMs;
        _state.Flags |= GameModeFlags.GraceActive;
        _state.GraceRemainingMs = (ushort)Math.Min(ushort.MaxValue, _startTagGraceMs);
        _lastBroadcastGraceSec = (ushort)(_startTagGraceMs / 1000);
    }

    private void ClearStartTagGrace()
    {
        _startTagGraceUntilTick = 0;
        _state.Flags &= ~GameModeFlags.GraceActive;
        _state.GraceRemainingMs = 0;
        _lastBroadcastGraceSec = ushort.MaxValue;
    }

    private void ArmProximityTagImmunity()
    {
        _proximityTagImmunityUntilTick = Environment.TickCount64 + TagProximityImmunityMs;
    }

    private void ClearProximityTagImmunity()
    {
        _proximityTagImmunityUntilTick = 0;
    }

    private void SyncGraceFieldsFromClock()
    {
        if (_startTagGraceUntilTick == 0)
        {
            _state.Flags &= ~GameModeFlags.GraceActive;
            _state.GraceRemainingMs = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (now >= _startTagGraceUntilTick)
        {
            // Keep the end tick until TickGrace broadcasts the clear.
            _state.Flags |= GameModeFlags.GraceActive;
            _state.GraceRemainingMs = 0;
            return;
        }

        var remaining = (ushort)Math.Min(ushort.MaxValue, _startTagGraceUntilTick - now);
        _state.Flags |= GameModeFlags.GraceActive;
        _state.GraceRemainingMs = remaining;
    }

    /// <summary>
    /// Advance Start Tag grace clock. Broadcasts when grace ends or whole seconds change
    /// so clients keep a shared countdown.
    /// </summary>
    internal void TickGrace()
    {
        if (_startTagGraceUntilTick == 0)
            return;

        SyncGraceFieldsFromClock();

        if (Environment.TickCount64 >= _startTagGraceUntilTick)
        {
            ClearStartTagGrace();
            BumpAndBroadcast();
            _server.LogMessage("Hide & Seek grace ended — seekers released.");
            return;
        }

        var sec = (ushort)(_state.GraceRemainingMs / 1000);
        if (sec == _lastBroadcastGraceSec)
            return;

        _lastBroadcastGraceSec = sec;
        BumpAndBroadcast();
    }

    /// <summary>
    /// True while Start Tag grace or warp proximity immunity is suppressing proximity tags.
    /// </summary>
    internal bool IsProximityTagImmunityActive
        => IsStartTagGraceActive || IsWarpProximityImmunityActive;

    internal bool IsStartTagGraceActive
        => _startTagGraceUntilTick != 0 &&
           Environment.TickCount64 < _startTagGraceUntilTick;

    private bool IsWarpProximityImmunityActive
        => _proximityTagImmunityUntilTick != 0 &&
           Environment.TickCount64 < _proximityTagImmunityUntilTick;

    /// <summary>Test helper: allow proximity tags immediately after Start Tag / warp.</summary>
    internal void NoteHiderDeathBaseline(byte slot, bool currentlyDead)
    {
        // Always record alive OR dead so a Clear()+Seed race cannot see a missing
        // entry and treat a stale VFX_DEAD packet as a fresh rising edge.
        _hiderDeathWasActive[slot] = currentlyDead;
    }

    internal void ExpireProximityTagImmunityForTests()
    {
        ClearStartTagGrace();
        ClearProximityTagImmunity();
    }

    /// <summary>Clear only the short warp/Start-Tag death-edge window; keep hide grace.</summary>
    internal void ExpireWarpProximityImmunityForTests()
    {
        ClearProximityTagImmunity();
    }

    /// <summary>
    /// Re-arm short proximity-only immunity while tag is running (e.g. host warp-all).
    /// Does not re-arm the full 30s Start Tag freeze/tint.
    /// </summary>
    internal void NotifyPlayersWarped()
    {
        if (_state.GameMode != GameMode.HideSeek || !_state.TagActive)
            return;

        ArmProximityTagImmunity();
    }

    public void SetGameMode(GameMode mode)
    {
        _state.GameMode = mode;
        if (mode == GameMode.Normal)
            ClearRoundState();

        BumpAndBroadcast();
    }

    public void SetRoles(IReadOnlyDictionary<byte, HideSeekRole> roles)
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        var connected = _server.GetConnectedSlots();
        var activeSlots = connected.Count > 0
            ? connected.ToHashSet()
            : roles.Keys.ToHashSet();
        var changed = false;
        var connectedRoleChanged = false;
        foreach (var (slot, role) in roles)
        {
            _assignedRoleSlots.Add(slot);

            // During tag, never demote Seeker→Hider via SetRoles. Rejoin UI races used to
            // push rejoining seekers as Hider and stop the round; intentional mid-tag
            // reassignment requires Stop Tag first.
            if (_state.TagActive && role == HideSeekRole.Hider &&
                _state.GetRole(slot) == (byte)HideSeekRole.Seeker)
            {
                continue;
            }

            if (_state.GetRole(slot) == (byte)role)
                continue;

            _state.SetRole(slot, role);
            changed = true;
            if (activeSlots.Contains(slot))
                connectedRoleChanged = true;
        }

        if (!changed)
            return;

        if (_state.TagActive && connectedRoleChanged)
        {
            AccumulateTagElapsed();
            _state.Flags &= ~GameModeFlags.TagActive;
            _state.RoundStartMs = _tagElapsedMs;
            ClearStartTagGrace();
            ClearProximityTagImmunity();
            _server.LogMessage("Hide & Seek tag stopped — roles changed.");
        }

        _state.Flags &= ~GameModeFlags.RoundComplete;
        _state.LastTaggedSlot = 0xFF;
        BumpAndBroadcast();
    }

    public bool TryStartTag(out string? error)
    {
        error = null;
        if (_state.GameMode != GameMode.HideSeek)
        {
            error = "Hide & Seek mode is not active.";
            return false;
        }

        var roleSlots = GetActiveRoleSlots();
        var hiders = CountConnectedRole(HideSeekRole.Hider, roleSlots);
        var seekers = CountConnectedRole(HideSeekRole.Seeker, roleSlots);
        if (hiders < 1 || seekers < 1)
        {
            error = "Need at least one hider and one seeker.";
            return false;
        }

        _state.Flags = GameModeFlags.TagActive;
        _state.Flags &= ~(GameModeFlags.RoundComplete | GameModeFlags.RoundFanfare);
        _state.RoundStartMs = _tagElapsedMs;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _tagEventId = 0;
        _tagCooldowns.Clear();
        _hiderDeathWasActive.Clear();
        // Do not change roles here — only arm TagActive. Clustered spawn/lobby positions
        // would otherwise mass-promote hiders on the first proximity checks.
        ClearProximityTagImmunity();
        // Fresh Start Tag (first start after Reset) gets hide grace.
        // Resume after Stop Tag continues the same round — no full hide grace.
        // Always arm brief proximity/death-edge immunity so clustered players and
        // leftover VFX_DEAD cannot instantly mass-promote on the first snapshots.
        var isResume = _tagRoundStarted;
        _tagRoundStarted = true;
        ArmProximityTagImmunity();
        if (isResume)
            ClearStartTagGrace();
        else
            ArmStartTagGrace();
        // Suppress stale VFX_DEAD rising edges left over from prior deaths / reloads so
        // Start Tag does not instantly promote a hider who is still carrying Dead.
        _server.SeedHideSeekDeathBaseline();
        BeginTagSegment();
        BumpAndBroadcast();
        _server.LogMessage(isResume
            ? "Hide & Seek tag resumed (no hide grace; brief proximity immunity)."
            : $"Hide & Seek tag started ({_startTagGraceMs / 1000}s hide grace).");
        return true;
    }

    public void StopTag()
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        AccumulateTagElapsed();
        _state.Flags &= ~(GameModeFlags.TagActive | GameModeFlags.RoundComplete |
                          GameModeFlags.RoundFanfare | GameModeFlags.GraceActive);
        _state.RoundStartMs = _tagElapsedMs;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _state.GraceRemainingMs = 0;
        // Tag ids restart at 1 on the next promotion; a client that missed the last
        // event must never see a reused id as "already applied" (wire id is u8).
        _tagEventId = 0;
        _tagCooldowns.Clear();
        _hiderDeathWasActive.Clear();
        ClearStartTagGrace();
        ClearProximityTagImmunity();
        BumpAndBroadcast();
        _server.LogMessage("Hide & Seek tag stopped.");
    }

    public void ResetTag(bool playRoundFanfare = false)
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        foreach (var slot in GetActiveRoleSlots())
            _state.SetRole(slot, HideSeekRole.Hider);

        AccumulateTagElapsed();
        _tagElapsedMs = 0;
        _tagSegmentStartTick = 0;
        _tagRoundStarted = false;
        _state.Flags &= ~(GameModeFlags.TagActive | GameModeFlags.RoundComplete |
                          GameModeFlags.RoundFanfare | GameModeFlags.GraceActive);
        _state.RoundStartMs = 0;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _state.GraceRemainingMs = 0;
        _tagEventId = 0;
        _tagCooldowns.Clear();
        _hiderDeathWasActive.Clear();
        ClearStartTagGrace();
        ClearProximityTagImmunity();
        if (playRoundFanfare)
            _state.Flags |= GameModeFlags.RoundFanfare;
        _state.Flags |= GameModeFlags.TimerReset;
        BumpAndBroadcast();
        _state.Flags &= ~GameModeFlags.TimerReset;
        BumpAndBroadcast();
        _server.LogMessage("Hide & Seek roles reset — everyone is a hider.");
    }

    public void ClearTagPulse()
    {
        if (_state.LastTaggedSlot == 0xFF)
            return;

        _state.LastTaggedSlot = 0xFF;
        BumpAndBroadcast();
    }

    public void ProcessSnapshot(
        byte seekerSlot,
        in PlayerSnapshot seekerSnap,
        byte hiderSlot,
        in PlayerSnapshot hiderSnap,
        float seekerLagSeconds = 0f,
        float hiderLagSeconds = 0f)
    {
        if (!_state.TagActive || _state.GameMode != GameMode.HideSeek)
            return;

        TickGrace();

        // Proximity-only immunity / Start Tag grace: death promotions still apply.
        if (IsProximityTagImmunityActive)
            return;

        if (_state.GetRole(seekerSlot) != (byte)HideSeekRole.Seeker)
            return;
        if (_state.GetRole(hiderSlot) != (byte)HideSeekRole.Hider)
            return;
        if (seekerSnap.Connected == 0 || hiderSnap.Connected == 0)
            return;
        if (seekerSnap.StageId != hiderSnap.StageId)
            return;

        var seekerEpisode = LevelCatalog.NormalizeEpisodeFromGame(seekerSnap.StageId, seekerSnap.EpisodeId,
            _server.Levels);
        var hiderEpisode = LevelCatalog.NormalizeEpisodeFromGame(hiderSnap.StageId, hiderSnap.EpisodeId,
            _server.Levels);
        if (seekerEpisode != hiderEpisode)
            return;

        var key = (seekerSlot, hiderSlot);
        var now = Environment.TickCount64;
        if (_tagCooldowns.TryGetValue(key, out var last) && now - last < TagCooldownMs)
            return;

        if (!IsWithinTagRange(seekerSnap, hiderSnap, seekerLagSeconds, hiderLagSeconds))
            return;

        _tagCooldowns[key] = now;
        PromoteHiderToSeeker(hiderSlot, seekerSlot);
    }

    public void ProcessHiderDeath(byte hiderSlot, in PlayerSnapshot snap)
    {
        if (!_state.TagActive || _state.GameMode != GameMode.HideSeek)
            return;

        if (_state.GetRole(hiderSlot) != (byte)HideSeekRole.Hider)
        {
            _hiderDeathWasActive.Remove(hiderSlot);
            return;
        }

        if (snap.Connected == 0)
        {
            _hiderDeathWasActive.Remove(hiderSlot);
            return;
        }

        var isDead = IsSnapshotDead(snap);
        var wasDead = _hiderDeathWasActive.TryGetValue(hiderSlot, out var previous) && previous;

        // Brief proximity immunity (armed on every Start Tag + after warps): absorb
        // stale VFX_DEAD rising edges without promoting. Full Start Tag hide grace
        // still allows real water/void deaths to promote after this window.
        if (IsWarpProximityImmunityActive)
        {
            _hiderDeathWasActive[hiderSlot] = isDead;
            return;
        }

        _hiderDeathWasActive[hiderSlot] = isDead;

        if (!isDead || wasDead)
            return;

        PromoteHiderToSeeker(hiderSlot, taggedBySeekerSlot: null);
    }

    internal static bool IsSnapshotDead(in PlayerSnapshot snap)
        => (snap.VfxFlags & (ushort)VfxFlags.Dead) != 0;

    internal static bool IsWithinTagRange(
        in PlayerSnapshot seekerSnap,
        in PlayerSnapshot hiderSnap,
        float seekerLagSeconds = 0f,
        float hiderLagSeconds = 0f)
    {
        var seekerPos = ExtrapolatePosition(seekerSnap, seekerLagSeconds);
        var hiderPos = ExtrapolatePosition(hiderSnap, hiderLagSeconds);

        var dx = seekerPos.X - hiderPos.X;
        var dy = seekerPos.Y - hiderPos.Y;
        var dz = seekerPos.Z - hiderPos.Z;
        var horizontal = MathF.Sqrt(dx * dx + dz * dz);

        // Compare pivot-to-pivot distance against two Mario body radii plus touch slack.
        return horizontal <= HideSeekTagConstants.MaxHorizontalReach &&
               MathF.Abs(dy) <= HideSeekTagConstants.MaxVerticalSeparation;
    }

    private static Vec3 ExtrapolatePosition(in PlayerSnapshot snap, float lagSeconds)
    {
        var seconds = MathF.Min(lagSeconds + TagFreshExtrapolationSeconds, TagLagCompensationMaxSeconds);
        return new Vec3
        {
            X = snap.Position.X + snap.Velocity.X * seconds,
            Y = snap.Position.Y + snap.Velocity.Y * seconds,
            Z = snap.Position.Z + snap.Velocity.Z * seconds,
        };
    }

    private void PromoteHiderToSeeker(byte hiderSlot, byte? taggedBySeekerSlot)
    {
        if (_state.GetRole(hiderSlot) != (byte)HideSeekRole.Hider)
            return;

        _state.SetRole(hiderSlot, HideSeekRole.Seeker);
        _state.LastTaggedSlot = hiderSlot;
        _tagEventId++;
        if (_tagEventId == 0)
            _tagEventId = 1;
        _state.TagEventId = _tagEventId;
        _hiderDeathWasActive[hiderSlot] = true;

        var roundComplete = CountConnectedRole(HideSeekRole.Hider, GetActiveRoleSlots()) == 0;
        if (roundComplete)
            MarkRoundCompleteFlags();

        BumpAndBroadcast();
        if (taggedBySeekerSlot.HasValue)
            _server.LogMessage($"Tagged slot {hiderSlot} (by seeker {taggedBySeekerSlot.Value}).");
        else
            _server.LogMessage($"Hider slot {hiderSlot} died — now a seeker.");

        if (roundComplete)
        {
            _server.LogMessage("Hide & Seek round complete — all hiders found.");
            ResetTag(playRoundFanfare: true);
        }
    }

    public void ConsumeTagPulse()
    {
        ClearTagPulse();
    }

    /// <summary>Clear tag/grace and flag the round as won. Caller broadcasts.</summary>
    private void MarkRoundCompleteFlags()
    {
        _state.Flags &= ~(GameModeFlags.TagActive | GameModeFlags.GraceActive);
        _state.GraceRemainingMs = 0;
        _startTagGraceUntilTick = 0;
        // Set RoundFanfare here too so a coalesced mailbox write cannot drop the
        // brief RoundComplete-only packet before ResetTag (module missed fanfare).
        _state.Flags |= GameModeFlags.RoundComplete | GameModeFlags.RoundFanfare;
    }

    public void OnPlayerDisconnected(byte slot, string? username = null)
    {
        _assignedRoleSlots.Remove(slot);
        _hiderDeathWasActive.Remove(slot);

        // Remember who released the slot so a different player reclaiming it cannot
        // inherit their role. The role itself stays put: the reconnect window restores
        // the same slot for the same name, and a returning seeker must stay a seeker.
        if (!string.IsNullOrEmpty(username))
            _lastOccupantBySlot[slot] = username;

        var staleCooldowns = new List<(byte seeker, byte hider)>();
        foreach (var key in _tagCooldowns.Keys)
        {
            if (key.seeker == slot || key.hider == slot)
                staleCooldowns.Add(key);
        }

        foreach (var key in staleCooldowns)
            _tagCooldowns.Remove(key);

        if (_state.GameMode != GameMode.HideSeek || !_state.TagActive)
            return;

        // A disconnect alone must not end a tag with players left on both sides — the
        // host decides when a round is over. But a round with nobody left to find (or
        // nobody left to find them) can never end on its own, so resolve it here.
        var roleSlots = GetActiveRoleSlots();
        if (roleSlots.Count == 0)
        {
            _server.LogMessage("Hide & Seek tag stopped — no players remain.");
            StopTag();
            return;
        }

        if (CountConnectedRole(HideSeekRole.Hider, roleSlots) == 0)
        {
            MarkRoundCompleteFlags();
            BumpAndBroadcast();
            _server.LogMessage("Hide & Seek round complete — the last hider disconnected.");
            ResetTag(playRoundFanfare: true);
            return;
        }

        if (CountConnectedRole(HideSeekRole.Seeker, roleSlots) == 0)
        {
            _server.LogMessage("Hide & Seek tag stopped — the last seeker disconnected.");
            StopTag();
            return;
        }

        _server.LogMessage($"Player slot {slot} left during Hide & Seek — tag continues.");
    }

    /// <summary>
    /// Rejoin / first join while Hide &amp; Seek is active — restore assignment membership
    /// without stopping tag. A slot reclaimed by a different player is forced back to
    /// Hider so a new arrival never spawns as a seeker (or worse, mid-round with no grace).
    /// </summary>
    public void OnPlayerJoined(byte slot, string? username = null)
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        _assignedRoleSlots.Add(slot);

        if (string.IsNullOrEmpty(username) ||
            !_lastOccupantBySlot.TryGetValue(slot, out var previous))
        {
            return;
        }

        var sameOccupant = string.Equals(previous, username, StringComparison.OrdinalIgnoreCase);
        _lastOccupantBySlot.Remove(slot);
        if (sameOccupant)
            return;

        if (_state.GetRole(slot) == (byte)HideSeekRole.Hider)
            return;

        _state.SetRole(slot, HideSeekRole.Hider);
        _hiderDeathWasActive.Remove(slot);
        _server.LogMessage(
            $"Hide & Seek: slot {slot} reclaimed by '{username}' — role reset to hider.");
        BumpAndBroadcast();
    }

    private IReadOnlyList<byte> GetActiveRoleSlots()
    {
        var connected = _server.GetConnectedSlots();
        if (connected.Count > 0)
            return connected;

        return _assignedRoleSlots.OrderBy(slot => slot).ToArray();
    }

    private int CountConnectedRole(HideSeekRole role, IReadOnlyList<byte> roleSlots)
    {
        var count = 0;
        foreach (var slot in roleSlots)
        {
            if (_state.GetRole(slot) == (byte)role)
                ++count;
        }

        return count;
    }

    private void BumpAndBroadcast()
    {
        SyncGraceFieldsFromClock();
        _state.Seq++;
        _server.BroadcastGameModeState(_state);
    }
}

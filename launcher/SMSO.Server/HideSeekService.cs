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

    private readonly GameServer _server;
    private GameModeStatePacket _state = GameModeStatePacket.CreateDefault();
    private readonly Dictionary<(byte seeker, byte hider), long> _tagCooldowns = new();
    private readonly Dictionary<byte, bool> _hiderDeathWasActive = new();
    private readonly HashSet<byte> _assignedRoleSlots = new();
    private byte _tagEventId;
    private uint _tagElapsedMs;
    private long _tagSegmentStartTick;

    public HideSeekService(GameServer server) => _server = server;

    public GameModeStatePacket CurrentState => _state.Clone();

    public void Reset()
    {
        _state = GameModeStatePacket.CreateDefault();
        _tagCooldowns.Clear();
        _hiderDeathWasActive.Clear();
        _assignedRoleSlots.Clear();
        _tagEventId = 0;
        _tagElapsedMs = 0;
        _tagSegmentStartTick = 0;
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

    public void SetGameMode(GameMode mode)
    {
        _state.GameMode = mode;
        if (mode == GameMode.Normal)
        {
            _state.Flags = GameModeFlags.None;
            _state.RoundStartMs = 0;
            _state.LastTaggedSlot = 0xFF;
            for (int i = 0; i < _state.Roles.Length; i++)
                _state.Roles[i] = HideSeekRole.Hider;
            _tagCooldowns.Clear();
            _hiderDeathWasActive.Clear();
            _assignedRoleSlots.Clear();
            _tagEventId = 0;
            _tagElapsedMs = 0;
            _tagSegmentStartTick = 0;
        }

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
        BeginTagSegment();
        BumpAndBroadcast();
        _server.LogMessage(_tagElapsedMs > 0
            ? "Hide & Seek tag resumed."
            : "Hide & Seek tag started.");
        return true;
    }

    public void StopTag()
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        AccumulateTagElapsed();
        _state.Flags &= ~(GameModeFlags.TagActive | GameModeFlags.RoundComplete | GameModeFlags.RoundFanfare);
        _state.RoundStartMs = _tagElapsedMs;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _tagCooldowns.Clear();
        _hiderDeathWasActive.Clear();
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
        _state.Flags &= ~(GameModeFlags.TagActive | GameModeFlags.RoundComplete | GameModeFlags.RoundFanfare);
        _state.RoundStartMs = 0;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _tagEventId = 0;
        _tagCooldowns.Clear();
        _hiderDeathWasActive.Clear();
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

        if (_state.GetRole(seekerSlot) != (byte)HideSeekRole.Seeker)
            return;
        if (_state.GetRole(hiderSlot) != (byte)HideSeekRole.Hider)
            return;
        if (seekerSnap.Connected == 0 || hiderSnap.Connected == 0)
            return;
        if (seekerSnap.StageId != hiderSnap.StageId)
            return;

        var seekerEpisode = LevelCatalog.NormalizeEpisodeFromGame(seekerSnap.StageId, seekerSnap.EpisodeId);
        var hiderEpisode = LevelCatalog.NormalizeEpisodeFromGame(hiderSnap.StageId, hiderSnap.EpisodeId);
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
        {
            _state.Flags &= ~GameModeFlags.TagActive;
            _state.Flags |= GameModeFlags.RoundComplete;
        }

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

    public void OnPlayerDisconnected(byte slot)
    {
        _assignedRoleSlots.Remove(slot);
        _hiderDeathWasActive.Remove(slot);

        var staleCooldowns = new List<(byte seeker, byte hider)>();
        foreach (var key in _tagCooldowns.Keys)
        {
            if (key.seeker == slot || key.hider == slot)
                staleCooldowns.Add(key);
        }

        foreach (var key in staleCooldowns)
            _tagCooldowns.Remove(key);

        if (_state.GameMode != GameMode.HideSeek)
            return;

        if (!_state.TagActive)
            return;

        if (CountConnectedRole(HideSeekRole.Hider, GetActiveRoleSlots()) == 0)
        {
            _server.LogMessage("Hide & Seek round complete — all hiders left.");
            ResetTag(playRoundFanfare: true);
            return;
        }

        _server.LogMessage($"Player slot {slot} left during Hide & Seek — tag continues.");
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
        _state.Seq++;
        _server.BroadcastGameModeState(_state);
    }
}

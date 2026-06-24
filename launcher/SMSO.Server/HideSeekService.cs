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
    private readonly HashSet<byte> _assignedRoleSlots = new();
    private byte _tagEventId;

    public HideSeekService(GameServer server) => _server = server;

    public GameModeStatePacket CurrentState => _state.Clone();

    public void Reset()
    {
        _state = GameModeStatePacket.CreateDefault();
        _tagCooldowns.Clear();
        _assignedRoleSlots.Clear();
        _tagEventId = 0;
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
            _assignedRoleSlots.Clear();
            _tagEventId = 0;
        }

        BumpAndBroadcast();
    }

    public void SetRoles(IReadOnlyDictionary<byte, HideSeekRole> roles)
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        var changed = false;
        foreach (var (slot, role) in roles)
        {
            _assignedRoleSlots.Add(slot);
            if (_state.GetRole(slot) == (byte)role)
                continue;

            _state.SetRole(slot, role);
            changed = true;
        }

        if (!changed)
            return;

        if (_state.TagActive)
        {
            _state.Flags &= ~GameModeFlags.TagActive;
            _state.RoundStartMs = 0;
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
        _state.RoundStartMs = (uint)Environment.TickCount64;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _tagEventId = 0;
        _tagCooldowns.Clear();
        BumpAndBroadcast();
        _server.LogMessage("Hide & Seek tag started.");
        return true;
    }

    public void StopTag()
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        _state.Flags &= ~(GameModeFlags.TagActive | GameModeFlags.RoundComplete | GameModeFlags.RoundFanfare);
        _state.RoundStartMs = 0;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _tagCooldowns.Clear();
        BumpAndBroadcast();
        _server.LogMessage("Hide & Seek tag stopped.");
    }

    public void ResetTag(bool playRoundFanfare = false)
    {
        if (_state.GameMode != GameMode.HideSeek)
            return;

        foreach (var slot in GetActiveRoleSlots())
            _state.SetRole(slot, HideSeekRole.Hider);

        _state.Flags &= ~(GameModeFlags.TagActive | GameModeFlags.RoundComplete | GameModeFlags.RoundFanfare);
        _state.RoundStartMs = 0;
        _state.LastTaggedSlot = 0xFF;
        _state.TagEventId = 0;
        _tagEventId = 0;
        _tagCooldowns.Clear();
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
        ApplyTag(hiderSlot, seekerSlot);
    }

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

    private void ApplyTag(byte hiderSlot, byte seekerSlot)
    {
        _state.SetRole(hiderSlot, HideSeekRole.Seeker);
        _state.LastTaggedSlot = hiderSlot;
        _tagEventId++;
        if (_tagEventId == 0)
            _tagEventId = 1;
        _state.TagEventId = _tagEventId;

        var roundComplete = CountConnectedRole(HideSeekRole.Hider, GetActiveRoleSlots()) == 0;
        if (roundComplete)
        {
            _state.Flags &= ~GameModeFlags.TagActive;
            _state.Flags |= GameModeFlags.RoundComplete;
        }

        BumpAndBroadcast();
        _server.LogMessage($"Tagged slot {hiderSlot} (by seeker {seekerSlot}).");

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

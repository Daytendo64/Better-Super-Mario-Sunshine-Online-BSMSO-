namespace SMSO.Net;

/// <summary>
/// Per-remote display smoothing: position/velocity lerped with render delay.
/// Animation id/frame are time-synced between packets (lerp + rate extrapolation).
/// Call <see cref="PushPacket"/> on UDP receive and <see cref="Advance"/> every bridge tick (~60 Hz).
/// </summary>
public sealed class RemoteInterpolation
{
    private readonly object _lock = new();
    private readonly Dictionary<byte, InterpState> _states = new();
    private const float SnapDistance = 4.0f;
    private const int RenderDelayMs = 20;
    private const int MaxExtrapolateMs = 150;
    private const float ExtrapolateVelocityScale = 0.92f;
    private const float MaxAnimExtrapolateFrames = 4.0f;
    private const ushort AnimTurn = 0xBC;
    private const ushort AnimTurnEnd = 0xBD;
    private const ushort AnimSpinJump = 0xF4;
    private const ushort AnimSideFlipAir = 0xBF;
    private const ushort AnimSideFlipLand = 0xBE;

    private sealed class InterpState
    {
        public Vec3 DisplayPosition;
        public Vec3 DisplayVelocity;
        public float DisplayRotationY;
        public float DisplayAnimFrameF;
        public ushort DisplayAnimId;
        public PlayerSnapshot LastRaw;
        public PlayerSnapshot PreviousRaw;
        public long LastPacketMs;
        public long PreviousPacketMs;
        public long LastAdvanceMs;
        public bool HasPrevious;
        public bool HasDisplay;
    }

    /// <summary>Record a new network sample for a remote slot.</summary>
    public void PushPacket(byte slot, in PlayerSnapshot incoming)
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            var packet = incoming;
            packet.Slot = slot;
            if (packet.Connected == 0)
                packet.Connected = 1;

            if (!_states.TryGetValue(slot, out var state))
            {
                _states[slot] = CreateState(packet, now);
                return;
            }

            var animChanged = state.LastRaw.AnimId != packet.AnimId;
            var isNew = IsNewPacket(state.LastRaw, packet);
            if (isNew && state.HasPrevious)
                state.PreviousRaw = state.LastRaw;

            if (isNew)
                state.PreviousPacketMs = state.LastPacketMs;

            state.LastPacketMs = now;
            state.LastRaw = packet;
            state.HasPrevious = state.HasPrevious || isNew;

            var dx = packet.Position.X - state.DisplayPosition.X;
            var dy = packet.Position.Y - state.DisplayPosition.Y;
            var dz = packet.Position.Z - state.DisplayPosition.Z;
            var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!state.HasDisplay || dist > SnapDistance || animChanged)
            {
                state.DisplayPosition = packet.Position;
                state.DisplayVelocity = packet.Velocity;
                state.DisplayRotationY = packet.RotationY;
                state.DisplayAnimFrameF = packet.AnimFrame;
                state.DisplayAnimId = packet.AnimId;
                state.HasDisplay = true;
            }
            else if (isNew)
            {
                state.DisplayAnimFrameF = packet.AnimFrame;
                state.DisplayAnimId = packet.AnimId;
                if (ShouldSnapRotation(packet.AnimId))
                    state.DisplayRotationY = packet.RotationY;
            }
        }
    }

    /// <summary>Advance display state to the current time and return a smoothed snapshot.</summary>
    public PlayerSnapshot Advance(byte slot)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(slot, out var state))
                return default;

            var now = Environment.TickCount64;
            AdvanceDisplay(state, now);
            state.LastAdvanceMs = now;
            return BuildDisplaySnapshot(state);
        }
    }

    public bool HasSlot(byte slot)
    {
        lock (_lock)
            return _states.ContainsKey(slot);
    }

    public void Remove(byte slot)
    {
        lock (_lock)
            _states.Remove(slot);
    }

    public void Clear()
    {
        lock (_lock)
            _states.Clear();
    }

    private static void AdvanceDisplay(InterpState state, long now)
    {
        if (!state.HasDisplay)
        {
            state.DisplayPosition = state.LastRaw.Position;
            state.DisplayVelocity = state.LastRaw.Velocity;
            state.DisplayRotationY = state.LastRaw.RotationY;
            state.DisplayAnimFrameF = state.LastRaw.AnimFrame;
            state.DisplayAnimId = state.LastRaw.AnimId;
            state.HasDisplay = true;
            state.LastAdvanceMs = now;
        }

        var renderTime = now - RenderDelayMs;
        if (state.HasPrevious && state.LastPacketMs >= state.PreviousPacketMs)
        {
            var span = Math.Max(1L, state.LastPacketMs - state.PreviousPacketMs);
            var t = Math.Clamp((renderTime - state.PreviousPacketMs) / (float)span, 0f, 1f);
            state.DisplayPosition = new Vec3
            {
                X = Lerp(state.PreviousRaw.Position.X, state.LastRaw.Position.X, t),
                Y = Lerp(state.PreviousRaw.Position.Y, state.LastRaw.Position.Y, t),
                Z = Lerp(state.PreviousRaw.Position.Z, state.LastRaw.Position.Z, t),
            };
            state.DisplayVelocity = new Vec3
            {
                X = Lerp(state.PreviousRaw.Velocity.X, state.LastRaw.Velocity.X, t),
                Y = Lerp(state.PreviousRaw.Velocity.Y, state.LastRaw.Velocity.Y, t),
                Z = Lerp(state.PreviousRaw.Velocity.Z, state.LastRaw.Velocity.Z, t),
            };

            AdvanceAnimDisplay(state, now, interpolateBetweenPackets: true);

            if (ShouldSnapRotation(state.LastRaw.AnimId))
                state.DisplayRotationY = state.LastRaw.RotationY;
            else
                state.DisplayRotationY = Lerp(state.PreviousRaw.RotationY, state.LastRaw.RotationY, t);
        }
        else
        {
            var elapsedMs = Math.Min(now - state.LastPacketMs, MaxExtrapolateMs);
            var extrapolate = elapsedMs / 1000f;
            state.DisplayPosition = new Vec3
            {
                X = state.LastRaw.Position.X + state.LastRaw.Velocity.X * extrapolate * ExtrapolateVelocityScale,
                Y = state.LastRaw.Position.Y + state.LastRaw.Velocity.Y * extrapolate * ExtrapolateVelocityScale,
                Z = state.LastRaw.Position.Z + state.LastRaw.Velocity.Z * extrapolate * ExtrapolateVelocityScale,
            };
            state.DisplayVelocity = state.LastRaw.Velocity;
            state.DisplayRotationY = state.LastRaw.RotationY;
            AdvanceAnimDisplay(state, now, interpolateBetweenPackets: false);
        }
    }

    private static void AdvanceAnimDisplay(InterpState state, long now, bool interpolateBetweenPackets)
    {
        state.DisplayAnimId = state.LastRaw.AnimId;

        if (interpolateBetweenPackets &&
            state.HasPrevious &&
            state.PreviousRaw.AnimId == state.LastRaw.AnimId &&
            state.PreviousRaw.AnimFrame != state.LastRaw.AnimFrame &&
            state.LastPacketMs > state.PreviousPacketMs)
        {
            var span = state.LastPacketMs - state.PreviousPacketMs;
            var elapsed = Math.Clamp(now - state.PreviousPacketMs, 0L, span + MaxExtrapolateMs);
            var t = Math.Clamp(elapsed / (float)Math.Max(1, span), 0f, 1f);
            state.DisplayAnimFrameF = Lerp(state.PreviousRaw.AnimFrame, state.LastRaw.AnimFrame, t);
        }
        else
        {
            state.DisplayAnimFrameF = state.LastRaw.AnimFrame;
        }

        ExtrapolateAnimAhead(state, now);
    }

    private static void ExtrapolateAnimAhead(InterpState state, long now)
    {
        if (IsSpinJumpAnim(state.LastRaw.AnimId))
            return;

        var elapsedMs = Math.Max(0L, now - state.LastPacketMs);
        if (elapsedMs <= 0)
            return;

        var rate = DecodeAnimRate(state.LastRaw.PingMs);
        var extrapSeconds = Math.Min(elapsedMs, MaxExtrapolateMs) / 1000f;
        var extrapFixed = rate * extrapSeconds * 256f;
        if (extrapFixed > MaxAnimExtrapolateFrames * 256f)
            extrapFixed = MaxAnimExtrapolateFrames * 256f;

        state.DisplayAnimFrameF += extrapFixed;
    }

    private static PlayerSnapshot BuildDisplaySnapshot(InterpState state)
    {
        var result = state.LastRaw;
        result.Position = state.DisplayPosition;
        result.Velocity = state.DisplayVelocity;
        result.RotationY = state.DisplayRotationY;
        result.AnimFrame = (ushort)Math.Clamp((int)MathF.Round(state.DisplayAnimFrameF), 0, 65535);
        result.AnimId = state.DisplayAnimId;
        if (result.Connected == 0)
            result.Connected = 1;
        return result;
    }

    private static InterpState CreateState(in PlayerSnapshot incoming, long now)
    {
        return new InterpState
        {
            DisplayPosition = incoming.Position,
            DisplayVelocity = incoming.Velocity,
            DisplayRotationY = incoming.RotationY,
            DisplayAnimFrameF = incoming.AnimFrame,
            DisplayAnimId = incoming.AnimId,
            LastRaw = incoming,
            PreviousRaw = incoming,
            LastPacketMs = now,
            PreviousPacketMs = now,
            LastAdvanceMs = now,
            HasPrevious = false,
            HasDisplay = true,
        };
    }

    private static bool IsTurnAnim(ushort animId) => animId is AnimTurn or AnimTurnEnd;

    private static bool IsSideFlipAnim(ushort animId) => animId is AnimSideFlipAir or AnimSideFlipLand;

    private static bool IsSpinJumpAnim(ushort animId) => animId == AnimSpinJump;

    private static bool ShouldSnapRotation(ushort animId) =>
        IsTurnAnim(animId) || IsSpinJumpAnim(animId) || IsSideFlipAnim(animId);

    /// <summary>Decode BCK playback rate from snapshot pingMs low byte (not network latency).</summary>
    public static float DecodeAnimRate(ushort pingMs)
    {
        var rateEnc = (byte)(pingMs & 0xFF);
        return rateEnc != 0 ? rateEnc / 64f : 1f;
    }

    private static bool IsNewPacket(in PlayerSnapshot previous, in PlayerSnapshot incoming)
    {
        return previous.Position.X != incoming.Position.X ||
               previous.Position.Y != incoming.Position.Y ||
               previous.Position.Z != incoming.Position.Z ||
               previous.Velocity.X != incoming.Velocity.X ||
               previous.Velocity.Y != incoming.Velocity.Y ||
               previous.Velocity.Z != incoming.Velocity.Z ||
               previous.RotationY != incoming.RotationY ||
               previous.AnimId != incoming.AnimId ||
               previous.AnimFrame != incoming.AnimFrame ||
               previous.PingMs != incoming.PingMs ||
               previous.StageId != incoming.StageId ||
               previous.EpisodeId != incoming.EpisodeId ||
               previous.NozzleId != incoming.NozzleId ||
               previous.MovementState != incoming.MovementState ||
               previous.VfxFlags != incoming.VfxFlags ||
               previous.ActionId != incoming.ActionId ||
               previous.ActionIdHi != incoming.ActionIdHi ||
               previous.Health != incoming.Health ||
               previous.Water != incoming.Water;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

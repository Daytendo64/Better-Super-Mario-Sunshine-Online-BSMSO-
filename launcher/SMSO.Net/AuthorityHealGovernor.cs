using System;

namespace SMSO.Net;

/// <summary>
/// Phase 1 authority-first heal governor.
///
/// Server bitsets / sparse maps are the durable source of truth. Live collectible
/// deltas remain best-effort; recovery MUST come from a compact snapshot cache —
/// never from unbounded event history or an unbounded force-TCP retry storm.
///
/// Soft-death class this eliminates: force-full used to <c>ClearProgressSnapshot</c>
/// then wait for a TCP reply. When the reply was lost/coalesced, the mailbox stayed
/// empty and <c>force-progress-retry</c> looped forever (see 2026-07-20 logs: 104
/// retries with zero mailbox writes). Cache restage heals within the timeout even
/// when TCP is silent.
///
/// Build 26: cache restage uses the real <see cref="WorldProgressSnapshot.ProgressSeq"/>
/// as mailbox hostSeq (Push already zeros moduleApplied). The old 0x60000000 synthetic
/// band poisoned periodic-catchup advertise and left stage-enter TCP refresh without a
/// watchdog when the server went silent mid-run.
/// </summary>
public sealed class AuthorityHealGovernor
{
    public const int MaxTcpForceAttempts = 5;
    public static readonly TimeSpan ForceReplyTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Informational max age for telemetry / docs only. Clear-vs-restage decisions must
    /// NOT use this — a stale cache is still safer than emptying the mailbox.
    /// </summary>
    public static readonly TimeSpan AuthorityCacheMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Legacy synthetic mailbox hostSeq band (builds 18–25). Still recognized so a hot
    /// mailbox / moduleApplied stuck in-band is never advertised to the server.
    /// </summary>
    public const uint CacheHealHostSeqBase = 0x60000000u;

    /// <summary>
    /// Heal event ids live at/above this; legacy cache band stays below it.
    /// </summary>
    public static uint CacheHealHostSeqEndExclusive => WorldProgressSnapshot.HealEventIdBase;

    private readonly object _gate = new();
    private WorldProgressSnapshot? _cachedAuthority;
    private DateTime _cachedUtc = DateTime.MinValue;
    private int _tcpForceAttempts;
    private DateTime _awaitingSinceUtc = DateTime.MinValue;
    private DateTime _circuitOpenUntilUtc = DateTime.MinValue;
    private AuthorityHealState _state = AuthorityHealState.Idle;

    public AuthorityHealState State
    {
        get { lock (_gate) return _state; }
    }

    public int TcpForceAttempts
    {
        get { lock (_gate) return _tcpForceAttempts; }
    }

    /// <summary>
    /// True when any changed authority snapshot is cached (age ignored). Prefer restage
    /// over clear whenever this is true.
    /// </summary>
    public bool HasAuthorityCache()
    {
        lock (_gate)
            return HasAuthorityCacheUnlocked();
    }

    /// <summary>
    /// Back-compat alias: clear/restage uses any cache, not a freshness TTL.
    /// </summary>
    public bool HasFreshAuthorityCache(DateTime utcNow)
    {
        _ = utcNow;
        return HasAuthorityCache();
    }

    public WorldProgressSnapshot? PeekAuthorityCache()
    {
        lock (_gate)
            return _cachedAuthority;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _cachedAuthority = null;
            _cachedUtc = DateTime.MinValue;
            _tcpForceAttempts = 0;
            _awaitingSinceUtc = DateTime.MinValue;
            _circuitOpenUntilUtc = DateTime.MinValue;
            _state = AuthorityHealState.Idle;
        }
    }

    /// <summary>
    /// Store a changed server snapshot as the durable local heal source.
    /// Unchanged acks only refresh the cache timestamp (authority still current).
    /// Does NOT clear force-await — callers must <see cref="NoteForceSatisfied"/> only
    /// after a successful mailbox write or expand fallback.
    /// </summary>
    public void NoteAuthoritySnapshot(WorldProgressSnapshot snapshot, DateTime utcNow)
    {
        lock (_gate)
        {
            if (snapshot.Unchanged)
            {
                // Periodic catch-up Unchanged proves server authority is still current —
                // refresh the stamp so telemetry age does not look abandoned.
                if (_cachedAuthority is { Unchanged: false })
                    _cachedUtc = utcNow;
                return;
            }

            _cachedAuthority = snapshot;
            _cachedUtc = utcNow;
        }
    }

    /// <summary>
    /// Begin a force-full heal. Prefer restaging from cache — do NOT clear the mailbox
    /// and wait on TCP when authority is already known (even if the cache stamp is old).
    /// <para>
    /// Build 33: cache restage is the heal — do <b>not</b> arm force-await / watchdog.
    /// Build 26 re-armed await for a best-effort TCP refresh; when that reply was silent
    /// the 2s watchdog restaged forever (<c>stage-enter … seq=0 force</c> soft-death,
    /// dolphin bulk-apply every ~2s). TCP refresh stays best-effort without await.
    /// </para>
    /// </summary>
    public ForceHealPlan BeginForce(DateTime utcNow)
    {
        lock (_gate)
        {
            if (_state == AuthorityHealState.CircuitOpen)
            {
                if (utcNow < _circuitOpenUntilUtc)
                    return ForceHealPlan.CircuitBlocked();
                _state = AuthorityHealState.Idle;
                _tcpForceAttempts = 0;
            }

            if (HasAuthorityCacheUnlocked())
            {
                var hostSeq = MailboxHostSeqForCache(_cachedAuthority!);
                // Restage fills the mailbox; heal is complete. Never arm await and never
                // request a best-effort TCP refresh (build 36 — stage-enter TCP flood).
                _tcpForceAttempts = 0;
                _awaitingSinceUtc = DateTime.MinValue;
                _state = AuthorityHealState.Idle;
                return ForceHealPlan.RestageFromCache(_cachedAuthority!, hostSeq, requestTcpRefresh: false);
            }

            _tcpForceAttempts = 1;
            _awaitingSinceUtc = utcNow;
            _state = AuthorityHealState.AwaitingForce;
            // No cache yet (pre-first-heal). Clear + TCP is the only option.
            return ForceHealPlan.ClearAndRequestTcp();
        }
    }

    /// <summary>
    /// Force-reply timeout. Restage from cache when possible (and clear await — the
    /// method name is literal); otherwise retry TCP or open the circuit so we never
    /// storm forever.
    /// </summary>
    public ForceTimeoutDecision OnForceTimeout(DateTime utcNow)
    {
        lock (_gate)
        {
            if (_state != AuthorityHealState.AwaitingForce)
                return ForceTimeoutDecision.NotAwaiting();

            if (_tcpForceAttempts >= MaxTcpForceAttempts)
            {
                _state = AuthorityHealState.CircuitOpen;
                _circuitOpenUntilUtc = utcNow + CircuitCooldown;
                _awaitingSinceUtc = DateTime.MinValue;
                return ForceTimeoutDecision.OpenCircuit();
            }

            if (HasAuthorityCacheUnlocked())
            {
                var hostSeq = MailboxHostSeqForCache(_cachedAuthority!);
                // Cache restage heals ownership/mission — clear await (match the Kind name).
                // Build 36: no TCP refresh — cache is enough; seq=0 refresh flooded the lobby.
                _tcpForceAttempts = 0;
                _awaitingSinceUtc = DateTime.MinValue;
                _state = AuthorityHealState.Idle;
                return ForceTimeoutDecision.RestageFromCacheAndClearAwait(
                    _cachedAuthority!, hostSeq, requestTcpRefresh: false);
            }

            _tcpForceAttempts++;
            _awaitingSinceUtc = utcNow;
            return ForceTimeoutDecision.RetryTcp(_tcpForceAttempts);
        }
    }

    public void NoteForceSatisfied()
    {
        lock (_gate)
        {
            _tcpForceAttempts = 0;
            _awaitingSinceUtc = DateTime.MinValue;
            if (_state == AuthorityHealState.AwaitingForce)
                _state = AuthorityHealState.Idle;
        }
    }

    public bool IsAwaitingForce
    {
        get { lock (_gate) return _state == AuthorityHealState.AwaitingForce; }
    }

    public bool ForceReplyTimedOut(DateTime utcNow)
    {
        lock (_gate)
        {
            return _state == AuthorityHealState.AwaitingForce &&
                   _awaitingSinceUtc != DateTime.MinValue &&
                   utcNow - _awaitingSinceUtc >= ForceReplyTimeout;
        }
    }

    /// <summary>
    /// True when force-full may clear the mailbox before TCP. Only safe when there is
    /// no authority cache to restage — otherwise clear creates an empty soft-death window.
    /// Age of the cache does not matter: restage always beats clear.
    /// </summary>
    public static bool ShouldClearMailboxBeforeForceTcp(bool hasAuthorityCache)
        => !hasAuthorityCache;

    /// <summary>
    /// Unchanged acks never satisfy a force await (mailbox was cleared / needs a body).
    /// </summary>
    public static bool ClearsForceProgressAwait(bool snapshotUnchanged)
        => !snapshotUnchanged;

    /// <summary>
    /// Legacy synthetic cache-heal hostSeq band (builds 18–25). Never advertise these to
    /// the server as a client progress proof seq.
    /// </summary>
    public static bool IsCacheHealHostSeq(uint hostSeq)
        => hostSeq >= CacheHealHostSeqBase && hostSeq < CacheHealHostSeqEndExclusive;

    /// <summary>
    /// Mailbox hostSeq for a cache restage. Uses the real authority ProgressSeq —
    /// <c>PushProgressSnapshot</c> already stages moduleApplied=0 so same-seq reheal works.
    /// </summary>
    public static uint MailboxHostSeqForCache(WorldProgressSnapshot snapshot)
        => snapshot.ProgressSeq == 0 ? 1u : snapshot.ProgressSeq;

    private bool HasAuthorityCacheUnlocked()
        => _cachedAuthority is { Unchanged: false };
}

public enum AuthorityHealState : byte
{
    Idle = 0,
    AwaitingForce = 1,
    CircuitOpen = 2,
}

public readonly struct ForceHealPlan
{
    public enum Kind : byte
    {
        ClearAndRequestTcp = 0,
        RestageFromCache = 1,
        CircuitBlocked = 2,
    }

    public Kind Action { get; init; }
    public WorldProgressSnapshot? Snapshot { get; init; }
    public uint HostSeq { get; init; }
    public bool RequestTcpRefresh { get; init; }

    public static ForceHealPlan ClearAndRequestTcp() => new()
    {
        Action = Kind.ClearAndRequestTcp,
        RequestTcpRefresh = true,
    };

    public static ForceHealPlan RestageFromCache(WorldProgressSnapshot snapshot, uint hostSeq,
        bool requestTcpRefresh) => new()
    {
        Action = Kind.RestageFromCache,
        Snapshot = snapshot,
        HostSeq = hostSeq,
        RequestTcpRefresh = requestTcpRefresh,
    };

    public static ForceHealPlan CircuitBlocked() => new()
    {
        Action = Kind.CircuitBlocked,
        RequestTcpRefresh = false,
    };
}

public readonly struct ForceTimeoutDecision
{
    public enum Kind : byte
    {
        NotAwaiting = 0,
        RestageFromCacheAndClearAwait = 1,
        RetryTcp = 2,
        OpenCircuit = 3,
    }

    public Kind Action { get; init; }
    public WorldProgressSnapshot? Snapshot { get; init; }
    public uint HostSeq { get; init; }
    public bool RequestTcpRefresh { get; init; }
    public int Attempt { get; init; }

    public static ForceTimeoutDecision NotAwaiting() => new() { Action = Kind.NotAwaiting };

    public static ForceTimeoutDecision RestageFromCacheAndClearAwait(
        WorldProgressSnapshot snapshot, uint hostSeq, bool requestTcpRefresh) => new()
    {
        Action = Kind.RestageFromCacheAndClearAwait,
        Snapshot = snapshot,
        HostSeq = hostSeq,
        RequestTcpRefresh = requestTcpRefresh,
    };

    public static ForceTimeoutDecision RetryTcp(int attempt) => new()
    {
        Action = Kind.RetryTcp,
        RequestTcpRefresh = true,
        Attempt = attempt,
    };

    public static ForceTimeoutDecision OpenCircuit() => new()
    {
        Action = Kind.OpenCircuit,
    };
}

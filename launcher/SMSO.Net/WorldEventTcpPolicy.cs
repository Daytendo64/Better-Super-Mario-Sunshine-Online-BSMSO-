namespace SMSO.Net;

/// <summary>
/// Phase A TCP durable-only policy. TCP carries ownership/progress snapshots and
/// session-control events only — never ephemeral VFX/object spam.
/// </summary>
public static class WorldEventTcpPolicy
{
    /// <summary>
    /// Card ownership mutations. Remote apply is via coalesced
    /// <see cref="WorldProgressSnapshot"/> push (~100–150 ms); live WorldEvent fanout
    /// is optional and must not share queues with ephemeral traffic.
    /// </summary>
    public static bool IsSnapshotOwnership(WorldEventType type) => type is
        WorldEventType.ShineCollected or
        WorldEventType.BlueCoinCollected or
        WorldEventType.StoryFlag or
        WorldEventType.TriggerFlag or
        WorldEventType.SecretComplete or
        WorldEventType.EpisodeComplete;

    /// <summary>
    /// Stage-scoped mission masks. Coalesce into snapshot / stage-enter heal —
    /// never per-coin TCP storms.
    /// </summary>
    public static bool IsSnapshotMission(WorldEventType type) => type is
        WorldEventType.RedCoinCollected or
        WorldEventType.NpcCleaned;

    /// <summary>
    /// Must still live-broadcast on TCP (host session wipe). Not snapshot-healed.
    /// </summary>
    public static bool RequiresLiveTcpFanout(WorldEventType type) =>
        type == WorldEventType.SessionProgressReset;

    /// <summary>
    /// Never network (TCP or UDP) in Phase A. Chosen for 120-shine reliability —
    /// fruit / NPC react / hip-drop / gold are cosmetic and historically flooded
    /// mission localPending + TCP DropOldest under 10p.
    /// </summary>
    public static bool IsNonNetworkedEphemeral(WorldEventType type) => type is
        WorldEventType.GoldCoinCollected or
        WorldEventType.HipDropObject or
        WorldEventType.YoshiFruitTaken or
        WorldEventType.MarioFruitKicked or
        WorldEventType.MarioFruitPicked or
        WorldEventType.MarioFruitThrown or
        WorldEventType.MarioFruitDropped or
        WorldEventType.MarioFruitSync or
        WorldEventType.NpcReact or
        WorldEventType.GraffitiCleaned;

    /// <summary>
    /// Client→server WorldEvent requests the server will accept (authority mutation path).
    /// Ephemeral types are dropped at the edge.
    /// </summary>
    public static bool AcceptsClientWorldEvent(WorldEventType type) =>
        IsSnapshotOwnership(type) ||
        IsSnapshotMission(type) ||
        RequiresLiveTcpFanout(type);

    /// <summary>
    /// Outbound launcher filter: only send durable collectible / flag events.
    /// </summary>
    public static bool ShouldSendLocalWorldEvent(WorldEventType type) =>
        AcceptsClientWorldEvent(type) && !RequiresLiveTcpFanout(type);
}

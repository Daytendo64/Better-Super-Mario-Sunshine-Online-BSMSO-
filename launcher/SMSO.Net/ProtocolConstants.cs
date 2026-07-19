namespace SMSO.Net;

public static class ProtocolConstants
{
    public const uint Magic = 0x534D534F;
    // v2 adds coalesced UDP SnapshotBatch server fanout.
    public const ushort ProtocolVersion = 2;
    public const ushort CommVersion = 11;
    /// <summary>
    /// Release build gate for multiplayer. Bump on every zip build so mismatched
    /// clients are rejected with <see cref="JoinRejectReason.VersionMismatch"/>.
    /// Independent of <see cref="CommVersion"/> / <see cref="ProtocolVersion"/>.
    /// </summary>
    public const ushort ModBuildId = 5;
    public const int DefaultPort = 27015;
    public const int StableMaxPlayers = 10;
    public const int MaxPlayers = 10;
    public const int MaxRemoteSlots = 10;
    public const int MarioModelIdSize = 8;
    public const int MarioModelIntentSize = 4 + MarioModelIdSize;
    public const int MarioVoiceEventSize = 12;
    public const int CommMarioVoiceEventsOffset = 862;
    public const int CommMarioVoiceEventsSize = MarioVoiceEventSize * (MaxRemoteSlots + 1);
    // mode+flags+localRole+lastTagged+tagEventId+roundStartMs(4)+roleBySlot[N]+graceRemainingMs(2)
    public const int CommGameModeStateSize = 11 + MaxPlayers;
    public const int CommGameModeStateOffset = CommMarioVoiceEventsOffset + CommMarioVoiceEventsSize;
    public const int CommWorldEventSize = 19;
    public const int CommWorldSyncSize = CommWorldEventSize * 2 + 4;
    public const int CommWorldSyncOffset = CommGameModeStateOffset + CommGameModeStateSize;
    public const int CommIncomingWorldEventOffset = CommWorldSyncOffset + CommWorldEventSize;
    public const int CommRosterHudEventSize = 20;
    // One slot per player so a full-lobby connect/disconnect wave cannot overwrite unread HUD events.
    public const int CommRosterHudRingSlots = MaxPlayers;
    public const int CommRosterHudSyncSize = 2 + CommRosterHudEventSize * CommRosterHudRingSlots;
    public const int CommRosterHudOffset = CommWorldSyncOffset + CommWorldSyncSize;
    public const int CommMarioModelIdsOffset = CommRosterHudOffset + CommRosterHudSyncSize;
    public const int CommMarioModelIdsSize = MarioModelIdSize * (MaxRemoteSlots + 1);
    public const int CommBufferSize = CommMarioModelIdsOffset + CommMarioModelIdsSize;
    public const int RosterEntrySize = 30; // slot(1)+name(16)+stage(1)+ep(1)+state(1)+ping(2)+modelId(8)
    // name(16)+modelId(8)+modBuildId(2)
    public const int JoinRequestSize = 16 + MarioModelIdSize + 2;
    public const int WorldEventClientPayloadSize = 15;
    public const int WorldEventBroadcastPayloadSize = 17;
    public const int CommNameTagAppearancesOffset = 752;
    public const int CommNameTagAppearancesSize = 10 * (MaxRemoteSlots + 1);
    public const int CommBridgeControlOffset = 6;
    public const int CommBridgeControlSize = 26;
    public const int CommRemoteSnapshotsOffset = 112;
    public const int CommRemoteSnapshotsSize = PlayerSnapshotSize * MaxRemoteSlots;
    public const int UdpSnapshotPayloadOffset = 10;
    public const int PlayerSnapshotSize = 64;
    // Batched server fanout: magic(4)+id(1)+count(1), then
    // slot(1)+source sequence(4)+snapshot(64) per player.
    public const int UdpSnapshotBatchHeaderSize = 6;
    public const int UdpSnapshotBatchEntrySize = 1 + 4 + PlayerSnapshotSize;
    public const int UdpSnapshotBatchMaxSize =
        UdpSnapshotBatchHeaderSize + UdpSnapshotBatchEntrySize * StableMaxPlayers;
    public const int UdpPingPayloadSize = 8;
    // Must fit the worst-case sparse authority snapshot (collectibles + every durable
    // card bit + stage-scoped trigger bits). 32 KiB truncated the tail — exactly where
    // story flags are serialized. The frame length is ushort, so retain safe headroom.
    public const int MaxTcpPayloadSize = 60000;
    public const int WorldProgressResyncIntervalMs = 45000;
    public const uint DefaultMailboxAddress = 0x817FC000;
    public const int SnapshotRateHz = 60;
    public const int BridgePollMs = 1000 / SnapshotRateHz;
    public const int UdpSnapshotIntervalMs = 1000 / SnapshotRateHz;
    public const int HeartbeatIntervalMs = 2000;
    public const int StaleTimeoutMs = 10000;
    public const int DisconnectTimeoutMs = 15000;
    public const int ConnectTimeoutMs = 10000;
    public const int RosterBroadcastIntervalMs = 200;
    public const int ReconnectWindowMs = 30000;
    /// <summary>
    /// Unnamed handshake sessions older than this are reclaimable so abandoned
    /// TCP connects cannot permanently block joins when the lobby is at capacity.
    /// </summary>
    public const int AbandonedHandshakeGraceMs = 5000;
    public const byte WarpNoTarget = 0xFC;
    public const byte WarpAllSlots = 0xFF;
}

public enum TcpPacketId : byte
{
    Handshake = 1,
    HandshakeAck = 2,
    JoinRequest = 3,
    JoinAccepted = 4,
    JoinRejected = 5,
    RosterSnapshot = 6,
    WarpRequest = 7,
    WarpCommand = 8,
    SyncSettings = 9,
    WorldEvent = 10,
    Disconnect = 11,
    Heartbeat = 12,
    PlayerLeft = 13,
    UdpRegister = 14,
    MarioVoiceEvent = 15,
    ClientTeleportSettings = 16,
    GameModeState = 17,
    WorldStateReplay = 18,
    /// <summary>Client asks server to rebroadcast authoritative collectible state.</summary>
    WorldProgressRequest = 19,
    /// <summary>
    /// Client immediately announces its desired Mario model. Unknown TCP ids are
    /// ignored by v2 peers, so heartbeat advertisement remains a safe fallback
    /// without changing protocol or CommBuffer versions.
    /// </summary>
    MarioModelIntent = 20,
}

public enum UdpPacketId : byte
{
    PlayerSnapshot = 20,
    SnapshotBatch = 21,
    Ping = 22,
    Pong = 23,
}

public enum JoinRejectReason : byte
{
    None = 0,
    NameTaken = 1,
    Full = 2,
    InvalidName = 3,
    VersionMismatch = 4,
}

public enum DisconnectReason : byte
{
    UserRequest = 0,
    Timeout = 1,
    Kicked = 2,
    ServerShutdown = 3,
    DolphinClosed = 4,
}

[Flags]
public enum BridgeFlags : uint
{
    Connected = 1 << 0,
    Host = 1 << 1,
    WarpPending = 1 << 2,
    Loading = 1 << 3,
    SyncShine = 1 << 4,
    SyncBlueCoin = 1 << 5,
    SyncEvent = 1 << 6,
    SyncStory = 1 << 7,
    SyncMission = 1 << 8,
    SyncSecret = 1 << 9,
    SyncObjects = 1 << 10,
    SyncProgress = 1 << 11,
    /// Module requests an immediate WorldProgress snapshot (e.g. co-op same-stage death reload).
    RequestProgress = 1 << 12,
    WarpToPoint = 1 << 13,
    WarpAll = 1 << 14,
}

public enum DolphinState : byte
{
    None = 0,
    Booting = 1,
    Loading = 2,
    Active = 3,
    Warping = 4,
}

[Flags]
public enum VfxFlags : ushort
{
    WaterSpray = 1 << 0,
    Hover = 1 << 1,
    Rocket = 1 << 2,
    Turbo = 1 << 3,
    Dead = 1 << 4,
    FluddEmpty = 1 << 5, // spray trigger held with empty tank (dry pump)
    YCam = 1 << 6,
    NozzleSwitching = 1 << 7,
    WetSlide = 1 << 8,
    NoFludd = 1 << 9, // FLUDD pack hidden on Mario's back (stolen / on Yoshi)
    YoshiFruitMouth = 1 << 10, // fruit actor encode (1..7) in vfx bits 11..13
}

public enum RosterHudEventKind : byte
{
    None = 0,
    Connected = 1,
    Disconnected = 2,
}

public enum WorldEventType : byte
{
    ShineCollected = 1,
    BlueCoinCollected = 2,
    EpisodeComplete = 3,
    StoryFlag = 4,
    TriggerFlag = 5,
    SecretComplete = 6,
    GoldCoinCollected = 7,
    HipDropObject = 8,
    RedCoinCollected = 9,
    YoshiFruitTaken = 10,
    MarioFruitKicked = 11,
    MarioFruitPicked = 12,
    MarioFruitThrown = 13,
    MarioFruitDropped = 14,
    MarioFruitSync = 15,
    NpcReact = 16,
    NpcCleaned = 17,
    GraffitiCleaned = 18,
}

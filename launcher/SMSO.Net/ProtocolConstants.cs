namespace SMSO.Net;

public static class ProtocolConstants
{
    public const uint Magic = 0x534D534F;
    public const ushort ProtocolVersion = 1;
    public const ushort CommVersion = 4;
    public const int DefaultPort = 27015;
    public const int StableMaxPlayers = 4;
    public const int MaxPlayers = 10;
    public const int MaxRemoteSlots = 9;
    public const int MarioVoiceEventSize = 12;
    public const int CommMarioVoiceEventsOffset = 788;
    public const int CommMarioVoiceEventsSize = MarioVoiceEventSize * MaxPlayers;
    public const int CommGameModeStateSize = 13;
    public const int CommGameModeStateOffset = CommMarioVoiceEventsOffset + CommMarioVoiceEventsSize;
    public const int CommBufferSize = CommGameModeStateOffset + CommGameModeStateSize;
    public const int CommNameTagAppearancesOffset = 688;
    public const int CommNameTagAppearancesSize = 100;
    public const int CommBridgeControlOffset = 6;
    public const int CommBridgeControlSize = 26;
    public const int CommRemoteSnapshotsOffset = 112;
    public const int CommRemoteSnapshotsSize = 576;
    public const int UdpSnapshotPayloadOffset = 10;
    public const int PlayerSnapshotSize = 64;
    public const int MaxTcpPayloadSize = 4096;
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
    SkipEntryDemo = 1 << 12,
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
}

public enum WorldEventType : byte
{
    ShineCollected = 1,
    BlueCoinCollected = 2,
    EpisodeComplete = 3,
    StoryFlag = 4,
    TriggerFlag = 5,
    SecretComplete = 6,
}

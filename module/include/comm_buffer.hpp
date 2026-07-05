#pragma once

#include <Dolphin/mem.h>
#include <Dolphin/types.h>
#include <stddef.h>

namespace smso {

constexpr u32 COMM_MAGIC = 0x534D534F; // "SMSO"
constexpr u16 COMM_VERSION = 7;
// Legacy scan hint for the launcher; the live buffer lives in module BSS.
constexpr u32 COMM_GUEST_ADDRESS = 0x817FC000;
// Maximum connected players (slots 0..MAX_PLAYERS-1). roleBySlot and roster logic key off this.
constexpr u32 MAX_PLAYERS = 10;
// Remote snapshot slots are indexed directly by network slot id, so the array must hold every
// possible slot index (0..MAX_PLAYERS-1). Keep >= MAX_PLAYERS.
constexpr u32 MAX_REMOTE_SLOTS = MAX_PLAYERS;
constexpr u32 MARIO_VOICE_EVENT_SIZE = 12;
constexpr u32 COMM_MARIO_VOICE_EVENTS_OFFSET = 862;
constexpr u32 COMM_MARIO_VOICE_EVENTS_SIZE = MARIO_VOICE_EVENT_SIZE * (MAX_REMOTE_SLOTS + 1);
constexpr u32 PLAYER_SNAPSHOT_SIZE = 64;
constexpr u32 MAX_PLAYER_NAME = 16;
enum GameMode : u8 {
    GM_NORMAL = 0,
    GM_HIDE_SEEK = 1,
};

enum HideSeekRole : u8 {
    HSR_HIDER = 0,
    HSR_SEEKER = 1,
};

enum GameModeFlags : u8 {
    GMF_TAG_ACTIVE = 1 << 0,
    GMF_ROUND_COMPLETE = 1 << 1,
    GMF_TIMER_RESET = 1 << 2,
    GMF_ROUND_FANFARE = 1 << 3,
};

// GameModeState: mode(1)+flags(1)+localRole(1)+lastTaggedSlot(1)+tagEventId(1)+roundStartMs(4)
// + roleBySlot[MAX_PLAYERS](10) = 19 bytes.
constexpr u32 COMM_GAME_MODE_STATE_SIZE = 9 + MAX_PLAYERS;
constexpr u32 COMM_GAME_MODE_STATE_OFFSET = COMM_MARIO_VOICE_EVENTS_OFFSET + COMM_MARIO_VOICE_EVENTS_SIZE;
constexpr u32 COMM_WORLD_EVENT_SIZE = 15;
constexpr u32 COMM_WORLD_SYNC_SIZE = COMM_WORLD_EVENT_SIZE * 2 + 4;
constexpr u32 COMM_WORLD_SYNC_OFFSET = COMM_GAME_MODE_STATE_OFFSET + COMM_GAME_MODE_STATE_SIZE;
constexpr u32 COMM_ROSTER_HUD_EVENT_SIZE = 20;
constexpr u32 COMM_ROSTER_HUD_RING_SLOTS = 8;
constexpr u32 COMM_ROSTER_HUD_SYNC_SIZE = 2 + COMM_ROSTER_HUD_RING_SLOTS * COMM_ROSTER_HUD_EVENT_SIZE;
constexpr u32 COMM_ROSTER_HUD_OFFSET = COMM_WORLD_SYNC_OFFSET + COMM_WORLD_SYNC_SIZE;
constexpr u32 COMM_BUFFER_SIZE = COMM_ROSTER_HUD_OFFSET + COMM_ROSTER_HUD_SYNC_SIZE;
// localNameTagAppearance starts right after remoteSnapshots[10] (48 + 64 + 64*10 = 752).
constexpr u32 COMM_NAME_TAG_APPEARANCES_OFFSET = 752;
// local(10) + remote[10](100) = 110.
constexpr u32 COMM_NAME_TAG_APPEARANCES_SIZE = 10 + 10 * MAX_REMOTE_SLOTS;
constexpr u32 COMM_REMOTE_SNAPSHOTS_OFFSET = 112;
constexpr u32 COMM_REMOTE_SNAPSHOTS_SIZE = PLAYER_SNAPSHOT_SIZE * MAX_REMOTE_SLOTS;

enum BridgeFlags : u32 {
    BF_CONNECTED = 1 << 0,
    BF_HOST = 1 << 1,
    BF_WARP_PENDING = 1 << 2,
    BF_LOADING = 1 << 3,
    BF_SYNC_SHINE = 1 << 4,
    BF_SYNC_BLUE_COIN = 1 << 5,
    BF_SYNC_EVENT = 1 << 6,
    BF_SYNC_STORY = 1 << 7,
    BF_SYNC_MISSION = 1 << 8,
    BF_SYNC_SECRET = 1 << 9,
    BF_SYNC_OBJECTS = 1 << 10,
    BF_SYNC_PROGRESS = 1 << 11,
    BF_WARP_TO_POINT = 1 << 13,   // apply warpPos* after stage load (or immediately if same stage)
    BF_WARP_ALL = 1 << 14,        // warp command explicitly targets every connected slot
};

constexpr u8 WARP_NO_TARGET = 0xFC;
constexpr u8 WARP_ALL_SLOTS = 0xFF;

enum DolphinState : u8 {
    DS_NONE = 0,
    DS_BOOTING = 1,
    DS_LOADING = 2,
    DS_ACTIVE = 3,
    DS_WARPING = 4,
};

enum VfxFlags : u16 {
    VFX_WATER_SPRAY = 1 << 0,
    VFX_HOVER = 1 << 1,
    VFX_ROCKET = 1 << 2,
    VFX_TURBO = 1 << 3,
    VFX_DEAD = 1 << 4,
    VFX_FLUDD_EMPTY = 1 << 5, // spray trigger held with empty tank (dry pump)
    VFX_Y_CAM = 1 << 6, // L-button free-look camera (hold-pump + helmet pose)
    VFX_NOZZLE_SWITCHING = 1 << 7, // host mSwitchToSecondNozzleSpeed != 0
    VFX_WET_SLIDE = 1 << 8,        // belly CATCH slide in water / on wet ground
    VFX_NO_FLUDD = 1 << 9,         // FLUDD pack hidden on Mario's back (see shouldShowFluddPackOnMario)
    // Host riding Yoshi with a fruit in TYoshiTongue::mActorTypeInMouth — episodeId is fruit encode.
    VFX_YOSHI_FRUIT_MOUTH = 1 << 10,
};

// Bits 8-9 are persistent VFX flags. Bits 10-15 pack Y-cam pitch, active-spray FLUDD gun
// angle (mGunAngle), or run waist roll so auxiliary angle data never clobbers VFX_WET_SLIDE
// or VFX_NO_FLUDD during movement.
constexpr u16 kVfxPersistentHighMask = static_cast<u16>(VFX_WET_SLIDE | VFX_NO_FLUDD);
constexpr u16 kVfxAuxAngleShift = 10;

// pingMs high byte and vfxFlags bits 10-15 pack s16 angles into 0..63.
// Aux-bit priority on the wire: VFX_Y_CAM L-button pitch > active-spray (VFX_WATER_SPRAY /
// VFX_FLUDD_EMPTY) FLUDD gun angle > run/ride-shell/blooper waist roll. The gun angle is what
// retail MarioHeadCtrl/MarioWaistCtrl read to aim the head and chest during hover/spray;
// mGunAngle is negative while hovering (head aims down) — sync raw, no sign inversion.
// NOTE: pingMs is NOT network latency — low byte is BCK rate*64, high byte is head/waist aux.
constexpr s16 kSnapshotAngleMin = static_cast<s16>(-0x6000);
constexpr s16 kSnapshotAngleMax = static_cast<s16>(0x6000);

inline u8 encodeSnapshotAngle(s16 angle) {
    if (angle < kSnapshotAngleMin)
        angle = kSnapshotAngleMin;
    if (angle > kSnapshotAngleMax)
        angle = kSnapshotAngleMax;
    const s32 range = static_cast<s32>(kSnapshotAngleMax) - kSnapshotAngleMin;
    return static_cast<u8>(((static_cast<s32>(angle) - kSnapshotAngleMin) * 255) / range);
}

inline s16 decodeSnapshotAngle(u8 enc) {
    const s32 range = static_cast<s32>(kSnapshotAngleMax) - kSnapshotAngleMin;
    return static_cast<s16>((static_cast<s32>(enc) * range) / 255 + kSnapshotAngleMin);
}

inline u8 encodeSnapshotAngle6(s16 angle) {
    return static_cast<u8>(encodeSnapshotAngle(angle) >> 2);
}

inline s16 decodeSnapshotAngle6(u8 enc6) {
    return decodeSnapshotAngle(static_cast<u8>(enc6 << 2));
}

inline u16 packVfxAuxAngle(u16 vfxFlags, u8 angleEnc6) {
    return static_cast<u16>((vfxFlags & (0x00FFu | kVfxPersistentHighMask)) |
                            ((static_cast<u16>(angleEnc6) & 0x3Fu) << kVfxAuxAngleShift));
}

inline u8 unpackVfxAuxAngle(u16 vfxFlags) {
    return static_cast<u8>((vfxFlags >> kVfxAuxAngleShift) & 0x3Fu);
}

// movementState packs upper-body state (low 3 bits), FLUDD switch progress (bits 3-6),
// and switch direction (bit 7: 0=toward secondary, 1=toward spray).
constexpr u8 kFluddSwitchProgressScale = 15;

inline u8 packMovementState(u8 upperState, f32 switchProgress, f32 switchSpeed) {
    u8 progEnc = static_cast<u8>(switchProgress * static_cast<f32>(kFluddSwitchProgressScale));
    if (progEnc > kFluddSwitchProgressScale)
        progEnc = kFluddSwitchProgressScale;
    u8 packed = static_cast<u8>((progEnc << 3) | (upperState & 0x07));
    if (switchSpeed < 0.0f)
        packed |= 0x80;
    return packed;
}

inline bool unpackFluddSwitchTowardSpray(u8 packed) {
    return (packed & 0x80) != 0;
}

inline u8 unpackUpperState(u8 packed) {
    return packed & 0x07;
}

inline f32 unpackFluddSwitchProgress(u8 packed) {
    return static_cast<f32>((packed >> 3) & 0x0F) / static_cast<f32>(kFluddSwitchProgressScale);
}

// nozzleId packs current nozzle (low 4 bits) and second/target nozzle (high 4 bits).
inline u8 packNozzleIds(u8 currentNozzle, u8 secondNozzle) {
    return static_cast<u8>(((secondNozzle & 0x0F) << 4) | (currentNozzle & 0x0F));
}

inline u8 unpackCurrentNozzle(u8 packed) {
    return packed & 0x0F;
}

inline u8 unpackSecondNozzle(u8 packed) {
    return (packed >> 4) & 0x0F;
}

// health byte packs animation aux (repurposed from unused shine export):
// bits 0-1: hand pose index (changeHand 0=open, 1=mid, 2=closed)
// bits 2-5: FLUDD deploy blend 0..15 (maps to unk1CEC 0..1)
constexpr u8 kAnimAuxHandMask = 0x03;
constexpr u8 kAnimAuxDeployShift = 2;
constexpr u8 kAnimAuxDeployScale = 15;

inline u8 packAnimAux(u8 handIndex, f32 deployBlend) {
    u8 hand = handIndex > 2 ? 2 : handIndex;
    u8 deployEnc = static_cast<u8>(deployBlend * static_cast<f32>(kAnimAuxDeployScale));
    if (deployEnc > kAnimAuxDeployScale)
        deployEnc = kAnimAuxDeployScale;
    return static_cast<u8>((deployEnc << kAnimAuxDeployShift) | (hand & kAnimAuxHandMask));
}

inline u8 unpackAnimAuxHand(u8 packed) {
    return packed & kAnimAuxHandMask;
}

inline f32 unpackAnimAuxDeploy(u8 packed) {
    return static_cast<f32>((packed >> kAnimAuxDeployShift) & 0x0F) /
           static_cast<f32>(kAnimAuxDeployScale);
}

// When snapshotHostOnYoshi, health/stageId/episodeId/velocity carry TYoshiTongue sync.
// health: bits 0-1 hand, 2-4 tongue state (doldecomp TYoshiTongue::STATE_*), 5-7 progress/8.
// stageId: exact mProgress (0..255) while tongue is active, else host stage area id.
// episodeId: fruit mouth actor encode (see yoshi_sync.cpp) while set, else host episode id.
// velocity: tongue tip offset from Mario while tongue is active, else Mario speed.
constexpr u8 kYoshiTongueHandMask = 0x03;
constexpr u8 kYoshiTongueStateShift = 2;
constexpr u8 kYoshiTongueStateMask = 0x07;
constexpr u8 kYoshiTongueProgressShift = 5;
constexpr u8 kYoshiTongueProgressMask = 0x07;

inline u8 packYoshiTongueHealth(u8 handIndex, u16 tongueState, u16 tongueProgress) {
    const u8 hand = handIndex & kYoshiTongueHandMask;
    const u8 state = static_cast<u8>((tongueState & kYoshiTongueStateMask) << kYoshiTongueStateShift);
    const u8 progress =
        static_cast<u8>(((tongueProgress / 8) & kYoshiTongueProgressMask) << kYoshiTongueProgressShift);
    return static_cast<u8>(hand | state | progress);
}

inline u8 unpackYoshiTongueHand(u8 packed) {
    return packed & kYoshiTongueHandMask;
}

inline u8 unpackYoshiTongueState(u8 packed) {
    return static_cast<u8>((packed >> kYoshiTongueStateShift) & kYoshiTongueStateMask);
}

inline u8 unpackYoshiTongueProgressCoarse(u8 packed) {
    return static_cast<u8>(((packed >> kYoshiTongueProgressShift) & kYoshiTongueProgressMask) * 8);
}

inline bool yoshiTongueIsActive(u8 tongueState) {
    return tongueState != 0;
}

// When VFX_WATER_SPRAY is set, the water byte carries nozzle pressure (0..255), not tank level.
inline u8 encodeSprayPressure(f32 pressure) {
    if (pressure <= 0.0f)
        return 0;
    if (pressure >= 1.0f)
        return 255;
    return static_cast<u8>(pressure * 255.0f);
}

inline f32 decodeSprayPressure(u8 enc) {
    return static_cast<f32>(enc) / 255.0f;
}

// UDP snapshot name overlay bytes (16 total) carry appearance colors on the wire.
// Dolphin CommBuffer stores the full UTF-8 display name in PlayerSnapshot::name and
// appearance colors in NameTagAppearance sidecars at the end of CommBuffer.
//   5..7   gradient bottom RGB when gradient marker is set
//   8..10  outline RGB
//   12..14 text top RGB
//   15     0x7F = text only, 0x7D = outline + text, 0x7B = outline + gradient text
constexpr u8 kNameTextBytes = 15;
constexpr u8 kNameTextBytesWithOutline = 15;
constexpr u8 kNameTextBytesWithGradient = 15;
constexpr u8 kNameTagColorMarker = 0x7F;
constexpr u8 kNameTagExtendedMarker = 0x7D;
constexpr u8 kNameTagGradientMarker = 0x7B;
constexpr u8 kNameTagAppearanceValidFlag = 0x80;
constexpr u8 kNameTagAppearanceGradientFlag = 0x01;

struct NameTagAppearance {
    u8 textTopR;
    u8 textTopG;
    u8 textTopB;
    u8 textBottomR;
    u8 textBottomG;
    u8 textBottomB;
    u8 outlineR;
    u8 outlineG;
    u8 outlineB;
    u8 flags;
};

static_assert(sizeof(NameTagAppearance) == 10, "NameTagAppearance must be 10 bytes");

struct MarioVoiceEvent {
    u32 soundId;
    u16 sequence;
    u8 flags;
    u8 health;
    u8 stageId;
    u8 episodeId;
    u8 reserved0;
    u8 reserved1;
};

static_assert(sizeof(MarioVoiceEvent) == MARIO_VOICE_EVENT_SIZE,
              "MarioVoiceEvent must match launcher wire size");

inline bool hasNameTagAppearanceMarker(u8 marker) {
    return marker == kNameTagColorMarker || marker == kNameTagExtendedMarker ||
           marker == kNameTagGradientMarker;
}

inline void packNameTagAppearance(char name[MAX_PLAYER_NAME], u8 textTopR, u8 textTopG, u8 textTopB,
                                  u8 textBottomR, u8 textBottomG, u8 textBottomB, u8 outlineR,
                                  u8 outlineG, u8 outlineB, bool gradientEnabled) {
    if (gradientEnabled) {
        name[5] = static_cast<char>(textBottomR);
        name[6] = static_cast<char>(textBottomG);
        name[7] = static_cast<char>(textBottomB);
        name[15] = static_cast<char>(kNameTagGradientMarker);
    } else {
        name[15] = static_cast<char>(kNameTagExtendedMarker);
    }

    name[8] = static_cast<char>(outlineR);
    name[9] = static_cast<char>(outlineG);
    name[10] = static_cast<char>(outlineB);
    name[11] = 0;
    name[12] = static_cast<char>(textTopR);
    name[13] = static_cast<char>(textTopG);
    name[14] = static_cast<char>(textTopB);
}

inline void packNameTagAppearanceToSidecar(NameTagAppearance &appearance, u8 textTopR, u8 textTopG,
                                           u8 textTopB, u8 textBottomR, u8 textBottomG, u8 textBottomB,
                                           u8 outlineR, u8 outlineG, u8 outlineB, bool gradientEnabled) {
    appearance.textTopR = textTopR;
    appearance.textTopG = textTopG;
    appearance.textTopB = textTopB;
    appearance.textBottomR = textBottomR;
    appearance.textBottomG = textBottomG;
    appearance.textBottomB = textBottomB;
    appearance.outlineR = outlineR;
    appearance.outlineG = outlineG;
    appearance.outlineB = outlineB;
    appearance.flags = static_cast<u8>(kNameTagAppearanceValidFlag |
                                       (gradientEnabled ? kNameTagAppearanceGradientFlag : 0));
}

inline bool readNameTagAppearanceFromSidecar(const NameTagAppearance &appearance, u8 &textTopR,
                                             u8 &textTopG, u8 &textTopB, u8 &textBottomR,
                                             u8 &textBottomG, u8 &textBottomB, u8 &outlineR,
                                             u8 &outlineG, u8 &outlineB, bool &gradientEnabled) {
    if ((appearance.flags & kNameTagAppearanceValidFlag) == 0)
        return false;

    textTopR = appearance.textTopR;
    textTopG = appearance.textTopG;
    textTopB = appearance.textTopB;
    textBottomR = appearance.textBottomR;
    textBottomG = appearance.textBottomG;
    textBottomB = appearance.textBottomB;
    outlineR = appearance.outlineR;
    outlineG = appearance.outlineG;
    outlineB = appearance.outlineB;
    gradientEnabled = (appearance.flags & kNameTagAppearanceGradientFlag) != 0;
    return true;
}

inline void packNameTagColors(char name[MAX_PLAYER_NAME], u8 textR, u8 textG, u8 textB, u8 outlineR,
                              u8 outlineG, u8 outlineB, bool includeOutline) {
    packNameTagAppearance(name, textR, textG, textB, textR, textG, textB, outlineR, outlineG, outlineB,
                          false);
    (void)includeOutline;
}

inline bool readNameTagTextColor(const char name[MAX_PLAYER_NAME], u8 &r, u8 &g, u8 &b) {
    const u8 marker = static_cast<u8>(name[15]);
    if (!hasNameTagAppearanceMarker(marker))
        return false;
    r = static_cast<u8>(name[12]);
    g = static_cast<u8>(name[13]);
    b = static_cast<u8>(name[14]);
    return true;
}

inline bool readNameTagTextBottomColor(const char name[MAX_PLAYER_NAME], u8 &r, u8 &g, u8 &b) {
    if (static_cast<u8>(name[15]) != kNameTagGradientMarker)
        return false;
    r = static_cast<u8>(name[5]);
    g = static_cast<u8>(name[6]);
    b = static_cast<u8>(name[7]);
    return true;
}

inline bool readNameTagOutlineColor(const char name[MAX_PLAYER_NAME], u8 &r, u8 &g, u8 &b) {
    const u8 marker = static_cast<u8>(name[15]);
    if (marker != kNameTagExtendedMarker && marker != kNameTagGradientMarker)
        return false;
    r = static_cast<u8>(name[8]);
    g = static_cast<u8>(name[9]);
    b = static_cast<u8>(name[10]);
    return true;
}

inline bool readNameTagGradientEnabled(const char name[MAX_PLAYER_NAME]) {
    return static_cast<u8>(name[15]) == kNameTagGradientMarker;
}

inline int readNameTagTextLength(const char name[MAX_PLAYER_NAME]) {
    for (int i = 0; i < static_cast<int>(MAX_PLAYER_NAME); ++i) {
        if (name[i] == '\0')
            return i;
    }
    return static_cast<int>(MAX_PLAYER_NAME);
}

inline void copyPurePlayerName(char dest[MAX_PLAYER_NAME], const char src[MAX_PLAYER_NAME]) {
    int len = readNameTagTextLength(src);
    if (len <= 0) {
        dest[0] = '\0';
        return;
    }

    const int copyLen =
        len < static_cast<int>(MAX_PLAYER_NAME) ? len : static_cast<int>(MAX_PLAYER_NAME) - 1;
    memcpy(dest, src, static_cast<size_t>(copyLen));
    dest[copyLen] = '\0';
}

inline void preserveNameTagOverlay(const char name[MAX_PLAYER_NAME], char overlay[11]) {
    overlay[0] = name[5];
    overlay[1] = name[6];
    overlay[2] = name[7];
    overlay[3] = name[8];
    overlay[4] = name[9];
    overlay[5] = name[10];
    overlay[6] = name[11];
    overlay[7] = name[12];
    overlay[8] = name[13];
    overlay[9] = name[14];
    overlay[10] = name[15];
}

inline void restoreNameTagOverlay(char name[MAX_PLAYER_NAME], const char overlay[11]) {
    name[5] = overlay[0];
    name[6] = overlay[1];
    name[7] = overlay[2];
    name[8] = overlay[3];
    name[9] = overlay[4];
    name[10] = overlay[5];
    name[11] = overlay[6];
    name[12] = overlay[7];
    name[13] = overlay[8];
    name[14] = overlay[9];
    name[15] = overlay[10];
}

inline void copyPlayerNamePreserveOverlay(char name[MAX_PLAYER_NAME], const char *playerName) {
    char overlay[11];
    const u8 marker = static_cast<u8>(name[15]);
    const bool hasOverlay = hasNameTagAppearanceMarker(marker);
    if (hasOverlay)
        preserveNameTagOverlay(name, overlay);

    memset(name, 0, MAX_PLAYER_NAME);
    if (playerName && playerName[0] != '\0') {
        int copyLen = 0;
        while (copyLen < static_cast<int>(MAX_PLAYER_NAME) && playerName[copyLen] != '\0')
            ++copyLen;
        memcpy(name, playerName, static_cast<size_t>(copyLen));
        if (copyLen < static_cast<int>(MAX_PLAYER_NAME))
            name[copyLen] = '\0';
    }

    if (hasOverlay)
        restoreNameTagOverlay(name, overlay);
}

#pragma pack(push, 1)

struct Vec3 {
    f32 x, y, z;
};

struct PlayerSnapshot {
    Vec3 position;
    Vec3 velocity;
    f32 rotationY;
    u16 animId;
    u8 nozzleId;
    u8 water;
    u8 health;
    u8 stageId;
    u8 episodeId;
    u8 movementState;
    u16 actionId;
    u16 vfxFlags;
    u8 connected;
    u8 slot;
    // pingMs packs animation aux data for network sync — NOT network latency.
    // Low byte: BCK playback rate (frameRate * 64). High byte: head/waist angle encoding.
    u16 pingMs;
    char name[MAX_PLAYER_NAME];
    u16 animFrame;
    u16 actionIdHi; // high 16 bits of TMario::mState (actionId holds low 16)
};

// 12-byte indirection block at COMM_GUEST_ADDRESS; launcher reads this to find CommBuffer in module BSS.
struct CommMailboxAnchor {
    u32 magic;
    u16 version;
    u16 reserved;
    u32 bufferGuest;
};

struct GameModeState {
    u8 mode;
    u8 flags;
    u8 localRole;
    u8 lastTaggedSlot;
    u8 tagEventId;
    u32 roundStartMs;
    u8 roleBySlot[MAX_PLAYERS];
};

static_assert(sizeof(GameModeState) == COMM_GAME_MODE_STATE_SIZE, "GameModeState size mismatch");

enum WorldEventType : u8 {
    WE_SHINE_COLLECTED = 1,
    WE_BLUE_COIN_COLLECTED = 2,
    WE_EPISODE_COMPLETE = 3,
    WE_STORY_FLAG = 4,
    WE_TRIGGER_FLAG = 5,
    WE_SECRET_COMPLETE = 6,
    WE_GOLD_COIN_COLLECTED = 7,
    // Mario ground-pounded a managed object (crate / super hip-drop block / hide
    // object / switch / etc.). payload0 = object mMapObjID, reserved = pounder
    // slot, payload1 = packed world position of the object (packCollectibleWorldPos).
    // Remotes replay via THitActor::receiveMessage(gpMario, HIT_MESSAGE_HIP_DROP).
    // payload1 bit 31 set when the pound was a super hip-drop.
    WE_HIP_DROP_OBJECT = 8,
    WE_RED_COIN_COLLECTED = 9,
    // Host Yoshi ate a fruit with the tongue. payload0 = encoded fruit actor type,
    // payload1 = packed fruit world position (packCollectibleWorldPos), reserved = eater slot.
    WE_YOSHI_FRUIT_TAKEN = 10,
};

struct CommWorldEvent {
    u32 eventId;
    u16 sequence;
    u8 type;
    u8 courseId;
    u8 episodeId;
    u8 payload0;
    u8 reserved;
    u32 payload1;
};

static_assert(sizeof(CommWorldEvent) == COMM_WORLD_EVENT_SIZE, "CommWorldEvent size mismatch");

struct WorldSyncState {
    CommWorldEvent localPending;
    CommWorldEvent incoming;
    u32 lastAppliedEventId;
};

static_assert(sizeof(WorldSyncState) == COMM_WORLD_SYNC_SIZE, "WorldSyncState size mismatch");

enum RosterHudEventKind : u8 {
    RHE_NONE = 0,
    RHE_CONNECTED = 1,
    RHE_DISCONNECTED = 2,
};

struct RosterHudEvent {
    u16 sequence;
    u8 kind;
    u8 slot;
    char name[MAX_PLAYER_NAME];
};

struct RosterHudSync {
    u16 latestSequence;
    RosterHudEvent events[COMM_ROSTER_HUD_RING_SLOTS];
};

static_assert(sizeof(RosterHudEvent) == COMM_ROSTER_HUD_EVENT_SIZE, "RosterHudEvent size mismatch");
static_assert(sizeof(RosterHudSync) == COMM_ROSTER_HUD_SYNC_SIZE, "RosterHudSync size mismatch");

struct CommBuffer {
    u32 magic;
    u16 version;
    u32 bridgeFlags;
    u8 localSlot;
    u8 dolphinState;
    u8 playerCount;
    u8 warpTargetSlot;
    u8 warpCourseId;
    u8 warpEpisodeId;
    f32 warpPosX;
    f32 warpPosY;
    f32 warpPosZ;
    f32 warpFacingY;
    char localPlayerName[MAX_PLAYER_NAME];
    PlayerSnapshot localSnapshot;
    PlayerSnapshot remoteSnapshots[MAX_REMOTE_SLOTS];
    NameTagAppearance localNameTagAppearance;
    NameTagAppearance remoteNameTagAppearances[MAX_REMOTE_SLOTS];
    MarioVoiceEvent localMarioVoiceEvent;
    MarioVoiceEvent remoteMarioVoiceEvents[MAX_REMOTE_SLOTS];
    GameModeState gameModeState;
    WorldSyncState worldSync;
    RosterHudSync rosterHud;
};

#pragma pack(pop)

static_assert(sizeof(PlayerSnapshot) == PLAYER_SNAPSHOT_SIZE, "PlayerSnapshot must be 64 bytes");
static_assert(sizeof(CommBuffer) == COMM_BUFFER_SIZE, "CommBuffer size mismatch");
static_assert(sizeof(CommMailboxAnchor) == 12, "CommMailboxAnchor must be 12 bytes");
static_assert(offsetof(CommBuffer, remoteSnapshots) == COMM_REMOTE_SNAPSHOTS_OFFSET,
              "remoteSnapshots offset mismatch");
static_assert(offsetof(CommBuffer, localNameTagAppearance) == COMM_NAME_TAG_APPEARANCES_OFFSET,
              "name-tag appearance offset mismatch");
static_assert(offsetof(CommBuffer, localMarioVoiceEvent) == COMM_MARIO_VOICE_EVENTS_OFFSET,
              "mario voice events offset mismatch");
static_assert(offsetof(CommBuffer, gameModeState) == COMM_GAME_MODE_STATE_OFFSET,
              "game mode state offset mismatch");

CommBuffer *getCommBuffer();
void publishMailboxAnchor();
void initCommBuffer();
void resetCommBuffer();

} // namespace smso

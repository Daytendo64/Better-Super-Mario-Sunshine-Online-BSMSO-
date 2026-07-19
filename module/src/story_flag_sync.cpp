#include "story_flag_sync.hpp"

#include "collectible_scan.hpp"
#include "comm_buffer.hpp"
#include "world_sync.hpp"

#include <SMS/Manager/FlagManager.hxx>
#include <SMS/MapObj/MapObjBase.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <Dolphin/OS.h>
#include <string.h>

extern TMarDirector *gpMarDirector;
extern TApplication gpApplication;

namespace {

// Card bools: 0x10000 .. 0x103B3 (948 bits). Shine 0..119 and blue-coin slots are
// already synced via ShineAuthority / BlueCoinAuthority — skip those on emit.
constexpr u32 kCardBoolBase = 0x10000u;
constexpr u32 kCardBoolEnd = 0x103B4u; // exclusive (decomp)
constexpr u32 kShineFlagCount = 120u;
constexpr u32 kBlueCoinFlagBase = 0x10078u;
constexpr u32 kBlueCoinFlagEnd = 0x10078u + 9u * 50u; // stages 1..9 × 50

// Stage bools: 0x50000 .. 0x50063 (100 bits).
constexpr u32 kStageBoolBase = 0x50000u;
constexpr u32 kStageBoolEnd = 0x50064u; // exclusive

constexpr u32 kRedCoinSwitchPressedFlagId = 0x50009u;

// Game bools: 0x30000 .. 0x3001C — runtime bank; never durable.
constexpr u32 kGameBoolBase = 0x30000u;
constexpr u32 kGameBoolEnd = 0x3001Du;

constexpr u32 kSpawnDirectorFlag30001 = 0x30001u;
constexpr u32 kSpawnDirectorFlag30004 = 0x30004u;

// Verified plaza MapEvent / loadAfter latches. These are hub-global on Delfino
// (area 1): dolpic scenario indices differ (8/7/6/…) while the Type5 meaning is
// shared. Keying by raw mEpisodeID made live apply miss across hub scenarios and
// deferred healing until a matching re-enter.
constexpr u8 kPlazaAreaId = 1u;
constexpr u8 kPlazaHubEpisode = 0xFFu;
constexpr u32 kPlazaTriggerFlags[] = {0x50001u, 0x50002u, 0x50004u};
constexpr u32 kPlazaTriggerCount = sizeof(kPlazaTriggerFlags) / sizeof(kPlazaTriggerFlags[0]);

constexpr u32 kCardBitCount = kCardBoolEnd - kCardBoolBase;   // 948
constexpr u32 kStageBitCount = kStageBoolEnd - kStageBoolBase; // 100
constexpr u32 kGameBitCount = kGameBoolEnd - kGameBoolBase;    // 29
constexpr u32 kCardScanSlice = 256u;
constexpr bool kStoryFlagHotPathOsReport = true;

constexpr u32 kCardByteCount = (kCardBitCount + 7u) / 8u;
constexpr u32 kStageByteCount = (kStageBitCount + 7u) / 8u;
constexpr u32 kGameByteCount = (kGameBitCount + 7u) / 8u;

// NTSC-U TMareGate vtable — loadAfter kills the actor when 0x50004 is clear.
constexpr u32 kVtMareGate = SMS_PORT_REGION(0x803D3480, 0x803CAC70, 0, 0x803D3480);

static bool isShineOrBlueCoinCardFlag(u32 flagId) {
    if (flagId < kCardBoolBase || flagId >= kCardBoolEnd)
        return false;
    const u32 low = flagId - kCardBoolBase;
    if (low < kShineFlagCount)
        return true;
    if (flagId >= kBlueCoinFlagBase && flagId < kBlueCoinFlagEnd)
        return true;
    return false;
}

static bool isEphemeralStageSessionBool(u32 flagId) {
    return flagId == kRedCoinSwitchPressedFlagId;
}

static bool isEphemeralSpawnDirectorBool(u32 flagId) {
    return flagId == kSpawnDirectorFlag30001 || flagId == kSpawnDirectorFlag30004;
}

static bool isNonDurableBoolFlag(u32 flagId) {
    return isEphemeralStageSessionBool(flagId) || isEphemeralSpawnDirectorBool(flagId) ||
           (flagId >= kGameBoolBase && flagId < kGameBoolEnd);
}

static bool isDurableCardBool(u32 flagId) {
    return flagId >= kCardBoolBase && flagId < kCardBoolEnd &&
           !isShineOrBlueCoinCardFlag(flagId);
}

static bool isDurablePlazaTrigger(u32 flagId) {
    for (u32 i = 0; i < kPlazaTriggerCount; ++i) {
        if (flagId == kPlazaTriggerFlags[i])
            return true;
    }
    return false;
}

static s32 plazaTriggerIndex(u32 flagId) {
    for (u32 i = 0; i < kPlazaTriggerCount; ++i) {
        if (flagId == kPlazaTriggerFlags[i])
            return static_cast<s32>(i);
    }
    return -1;
}

static u8 sCardBits[kCardByteCount] = {};
static u8 sStageBits[kStageByteCount] = {};
static u8 sGameBits[kGameByteCount] = {};
static u8 sAuthorityCardBits[kCardByteCount] = {};
static u8 sBootstrapCardBits[kCardByteCount] = {};
static u8 sBootstrapStageBits[kStageByteCount] = {};

// Grow-only plaza hub overlay — survives leaving Delfino so the next plaza
// stageInit can write bits before setupObjects/loadAfter.
static bool sPlazaTriggerOverlay[kPlazaTriggerCount] = {};

static bool sTrackersReady = false;
static bool sApplyingRemote = false;
static bool sConnectionObserved = false;
static bool sSyncObserved = false;
static bool sCardBootstrapPending = false;
static bool sStageBootstrapPending = false;
static u32 sCardBootstrapCursor = 0;
static u32 sStageBootstrapCursor = 0;
static u32 sCardScanCursor = 0;
static u32 sCardBankHash = 0;
static u32 sStageAllowlistHash = 0;

static bool bitGet(const u8 *bits, u32 index) {
    return (bits[index >> 3] & static_cast<u8>(1u << (index & 7))) != 0;
}

static void bitSet(u8 *bits, u32 index, bool value) {
    const u8 mask = static_cast<u8>(1u << (index & 7));
    if (value)
        bits[index >> 3] |= mask;
    else
        bits[index >> 3] &= static_cast<u8>(~mask);
}

static u32 fnv1aBytes(const u8 *data, u32 len) {
    u32 hash = 2166136261u;
    for (u32 i = 0; i < len; ++i) {
        hash ^= data[i];
        hash *= 16777619u;
    }
    return hash;
}

static void snapshotBank(TFlagManager *fm, u32 base, u32 count, u8 *outBits) {
    for (u32 i = 0; i < count; ++i)
        bitSet(outBits, i, fm->getBool(base + i));
}

static u32 snapshotPlazaAllowlistHash(TFlagManager *fm) {
    u8 bits = 0;
    for (u32 i = 0; i < kPlazaTriggerCount; ++i) {
        if (fm->getBool(kPlazaTriggerFlags[i]))
            bits = static_cast<u8>(bits | static_cast<u8>(1u << i));
    }
    return bits;
}

static void publishFlagEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u32 flagId,
                             u8 value) {
    smso::enqueueLocalWorldEvent(static_cast<u8>(type), courseId, episodeId, value, 0, flagId);
}

static void publishCardOrSecret(smso::WorldEventType type, u32 flagId) {
    if (!gpMarDirector)
        return;
    publishFlagEvent(type, gpMarDirector->mAreaID, gpMarDirector->mEpisodeID, flagId, 1);
}

static void publishPlazaTrigger(u32 flagId) {
    // Hub-global wire key: course=plaza, episode=wildcard. Server coalesces any
    // legacy plaza episode into the same authority slot.
    publishFlagEvent(smso::WE_TRIGGER_FLAG, kPlazaAreaId, kPlazaHubEpisode, flagId, 1);
}

static void markLocalTracker(u32 flagId, bool value) {
    if (flagId >= kCardBoolBase && flagId < kCardBoolEnd)
        bitSet(sCardBits, flagId - kCardBoolBase, value);
    else if (flagId >= kStageBoolBase && flagId < kStageBoolEnd)
        bitSet(sStageBits, flagId - kStageBoolBase, value);
    else if (flagId >= kGameBoolBase && flagId < kGameBoolEnd)
        bitSet(sGameBits, flagId - kGameBoolBase, value);
}

static bool applyBoolFlag(TFlagManager *fm, u32 flagId, u8 value, bool *changedOut) {
    if (!fm || flagId == 0) {
        if (changedOut)
            *changedOut = false;
        return false;
    }

    const bool want = value != 0;
    const bool have = fm->getBool(flagId);
    if (have == want) {
        if (changedOut)
            *changedOut = false;
        return true;
    }

    sApplyingRemote = true;
    fm->setBool(want, flagId);
    sApplyingRemote = false;

    if (changedOut)
        *changedOut = true;
    return true;
}

static bool visitReviveMareGate(TMapObjBase *obj, void *ctx) {
    (void)ctx;
    if (!obj)
        return true;
    const u32 vt = *reinterpret_cast<const u32 *>(obj);
    if (vt != kVtMareGate)
        return true;

    // loadAfter called makeObjDead when 0x50004 was clear. Flag is now set —
    // revive without plaza soft-reload.
    obj->makeObjAppeared();
    *reinterpret_cast<u32 *>(ctx) = 1u;
    OSReport("[SMSOBB] flag-wake MareGate revived (0x50004 live apply)\n");
    return false;
}

static void wakePlazaGeometryForFlag(u32 flagId, bool changed) {
    if (!gpMarDirector || gpMarDirector->mAreaID != kPlazaAreaId)
        return;

    if (flagId == 0x50004u && changed) {
        u32 revived = 0;
        smso::forEachManagedMapObj(visitReviveMareGate, &revived);
        if (kStoryFlagHotPathOsReport) {
            OSReport("[SMSOBB] flag-wake id=0x%08X mareGateRevived=%u\n", flagId, revived);
        }
        return;
    }

    // 0x50001 / 0x50002: MapEvent load/loadAfter bind raised vs sunk. Mid-visit
    // watch() for RiccoMammaGate triggers a camera demo + Mario warp — unsafe for
    // remotes. Honest live path is FlagManager write; geometry that already took
    // the loadAfter "done" path stays correct. Sunk mid-visit actors still need
    // natural re-enter (documented).
    if ((flagId == 0x50001u || flagId == 0x50002u) && kStoryFlagHotPathOsReport) {
        OSReport("[SMSOBB] flag-wake id=0x%08X MapEvent=loadAfter-bound (no demo force)\n",
                 flagId);
    }

    // Card-watched gates (e.g. Bianco 0x10384): TMapEvent::perform polls watch()
    // every MOVE cue while state==1 — setBool is sufficient.
}

static void writePlazaOverlayToFlagManager(TFlagManager *fm, const char *reason) {
    if (!fm)
        return;

    u32 wrote = 0;
    sApplyingRemote = true;
    for (u32 i = 0; i < kPlazaTriggerCount; ++i) {
        if (!sPlazaTriggerOverlay[i])
            continue;
        const u32 flagId = kPlazaTriggerFlags[i];
        if (!fm->getBool(flagId)) {
            fm->setBool(true, flagId);
            ++wrote;
        }
        markLocalTracker(flagId, true);
    }
    sApplyingRemote = false;

    OSReport("[SMSOBB] story-flag plaza overlay apply wrote=%u reason=%s\n", wrote, reason);
}

static void admitPlazaTrigger(u32 flagId) {
    const s32 idx = plazaTriggerIndex(flagId);
    if (idx < 0)
        return;
    sPlazaTriggerOverlay[idx] = true;
}

// Publish at most one baseline set per frame.
static void publishNextBootstrapSet(TFlagManager *fm, bool syncStory, bool syncSecret) {
    if (!fm)
        return;

    while (sCardBootstrapPending && sCardBootstrapCursor < kCardBitCount) {
        const u32 flagId = kCardBoolBase + sCardBootstrapCursor++;
        if (!isDurableCardBool(flagId) || !bitGet(sBootstrapCardBits, sCardBootstrapCursor - 1) ||
            (!syncStory && (!syncSecret || flagId < 0x10366u)))
            continue;
        bitSet(sAuthorityCardBits, flagId - kCardBoolBase, true);
        publishCardOrSecret(syncStory ? smso::WE_STORY_FLAG : smso::WE_SECRET_COMPLETE, flagId);
        return;
    }
    if (sCardBootstrapCursor >= kCardBitCount)
        sCardBootstrapPending = false;

    while (sStageBootstrapPending && sStageBootstrapCursor < kStageBitCount) {
        const u32 flagId = kStageBoolBase + sStageBootstrapCursor++;
        if (!isDurablePlazaTrigger(flagId) ||
            !bitGet(sBootstrapStageBits, sStageBootstrapCursor - 1))
            continue;
        // Only merge plaza allowlist when we are actually on the hub — Type5 on
        // other courses is resetStage scratch that happens to reuse the same ids.
        if (!gpMarDirector || gpMarDirector->mAreaID != kPlazaAreaId)
            continue;
        admitPlazaTrigger(flagId);
        publishPlazaTrigger(flagId);
        return;
    }
    if (sStageBootstrapCursor >= kStageBitCount)
        sStageBootstrapPending = false;
}

static void scanCardBankForChanges(TFlagManager *fm, u32 start, u32 count, bool syncStory) {
    const u32 end = start + count;
    for (u32 i = start; i < end; ++i) {
        const u32 flagId = kCardBoolBase + i;
        if (isShineOrBlueCoinCardFlag(flagId))
            continue;

        const bool now = fm->getBool(flagId);
        const bool was = bitGet(sCardBits, i);
        if (now == was)
            continue;

        bitSet(sCardBits, i, now);
        if (sApplyingRemote || !now || !isDurableCardBool(flagId))
            continue;

        bitSet(sAuthorityCardBits, i, true);
        publishCardOrSecret(syncStory ? smso::WE_STORY_FLAG : smso::WE_SECRET_COMPLETE, flagId);
        if (kStoryFlagHotPathOsReport) {
            OSReport("[SMSOBB] story-flag emit-set id=0x%08X live=1\n", flagId);
        }
    }
}

static void scanPlazaTriggersForChanges(TFlagManager *fm) {
    if (!gpMarDirector || gpMarDirector->mAreaID != kPlazaAreaId)
        return;

    for (u32 i = 0; i < kPlazaTriggerCount; ++i) {
        const u32 flagId = kPlazaTriggerFlags[i];
        const u32 bitIndex = flagId - kStageBoolBase;
        const bool now = fm->getBool(flagId);
        const bool was = bitGet(sStageBits, bitIndex);
        if (now == was)
            continue;

        bitSet(sStageBits, bitIndex, now);
        if (sApplyingRemote || !now)
            continue;

        admitPlazaTrigger(flagId);
        publishPlazaTrigger(flagId);
        if (kStoryFlagHotPathOsReport) {
            OSReport("[SMSOBB] trigger-flag emit-set id=0x%08X plazaHub=1\n", flagId);
        }
    }
}

} // namespace

namespace smso {

void initStoryFlagSync() {
    memset(sAuthorityCardBits, 0, sizeof(sAuthorityCardBits));
    memset(sBootstrapCardBits, 0, sizeof(sBootstrapCardBits));
    memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
    memset(sPlazaTriggerOverlay, 0, sizeof(sPlazaTriggerOverlay));
    sConnectionObserved = false;
    sSyncObserved = false;
    sCardBankHash = 0;
    sStageAllowlistHash = 0;
    resetStoryFlagTrackers();
    OSReport("[SMSOBB] story/trigger flag sync ready (card live + plaza-hub Type5)\n");
}

void resetStoryFlagTrackers() {
    memset(sCardBits, 0, sizeof(sCardBits));
    memset(sStageBits, 0, sizeof(sStageBits));
    memset(sGameBits, 0, sizeof(sGameBits));
    sTrackersReady = false;
    sCardScanCursor = 0;
    sCardBankHash = 0;
    sStageAllowlistHash = 0;
}

void updateStoryFlagSyncConnectionState(bool connected, bool syncEnabled) {
    if (!connected) {
        if (sConnectionObserved) {
            memset(sAuthorityCardBits, 0, sizeof(sAuthorityCardBits));
            memset(sBootstrapCardBits, 0, sizeof(sBootstrapCardBits));
            memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
            memset(sPlazaTriggerOverlay, 0, sizeof(sPlazaTriggerOverlay));
            sCardBootstrapPending = false;
            sStageBootstrapPending = false;
            resetStoryFlagTrackers();
            OSReport("[SMSOBB] story-flag session authority cache cleared\n");
        }
        sConnectionObserved = false;
        sSyncObserved = false;
        return;
    }

    sConnectionObserved = true;
    if (syncEnabled && !sSyncObserved) {
        TFlagManager *fm = TFlagManager::smInstance;
        if (fm) {
            snapshotBank(fm, kCardBoolBase, kCardBitCount, sBootstrapCardBits);
            snapshotBank(fm, kStageBoolBase, kStageBitCount, sBootstrapStageBits);
            // Seed plaza overlay from local hub bits if already on Delfino.
            if (gpMarDirector && gpMarDirector->mAreaID == kPlazaAreaId) {
                for (u32 i = 0; i < kPlazaTriggerCount; ++i) {
                    if (fm->getBool(kPlazaTriggerFlags[i]))
                        sPlazaTriggerOverlay[i] = true;
                }
            }
        } else {
            memset(sBootstrapCardBits, 0, sizeof(sBootstrapCardBits));
            memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
        }
        sCardBootstrapPending = true;
        sStageBootstrapPending = true;
        sCardBootstrapCursor = 0;
        sStageBootstrapCursor = 0;
        resetStoryFlagTrackers();
        OSReport("[SMSOBB] story-flag baseline merge started\n");
    }
    sSyncObserved = syncEnabled;
}

void notifyStoryFlagStageEnter(u8 courseId, u8 episodeId) {
    // Runs even for same-course/same-episode reloads (ID-change polling cannot see those).
    resetStoryFlagTrackers();

    TFlagManager *fm = TFlagManager::smInstance;
    if (fm)
        snapshotBank(fm, kStageBoolBase, kStageBitCount, sBootstrapStageBits);
    else
        memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
    sStageBootstrapPending = sSyncObserved;
    sStageBootstrapCursor = 0;

    // CRITICAL: write plaza overlay BEFORE setupObjects/loadAfter (BSE calls this
    // stageInit callback between resetStage and director->setupObjects()).
    if (courseId == kPlazaAreaId && fm) {
        writePlazaOverlayToFlagManager(fm, "stageInit-pre-loadAfter");
        // Wake actors that already exist is a no-op here (setupObjects not yet);
        // mid-visit applies call wakePlazaGeometryForFlag separately.
    }

    OSReport("[SMSOBB] story-flag stage enter course=%u/%u plazaOverlay=%u/%u/%u\n", courseId,
             episodeId, sPlazaTriggerOverlay[0] ? 1u : 0u, sPlazaTriggerOverlay[1] ? 1u : 0u,
             sPlazaTriggerOverlay[2] ? 1u : 0u);
}

void scrubEphemeralSpawnDirectorFlagsOnStageExit() {
    if (gpApplication.mCurrentScene.mAreaID == 1 && gpApplication.mNextScene.mAreaID == 1)
        return;

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return;

    bool scrubbed = false;
    sApplyingRemote = true;
    if (fm->getBool(kSpawnDirectorFlag30001)) {
        fm->setBool(false, kSpawnDirectorFlag30001);
        scrubbed = true;
    }
    if (fm->getBool(kSpawnDirectorFlag30004)) {
        fm->setBool(false, kSpawnDirectorFlag30004);
        scrubbed = true;
    }
    sApplyingRemote = false;

    if (scrubbed) {
        markLocalTracker(kSpawnDirectorFlag30001, false);
        markLocalTracker(kSpawnDirectorFlag30004, false);
        OSReport("[SMSOBB] scrubbed ephemeral spawn-director flags on stage exit "
                 "(cur=%u next=%u)\n",
                 gpApplication.mCurrentScene.mAreaID, gpApplication.mNextScene.mAreaID);
    }
}

void captureLocalStoryFlagProgress() {
    if (sApplyingRemote)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return;

    const bool syncStory = (buf->bridgeFlags & smso::BF_SYNC_STORY) != 0;
    const bool syncSecret = (buf->bridgeFlags & smso::BF_SYNC_SECRET) != 0;
    const bool syncMission = (buf->bridgeFlags & smso::BF_SYNC_MISSION) != 0;
    if (!syncStory && !syncSecret && !syncMission)
        return;

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return;

    if (!sTrackersReady) {
        snapshotBank(fm, kCardBoolBase, kCardBitCount, sCardBits);
        snapshotBank(fm, kStageBoolBase, kStageBitCount, sStageBits);
        snapshotBank(fm, kGameBoolBase, kGameBitCount, sGameBits);
        sCardBankHash = fnv1aBytes(sCardBits, kCardByteCount);
        sStageAllowlistHash = snapshotPlazaAllowlistHash(fm);
        sTrackersReady = true;
        return;
    }

    publishNextBootstrapSet(fm, syncStory, syncSecret);

    // Card story / nozzle / plaza gate bools — sliced scan with hash short-circuit.
    if (syncStory) {
        u8 sliceBits[(kCardScanSlice + 7u) / 8u] = {};
        const u32 remaining = kCardBitCount - sCardScanCursor;
        const u32 scanCount = remaining < kCardScanSlice ? remaining : kCardScanSlice;
        for (u32 i = 0; i < scanCount; ++i)
            bitSet(sliceBits, i, fm->getBool(kCardBoolBase + sCardScanCursor + i));

        // Cheap dirty check against tracker slice before edge-detect publish.
        bool dirty = false;
        for (u32 i = 0; i < scanCount; ++i) {
            const bool now = bitGet(sliceBits, i);
            const bool was = bitGet(sCardBits, sCardScanCursor + i);
            if (now != was) {
                dirty = true;
                break;
            }
        }
        if (dirty)
            scanCardBankForChanges(fm, sCardScanCursor, scanCount, true);

        sCardScanCursor += scanCount;
        if (sCardScanCursor >= kCardBitCount) {
            sCardScanCursor = 0;
            snapshotBank(fm, kCardBoolBase, kCardBitCount, sCardBits);
            sCardBankHash = fnv1aBytes(sCardBits, kCardByteCount);
            (void)sCardBankHash;
        }
    }

    // Plaza Type5 allowlist only — O(3) not O(100).
    if ((syncStory || syncMission) && gpMarDirector &&
        gpMarDirector->mAreaID == kPlazaAreaId) {
        const u32 nowHash = snapshotPlazaAllowlistHash(fm);
        if (nowHash != sStageAllowlistHash) {
            scanPlazaTriggersForChanges(fm);
            sStageAllowlistHash = nowHash;
        }
    }

    // Secret-complete uses the same card bank when story sync is off.
    if (syncSecret && !syncStory) {
        for (u32 flagId = 0x10366u; flagId < kCardBoolEnd; ++flagId) {
            const u32 idx = flagId - kCardBoolBase;
            const bool now = fm->getBool(flagId);
            const bool was = bitGet(sCardBits, idx);
            if (now == was)
                continue;
            bitSet(sCardBits, idx, now);
            if (!now)
                continue;
            bitSet(sAuthorityCardBits, idx, true);
            publishCardOrSecret(WE_SECRET_COMPLETE, flagId);
        }
    }
}

bool applyStoryFlagWorldEvent(const CommWorldEvent &event) {
    if (isShineOrBlueCoinCardFlag(event.payload1))
        return true;

    if (isEphemeralSpawnDirectorBool(event.payload1)) {
        TFlagManager *fm = TFlagManager::smInstance;
        if (fm && fm->getBool(event.payload1)) {
            sApplyingRemote = true;
            fm->setBool(false, event.payload1);
            sApplyingRemote = false;
            OSReport("[SMSOBB] story-flag scrub spawn-director id=0x%08X\n", event.payload1);
        }
        markLocalTracker(event.payload1, false);
        return true;
    }

    if (event.payload0 == 0 || !isDurableCardBool(event.payload1))
        return true;

    TFlagManager *fm = TFlagManager::smInstance;
    bool changed = false;
    if (!applyBoolFlag(fm, event.payload1, event.payload0, &changed))
        return false;
    bitSet(sAuthorityCardBits, event.payload1 - kCardBoolBase, true);
    markLocalTracker(event.payload1, event.payload0 != 0);
    if (changed) {
        OSReport("[SMSOBB] story-flag apply id=0x%08X val=%u live=1\n", event.payload1,
                 event.payload0);
        // Bianco king gate etc. — MapEvent watch() polls getBool each frame.
        wakePlazaGeometryForFlag(event.payload1, changed);
    }
    return true;
}

bool applyTriggerFlagWorldEvent(const CommWorldEvent &event) {
    if (event.payload0 == 0 || !isDurablePlazaTrigger(event.payload1))
        return true;

    // Grow-only plaza hub overlay — admit even when not on Delfino so the next
    // plaza stageInit writes bits before loadAfter.
    admitPlazaTrigger(event.payload1);

    const bool onPlaza = gpMarDirector && gpMarDirector->mAreaID == kPlazaAreaId;
    if (!onPlaza) {
        if (kStoryFlagHotPathOsReport) {
            OSReport("[SMSOBB] trigger-flag defer id=0x%08X (pending plaza overlay)\n",
                     event.payload1);
        }
        return true;
    }

    TFlagManager *fm = TFlagManager::smInstance;
    bool changed = false;
    if (!applyBoolFlag(fm, event.payload1, event.payload0, &changed))
        return false;
    markLocalTracker(event.payload1, event.payload0 != 0);
    wakePlazaGeometryForFlag(event.payload1, changed);
    OSReport("[SMSOBB] trigger-flag apply id=0x%08X val=%u live=%u deferred=0\n", event.payload1,
             event.payload0, changed ? 1u : 0u);
    return true;
}

bool applySecretCompleteWorldEvent(const CommWorldEvent &event) {
    if (event.payload0 == 0 || !isDurableCardBool(event.payload1))
        return true;

    TFlagManager *fm = TFlagManager::smInstance;
    bool changed = false;
    if (!applyBoolFlag(fm, event.payload1, event.payload0, &changed))
        return false;
    bitSet(sAuthorityCardBits, event.payload1 - kCardBoolBase, true);
    markLocalTracker(event.payload1, event.payload0 != 0);
    if (changed)
        OSReport("[SMSOBB] secret-flag apply id=0x%08X val=%u live=1\n", event.payload1,
                 event.payload0);
    return true;
}

} // namespace smso

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

// decideNextScenario (area 1): 0x103AE set → scenario 2 (dolpic10 post-flood).
// Flooded (scenario 9 / dolpic9) is shine-gated (Shadow Mario episode shines
// 6/16/26/36/46/56/66) — those already ride ShineAuthority. Vanilla latches
// 0x103AE during Corona Mountain (area 52) load / flooded→Corona transition;
// stageInit tracker reseed must not swallow that edge without publishing.
constexpr u8 kCoronaMountainAreaId = 52u;
constexpr u32 kCoronaVisitedFlagId = 0x103AEu;

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
/// Published to the bridge but not yet echoed by the server (story/secret apply or
/// progress snapshot). Suppresses per-frame re-publish spam while still allowing a
/// bounded stage-enter retry, so a lost TCP publish cannot strand a card bit that only
/// this client knows about (peers stuck pre-flood on 0x103AE was exactly this).
static u8 sPendingConfirmCardBits[kCardByteCount] = {};
static u8 sCardConfirmRetryPasses = 0;
constexpr u8 kMaxCardConfirmRetryPasses = 3;
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

static bool publishFlagEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u32 flagId,
                             u8 value) {
    return smso::enqueueLocalWorldEvent(static_cast<u8>(type), courseId, episodeId, value, 0,
                                        flagId);
}

static bool publishCardOrSecret(smso::WorldEventType type, u32 flagId) {
    if (!gpMarDirector)
        return false;
    return publishFlagEvent(type, gpMarDirector->mAreaID, gpMarDirector->mEpisodeID, flagId, 1);
}

static bool publishPlazaTrigger(u32 flagId) {
    // Hub-global wire key: course=plaza, episode=wildcard. Server coalesces any
    // legacy plaza episode into the same authority slot.
    return publishFlagEvent(smso::WE_TRIGGER_FLAG, kPlazaAreaId, kPlazaHubEpisode, flagId, 1);
}

/// Local publish handed to the bridge — not yet an ack.
static void markCardPublished(u32 bitIndex) {
    bitSet(sPendingConfirmCardBits, bitIndex, true);
}

/// Server confirmed the bit (ownership apply / progress snapshot): stop retrying.
static void confirmCardPublished(u32 bitIndex) {
    bitSet(sAuthorityCardBits, bitIndex, true);
    bitSet(sPendingConfirmCardBits, bitIndex, false);
}

/// True while a publish is outstanding — suppresses duplicate emits between retries.
static bool cardPublishInFlight(u32 bitIndex) {
    return bitGet(sAuthorityCardBits, bitIndex) || bitGet(sPendingConfirmCardBits, bitIndex);
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

/// Queue durable card bits that are set in FlagManager but not yet in the
/// session authority cache. Used after stageInit tracker resets so load-time
/// latches (Corona visited 0x103AE, etc.) are not absorbed into the baseline
/// without ever publishing to the server.
/// <param name="retryUnconfirmed">
/// Also re-queue bits that were published locally but never echoed by the server. Server
/// accepts are grow-only and idempotent, so a duplicate is harmless; the caller bounds how
/// many times this happens (<see cref="kMaxCardConfirmRetryPasses"/>).
/// </param>
/// Returns true when at least one unconfirmed (already published) bit was re-queued.
static bool queueUnpublishedDurableCardSets(TFlagManager *fm, bool retryUnconfirmed = false) {
    if (!fm || !sSyncObserved)
        return false;

    bool any = false;
    bool retried = false;
    for (u32 i = 0; i < kCardBitCount; ++i) {
        const u32 flagId = kCardBoolBase + i;
        if (!isDurableCardBool(flagId))
            continue;
        if (!fm->getBool(flagId))
            continue;
        if (bitGet(sAuthorityCardBits, i))
            continue;
        if (bitGet(sPendingConfirmCardBits, i)) {
            if (!retryUnconfirmed)
                continue;
            retried = true;
        }

        bitSet(sBootstrapCardBits, i, true);
        any = true;
    }

    if (!any)
        return false;

    sCardBootstrapPending = true;
    sCardBootstrapCursor = 0;
    return retried;
}

/// Prefer immediate publish for the post-flood latch so peers unlock dolpic10
/// without waiting on the 948-bit bootstrap scan (bit index 942).
static bool tryPublishCoronaVisitedFlag(TFlagManager *fm, bool syncStory, bool syncSecret) {
    if (!fm || (!syncStory && !syncSecret))
        return false;
    if (!fm->getBool(kCoronaVisitedFlagId))
        return false;

    const u32 idx = kCoronaVisitedFlagId - kCardBoolBase;
    if (cardPublishInFlight(idx))
        return true;

    const smso::WorldEventType type =
        syncStory ? smso::WE_STORY_FLAG : smso::WE_SECRET_COMPLETE;
    if (!publishCardOrSecret(type, kCoronaVisitedFlagId))
        return false;

    markCardPublished(idx);
    markLocalTracker(kCoronaVisitedFlagId, true);
    bitSet(sBootstrapCardBits, idx, false);
    OSReport("[SMSOBB] story-flag emit-set id=0x%08X coronaVisited=1\n", kCoronaVisitedFlagId);
    return true;
}

// Publish at most one baseline set per frame.
static void publishNextBootstrapSet(TFlagManager *fm, bool syncStory, bool syncSecret) {
    if (!fm)
        return;

    while (sCardBootstrapPending && sCardBootstrapCursor < kCardBitCount) {
        const u32 flagId = kCardBoolBase + sCardBootstrapCursor++;
        const u32 bitIndex = sCardBootstrapCursor - 1;
        if (!isDurableCardBool(flagId) || !bitGet(sBootstrapCardBits, bitIndex) ||
            (!syncStory && (!syncSecret || flagId < 0x10366u)))
            continue;
        if (!publishCardOrSecret(syncStory ? smso::WE_STORY_FLAG : smso::WE_SECRET_COMPLETE,
                                 flagId)) {
            // Outbound queue full — retry this bit next frame instead of marking it
            // published and losing it.
            --sCardBootstrapCursor;
            return;
        }

        markCardPublished(bitIndex);
        bitSet(sBootstrapCardBits, bitIndex, false);
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

        if (sApplyingRemote || !now || !isDurableCardBool(flagId)) {
            bitSet(sCardBits, i, now);
            continue;
        }

        // Publish first — only advance the local tracker after enqueue so a queue-full
        // drop retries next frame instead of permanently silencing the bit.
        if (!publishCardOrSecret(syncStory ? smso::WE_STORY_FLAG : smso::WE_SECRET_COMPLETE,
                                 flagId))
            continue;

        bitSet(sCardBits, i, now);
        markCardPublished(i);
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

        if (sApplyingRemote || !now) {
            bitSet(sStageBits, bitIndex, now);
            continue;
        }

        if (!publishPlazaTrigger(flagId))
            continue;

        bitSet(sStageBits, bitIndex, now);
        admitPlazaTrigger(flagId);
        if (kStoryFlagHotPathOsReport) {
            OSReport("[SMSOBB] trigger-flag emit-set id=0x%08X plazaHub=1\n", flagId);
        }
    }
}

} // namespace

namespace smso {

void initStoryFlagSync() {
    memset(sAuthorityCardBits, 0, sizeof(sAuthorityCardBits));
    memset(sPendingConfirmCardBits, 0, sizeof(sPendingConfirmCardBits));
    sCardConfirmRetryPasses = 0;
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

void clearStoryFlagSessionProgress() {
    memset(sAuthorityCardBits, 0, sizeof(sAuthorityCardBits));
    memset(sPendingConfirmCardBits, 0, sizeof(sPendingConfirmCardBits));
    sCardConfirmRetryPasses = 0;
    memset(sBootstrapCardBits, 0, sizeof(sBootstrapCardBits));
    memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
    memset(sPlazaTriggerOverlay, 0, sizeof(sPlazaTriggerOverlay));
    sCardBootstrapPending = false;
    sStageBootstrapPending = false;
    sCardBootstrapCursor = 0;
    sStageBootstrapCursor = 0;
    resetStoryFlagTrackers();
    OSReport("[SMSOBB] story-flag session progress cleared\n");
}

void updateStoryFlagSyncConnectionState(bool connected, bool syncEnabled) {
    if (!connected) {
        if (sConnectionObserved) {
            memset(sAuthorityCardBits, 0, sizeof(sAuthorityCardBits));
            memset(sPendingConfirmCardBits, 0, sizeof(sPendingConfirmCardBits));
            sCardConfirmRetryPasses = 0;
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

    // Card bits latched during stage load (notably Corona visited 0x103AE) are
    // already set before the first captureLocal seed. Without a re-queue against
    // sAuthorityCardBits, the 0→1 edge is never published and peers stay on
    // flooded plaza forever.
    if (fm && sSyncObserved) {
        // Also re-drive bits published earlier that the server never echoed — a lost TCP
        // publish is otherwise invisible to this client forever.
        const bool retry = sCardConfirmRetryPasses < kMaxCardConfirmRetryPasses;
        if (queueUnpublishedDurableCardSets(fm, retry)) {
            ++sCardConfirmRetryPasses;
            OSReport("[SMSOBB] story-flag publish unconfirmed — retry pass %u/%u\n",
                     static_cast<u32>(sCardConfirmRetryPasses),
                     static_cast<u32>(kMaxCardConfirmRetryPasses));
        }
    }

    OSReport("[SMSOBB] story-flag stage enter course=%u/%u plazaOverlay=%u/%u/%u "
             "coronaVisited=%u\n",
             courseId, episodeId, sPlazaTriggerOverlay[0] ? 1u : 0u,
             sPlazaTriggerOverlay[1] ? 1u : 0u, sPlazaTriggerOverlay[2] ? 1u : 0u,
             (fm && fm->getBool(kCoronaVisitedFlagId)) ? 1u : 0u);
}

void scrubEphemeralSpawnDirectorFlagsOnStageExit() {
    TFlagManager *fm = TFlagManager::smInstance;

    // Flooded plaza → Corona (or any leave into Corona): vanilla may latch
    // 0x103AE during the loading FMV / exit path before Corona stageInit.
    // Publish immediately so peers unlock post-flood without each visiting.
    if (fm && sSyncObserved && gpApplication.mNextScene.mAreaID == kCoronaMountainAreaId) {
        smso::CommBuffer *buf = smso::getCommBuffer();
        const bool syncStory =
            buf && (buf->bridgeFlags & smso::BF_SYNC_STORY) != 0;
        const bool syncSecret =
            buf && (buf->bridgeFlags & smso::BF_SYNC_SECRET) != 0;
        if (!tryPublishCoronaVisitedFlag(fm, syncStory, syncSecret) &&
            fm->getBool(kCoronaVisitedFlagId)) {
            // Queue-full or flag not set yet — bootstrap / Corona stageInit retry.
            queueUnpublishedDurableCardSets(fm);
        }
    }

    if (gpApplication.mCurrentScene.mAreaID == 1 && gpApplication.mNextScene.mAreaID == 1)
        return;

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
        // Seed must not silence load-time latches already present in FlagManager.
        queueUnpublishedDurableCardSets(fm);
        tryPublishCoronaVisitedFlag(fm, syncStory, syncSecret);
        sTrackersReady = true;
        // Fall through so bootstrap / corona publish can flush this frame.
    }

    // Corona stage: prefer immediate 0x103AE publish over slow card bootstrap.
    if (gpMarDirector && gpMarDirector->mAreaID == kCoronaMountainAreaId)
        tryPublishCoronaVisitedFlag(fm, syncStory, syncSecret);

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
            if (publishCardOrSecret(WE_SECRET_COMPLETE, flagId))
                markCardPublished(idx);
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
    confirmCardPublished(event.payload1 - kCardBoolBase);
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
    confirmCardPublished(event.payload1 - kCardBoolBase);
    markLocalTracker(event.payload1, event.payload0 != 0);
    if (changed)
        OSReport("[SMSOBB] secret-flag apply id=0x%08X val=%u live=1\n", event.payload1,
                 event.payload0);
    return true;
}

} // namespace smso

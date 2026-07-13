#include "story_flag_sync.hpp"

#include "comm_buffer.hpp"
#include "world_sync.hpp"

#include <SMS/Manager/FlagManager.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
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

// Stage bools: 0x50000 .. 0x50063 (100 bits). Drive plaza gates / boats / flood props.
// These map onto TFlagManager::Type5Flag (reset by resetStage each stage enter).
constexpr u32 kStageBoolBase = 0x50000u;
constexpr u32 kStageBoolEnd = 0x50064u; // exclusive

// Type5Flag.mRedCoinSwitchPressed — bit 9 of the Type5 bank (0xE4 + bit index).
// Ephemeral per stage/episode: vanilla clears it on resetStage. Durable global sync of this
// bit re-arms EVERY red-coin switch mission on stage enter (timer + empty→red spawn).
constexpr u32 kRedCoinSwitchPressedFlagId = 0x50009u;

// Game bools: 0x30000 .. 0x3001C — small runtime bank used by a few story gates.
constexpr u32 kGameBoolBase = 0x30000u;
constexpr u32 kGameBoolEnd = 0x3001Du;

// One-shot spawn directors consumed by TMarDirector::decideMarioPosIdx (NTSC-U 0x802B97A8):
//   0x30001 — cleared on some plaza entry paths
//   0x30004 — Pinna unlock FMV sets this; decideMarioPosIdx clears it and forces MarioPosIdx
//             for the cannon reveal. Durable sync re-applied it on every plaza enter
//             (authority snapshot / story-flag apply after stageInit), so returns from
//             Ricco/Bianco/etc. always spawned at the Pinna cannon. Durable Pinna unlock
//             progress is card bool 0x10389 (decideNextScenario → dolpic8), not 0x30004.
constexpr u32 kSpawnDirectorFlag30001 = 0x30001u;
constexpr u32 kSpawnDirectorFlag30004 = 0x30004u;

constexpr u32 kCardBitCount = kCardBoolEnd - kCardBoolBase;   // 948
constexpr u32 kStageBitCount = kStageBoolEnd - kStageBoolBase; // 100
constexpr u32 kGameBitCount = kGameBoolEnd - kGameBoolBase;    // 29
constexpr u32 kCardScanSlice = 256u; // Full persistent bank in at most four frames.
constexpr bool kStoryFlagHotPathOsReport = false;

constexpr u32 kCardByteCount = (kCardBitCount + 7u) / 8u;
constexpr u32 kStageByteCount = (kStageBitCount + 7u) / 8u;
constexpr u32 kGameByteCount = (kGameBitCount + 7u) / 8u;

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

// Stage-session bits that must never be durable-synced across course/episode boundaries.
static bool isEphemeralStageSessionBool(u32 flagId) {
    return flagId == kRedCoinSwitchPressedFlagId;
}

static bool isEphemeralSpawnDirectorBool(u32 flagId) {
    return flagId == kSpawnDirectorFlag30001 || flagId == kSpawnDirectorFlag30004;
}

static bool isNonDurableBoolFlag(u32 flagId) {
    // Type3 is reset by resetGame and includes one-shot directors and other runtime
    // latches. It has no stable cross-stage identity, so none of it belongs in the
    // durable grow-only session set.
    return isEphemeralStageSessionBool(flagId) || isEphemeralSpawnDirectorBool(flagId) ||
           (flagId >= kGameBoolBase && flagId < kGameBoolEnd);
}

static u8 sCardBits[kCardByteCount] = {};
static u8 sStageBits[kStageByteCount] = {};
static u8 sGameBits[kGameByteCount] = {};
static u8 sAuthorityCardBits[kCardByteCount] = {};
static u8 sAuthorityStageBits[kStageByteCount] = {};
static u8 sBootstrapCardBits[kCardByteCount] = {};
static u8 sBootstrapStageBits[kStageByteCount] = {};
static bool sTrackersReady = false;
static bool sApplyingRemote = false;
static bool sConnectionObserved = false;
static bool sSyncObserved = false;
static bool sCardBootstrapPending = false;
static bool sStageBootstrapPending = false;
static u32 sCardBootstrapCursor = 0;
static u32 sStageBootstrapCursor = 0;
static u32 sCardScanCursor = 0;
static u8 sAuthorityStageCourse = 0xFF;
static u8 sAuthorityStageEpisode = 0xFF;
static bool sStageReconcilePending = false;

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

static void snapshotBank(TFlagManager *fm, u32 base, u32 count, u8 *outBits) {
    for (u32 i = 0; i < count; ++i)
        bitSet(outBits, i, fm->getBool(base + i));
}

static bool isDurableCardBool(u32 flagId) {
    return flagId >= kCardBoolBase && flagId < kCardBoolEnd &&
           !isShineOrBlueCoinCardFlag(flagId);
}

static bool isDurableStageBool(u32 flagId) {
    // Type5 is resetStage scratch space and most bits are reused for graffiti,
    // timers, switches, and other episode-local one-shots. Only these verified
    // MapEvent progression latches have durable shared meaning.
    return flagId == 0x50001u || flagId == 0x50002u || flagId == 0x50004u;
}

static void publishFlagEvent(smso::WorldEventType type, u32 flagId, u8 value) {
    if (!gpMarDirector)
        return;
    const u8 courseId = gpMarDirector->mAreaID;
    const u8 episodeId = gpMarDirector->mEpisodeID;
    smso::enqueueLocalWorldEvent(static_cast<u8>(type), courseId, episodeId, value, 0, flagId);
}

// Publish at most one baseline set per frame. Initial/local progress is merged into the
// server's grow-only set without bursting ~1,000 flags into the 32-entry module queue.
static void publishNextBootstrapSet(TFlagManager *fm, bool syncStory, bool syncSecret) {
    if (!fm)
        return;

    while (sCardBootstrapPending && sCardBootstrapCursor < kCardBitCount) {
        const u32 flagId = kCardBoolBase + sCardBootstrapCursor++;
        if (!isDurableCardBool(flagId) || !bitGet(sBootstrapCardBits, sCardBootstrapCursor - 1) ||
            (!syncStory && (!syncSecret || flagId < 0x10366u)))
            continue;
        bitSet(sAuthorityCardBits, flagId - kCardBoolBase, true);
        publishFlagEvent(syncStory ? smso::WE_STORY_FLAG : smso::WE_SECRET_COMPLETE,
                         flagId, 1);
        return;
    }
    if (sCardBootstrapCursor >= kCardBitCount)
        sCardBootstrapPending = false;

    while (sStageBootstrapPending && sStageBootstrapCursor < kStageBitCount) {
        const u32 flagId = kStageBoolBase + sStageBootstrapCursor++;
        if (!isDurableStageBool(flagId) ||
            !bitGet(sBootstrapStageBits, sStageBootstrapCursor - 1))
            continue;
        bitSet(sAuthorityStageBits, flagId - kStageBoolBase, true);
        publishFlagEvent(smso::WE_TRIGGER_FLAG, flagId, 1);
        return;
    }
    if (sStageBootstrapCursor >= kStageBitCount)
        sStageBootstrapPending = false;
}

static void reconcileAuthorityStageBits(TFlagManager *fm) {
    if (!sStageReconcilePending || !fm || !gpMarDirector)
        return;
    if (gpMarDirector->mAreaID != sAuthorityStageCourse ||
        gpMarDirector->mEpisodeID != sAuthorityStageEpisode) {
        sStageReconcilePending = false;
        return;
    }

    u32 changed = 0;
    sApplyingRemote = true;
    for (u32 i = 0; i < kStageBitCount; ++i) {
        if (!bitGet(sAuthorityStageBits, i))
            continue;
        const u32 flagId = kStageBoolBase + i;
        if (!fm->getBool(flagId)) {
            fm->setBool(true, flagId);
            ++changed;
        }
    }
    sApplyingRemote = false;
    sStageReconcilePending = false;
    if (changed != 0)
        OSReport("[SMSOBB] story-flag stage reconcile restored=%u course=%u/%u\n",
                 changed, sAuthorityStageCourse, sAuthorityStageEpisode);
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

static void markLocalTracker(u32 flagId, bool value) {
    if (flagId >= kCardBoolBase && flagId < kCardBoolEnd)
        bitSet(sCardBits, flagId - kCardBoolBase, value);
    else if (flagId >= kStageBoolBase && flagId < kStageBoolEnd)
        bitSet(sStageBits, flagId - kStageBoolBase, value);
    else if (flagId >= kGameBoolBase && flagId < kGameBoolEnd)
        bitSet(sGameBits, flagId - kGameBoolBase, value);
}

static void scanBankForChanges(TFlagManager *fm, u32 base, u32 start, u32 count, u8 *tracked,
                               smso::WorldEventType type, bool skipShineBlue) {
    const u32 end = start + count;
    for (u32 i = start; i < end; ++i) {
        const u32 flagId = base + i;
        if (skipShineBlue && isShineOrBlueCoinCardFlag(flagId))
            continue;

        const bool now = fm->getBool(flagId);
        const bool was = bitGet(tracked, i);
        if (now == was)
            continue;

        bitSet(tracked, i, now);
        if (sApplyingRemote)
            continue;
        // Durable shared progress is grow-only. Vanilla stage/reset clears update the
        // observer baseline but can never erase authority state or produce a clear event.
        if (!now || isNonDurableBoolFlag(flagId))
            continue;

        if (flagId >= kCardBoolBase && flagId < kCardBoolEnd)
            bitSet(sAuthorityCardBits, flagId - kCardBoolBase, true);
        else if (flagId >= kStageBoolBase && flagId < kStageBoolEnd)
            bitSet(sAuthorityStageBits, flagId - kStageBoolBase, true);
        publishFlagEvent(type, flagId, 1);
        if (kStoryFlagHotPathOsReport) {
            OSReport("[SMSOBB] story-flag emit-set type=%u id=0x%08X\n",
                     static_cast<u32>(type), flagId);
        }
    }
}

} // namespace

namespace smso {

void initStoryFlagSync() {
    memset(sAuthorityCardBits, 0, sizeof(sAuthorityCardBits));
    memset(sAuthorityStageBits, 0, sizeof(sAuthorityStageBits));
    memset(sBootstrapCardBits, 0, sizeof(sBootstrapCardBits));
    memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
    sConnectionObserved = false;
    sSyncObserved = false;
    sAuthorityStageCourse = 0xFF;
    sAuthorityStageEpisode = 0xFF;
    resetStoryFlagTrackers();
    OSReport("[SMSOBB] story/trigger flag sync ready (card+stage+game bools)\n");
}

void resetStoryFlagTrackers() {
    memset(sCardBits, 0, sizeof(sCardBits));
    memset(sStageBits, 0, sizeof(sStageBits));
    memset(sGameBits, 0, sizeof(sGameBits));
    sTrackersReady = false;
    sCardScanCursor = 0;
}

void updateStoryFlagSyncConnectionState(bool connected, bool syncEnabled) {
    if (!connected) {
        if (sConnectionObserved) {
            memset(sAuthorityCardBits, 0, sizeof(sAuthorityCardBits));
            memset(sAuthorityStageBits, 0, sizeof(sAuthorityStageBits));
            memset(sBootstrapCardBits, 0, sizeof(sBootstrapCardBits));
            memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
            sAuthorityStageCourse = 0xFF;
            sAuthorityStageEpisode = 0xFF;
            sCardBootstrapPending = false;
            sStageBootstrapPending = false;
            sStageReconcilePending = false;
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
    const bool sameAuthorityStage =
        courseId == sAuthorityStageCourse && episodeId == sAuthorityStageEpisode;
    if (!sameAuthorityStage) {
        memset(sAuthorityStageBits, 0, sizeof(sAuthorityStageBits));
        sAuthorityStageCourse = courseId;
        sAuthorityStageEpisode = episodeId;
    }

    // Runs even for same-course/same-episode reloads, which ID-change polling cannot see.
    resetStoryFlagTrackers();
    TFlagManager *fm = TFlagManager::smInstance;
    if (fm)
        snapshotBank(fm, kStageBoolBase, kStageBitCount, sBootstrapStageBits);
    else
        memset(sBootstrapStageBits, 0, sizeof(sBootstrapStageBits));
    sStageBootstrapPending = sSyncObserved;
    sStageBootstrapCursor = 0;
    sStageReconcilePending = sameAuthorityStage;
    OSReport("[SMSOBB] story-flag stage enter course=%u/%u preserve=%u\n",
             courseId, episodeId, sameAuthorityStage ? 1u : 0u);
}

void scrubEphemeralSpawnDirectorFlagsOnStageExit() {
    // Pinna unlock FMV: fireStreamingMovie sets 0x30004 then reloads plaza→plaza;
    // decideMarioPosIdx must still see the one-shot for the cannon reveal. Scrub only
    // when leaving a non-plaza stage (or plaza→elsewhere) so sticky sync cannot force
    // cannon spawn on the next plaza entry.
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

    reconcileAuthorityStageBits(fm);

    if (!sTrackersReady) {
        snapshotBank(fm, kCardBoolBase, kCardBitCount, sCardBits);
        snapshotBank(fm, kStageBoolBase, kStageBitCount, sStageBits);
        snapshotBank(fm, kGameBoolBase, kGameBitCount, sGameBits);
        sTrackersReady = true;
        return;
    }

    publishNextBootstrapSet(fm, syncStory, syncSecret);

    // Card story / nozzle / plaza gate bools (excludes shine + blue which have own paths).
    if (syncStory) {
        const u32 remaining = kCardBitCount - sCardScanCursor;
        const u32 scanCount = remaining < kCardScanSlice ? remaining : kCardScanSlice;
        scanBankForChanges(fm, kCardBoolBase, sCardScanCursor, scanCount, sCardBits,
                           WE_STORY_FLAG, true);
        sCardScanCursor += scanCount;
        if (sCardScanCursor >= kCardBitCount)
            sCardScanCursor = 0;
    }

    // Stage triggers (Ricco house, lighthouse, MareGate, flood props, etc.).
    if (syncStory || syncMission)
        scanBankForChanges(fm, kStageBoolBase, 0, kStageBitCount, sStageBits,
                           WE_TRIGGER_FLAG, false);

    // Type3 game bools are resetGame runtime/one-shot latches, not durable progression.
    // Their stable story outcomes live in the persistent card bank.

    // Secret-complete uses the same card bank; emit as WE_SECRET_COMPLETE for nozzle /
    // secret-adjacent high card bits when BF_SYNC_SECRET is on (and story is off).
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
            publishFlagEvent(WE_SECRET_COMPLETE, flagId, 1);
        }
    }
}

bool applyStoryFlagWorldEvent(const CommWorldEvent &event) {
    // Belt-and-suspenders: shine/blue card bits are owned by Shine/BlueCoinAuthority.
    // Never apply them via the story path even if a stale/buggy event arrives.
    if (isShineOrBlueCoinCardFlag(event.payload1))
        return true;

    // One-shot spawn directors: consume stale authority history and scrub FlagManager
    // so a prior sync cannot keep forcing the Pinna cannon spawn on plaza entry.
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

    // Authority is a sparse grow-only set. Ignore legacy clear events and all Type3
    // resetGame latches; neither can represent durable shared progress.
    if (event.payload0 == 0 || !isDurableCardBool(event.payload1))
        return true;

    TFlagManager *fm = TFlagManager::smInstance;
    bool changed = false;
    if (!applyBoolFlag(fm, event.payload1, event.payload0, &changed))
        return false;
    bitSet(sAuthorityCardBits, event.payload1 - kCardBoolBase, true);
    markLocalTracker(event.payload1, event.payload0 != 0);
    if (changed)
        OSReport("[SMSOBB] story-flag apply id=0x%08X val=%u\n", event.payload1, event.payload0);
    return true;
}

bool applyTriggerFlagWorldEvent(const CommWorldEvent &event) {
    // Consume stale durable history that may still carry mRedCoinSwitchPressed from before
    // this exclusion — never write it into FlagManager on a different stage.
    if (event.payload0 == 0 || !isDurableStageBool(event.payload1))
        return true;
    if (!gpMarDirector || event.courseId != gpMarDirector->mAreaID ||
        event.episodeId != gpMarDirector->mEpisodeID)
        return true;

    if (sAuthorityStageCourse != event.courseId ||
        sAuthorityStageEpisode != event.episodeId) {
        memset(sAuthorityStageBits, 0, sizeof(sAuthorityStageBits));
        sAuthorityStageCourse = event.courseId;
        sAuthorityStageEpisode = event.episodeId;
    }

    TFlagManager *fm = TFlagManager::smInstance;
    bool changed = false;
    if (!applyBoolFlag(fm, event.payload1, event.payload0, &changed))
        return false;
    bitSet(sAuthorityStageBits, event.payload1 - kStageBoolBase, true);
    markLocalTracker(event.payload1, event.payload0 != 0);
    if (changed)
        OSReport("[SMSOBB] trigger-flag apply id=0x%08X val=%u\n", event.payload1, event.payload0);
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
        OSReport("[SMSOBB] secret-flag apply id=0x%08X val=%u\n", event.payload1, event.payload0);
    return true;
}

} // namespace smso

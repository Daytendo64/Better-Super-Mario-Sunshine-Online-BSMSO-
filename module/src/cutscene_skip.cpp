#include "cutscene_skip.hpp"

#include <Dolphin/OS.h>
#include <SMS/Manager/FlagManager.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <sdk.h>

extern TApplication gpApplication;
extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;

namespace {

// User Gecko codes (NTSC-U), applied at runtime when auto-skip is allowed.
// 042B5EF4 38600001  -> li r3, 1 @ 0x802B5EF4 (TMovieDirector::direct)
// 042B5E8C 38600001  -> li r3, 1 @ 0x802B5E8C (TMovieDirector::direct)
constexpr u32 kMovieSkipPatch = 0x38600001u;
constexpr u32 kMovieSkipPatchAddr1 = 0x802B5EF4;
constexpr u32 kMovieSkipPatchAddr2 = 0x802B5E8C;
constexpr u32 kMovieVanillaWord1 = 0x4BFDEB31u; // sys/main.dol NTSC-U
constexpr u32 kMovieVanillaWord2 = 0x4BFDEB99u;

// 04142998 48000078  -> b +0x78 @ 0x80142998 (TGCConsole2::perform — DEBS alert path)
constexpr u32 kDebsAlertSkipPatch = 0x48000078u;
constexpr u32 kDebsAlertSkipPatchAddr = 0x80142998;
constexpr u32 kDebsAlertVanillaWord = 0x41800078u;

constexpr u8 kCoronaMountainAreaId = 52; // 0x34
constexpr u8 kDelfinoPlazaAreaId = 1;
constexpr u8 kFloodedPlazaEpisodeId = 9; // dolpic9 load scenario

struct RuntimePatchSite {
    u32 memAddr;
    u32 patchWord;
    u32 vanillaWord;
};

static const RuntimePatchSite sMoviePatch1 = {kMovieSkipPatchAddr1, kMovieSkipPatch,
                                              kMovieVanillaWord1};
static const RuntimePatchSite sMoviePatch2 = {kMovieSkipPatchAddr2, kMovieSkipPatch,
                                              kMovieVanillaWord2};
static const RuntimePatchSite sDebsPatch = {kDebsAlertSkipPatchAddr, kDebsAlertSkipPatch,
                                            kDebsAlertVanillaWord};

static void flushPatchSite(u32 *site) {
    DCFlushRange(site, sizeof(u32));
    ICInvalidateRange(site, sizeof(u32));
}

static void setRuntimePatch(const RuntimePatchSite &site, bool skipEnabled) {
    u32 *mem = reinterpret_cast<u32 *>(SMS_PORT_REGION(site.memAddr, 0, 0, 0));
    const u32 desired = skipEnabled ? site.patchWord : site.vanillaWord;
    if (*mem != desired) {
        *mem = desired;
        flushPatchSite(mem);
    }
}

static bool isFloodedPlazaScene(u8 areaId, u8 episodeId) {
    return areaId == kDelfinoPlazaAreaId && episodeId == kFloodedPlazaEpisodeId;
}

static bool isFloodedPlazaContext() {
    if (isFloodedPlazaScene(gpApplication.mCurrentScene.mAreaID,
                            gpApplication.mCurrentScene.mEpisodeID))
        return true;
    if (isFloodedPlazaScene(gpApplication.mPrevScene.mAreaID,
                            gpApplication.mPrevScene.mEpisodeID))
        return true;
    if (gpMarDirector &&
        isFloodedPlazaScene(gpMarDirector->mAreaID, gpMarDirector->mEpisodeID))
        return true;
    return false;
}

static bool isCoronaMountainArea(u8 areaId) { return areaId == kCoronaMountainAreaId; }

static bool isCoronaMountainContext() {
    if (isCoronaMountainArea(gpApplication.mCurrentScene.mAreaID))
        return true;
    if (isCoronaMountainArea(gpApplication.mNextScene.mAreaID))
        return true;
    if (gpMarDirector && isCoronaMountainArea(gpMarDirector->mAreaID))
        return true;
    return false;
}

static bool isCoronaFirstVisitCutscenePending() {
    if (!isCoronaMountainContext())
        return false;

    if (isCoronaMountainArea(gpApplication.mNextScene.mAreaID) && isFloodedPlazaContext())
        return true;

    if (isCoronaMountainArea(gpApplication.mCurrentScene.mAreaID) && isFloodedPlazaContext())
        return true;

    if (gpMarDirector && isCoronaMountainArea(gpMarDirector->mAreaID) && gpMarioAddress &&
        isFloodedPlazaScene(gpApplication.mPrevScene.mAreaID,
                            gpApplication.mPrevScene.mEpisodeID) &&
        (gpMarioAddress->mState & TMario::STATE_CUTSCENE) != 0)
        return true;

    return false;
}

using SMSGetShineIDofExStageFn = u8 (*)(u8);

static SMSGetShineIDofExStageFn shineIdOfExStageFn() {
    return reinterpret_cast<SMSGetShineIDofExStageFn>(
        SMS_PORT_REGION(0x802A8A98, 0x802A0798, 0, 0));
}

static bool isPendingExStageIntroMovie() {
    const u8 nextArea = gpApplication.mNextScene.mAreaID;
    SMSGetShineIDofExStageFn getShineId = shineIdOfExStageFn();
    if (!getShineId)
        return false;

    const u8 shineId = getShineId(nextArea);
    if (shineId == 0xFF)
        return false;

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return false;

    return !fm->getShineFlag(shineId);
}

static bool shouldBlockMovieAutoSkip() {
    if (isPendingExStageIntroMovie())
        return true;
    if (isCoronaFirstVisitCutscenePending())
        return true;
    return false;
}

static bool shouldBlockDebsAutoSkip() { return isCoronaFirstVisitCutscenePending(); }

} // namespace

namespace smso {

void updateCutsceneSkipPatches() {
    const bool movieSkipEnabled = !shouldBlockMovieAutoSkip();
    const bool debsSkipEnabled = !shouldBlockDebsAutoSkip();

    setRuntimePatch(sMoviePatch1, movieSkipEnabled);
    setRuntimePatch(sMoviePatch2, movieSkipEnabled);
    setRuntimePatch(sDebsPatch, debsSkipEnabled);
}

} // namespace smso

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

// User Gecko codes (NTSC-U).
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

// 0426078C 60000000  -> nop @ 0x8026078C
constexpr u32 kUserNopPatch = 0x60000000u;
constexpr u32 kUserNopPatchAddr = 0x8026078C;

constexpr u8 kCoronaMountainAreaId = 52; // 0x34 — flooded-plaza loading FMV dest
constexpr u8 kDelfinoPlazaAreaId = 1;
constexpr u8 kFloodedPlazaEpisodeId = 9; // dolpic9 load scenario

// Default to skip enabled; runtime may temporarily restore vanilla for first Corona FMV.
// Bowser ending stays skippable — stage_guard catches title/15 leave after skip.
SMS_WRITE_32(SMS_PORT_REGION(kMovieSkipPatchAddr1, 0x802ADE88, 0, 0), kMovieSkipPatch);
SMS_WRITE_32(SMS_PORT_REGION(kMovieSkipPatchAddr2, 0x802ADE20, 0, 0), kMovieSkipPatch);
SMS_WRITE_32(SMS_PORT_REGION(kDebsAlertSkipPatchAddr, 0x80135CF8, 0, 0), kDebsAlertSkipPatch);
SMS_WRITE_32(SMS_PORT_REGION(kUserNopPatchAddr, 0, 0, 0), kUserNopPatch);

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
    if (*mem != desired)
        *mem = desired;
    // Always flush: SMS_WRITE_32 may already match `desired` in RAM while the icache
    // still holds vanilla TMovieDirector::direct code from boot.
    flushPatchSite(mem);
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

// Only block skip for the flooded-plaza -> Corona loading FMV (not the whole visit).
// Post-Bowser ending may use the auto-skip patch again — stage_guard redirects a
// collapsed title leave (60→15/255) to plaza hub so skip cannot black-freeze.
static bool isCoronaLoadingMoviePending() {
    return gpApplication.mNextScene.mAreaID == kCoronaMountainAreaId &&
           gpApplication.mCurrentScene.mAreaID != kCoronaMountainAreaId &&
           isFloodedPlazaContext();
}

static bool shouldBlockMovieAutoSkip() { return isCoronaLoadingMoviePending(); }

} // namespace

namespace smso {

void updateCutsceneSkipPatches() {
    const bool movieSkipEnabled = !shouldBlockMovieAutoSkip();

    setRuntimePatch(sMoviePatch1, movieSkipEnabled);
    setRuntimePatch(sMoviePatch2, movieSkipEnabled);
    setRuntimePatch(sDebsPatch, true);
}

} // namespace smso

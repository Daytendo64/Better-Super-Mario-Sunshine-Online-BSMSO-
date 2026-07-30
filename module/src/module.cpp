#include "comm_buffer.hpp"
#include "puppets.hpp"
#include "remote_mario.hpp"
#include "remote_actor.hpp"
#include "remote_water_sync.hpp"
#include "voice_sync.hpp"
#include "world_sync.hpp"
#include "red_coin_sync.hpp"
#include "fruit_sync.hpp"
#include "npc_sync.hpp"
#include "monte_clean_sync.hpp"
#include "graffiti_clean_sync.hpp"
#include "hide_seek.hpp"
#include "stage_guard.hpp"
#include "connection_hud.hpp"
#include "cutscene_skip.hpp"
#include "story_flag_sync.hpp"
#include "mario_model_system.hpp"
#include "mario_tex_anim.hpp"
#include "jump_spray_rspmp.hpp"
#include "music_volume.hpp"

#include <BetterSMS/application.hxx>
#include <BetterSMS/game.hxx>
#include <BetterSMS/module.hxx>
#include <BetterSMS/player.hxx>
#include <BetterSMS/stage.hxx>
#include <Dolphin/OS.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/Manager/FlagManager.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/Player/Mario.hxx>

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;

BETTER_SMS_FOR_CALLBACK static bool appContextHeartbeat(TApplication *app) {
    (void)app;
    smso::publishMailboxAnchor();
    // Title / boot / main-loop contexts have no stageUpdate — still drive BGM volume.
    smso::updateMusicVolume();
    return true;
}

BETTER_SMS_FOR_CALLBACK static void movieLoopCutsceneSkipRefresh(TApplication *app) {
    (void)app;
    // Runs from gameLoopCallbackHandler immediately before director->direct(), including
    // CONTEXT_DIRECT_MOVIE where stage callbacks are inactive.
    smso::updateCutsceneSkipPatches();
    smso::CommBuffer *buf = smso::getCommBuffer();
    const bool connected =
        buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0;
    const bool storySync =
        connected &&
        (buf->bridgeFlags &
         (smso::BF_SYNC_STORY | smso::BF_SYNC_MISSION | smso::BF_SYNC_SECRET)) != 0;
    smso::updateStoryFlagSyncConnectionState(connected, storySync);
    // Bowser epilogue (movie 14) latches shine 0x77 here — stageUpdate is idle.
    if (connected)
        smso::tryPublishBowserEpilogueShine();
}

BETTER_SMS_FOR_CALLBACK static void stageInit(TMarDirector *director) {
    // Soft same-stage reloads can skip exitStageCallbacks. Tear perform-group /
    // actor residency before model re-init, but KEEP the remote heap+pool so
    // Hide & Seek death reloads (and other soft paths) can reattach remotes
    // immediately. keepHeapAndPool=false previously destroyed the kept pool when
    // exit was skipped / raced, leaving remotes missing after control-ready.
    if (smso::marioModelSystemIsLive())
        smso::clearRemoteActors(/*keepHeapAndPool=*/true);

    // Remount character archives before remote/local Mario bodies init this stage.
    smso::initMarioModelSystem();
    smso::initPuppets();
    smso::initRemoteMarioVisuals();
    smso::initRemoteActors();
    smso::initMarioVoiceSync();
    smso::initRemoteWaterSync();
    smso::initHideSeek();
    smso::initStageGuard();
    smso::ensureHipDropObjectHooks();
    smso::ensureMarioFruitHooks();
    smso::ensureNpcReactHooks();
    smso::applyHotelWarpMissionOverride(director);
    smso::normalizeSirenaSecretMissionEpisode(director);
    smso::ensureJumpSprayRspmpAnimRegistered();
    smso::updateCutsceneSkipPatches();
    // Same course/episode reloads must clear red-coin trackers; course/episode IDs alone
    // do not change, so captureLocalRedCoinProgress would otherwise keep stale state.
    smso::notifyRedCoinStageEnter();
    smso::notifyMonteCleanStageEnter();
    smso::notifyGraffitiCleanStageEnter();
    smso::notifyStoryFlagStageEnter(director->mAreaID, director->mEpisodeID);
    const u8 missionEp = static_cast<u8>(TFlagManager::smInstance->getFlag(0x40003));
    const u8 sceneEp = gpApplication.mCurrentScene.mEpisodeID;
    OSReport("[SMSOBB] Stage init area=%u director=%u sceneLoad=%u flagMission=%u\n",
             director->mAreaID, director->mEpisodeID, sceneEp, missionEp);
    if (director->mAreaID == 14)
        smso::reportCasinoHipDropSpawnDiag(director->mAreaID);
}

BETTER_SMS_FOR_CALLBACK static void stageUpdate(TMarDirector *director) {
    smso::publishMailboxAnchor();
    // Apply before hotel DistFade so slot-3 main volume multiplies with slot-0 mixes.
    smso::updateMusicVolume();

    if (gpMarDirector) {
        smso::guardHideSeekDeathBeforeWarp(director);
        smso::consumeWarpIntent();
        smso::syncHotelWarpMissionEpisode(director);
        smso::updateHotelShadowMarioBgm(director);
        smso::applyPendingWarpPoint(director);
    }

    // Hub / file-select (area 15): skip gameplay remote sync/draw, but still
    // prefetch packs + stagger body prewarm while connected so stage entry is cheap.
    if (director && smso::isNonGameplayStage(director->mAreaID)) {
        smso::updateRemoteModelPreload(director);
        return;
    }

    // Prefetch/prewarm during loading even before local Mario / STATE_NORMAL.
    smso::updateRemoteModelPreload(director);

    if (!gpMarDirector || !gpMarioAddress)
        return;

    smso::updateCutsceneSkipPatches();
    smso::updateMarioModelSystem(director);
    // Local Mario is constructed by the stage after initMarioModelSystem remounts
    // the pack; bind optional BTKs once visuals exist, then tick all bindings.
    // BodyAngleFree.prm is not loaded from the mario pack at construct time
    // (TParams uses params.szs) — re-apply pack override after Mario exists.
    smso::ensureLocalBodyAngleFreeParams(gpMarioAddress);
    smso::ensureMarioTexAnimsBound(gpMarioAddress);
    smso::updateJumpSprayRspmpAnim(gpMarioAddress);
    smso::exportLocalPlayer(gpMarioAddress, director);
    smso::updatePuppets(director);
    smso::updateRemoteActors(director);
    // Remote texture animation follows the visual schedule computed above;
    // local bindings remain full-rate.
    smso::updateAllMarioTexAnims(gpMarioAddress);
    smso::updateRemoteMarioVisuals(director);
    smso::updateMarioVoiceSync(director);
    smso::updateRemoteWaterSync();
    smso::updateHideSeek(director);
    smso::processWorldEvents();
    if (director && director->mAreaID == 14)
        smso::reportCasinoHipDropSpawnDiag(director->mAreaID);
}

BETTER_SMS_FOR_CALLBACK static void stageDraw2D(TMarDirector *director, const J2DOrthoGraph *graph) {
    (void)director;
    // Nametags first; grace wash draws last so the blue cover is truly fullscreen.
    smso::drawRemoteMarioOverlays(graph);
    smso::drawHideSeekGrace(graph);
}

BETTER_SMS_FOR_CALLBACK static void stageExit(TApplication *app) {
    (void)app;
    smso::scrubEphemeralSpawnDirectorFlagsOnStageExit();
    // Corona boss leave / movie collapse may have latched shine 0x77 just before
    // exit — flush before trackers reseed on the next stageInit.
    smso::tryPublishBowserEpilogueShine();
    smso::updateCutsceneSkipPatches();
    // Mark loading before clearPuppets so the bridge never observes Active+cleared
    // remotes and republishes Connected snapshots mid-teardown.
    smso::CommBuffer *buf = smso::getCommBuffer();
    if (buf && buf->magic == smso::COMM_MAGIC)
        buf->dolphinState = smso::DS_LOADING;
    smso::clearPuppets();
    smso::clearRemoteMarioVisuals();

    // Remote pools and their bounded custom-pack cache survive connected stage
    // exits together. This keeps all J3D pointers valid and avoids repeating
    // synchronous DVD loads on every warp. A body-variant pressure condition
    // may still request a full recycle; disconnect always tears everything down.
    // Remount retail before destroying the owning heap.
    const bool connected =
        buf && buf->magic == smso::COMM_MAGIC && (buf->bridgeFlags & smso::BF_CONNECTED) != 0;
    const bool recycleRemoteHeap =
        connected && (smso::remoteActorHeapNeedsRecycleOnStageExit() ||
                      smso::marioModelPackCacheNeedsRecycleOnStageExit());
    const bool keepAlive = connected && !recycleRemoteHeap;
    if (recycleRemoteHeap)
        OSReport("[SMSOBB] Stage exit recycling remote heap/model pack cache\n");
    smso::clearMarioModelSystem(keepAlive);
    smso::clearRemoteActors(keepAlive);

    smso::clearMarioVoiceSync();
    smso::resetFruitSyncForStage();
    smso::resetNpcSyncForStage();
    smso::onHideSeekStageExit();
    OSReport("[SMSOBB] Stage exit (remoteHeap=%s)\n", keepAlive ? "kept" : "destroyed");
}

BETTER_SMS_FOR_CALLBACK static void connectionHudInit(TApplication *app) {
    (void)app;
    smso::connection_hud::initSystem();
}

BETTER_SMS_FOR_CALLBACK static void connectionHudUpdate(TApplication *app) {
    smso::connection_hud::updateSystem(app);
}

BETTER_SMS_FOR_CALLBACK static void connectionHudDraw(TApplication *app, const J2DOrthoGraph *ortho) {
    smso::connection_hud::drawSystem(app, ortho);
}

BETTER_SMS_FOR_CALLBACK static void playerUpdateJumpSprayRspmp(TMario *player, bool isMario) {
    if (!isMario)
        return;
    smso::updateJumpSprayRspmpAnim(player);
}

static void registerCallbacks() {
    BetterSMS::Application::registerContextCallback(TApplication::CONTEXT_GAME_BOOT, appContextHeartbeat);
    BetterSMS::Application::registerContextCallback(TApplication::CONTEXT_GAME_BOOT_LOGO, appContextHeartbeat);
    BetterSMS::Application::registerContextCallback(TApplication::CONTEXT_DIRECT_MAIN_LOOP, appContextHeartbeat);
    BetterSMS::Game::addInitCallback(connectionHudInit);
    BetterSMS::Game::addLoopCallback(movieLoopCutsceneSkipRefresh);
    BetterSMS::Game::addLoopCallback(connectionHudUpdate);
    BetterSMS::Game::addPostDrawCallback(connectionHudDraw);
    BetterSMS::Player::addUpdateCallback(playerUpdateJumpSprayRspmp);
    BetterSMS::Stage::addInitCallback(stageInit);
    BetterSMS::Stage::addUpdateCallback(stageUpdate);
    BetterSMS::Stage::addDraw2DCallback(stageDraw2D);
    BetterSMS::Stage::addExitCallback(stageExit);
}

KURIBO_MODULE_BEGIN("Better Super Mario Sunshine Online", "BSMSO", "v1.1") {
    KURIBO_EXECUTE_ON_LOAD {
        registerCallbacks();
        smso::initCommBuffer();
        smso::initWorldSync();
        smso::bootHideSeek();
        smso::updateCutsceneSkipPatches();
        OSReport("[SMSOBB] v1.1 loaded (comm @ %p)\n", smso::getCommBuffer());
        OSReport("[SMSO] body-reclaim=stage-only\n");
    }
}
KURIBO_MODULE_END()

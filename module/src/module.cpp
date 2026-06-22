#include "comm_buffer.hpp"
#include "puppets.hpp"
#include "remote_mario.hpp"
#include "remote_actor.hpp"
#include "remote_water_sync.hpp"
#include "voice_sync.hpp"
#include "world_sync.hpp"
#include "hide_seek.hpp"

#include <BetterSMS/application.hxx>
#include <BetterSMS/module.hxx>
#include <BetterSMS/stage.hxx>
#include <Dolphin/OS.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/Player/Mario.hxx>

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;

BETTER_SMS_FOR_CALLBACK static bool appContextHeartbeat(TApplication *app) {
    (void)app;
    smso::publishMailboxAnchor();
    return true;
}

BETTER_SMS_FOR_CALLBACK static void stageInit(TMarDirector *director) {
    smso::initPuppets();
    smso::initRemoteMarioVisuals();
    smso::initRemoteActors();
    smso::initMarioVoiceSync();
    smso::initRemoteWaterSync();
    smso::initWorldSync();
    smso::initHideSeek();
    OSReport("[SMSOBB] Stage init area=%u episode=%u\n", director->mAreaID, director->mEpisodeID);
}

BETTER_SMS_FOR_CALLBACK static void stageUpdate(TMarDirector *director) {
    smso::publishMailboxAnchor();

    if (gpMarDirector) {
        smso::consumeWarpIntent();
        smso::skipEntryDemoIfPending(director);
        smso::skipCutscenesIfConnected(director);
        smso::applyPendingWarpPoint(director);
    }

    if (!gpMarDirector || !gpMarioAddress)
        return;

    smso::exportLocalPlayer(gpMarioAddress, director);
    smso::updatePuppets(director);
    smso::updateRemoteActors(director);
    smso::updateRemoteMarioVisuals(director);
    smso::updateMarioVoiceSync(director);
    smso::updateRemoteWaterSync();
    smso::updateHideSeek(director);
    smso::processWorldEvents();
}

BETTER_SMS_FOR_CALLBACK static void stageDraw2D(TMarDirector *director, const J2DOrthoGraph *graph) {
    (void)director;
    smso::drawRemoteMarioOverlays(graph);
}

BETTER_SMS_FOR_CALLBACK static void stageExit(TApplication *app) {
    (void)app;
    smso::clearPuppets();
    smso::clearRemoteMarioVisuals();
    smso::clearRemoteActors();
    smso::clearMarioVoiceSync();
    smso::onHideSeekStageExit();
    OSReport("[SMSOBB] Stage exit\n");
}

static void registerCallbacks() {
    BetterSMS::Application::registerContextCallback(TApplication::CONTEXT_GAME_BOOT, appContextHeartbeat);
    BetterSMS::Application::registerContextCallback(TApplication::CONTEXT_GAME_BOOT_LOGO, appContextHeartbeat);
    BetterSMS::Application::registerContextCallback(TApplication::CONTEXT_DIRECT_MAIN_LOOP, appContextHeartbeat);
    BetterSMS::Stage::addInitCallback(stageInit);
    BetterSMS::Stage::addUpdateCallback(stageUpdate);
    BetterSMS::Stage::addDraw2DCallback(stageDraw2D);
    BetterSMS::Stage::addExitCallback(stageExit);
}

KURIBO_MODULE_BEGIN("Better Super Mario Sunshine Online", "BSMSO", "v1.0") {
    KURIBO_EXECUTE_ON_LOAD {
        registerCallbacks();
        smso::initCommBuffer();
        OSReport("[SMSOBB] v1.0 loaded (comm @ %p)\n", smso::getCommBuffer());
    }
}
KURIBO_MODULE_END()

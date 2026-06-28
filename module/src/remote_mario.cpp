#include "remote_mario.hpp"

#include "comm_buffer.hpp"
#include "nametag_system.hpp"
#include "hide_seek.hpp"
#include "remote_actor.hpp"

#include <Dolphin/mem.h>
#include <Dolphin/string.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <JSystem/JUtility/JUTColor.hxx>
#include <SMS/System/MarDirector.hxx>

namespace smso {

namespace {

struct RemoteVisual {
    bool active;
    u8 slot;
    char name[MAX_PLAYER_NAME];
};

static RemoteVisual gRemotes[MAX_REMOTE_SLOTS];

static bool isFiniteVec(f32 x, f32 y, f32 z) {
    return x == x && y == y && z == z;
}

static bool isReasonableWorldPos(f32 x, f32 y, f32 z) {
    return x > -50000.0f && x < 50000.0f && y > -50000.0f && y < 50000.0f && z > -50000.0f &&
           z < 50000.0f;
}

static bool isSameStage(const CommBuffer *buf, const PlayerSnapshot &snap) {
    return snap.stageId == buf->localSnapshot.stageId &&
           snap.episodeId == buf->localSnapshot.episodeId;
}

static bool isValidSnapshot(const PlayerSnapshot &snap) {
    return snap.connected != 0 && isFiniteVec(snap.position.x, snap.position.y, snap.position.z) &&
           isReasonableWorldPos(snap.position.x, snap.position.y, snap.position.z);
}

static void setFallbackName(char *name, u8 slot) {
    name[0] = 'P';
    name[1] = static_cast<char>('1' + (slot % 9));
    name[2] = '\0';
}

static JUtility::TColor toTagColor(u8 r, u8 g, u8 b) {
    return JUtility::TColor(r, g, b, 255);
}

static nametag::Appearance readNameTagAppearance(const CommBuffer *buf, const PlayerSnapshot &snap) {
    nametag::Appearance appearance{};
    appearance.textTopColor = toTagColor(255, 255, 255);
    appearance.textBottomColor = appearance.textTopColor;
    appearance.outlineColor = toTagColor(0, 0, 0);
    appearance.hasOutlineColor = true;
    appearance.gradientEnabled = false;

    const NameTagAppearance *sidecar = snap.slot < MAX_REMOTE_SLOTS
                                           ? &buf->remoteNameTagAppearances[snap.slot]
                                           : &buf->localNameTagAppearance;

    u8 textTopR = 255, textTopG = 255, textTopB = 255;
    u8 textBottomR = 255, textBottomG = 255, textBottomB = 255;
    u8 outlineR = 0, outlineG = 0, outlineB = 0;
    bool gradientEnabled = false;
    if (readNameTagAppearanceFromSidecar(*sidecar, textTopR, textTopG, textTopB, textBottomR,
                                         textBottomG, textBottomB, outlineR, outlineG, outlineB,
                                         gradientEnabled)) {
        appearance.textTopColor = toTagColor(textTopR, textTopG, textTopB);
        appearance.textBottomColor = toTagColor(textBottomR, textBottomG, textBottomB);
        appearance.outlineColor = toTagColor(outlineR, outlineG, outlineB);
        appearance.gradientEnabled = gradientEnabled;
    }

    return appearance;
}

static void copyNameTagText(char *name, const PlayerSnapshot &snap) {
    copyPurePlayerName(name, snap.name);
}

} // namespace

bool shouldUseParticleProxy(u8 slot) {
    (void)slot;
    return false;
}

void initRemoteMarioVisuals() {
    memset(gRemotes, 0, sizeof(gRemotes));
    nametag::initSystem();
}

void clearRemoteMarioVisuals() {
    memset(gRemotes, 0, sizeof(gRemotes));
    nametag::clearSystem();
}

void updateRemoteMarioVisuals(TMarDirector *director) {
    (void)director;
    CommBuffer *buf = getCommBuffer();

    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
        RemoteVisual &r = gRemotes[slot];
        const u8 playerId = snap.connected != 0 ? snap.slot : static_cast<u8>(slot);

        if (!isSameStage(buf, snap) || !isValidSnapshot(snap) ||
            !hasRemoteBodyForSlot(playerId) || !shouldDrawHideSeekNameTag(playerId)) {
            r.active = false;
            nametag::updateSlot(playerId, false, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, {}, nullptr);
            continue;
        }

        r.active = true;
        r.slot = playerId;
        copyNameTagText(r.name, snap);
        if (r.name[0] == '\0')
            setFallbackName(r.name, playerId);

        f32 bodyX, bodyY, bodyZ;
        f32 anchorX, anchorY, anchorZ;
        if (!getRemoteBodyPosition(playerId, bodyX, bodyY, bodyZ) ||
            !getRemoteHeadAnchorPosition(playerId, anchorX, anchorY, anchorZ)) {
            r.active = false;
            nametag::updateSlot(playerId, false, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, {}, nullptr);
            continue;
        }

        const nametag::Appearance appearance = readNameTagAppearance(buf, snap);
        nametag::updateSlot(playerId, true, anchorX, anchorY, anchorZ, bodyX, bodyY, bodyZ, appearance,
                            r.name);
    }
}

void drawRemoteMarioOverlays(const J2DOrthoGraph *graph) {
    nametag::drawAll(graph);
}

} // namespace smso

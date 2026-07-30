#include "remote_mario.hpp"

#include "comm_buffer.hpp"
#include "nametag_system.hpp"
#include "hide_seek.hpp"
#include "remote_actor.hpp"
#include "episode_equiv.hpp"

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
    u8 hideStreak;
    char name[MAX_PLAYER_NAME];
    nametag::Appearance appearance;
};

static RemoteVisual gRemotes[MAX_REMOTE_SLOTS];
static constexpr u8 kNameTagHideDebounceFrames = 3;

static bool isFiniteVec(f32 x, f32 y, f32 z) {
    return x == x && y == y && z == z;
}

static bool isReasonableWorldPos(f32 x, f32 y, f32 z) {
    return x > -50000.0f && x < 50000.0f && y > -50000.0f && y < 50000.0f && z > -50000.0f &&
           z < 50000.0f;
}

static bool isSameStage(const CommBuffer *buf, const PlayerSnapshot &snap) {
    const u8 localArea =
        gpMarDirector ? gpMarDirector->mAreaID : buf->localSnapshot.stageId;
    const u8 localEpisode =
        gpMarDirector ? gpMarDirector->mEpisodeID : buf->localSnapshot.episodeId;
    const u8 remoteArea = snap.stageId;
    const u8 remoteEpisode = snap.episodeId;
    // Hotel/casino load↔mission — keep nametags with bodies across death remount.
    return episode_equiv::sameStage(remoteArea, remoteEpisode, localArea, localEpisode);
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

static nametag::Appearance readNameTagAppearance(const CommBuffer *buf, u8 playerId,
                                                 const nametag::Appearance *fallback) {
    nametag::Appearance appearance{};
    appearance.textTopColor = toTagColor(255, 255, 255);
    appearance.textBottomColor = appearance.textTopColor;
    appearance.outlineColor = toTagColor(0, 0, 0);
    appearance.hasOutlineColor = true;
    appearance.gradientEnabled = false;

    // Always index by mailbox slot. Embedded snap.slot is not authoritative and on
    // clients often matches LocalSlot, whose remote appearance entry is cleared every
    // flush — that flashed tags between real colors and defaults while moving.
    const NameTagAppearance *sidecar = playerId < MAX_REMOTE_SLOTS
                                           ? &buf->remoteNameTagAppearances[playerId]
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
        return appearance;
    }

    // A transient Dolphin mailbox clobber (client poll race) must not flash the
    // tag to default white — keep the last good colors until a valid sidecar returns.
    if (fallback)
        return *fallback;
    return appearance;
}

static void copyNameTagText(char *name, const PlayerSnapshot &snap) {
    copyPurePlayerName(name, snap.name);
}

static bool isTruncatedPrefixOf(const char *full, const char *maybeTruncated) {
    if (!full || !maybeTruncated || full[0] == '\0' || maybeTruncated[0] == '\0')
        return false;

    u32 fullLen = 0;
    while (fullLen < MAX_PLAYER_NAME && full[fullLen] != '\0')
        ++fullLen;
    u32 truncLen = 0;
    while (truncLen < MAX_PLAYER_NAME && maybeTruncated[truncLen] != '\0')
        ++truncLen;

    // Legacy overlay packing truncates "Player" to "Playe" (5 chars). Reject any
    // shorter non-empty prefix of the already-stable name so tags cannot flicker.
    if (truncLen == 0 || truncLen >= fullLen || fullLen < 6)
        return false;
    for (u32 i = 0; i < truncLen; ++i) {
        if (full[i] != maybeTruncated[i])
            return false;
    }
    return true;
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
        // The mailbox array index is authoritative. A stale/malformed embedded
        // slot byte must not make one player's tag sample another body/appearance.
        const u8 playerId = static_cast<u8>(slot);

        // Unoccupied slot that is already cleared: its nametag runtime was reset
        // when it went inactive and nothing can change it until a snapshot
        // arrives. A 2-player session should not pay nine full tag evaluations
        // (projection, camera distance, hide&seek queries) every frame.
        if (!r.active && snap.connected == 0)
            continue;

        const bool validSnapshot = isValidSnapshot(snap);
        const bool sameStage = !validSnapshot || isSameStage(buf, snap);
        const bool bodyReady = hasRemoteBodyForSlotLoose(playerId);
        const bool hideSeekOk = shouldDrawHideSeekNameTag(playerId) &&
                                !shouldSuppressRemoteHiderFromSeekerGrace(playerId);
        const bool wantHide =
            (validSnapshot && !sameStage) || !bodyReady || !hideSeekOk;

        if (wantHide) {
            if (r.active && r.hideStreak < kNameTagHideDebounceFrames) {
                ++r.hideStreak;
                // Leave the last nametag draw state untouched for a few frames.
                continue;
            }

            r.active = false;
            r.hideStreak = 0;
            nametag::updateSlot(playerId, false, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, {}, nullptr);
            continue;
        }

        f32 bodyX, bodyY, bodyZ;
        f32 anchorX, anchorY, anchorZ;
        if (!getRemoteBodyPosition(playerId, bodyX, bodyY, bodyZ) ||
            !getRemoteHeadAnchorPosition(playerId, anchorX, anchorY, anchorZ)) {
            if (r.active && r.hideStreak < kNameTagHideDebounceFrames) {
                ++r.hideStreak;
                continue;
            }
            r.active = false;
            r.hideStreak = 0;
            nametag::updateSlot(playerId, false, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, {}, nullptr);
            continue;
        }

        r.hideStreak = 0;

        if (validSnapshot) {
            const bool wasActive = r.active;
            r.active = true;
            r.slot = playerId;
            char nextName[MAX_PLAYER_NAME];
            copyNameTagText(nextName, snap);
            if (nextName[0] == '\0')
                setFallbackName(nextName, playerId);
            // Keep the longer stable name when a legacy overlay briefly shrinks it.
            if (!(wasActive && isTruncatedPrefixOf(r.name, nextName))) {
                memcpy(r.name, nextName, MAX_PLAYER_NAME);
            }
            r.appearance =
                readNameTagAppearance(buf, playerId, wasActive ? &r.appearance : nullptr);
        } else if (!r.active) {
            nametag::updateSlot(playerId, false, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, {}, nullptr);
            continue;
        }

        // Remote actors already debounce invalid snapshots for three updates.
        // Keep the tag anchored to that still-live body for exactly the same
        // interval instead of resetting alpha on a one-packet mailbox gap.
        nametag::updateSlot(playerId, true, anchorX, anchorY, anchorZ, bodyX, bodyY, bodyZ, r.appearance,
                            r.name);
    }
}

void drawRemoteMarioOverlays(const J2DOrthoGraph *graph) {
    nametag::drawAll(graph);
}

} // namespace smso

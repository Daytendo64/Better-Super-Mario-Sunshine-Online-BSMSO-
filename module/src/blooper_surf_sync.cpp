#include "blooper_surf_sync.hpp"
#include "collectible_scan.hpp"
#include "remote_actor.hpp"
#include <SMS/Map/Map.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/macros.h>
#include <sdk.h>

#include <JSystem/J3D/J3DModel.hxx>
#include <JSystem/JDrama/JDRGraphics.hxx>
#include <JSystem/JKernel/JKRHeap.hxx>

extern TMap *gpMap;

namespace smso {

namespace {

// doldecomp MoveBG/MapObjManager.hpp — shared surf-blooper SDLModelData + MActor templates.
constexpr u32 kMapObjManagerSurfGessoModelDataOffset = 0x98u;
constexpr u32 kMapObjManagerRedGessoOffset = 0x9Cu;
constexpr u32 kMapObjManagerYellowGessoOffset = 0xA0u;
constexpr u32 kMapObjManagerGreenGessoOffset = 0xA4u;
constexpr u32 kMapObjManagerRedGessoColorOffset = 0xA8u;
constexpr u32 kMapObjManagerYellowGessoColorOffset = 0xB0u;
constexpr u32 kMapObjManagerGreenGessoColorOffset = 0xB8u;
constexpr u8 kSurfGessoCloneTypeNone = 0xFFu;
// Retail SMS_MakeMActorFromSDLModelData uses flag 3 + SDLModel(..., 1).
constexpr u32 kSurfGessoSdlModelFlags = 3u;
constexpr u32 kGxTevReg1 = 1u;

struct RemoteSurfGessoActor {
    void *anmData;
    J3DModel *model;
};

struct SurfGessoColorS10 {
    s16 r;
    s16 g;
    s16 b;
    s16 a;
};

using RemoteSurfGessoSetBckFn = void (*)(void *, const char *);
using RemoteSurfGessoSetFrameRateFn = void (*)(void *, f32, int);
using RemoteSurfGessoPerformFn = void (*)(void *, u32, JDrama::TGraphics *);
using MakeMActorFromSDLModelDataFn = void *(*)(void *sdlModelData, void *anmData, u32 flags);
using InitPacketMatColorFn = void (*)(J3DModel *, u32 tevRegId, const SurfGessoColorS10 *);
// MW complete destructor: second arg non-zero deletes the object after dtors run.
using RemoteSurfGessoModelDtorFn = void (*)(J3DModel *, s16);

RemoteSurfGessoSetBckFn sSurfGessoSetBck =
    reinterpret_cast<RemoteSurfGessoSetBckFn>(SMS_PORT_REGION(0x80238E40, 0, 0, 0));
RemoteSurfGessoSetFrameRateFn sSurfGessoSetFrameRate =
    reinterpret_cast<RemoteSurfGessoSetFrameRateFn>(SMS_PORT_REGION(0x80238E7C, 0, 0, 0));
RemoteSurfGessoPerformFn sSurfGessoPerform =
    reinterpret_cast<RemoteSurfGessoPerformFn>(SMS_PORT_REGION(0x802391BC, 0, 0, 0));
MakeMActorFromSDLModelDataFn sMakeMActorFromSDLModelData =
    reinterpret_cast<MakeMActorFromSDLModelDataFn>(
        SMS_PORT_REGION(0x8023E81C, 0x802365A8, 0, 0));
InitPacketMatColorFn sInitPacketMatColor =
    reinterpret_cast<InitPacketMatColorFn>(SMS_PORT_REGION(0x801BA650, 0x801B2508, 0, 0));
// Prefer SDLModel dtor — clones are SDLModel (0xAC), not plain J3DModel.
RemoteSurfGessoModelDtorFn sSurfGessoModelDtor =
    reinterpret_cast<RemoteSurfGessoModelDtorFn>(SMS_PORT_REGION(0x8023D308, 0x80235094, 0, 0));

void *resolveSurfGessoTemplate(u8 gessoType) {
    if (!gpMapObjManager)
        return nullptr;

    const u8 *mgr = reinterpret_cast<const u8 *>(gpMapObjManager);
    switch (gessoType) {
    default:
    case 0:
        return *reinterpret_cast<void *const *>(mgr + kMapObjManagerRedGessoOffset);
    case 1:
        return *reinterpret_cast<void *const *>(mgr + kMapObjManagerYellowGessoOffset);
    case 2:
        return *reinterpret_cast<void *const *>(mgr + kMapObjManagerGreenGessoOffset);
    }
}

void *resolveSurfGessoModelData() {
    if (!gpMapObjManager)
        return nullptr;
    const u8 *mgr = reinterpret_cast<const u8 *>(gpMapObjManager);
    return *reinterpret_cast<void *const *>(mgr + kMapObjManagerSurfGessoModelDataOffset);
}

const SurfGessoColorS10 *resolveSurfGessoColor(u8 gessoType) {
    if (!gpMapObjManager)
        return nullptr;

    const u8 *mgr = reinterpret_cast<const u8 *>(gpMapObjManager);
    u32 offset = kMapObjManagerRedGessoColorOffset;
    switch (gessoType) {
    case 1:
        offset = kMapObjManagerYellowGessoColorOffset;
        break;
    case 2:
        offset = kMapObjManagerGreenGessoColorOffset;
        break;
    default:
        break;
    }
    return reinterpret_cast<const SurfGessoColorS10 *>(mgr + offset);
}

void syncSurfGessoBaseMtx(TMario *mario, void *gesso) {
    auto *actor = reinterpret_cast<RemoteSurfGessoActor *>(gesso);
    if (!actor || !actor->model || !mario->mModelData || !mario->mModelData->mModel)
        return;

    MTXCopy(mario->mModelData->mModel->mBaseMtx, actor->model->mBaseMtx);
}

f32 blooperSurfGessoPlaybackRate(const TMario *mario) {
    if (!mario)
        return 0.5f;

    const f32 forward = sqrtf(mario->mSpeed.x * mario->mSpeed.x + mario->mSpeed.z * mario->mSpeed.z);
    f32 rate = 0.35f + forward * 0.04f;
    if (rate < 0.35f)
        rate = 0.35f;
    if (rate > 1.25f)
        rate = 1.25f;
    return rate;
}

void applySurfWaterContext(TMario *body) {
    if (!body)
        return;

    body->mWaterHeight = body->mTranslation.y;
    body->mAttributes.mIsWater = true;
    body->mAttributes.mIsShallowWater = false;
}

// Stage templates (mRed/Yellow/GreenGesso) are singletons. Sharing them across local +
// remotes (or across remotes) crashes when multiple players surf. Clone via the retail
// SMS_MakeMActorFromSDLModelData helper so each remote gets a real SDLModel (0xAC) with
// the correct vtable — plain J3DModel(modelData, 0, 0) crashes in MActor::perform.
void *createRemoteSurfGessoClone(u8 gessoType, J3DModel **outModel) {
    if (outModel)
        *outModel = nullptr;

    void *sdlModelData = resolveSurfGessoModelData();
    void *templateActor = resolveSurfGessoTemplate(gessoType);
    if (!sdlModelData || !templateActor)
        return nullptr;

    auto *templateSurf = reinterpret_cast<RemoteSurfGessoActor *>(templateActor);
    if (!templateSurf->anmData || !templateSurf->model)
        return nullptr;

    JKRHeap *remoteHeap = borrowRemoteActorHeap();
    if (!remoteHeap)
        return nullptr;

    JKRHeap *previousHeap = JKRHeap::sCurrentHeap;
    remoteHeap->becomeCurrentHeap();

    void *actorMem = sMakeMActorFromSDLModelData(sdlModelData, templateSurf->anmData,
                                                 kSurfGessoSdlModelFlags);
    if (!actorMem) {
        if (previousHeap)
            previousHeap->becomeCurrentHeap();
        return nullptr;
    }

    auto *actor = reinterpret_cast<RemoteSurfGessoActor *>(actorMem);
    if (!actor->model) {
        operator delete(actorMem);
        if (previousHeap)
            previousHeap->becomeCurrentHeap();
        return nullptr;
    }

    if (const SurfGessoColorS10 *color = resolveSurfGessoColor(gessoType))
        sInitPacketMatColor(actor->model, kGxTevReg1, color);

    sSurfGessoSetBck(actorMem, "surfgeso_run1");
    sSurfGessoSetFrameRate(actorMem, 0.5f, 0);

    if (previousHeap)
        previousHeap->becomeCurrentHeap();

    if (outModel)
        *outModel = actor->model;
    return actorMem;
}

void releaseClone(BlooperSurfSlot &slot) {
    if (!slot.cloneActor && !slot.cloneModel) {
        slot.cloneType = kSurfGessoCloneTypeNone;
        slot.bindPending = false;
        return;
    }

    JKRHeap *remoteHeap = borrowRemoteActorHeap();
    if (remoteHeap) {
        JKRHeap *previousHeap = JKRHeap::sCurrentHeap;
        remoteHeap->becomeCurrentHeap();

        // Model is owned by the MActor from SMS_MakeMActorFromSDLModelData. SDLModel's
        // complete dtor (2nd arg > 0) runs the chain and operator-deletes the model.
        // Anm objects from setBck live on the remote heap until that heap is destroyed.
        if (slot.cloneModel)
            sSurfGessoModelDtor(slot.cloneModel, 1);
        slot.cloneModel = nullptr;
        if (slot.cloneActor)
            operator delete(slot.cloneActor);

        if (previousHeap)
            previousHeap->becomeCurrentHeap();
    }

    slot.cloneActor = nullptr;
    slot.cloneModel = nullptr;
    slot.cloneType = kSurfGessoCloneTypeNone;
    slot.bindPending = false;
}

void *ensureRemoteSurfGessoClone(BlooperSurfSlot &slot, u8 gessoType) {
    if (slot.cloneActor && slot.cloneType == gessoType)
        return slot.cloneActor;

    releaseClone(slot);

    J3DModel *model = nullptr;
    void *actor = createRemoteSurfGessoClone(gessoType, &model);
    if (!actor)
        return nullptr;

    slot.cloneActor = actor;
    slot.cloneModel = model;
    slot.cloneType = gessoType;
    slot.bindPending = false;
    return actor;
}

bool bindRemoteSurfGesso(TMario *body, BlooperSurfSlot &slot, u8 gessoType, bool active) {
    if (!body)
        return false;

    if (!active) {
        releaseClone(slot);
        body->mSurfGesso = nullptr;
        body->mSurfGessoID = 0;
        slot.gessoType = 0;
        return true;
    }

    if (gessoType >= kSurfGessoTypeCount)
        gessoType = 0;

    void *gesso = ensureRemoteSurfGessoClone(slot, gessoType);
    if (!gesso) {
        body->mSurfGesso = nullptr;
        body->mSurfGessoID = 0;
        slot.gessoType = gessoType;
        slot.bindPending = true;
        return false;
    }

    body->mSurfGessoID = gessoType;
    body->mSurfGesso = gesso;
    slot.gessoType = gessoType;
    slot.bindPending = false;
    sSurfGessoSetBck(gesso, "surfgeso_run1");
    sSurfGessoSetFrameRate(gesso, blooperSurfGessoPlaybackRate(body), 0);
    return true;
}

} // namespace

bool isLocalBlooperSurf(const TMario *mario) {
    return mario && isBlooperSurfState(mario->mState);
}

u8 exportBlooperSurfWaterByte(const TMario *mario) {
    if (!mario)
        return 0;
    return static_cast<u8>(mario->mSurfGessoID & kSurfGessoTypeMask);
}

void exportBlooperSurfSnapshotFields(const TMario *mario, PlayerSnapshot &snap) {
    if (!isLocalBlooperSurf(mario))
        return;

    snap.water = exportBlooperSurfWaterByte(mario);
    if (snap.animId != kBlooperSurfRideShellAnim &&
        isBlooperSurfRideState(snapshotMarioState(snap))) {
        snap.animId = kBlooperSurfRideShellAnim;
    }
}

void initBlooperSurfSync() {}

void releaseBlooperSurfClone(BlooperSurfSlot &slot) {
    releaseClone(slot);
}

void resetBlooperSurfSlot(BlooperSurfSlot &slot) {
    releaseClone(slot);
    slot.gessoType = 0;
    slot.bindPending = false;
}

bool remoteBlooperSurfUsesVfx(u32 state) {
    return isBlooperSurfState(state);
}

void applyRemoteBlooperSurfSnapshot(TMario *body, BlooperSurfSlot &slot, const PlayerSnapshot &snap) {
    if (!body)
        return;

    const bool surfing = snapshotIsBlooperSurfing(snap);

    if (surfing) {
        const u8 gessoType = snapshotSurfGessoType(snap);
        slot.gessoType = gessoType;
        bindRemoteSurfGesso(body, slot, gessoType, true);
        applySurfWaterContext(body);
        return;
    }

    if (slot.gessoType != 0 || body->mSurfGesso || slot.bindPending) {
        slot.gessoType = 0;
        bindRemoteSurfGesso(body, slot, 0, false);
    }
}

void updateRemoteBlooperSurfFrame(TMario *body, BlooperSurfSlot *slot, JDrama::TGraphics *graphics) {
    if (!body || !slot)
        return;

    if (!isBlooperSurfState(body->mState)) {
        if (slot->bindPending || slot->gessoType != 0 || body->mSurfGesso)
            bindRemoteSurfGesso(body, *slot, 0, false);
        return;
    }

    applySurfWaterContext(body);

    if (slot->bindPending || !body->mSurfGesso || slot->gessoType != body->mSurfGessoID)
        bindRemoteSurfGesso(body, *slot, slot->gessoType, true);

    if (!body->mSurfGesso)
        return;

    sSurfGessoSetFrameRate(body->mSurfGesso, blooperSurfGessoPlaybackRate(body), 0);

    if (!graphics || !body->mModelData || !body->mModelData->mModel)
        return;

    syncSurfGessoBaseMtx(body, body->mSurfGesso);
    // Anim/calc only. With a valid SDLModel clone, retail calcView/entryModels keep the
    // surf draw flag and call MActor::perform(4 / 0x200) for view + entry.
    sSurfGessoPerform(body->mSurfGesso, 2, graphics);
}

} // namespace smso

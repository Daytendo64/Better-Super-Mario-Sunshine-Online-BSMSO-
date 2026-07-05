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

constexpr u32 kMapObjManagerRedGessoOffset = 0x9Cu;
constexpr u32 kMapObjManagerYellowGessoOffset = 0xA0u;
constexpr u32 kMapObjManagerGreenGessoOffset = 0xA4u;
constexpr u8 kSurfGessoCloneTypeNone = 0xFFu;
constexpr size_t kRemoteSurfGessoActorSize = 0x48u;

struct RemoteSurfGessoActor {
    void *anmData;
    J3DModel *model;
};

using RemoteSurfGessoSetBckFn = void (*)(void *, const char *);
using RemoteSurfGessoSetFrameRateFn = void (*)(void *, f32, int);
using RemoteSurfGessoPerformFn = void (*)(void *, u32, JDrama::TGraphics *);
using RemoteSurfGessoActorCtorFn = void (*)(void *, void *);
using RemoteSurfGessoActorSetModelFn = void (*)(void *, J3DModel *, u32);
using RemoteSurfGessoModelDtorFn = void (*)(J3DModel *);

RemoteSurfGessoSetBckFn sSurfGessoSetBck =
    reinterpret_cast<RemoteSurfGessoSetBckFn>(SMS_PORT_REGION(0x80238E40, 0, 0, 0));
RemoteSurfGessoSetFrameRateFn sSurfGessoSetFrameRate =
    reinterpret_cast<RemoteSurfGessoSetFrameRateFn>(SMS_PORT_REGION(0x80238E7C, 0, 0, 0));
RemoteSurfGessoPerformFn sSurfGessoPerform =
    reinterpret_cast<RemoteSurfGessoPerformFn>(SMS_PORT_REGION(0x802391BC, 0, 0, 0));
RemoteSurfGessoActorCtorFn sSurfGessoActorCtor =
    reinterpret_cast<RemoteSurfGessoActorCtorFn>(SMS_PORT_REGION(0x8023A408, 0x80232194, 0, 0));
RemoteSurfGessoActorSetModelFn sSurfGessoActorSetModel =
    reinterpret_cast<RemoteSurfGessoActorSetModelFn>(
        SMS_PORT_REGION(0x8023A110, 0x80231E9C, 0, 0));
RemoteSurfGessoModelDtorFn sSurfGessoModelDtor =
    reinterpret_cast<RemoteSurfGessoModelDtorFn>(SMS_PORT_REGION(0x802DDEA0, 0x802D6048, 0, 0));

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

void *createRemoteSurfGessoClone(u8 gessoType, J3DModel **outModel) {
    if (outModel)
        *outModel = nullptr;

    void *templateActor = resolveSurfGessoTemplate(gessoType);
    if (!templateActor)
        return nullptr;

    auto *templateSurf = reinterpret_cast<RemoteSurfGessoActor *>(templateActor);
    if (!templateSurf->anmData || !templateSurf->model || !templateSurf->model->mModelData)
        return nullptr;

    JKRHeap *remoteHeap = borrowRemoteActorHeap();
    if (!remoteHeap)
        return nullptr;

    JKRHeap *previousHeap = JKRHeap::sCurrentHeap;
    remoteHeap->becomeCurrentHeap();

    J3DModel *model = new (remoteHeap, 4) J3DModel(templateSurf->model->mModelData, 0, 0);
    void *actorMem = operator new(kRemoteSurfGessoActorSize, remoteHeap, 4);
    if (!model || !actorMem) {
        if (model) {
            sSurfGessoModelDtor(model);
            operator delete(model);
        }
        if (actorMem)
            operator delete(actorMem);
        if (previousHeap)
            previousHeap->becomeCurrentHeap();
        return nullptr;
    }

    sSurfGessoActorCtor(actorMem, templateSurf->anmData);
    sSurfGessoActorSetModel(actorMem, model, 0);
    sSurfGessoSetBck(actorMem, "surfgeso_run1");
    sSurfGessoSetFrameRate(actorMem, 0.5f, 0);

    if (previousHeap)
        previousHeap->becomeCurrentHeap();

    if (outModel)
        *outModel = model;
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

        if (slot.cloneModel) {
            sSurfGessoModelDtor(slot.cloneModel);
            operator delete(slot.cloneModel);
        }
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

    const u32 rawState = snapshotMarioState(snap);
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

    (void)rawState;
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
    sSurfGessoPerform(body->mSurfGesso, 2, graphics);
}

} // namespace smso

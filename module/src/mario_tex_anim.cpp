#include "mario_tex_anim.hpp"

#include <Dolphin/OS.h>
#include <Dolphin/printf.h>
#include <Dolphin/string.h>

#include <JSystem/J3D/J3DAnimation.hxx>
#include <JSystem/J3D/J3DModel.hxx>
#include <JSystem/JKernel/JKRFileLoader.hxx>
#include <JSystem/JKernel/JKRHeap.hxx>
#include <SMS/M3DUtil/MActor.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/MarioCap.hxx>
#include <SMS/macros.h>
#include <SMS/raw_fn.hxx>

#include <BetterSMS/module.hxx>
#include <BetterSMS/player.hxx>

#include "comm_buffer.hpp"
#include "mario_model_system.hpp"
#include "remote_actor.hpp"

extern TMario *gpMarioAddress;

namespace smso {
namespace {

// doldecomp TMultiBtk — binds J3DAnmTextureSRTKey + advances frames.
// Layout: count, anm**, modelData*, frameCtrl*
class TMultiBtk {
public:
    TMultiBtk(int count, J3DModelData *modelData);
    void setNthData(int n, J3DAnmTextureSRTKey *anm);
    void update();
};

using TMultiBtkCtorFn = TMultiBtk *(*)(TMultiBtk *self, int count, J3DModelData *modelData);
using TMultiBtkSetNthFn = void (*)(TMultiBtk *self, int n, J3DAnmTextureSRTKey *anm);
using TMultiBtkUpdateFn = void (*)(TMultiBtk *self);
using J3DAnmLoadFn = J3DAnmBase *(*)(const void *data);
// TScreenTexture::replace — swaps H_kagemario_dummy for the live framebuffer tex.
using ScreenTexReplaceFn = void (*)(void *screenTex, J3DModelData *modelData, const char *name);

static TMultiBtkCtorFn sMultiBtkCtor =
    reinterpret_cast<TMultiBtkCtorFn>(SMS_PORT_REGION(0x8023422C, 0x8022C180, 0, 0));
static TMultiBtkSetNthFn sMultiBtkSetNth =
    reinterpret_cast<TMultiBtkSetNthFn>(SMS_PORT_REGION(0x80234130, 0x8022C084, 0, 0));
static TMultiBtkUpdateFn sMultiBtkUpdate =
    reinterpret_cast<TMultiBtkUpdateFn>(SMS_PORT_REGION(0x802340A4, 0x8022BFF8, 0, 0));
static J3DAnmLoadFn sAnmLoad =
    reinterpret_cast<J3DAnmLoadFn>(SMS_PORT_REGION(0x802E8CA4, 0x802E0E4C, 0, 0));
static ScreenTexReplaceFn sScreenTexReplace = reinterpret_cast<ScreenTexReplaceFn>(
    SMS_PORT_REGION(0x8022D360, 0x802252B4, 0, 0));
static void **const sGpScreenTexture =
    reinterpret_cast<void **>(SMS_PORT_REGION(0x8040E0BC, 0x80405784, 0, 0));

static const char *const kShadowDummyTex = "H_kagemario_dummy";

// Placement-new size: int + 3 pointers = 0x10 on GameCube.
constexpr u32 kMultiBtkBytes = 0x10;
constexpr u32 kMaxBindings = 12; // local + remotes + spare
constexpr u32 kMaxTracks = 8;
constexpr u32 kMaxMActors = 10;
constexpr u8 kMActorRetryIntervalFrames = 30;
constexpr u8 kUnknownArchiveSlot = 0xFF;

struct TexTrack {
    TMultiBtk *multi;
    J3DAnmTextureSRTKey *anm;
};

struct TexBinding {
    TMario *mario;
    TexTrack tracks[kMaxTracks];
    u32 trackCount;
    MActor *mActors[kMaxMActors];
    MActorAnmData *mAnmData;
    u32 mActorCount;
    u8 archiveSlot; // CommBuffer slot whose pack owns the BTKs
    bool logged;
    bool screenTexApplied;
    bool mActorReady;
    u8 mActorRetryFrames;
    // True when bind saw Shadow body BTKs — do NOT re-query the live mount
    // (local pack is usually remounted after remote spawn).
    bool wantsShadowMActors;
    // When true, BSE playerUpdateHandler/playerDrawHandler own frameUpdate +
    // entryIn/Out via TPlayerData::mMActor[]. Remotes keep false.
    bool bseOwnsMActors;
};

static TexBinding sBindings[kMaxBindings];
static u32 sBindingCount = 0;

static BetterSMS::Player::TPlayerData *tryGetPlayerData(TMario *mario);

static const char *const kBodyBtkPath = "/mario/btk/ma_mdl1.btk";
static const char *const kBodyBtkCustomPath = "/mario/custom/ma_mdl1.btk";
static const char *const kHand2RBtkPath = "/mario/btk/ma_hnd2r.btk";
static const char *const kHand3RBtkPath = "/mario/btk/ma_hnd3r.btk";
static const char *const kHand4RBtkPath = "/mario/btk/ma_hnd4r.btk";
static const char *const kCap1BtkPath = "/mario/btk/ma_cap1.btk";
static const char *const kCap3BtkPath = "/mario/btk/ma_cap3.btk";

static TexBinding *findBinding(TMario *mario) {
    if (!mario)
        return nullptr;
    for (u32 i = 0; i < sBindingCount; ++i) {
        if (sBindings[i].mario == mario)
            return &sBindings[i];
    }
    return nullptr;
}

// Remote Shadow MActors / TMultiBtk must outlive stage teardown when the body
// pool is kept — always the remote heap. Local Mario dies with the stage, so
// local TexAnim must use the live stage heap (sCurrentHeap), NOT sSystemHeap.
//
// Bug (dolphin.log 06:11 Gelato→plaza): local MActors on sSystemHeap were never
// freed by clearBinding (memset only). After three successful Shadow binds
// (05:58 / 06:02 / 06:09) the fourth hung mid-create after ma_hnd4r, before
// ma_cap1 — identical body/hand pointers to the prior plaza success.
static JKRHeap *selectTexAnimHeap(TMario *mario) {
    if (mario && mario != gpMarioAddress)
        return borrowRemoteActorHeap();
    // Prefer the stage heap while Mario is live (stageUpdate / construct).
    if (JKRHeap::sCurrentHeap && JKRHeap::sCurrentHeap != JKRHeap::sSystemHeap)
        return JKRHeap::sCurrentHeap;
    if (JKRHeap::sCurrentHeap)
        return JKRHeap::sCurrentHeap;
    if (JKRHeap::sRootHeap)
        return JKRHeap::sRootHeap;
    return JKRHeap::sSystemHeap;
}

// Detach BSE draw ownership before dropping a binding. Do NOT delete MActors,
// MActorAnmData, or TMultiBtk here:
//   - setBtk installs MaterialAnm on live J3D models; deleting the MActor while
//     those models still draw leaves dangling MaterialAnm and hard-crashes
//     (black screen / silent abort, often before OSReport lands in dolphin.log).
//   - Local allocations live on the stage heap and are reclaimed on stage exit.
//   - Remote allocations live on the remote heap with the pool body; abandoned
//     bodies are never drawn again, and heap recycle frees them on disconnect.
// The previous hard-delete path (stage-heap "leak fix") caused cold-boot /
// rebind crashes. Stage-heap selection already fixes the sSystemHeap warp leak.
static void detachBindingDrawOwnership(TexBinding &b) {
    if (b.bseOwnsMActors && b.mario && b.mario == gpMarioAddress) {
        BetterSMS::Player::TPlayerData *params = tryGetPlayerData(b.mario);
        if (params) {
            params->mMActorAnmData = nullptr;
            for (u32 i = 0; i < kMaxMActors; ++i)
                params->mMActor[i] = nullptr;
        }
    }
    b.bseOwnsMActors = false;
}

static void clearBinding(TexBinding &b, bool detachDrawOwnership = false) {
    if (detachDrawOwnership)
        detachBindingDrawOwnership(b);
    // Drop tracking only — heap lifetime owns the allocations (see above).
    memset(&b, 0, sizeof(b));
    b.archiveSlot = kUnknownArchiveSlot;
}

// Drop null-mario slots so remote rebuilds cannot permanently fill the table.
static void compactBindings() {
    u32 dst = 0;
    for (u32 i = 0; i < sBindingCount; ++i) {
        if (!sBindings[i].mario)
            continue;
        if (dst != i)
            sBindings[dst] = sBindings[i];
        ++dst;
    }
    for (u32 i = dst; i < sBindingCount; ++i)
        clearBinding(sBindings[i]);
    sBindingCount = dst;
}

static u8 resolveArchiveSlot(TMario *mario, u8 hint) {
    if (hint != kUnknownArchiveSlot)
        return hint;
    if (!mario)
        return kUnknownArchiveSlot;
    if (mario == gpMarioAddress) {
        CommBuffer *buf = getCommBuffer();
        return buf ? buf->localSlot : 0;
    }
    for (u8 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (getRemoteBodyForSlotLoose(i) == mario)
            return i;
    }
    return kUnknownArchiveSlot;
}

static TexBinding *allocBinding(TMario *mario) {
    TexBinding *existing = findBinding(mario);
    if (existing) {
        clearBinding(*existing, /*detachDrawOwnership=*/true);
        existing->mario = mario;
        return existing;
    }
    if (sBindingCount >= kMaxBindings) {
        compactBindings();
        if (sBindingCount >= kMaxBindings)
            return nullptr;
    }
    TexBinding &b = sBindings[sBindingCount++];
    clearBinding(b);
    b.mario = mario;
    return &b;
}

static J3DAnmTextureSRTKey *loadBtk(const char *path) {
    if (!path || !sAnmLoad)
        return nullptr;
    void *res = JKRFileLoader::getGlbResource(path);
    if (!res)
        return nullptr;
    J3DAnmBase *base = sAnmLoad(res);
    return reinterpret_cast<J3DAnmTextureSRTKey *>(base);
}

static bool addTrack(TexBinding &b, J3DModelData *modelData, const char *path) {
    if (!modelData || !path || b.trackCount >= kMaxTracks)
        return false;
    if (!sMultiBtkCtor || !sMultiBtkSetNth)
        return false;

    JKRHeap *previousHeap = JKRHeap::sCurrentHeap;
    JKRHeap *texHeap = selectTexAnimHeap(b.mario);
    if (!texHeap)
        return false;
    texHeap->becomeCurrentHeap();

    J3DAnmTextureSRTKey *anm = loadBtk(path);
    if (!anm) {
        if (previousHeap)
            previousHeap->becomeCurrentHeap();
        return false;
    }

    void *mem = ::operator new(kMultiBtkBytes);
    if (!mem) {
        if (previousHeap)
            previousHeap->becomeCurrentHeap();
        OSReport("[BSMSO] TexAnim: TMultiBtk alloc failed for %s\n", path);
        return false;
    }

    TMultiBtk *multi = sMultiBtkCtor(reinterpret_cast<TMultiBtk *>(mem), 1, modelData);
    if (previousHeap)
        previousHeap->becomeCurrentHeap();
    if (!multi) {
        OSReport("[BSMSO] TexAnim: TMultiBtk ctor failed for %s\n", path);
        return false;
    }

    sMultiBtkSetNth(multi, 0, anm);

    TexTrack &t = b.tracks[b.trackCount++];
    t.multi = multi;
    t.anm = anm;
    OSReport("[BSMSO] TexAnim bound %s @ %p (modelData=%p)\n", path, anm, modelData);
    return true;
}

static J3DModelData *modelDataOf(J3DModel *model) {
    return model ? model->mModelData : nullptr;
}

static bool packHasShadowBodyBtk() {
    return JKRFileLoader::getGlbResource(kBodyBtkPath) != nullptr ||
           JKRFileLoader::getGlbResource(kBodyBtkCustomPath) != nullptr;
}

static bool packHasAnyCustomBtk() {
    return packHasShadowBodyBtk() ||
           JKRFileLoader::getGlbResource(kCap1BtkPath) != nullptr ||
           JKRFileLoader::getGlbResource(kHand2RBtkPath) != nullptr;
}

// Shadow Mario / Luigi body paint is H_kagemario_dummy — a placeholder that must
// be swapped for the live screen-capture texture. Cap UV scrolls work via BTK
// alone; body/hands need this replace. Run AFTER Mario construct (not in BSE
// initMario) — calling it during init freezes level load.
static int callReplace(J3DModelData *modelData) {
    if (!modelData || !sScreenTexReplace || !sGpScreenTexture || !*sGpScreenTexture)
        return -1;
    return reinterpret_cast<int (*)(void *, J3DModelData *, const char *)>(sScreenTexReplace)(
        *sGpScreenTexture, modelData, kShadowDummyTex);
}

static void tryReplaceOne(J3DModelData *md, const char *label, bool log, u32 *hits) {
    if (!md)
        return;
    int rc = callReplace(md);
    if (log)
        OSReport("[BSMSO] TexAnim: replace %s md=%p rc=%d\n", label, md, rc);
    if (rc > 0 && hits)
        ++(*hits);
}

static u32 applyShadowScreenTexture(TMario *mario, bool log) {
    if (!mario)
        return 0;

    u32 hits = 0;

    tryReplaceOne(mario->mBodyModelData, "body", log, &hits);
    if (mario->mModelData && mario->mModelData->mModel) {
        J3DModelData *drawn = mario->mModelData->mModel->mModelData;
        if (drawn == mario->mBodyModelData) {
            if (log)
                OSReport("[BSMSO] TexAnim: drawn==body md=%p\n", drawn);
        } else {
            tryReplaceOne(drawn, "drawn", log, &hits);
        }
    }

    tryReplaceOne(modelDataOf(mario->mHandModel2R), "h2r", log, &hits);
    tryReplaceOne(modelDataOf(mario->mHandModel2L), "h2l", log, &hits);
    tryReplaceOne(modelDataOf(mario->mHandModel3R), "h3r", log, &hits);
    tryReplaceOne(modelDataOf(mario->mHandModel3L), "h3l", log, &hits);
    tryReplaceOne(modelDataOf(mario->mHandModel4R), "h4r", log, &hits);

    if (mario->mCap) {
        tryReplaceOne(modelDataOf(mario->mCap->mCap1), "cap1", log, &hits);
        tryReplaceOne(modelDataOf(mario->mCap->mCap3), "cap3", log, &hits);
        tryReplaceOne(modelDataOf(mario->mCap->mDiverHelm), "helm", log, &hits);
        tryReplaceOne(modelDataOf(mario->mCap->maGlass1), "glass", log, &hits);
    }

    if (log) {
        OSReport("[BSMSO] TexAnim: screen texture done mario=%p hits=%u gp=%p\n", mario, hits,
                 *sGpScreenTexture);
    }
    return hits;
}

// TScreenTexture::replace mutates shared gpScreenTexture / ModelData state.
// A second Shadow instance (remote) while local is also Shadow crashes Dolphin.
// Remotes keep MActor BTK for UV scroll; only the local player gets the replace.
static bool isLocalMarioForScreenTex(TMario *mario) {
    return mario && mario == gpMarioAddress;
}

// One-shot screen-tex path used by bind + update + first-residency. Remotes
// latch without calling replace so we never retry every frame.
static void ensureShadowScreenTexture(TexBinding &b, TMario *mario) {
    if (b.screenTexApplied || !mario)
        return;

    if (!isLocalMarioForScreenTex(mario)) {
        b.screenTexApplied = true;
        OSReport("[BSMSO] TexAnim: skip screen texture on remote mario=%p slot=%u\n", mario,
                 b.archiveSlot);
        return;
    }

    u32 hits = applyShadowScreenTexture(mario, true);
    // Only latch when at least one dummy was swapped (models may not be ready
    // on the first bind attempt).
    if (hits > 0)
        b.screenTexApplied = true;
}

static void configureFrameCtrl(MActor *actor, int anmType, f32 framerate) {
    J3DFrameCtrl *ctrl = actor->getFrameCtrl(anmType);
    if (!ctrl)
        return;
    ctrl->mFrameRate = framerate;
    ctrl->mAnimState = J3DFrameCtrl::LOOP;
}

static MActor *createMActorForModel(MActorAnmData *anmData, J3DModel *model, f32 configuredFramerate,
                                    const char *modelName, bool logBtk, bool *outHasBtk) {
    if (outHasBtk)
        *outHasBtk = false;
    if (!anmData || !model || !modelName)
        return nullptr;

    MActor *actor = new MActor(anmData);
    if (!actor)
        return nullptr;

    actor->setModel(model, 0);
    // Cached DLs freeze tex-matrix state and fight BTK / screen-tex updates.
    actor->offMakeDL();

    f32 framerate = configuredFramerate * static_cast<f32>(SMSGetAnmFrameRate__Fv());
    bool hasBtk = false;

    // Shadow packs only need BTK for the reflective goop look. Do NOT bind
    // BCK/BLK/BRK/BPK/BTP on these MActors — those channels belong to the main
    // TMario animation system. Binding them here can pull mismatched clips into
    // the BSE draw path (entryIn) and abort before the stage finishes loading.
    //
    // Bind only an exact-name BTK (same as BetterSMS). Never alias ma_hnd2r →
    // ma_hnd2l / ma_hnd3r → ma_hnd3l: Shadow left-hand BMDs ship with TEX1=0
    // (no textures / no H_kagemario_dummy). setBtk of a right-hand UV clip onto
    // those models survives bind, then BSE playerDrawHandler→entryIn aborts
    // (dolphin.log ends at "deferred MActor ready" / "screen texture done").
    if (actor->checkAnmFileExist(modelName, MActor::BTK)) {
        actor->setBtk(modelName);
        configureFrameCtrl(actor, MActor::BTK, framerate);
        hasBtk = true;
    }

    if (outHasBtk)
        *outHasBtk = hasBtk;
    if (logBtk)
        OSReport("[BSMSO] TexAnim: MActor %s btk=%d model=%p\n", modelName, hasBtk ? 1 : 0, model);

    return actor;
}

static const char *resolveAnmFolder() {
    // Prefer BetterSMS layout; fall back to our btk/ inject so existing packs
    // work without a re-import.
    if (JKRFileLoader::findFirstFile("/mario/custom") != nullptr)
        return "mario/custom";
    if (JKRFileLoader::getGlbResource(kBodyBtkPath) != nullptr)
        return "mario/btk";
    return nullptr;
}

static BetterSMS::Player::TPlayerData *tryGetPlayerData(TMario *mario) {
    // Remotes have no TPlayerData; getData() spam-logs on miss.
    if (!mario || mario != gpMarioAddress)
        return nullptr;
    return BetterSMS::Player::getData(mario);
}

// Allocate AnmData/MActors on the remote heap for remotes, or the live stage
// heap for local (see selectTexAnimHeap). Never sSystemHeap for local — those
// allocations survived clearBinding and leaked across warps.
static bool tryCreateShadowMActors(TexBinding &b, TMario *mario) {
    if (b.mActorReady)
        return true;
    if (!mario || !mario->mModelData || !mario->mModelData->mModel || !mario->mCap)
        return false;

    const char *anmFolder = resolveAnmFolder();
    if (!anmFolder)
        return false;

    JKRHeap *previousHeap = JKRHeap::sCurrentHeap;
    JKRHeap *texHeap = selectTexAnimHeap(mario);
    if (!texHeap)
        return false;
    texHeap->becomeCurrentHeap();

    MActorAnmData *anmData = b.mAnmData;
    if (!anmData) {
        anmData = new MActorAnmData();
        if (!anmData) {
            if (previousHeap)
                previousHeap->becomeCurrentHeap();
            OSReport("[BSMSO] TexAnim: MActorAnmData alloc failed\n");
            return false;
        }
        anmData->init(anmFolder, nullptr);
        b.mAnmData = anmData;
    }

    constexpr f32 kFramerate = 1.0f;
    const bool logBtk = !b.logged;
    MActor *actors[kMaxMActors] = {};
    bool actorHasBtk[kMaxMActors] = {};
    actors[0] =
        createMActorForModel(anmData, mario->mModelData->mModel, kFramerate, "ma_mdl1", logBtk,
                             &actorHasBtk[0]);
    actors[1] = createMActorForModel(anmData, mario->mHandModel2R, kFramerate, "ma_hnd2r", logBtk,
                                     &actorHasBtk[1]);
    actors[2] = createMActorForModel(anmData, mario->mHandModel2L, kFramerate, "ma_hnd2l", logBtk,
                                     &actorHasBtk[2]);
    actors[3] = createMActorForModel(anmData, mario->mHandModel3R, kFramerate, "ma_hnd3r", logBtk,
                                     &actorHasBtk[3]);
    actors[4] = createMActorForModel(anmData, mario->mHandModel3L, kFramerate, "ma_hnd3l", logBtk,
                                     &actorHasBtk[4]);
    actors[5] = createMActorForModel(anmData, mario->mHandModel4R, kFramerate, "ma_hnd4r", logBtk,
                                     &actorHasBtk[5]);
    actors[6] = createMActorForModel(anmData, mario->mCap->mCap1, kFramerate, "ma_cap1", logBtk,
                                     &actorHasBtk[6]);
    actors[7] = createMActorForModel(anmData, mario->mCap->mCap3, kFramerate, "ma_cap3", logBtk,
                                     &actorHasBtk[7]);
    actors[8] =
        createMActorForModel(anmData, mario->mCap->mDiverHelm, kFramerate, "diver_helm", logBtk,
                             &actorHasBtk[8]);
    actors[9] =
        createMActorForModel(anmData, mario->mCap->maGlass1, kFramerate, "ma_glass1", logBtk,
                             &actorHasBtk[9]);

    u32 created = 0;
    u32 btkBound = 0;
    for (u32 i = 0; i < kMaxMActors; ++i) {
        b.mActors[i] = actors[i];
        if (actors[i]) {
            ++created;
            if (actorHasBtk[i])
                ++btkBound;
        }
    }
    b.mActorCount = created;
    b.mActorReady = created > 0;

    if (previousHeap)
        previousHeap->becomeCurrentHeap();

    if (!b.mActorReady) {
        b.mActorRetryFrames = kMActorRetryIntervalFrames;
        if (!b.logged) {
            OSReport("[BSMSO] TexAnim: MActor create produced 0 actors (folder=%s mario=%p)\n",
                     anmFolder, mario);
            b.logged = true;
        }
        return false;
    }

    // Wire into BSE draw/update when this Mario has TPlayerData (local player).
    BetterSMS::Player::TPlayerData *params = tryGetPlayerData(mario);
    if (params) {
        params->mMActorAnmData = anmData;
        for (u32 i = 0; i < kMaxMActors; ++i)
            params->mMActor[i] = actors[i];
        b.bseOwnsMActors = true;
    } else {
        b.bseOwnsMActors = false;
    }

    b.logged = true;
    OSReport("[BSMSO] TexAnim: deferred MActor ready mario=%p folder=%s actors=%u btk=%u bse=%d slot=%u\n",
             mario, anmFolder, created, btkBound, b.bseOwnsMActors ? 1 : 0, b.archiveSlot);
    return true;
}

static void frameUpdateShadowMActors(TexBinding &b) {
    if (!b.mActorReady || b.bseOwnsMActors)
        return;
    for (u32 i = 0; i < kMaxMActors; ++i) {
        if (b.mActors[i])
            b.mActors[i]->frameUpdate();
    }
}

// Remount this binding's pack for BTK / MActorAnmData resolution, then restore
// local. No-ops when already on the right volume or slot is unknown.
static bool withMountedPackForBinding(TexBinding &b, bool (*fn)(TexBinding &, TMario *),
                                      TMario *mario) {
    const u8 slot = resolveArchiveSlot(mario, b.archiveSlot);
    if (slot != kUnknownArchiveSlot)
        b.archiveSlot = slot;

    bool remounted = false;
    if (slot != kUnknownArchiveSlot) {
        // Local player: pack is usually already mounted. Remotes: always remount
        // — getGlbResource must see that slot's custom/btk tree.
        if (mario != gpMarioAddress) {
            remounted = setActiveMarioArchive(slot);
        } else if (!packHasShadowBodyBtk() && b.wantsShadowMActors) {
            remounted = setActiveMarioArchive(slot);
        }
    }

    const bool ok = fn(b, mario);

    if (remounted) {
        // Never leave the global mario volume on a remote Shadow pack — under
        // multi-player pressure local remount can fail; fall back to retail.
        if (!restoreLocalMarioArchiveGuarded()) {
            OSReport("[BSMSO] TexAnim: post-remount restore failed slot=%u\n", slot);
        }
    }
    return ok;
}

static bool tryCreateShadowMActorsThunk(TexBinding &b, TMario *mario) {
    return tryCreateShadowMActors(b, mario);
}

} // namespace

void clearMarioTexAnims(bool keepRemoteBindings) {
    if (!keepRemoteBindings) {
        // Full teardown (disconnect / heap recycle): detach BSE and drop tracking.
        // Remote/stage heaps reclaim the allocations — do not hard-delete.
        for (u32 i = 0; i < sBindingCount; ++i)
            clearBinding(sBindings[i], /*detachDrawOwnership=*/true);
        sBindingCount = 0;
        OSReport("[BSMSO] TexAnim cleared\n");
        return;
    }

    // Connected stage exit: drop local tracking only. Local MActors live on the
    // stage heap with Mario — stage teardown reclaims them. Remotes keep pool
    // bodies + MActors on the remote heap (tracking preserved).
    for (u32 i = 0; i < sBindingCount; ++i) {
        TexBinding &b = sBindings[i];
        if (!b.mario || b.mario == gpMarioAddress)
            clearBinding(b, /*detachDrawOwnership=*/true);
    }
    compactBindings();
    OSReport("[BSMSO] TexAnim cleared (kept %u remote binding(s))\n", sBindingCount);
}

void releaseMarioTexAnims(TMario *mario) {
    TexBinding *b = findBinding(mario);
    if (!b)
        return;
    // Body rebuild / abandon — detach BSE draw path; heap owns the allocations.
    clearBinding(*b, /*detachDrawOwnership=*/true);
    compactBindings();
    OSReport("[BSMSO] TexAnim released mario=%p (bindings=%u)\n", mario, sBindingCount);
}

bool marioHasTexAnimBinding(TMario *mario) { return findBinding(mario) != nullptr; }

static void bindMarioTexAnimsInternal(TMario *mario, bool force, u8 archiveSlot) {
    if (!mario)
        return;

    TexBinding *existing = findBinding(mario);
    if (!force && existing) {
        // Soft-fail with no resources must not permanently block a later mount.
        // Shadow MActor retries belong in updateMarioTexAnims (remounts this
        // mario's pack). Do NOT force-rebind here — that would clear
        // wantsShadowMActors when the local pack is mounted.
        if (existing->mActorReady || existing->trackCount > 0)
            return;
        if (existing->wantsShadowMActors)
            return;
        if (!packHasAnyCustomBtk())
            return;
        // Non-Shadow custom BTKs became visible — fall through to rebind.
        force = true;
    }

    // Probe currently mounted pack. Remote spawn/rebind callers remount first.
    const bool hasBodyBtk = packHasShadowBodyBtk();
    const bool hasAnyBtk = hasBodyBtk || packHasAnyCustomBtk();
    if (!hasAnyBtk && !(existing && existing->wantsShadowMActors)) {
        // Do not allocate a slot — keeps the table free and allows retry.
        return;
    }

    TexBinding *b = force && existing ? existing : allocBinding(mario);
    if (force && existing) {
        const u8 keepSlot = resolveArchiveSlot(mario, archiveSlot != kUnknownArchiveSlot
                                                          ? archiveSlot
                                                          : existing->archiveSlot);
        // Detach prior BSE draw wiring before rebuild / pack-swap rebind.
        // Do not delete MActors — MaterialAnm may still be on live models.
        clearBinding(*existing, /*detachDrawOwnership=*/true);
        existing->mario = mario;
        // Force rebind always reflects the currently mounted pack — do not keep
        // a stale Shadow flag after a retail / non-BTK rebuild on the same TMario*.
        existing->wantsShadowMActors = hasBodyBtk;
        existing->archiveSlot = keepSlot;
        b = existing;
    }
    if (!b) {
        OSReport("[BSMSO] TexAnim: binding table full\n");
        return;
    }

    b->archiveSlot = resolveArchiveSlot(mario, archiveSlot != kUnknownArchiveSlot
                                                   ? archiveSlot
                                                   : b->archiveSlot);
    if (hasBodyBtk)
        b->wantsShadowMActors = true;

    // Shadow Mario/Luigi: body paint needs MActor BTK (BetterSMS path).
    // TScreenTexture::replace is LOCAL-ONLY (see ensureShadowScreenTexture).
    // Do NOT bind body/hand via TMultiBtk — that installs MaterialAnm which
    // breaks the reflection TEV (flat purple body).
    // Order matches BSE initMario: MActors first, then one-shot screen replace.
    if (b->wantsShadowMActors) {
        tryCreateShadowMActors(*b, mario);
        ensureShadowScreenTexture(*b, mario);
        return;
    }

    // Non-Shadow packs with custom BTKs: TMultiBtk on body/hand/cap.
    u32 bound = 0;
    J3DModelData *bodyData = nullptr;
    if (mario->mModelData && mario->mModelData->mModel)
        bodyData = mario->mModelData->mModel->mModelData;
    if (!bodyData)
        bodyData = mario->mBodyModelData;
    if (bodyData)
        bound += addTrack(*b, bodyData, kBodyBtkPath) ? 1 : 0;

    bound += addTrack(*b, modelDataOf(mario->mHandModel2R), kHand2RBtkPath) ? 1 : 0;
    bound += addTrack(*b, modelDataOf(mario->mHandModel3R), kHand3RBtkPath) ? 1 : 0;
    bound += addTrack(*b, modelDataOf(mario->mHandModel4R), kHand4RBtkPath) ? 1 : 0;

    if (mario->mCap) {
        bound += addTrack(*b, modelDataOf(mario->mCap->mCap1), kCap1BtkPath) ? 1 : 0;
        bound += addTrack(*b, modelDataOf(mario->mCap->mCap3), kCap3BtkPath) ? 1 : 0;
    }

    if (bound > 0) {
        if (mario->mModelData)
            mario->mModelData->updateInTexPatternAnm();
        OSReport("[BSMSO] TexAnim: bound %u track(s) for mario=%p\n", bound, mario);
    } else if (!b->logged) {
        OSReport("[BSMSO] TexAnim: no BTKs applied for mario=%p (missing resources)\n", mario);
        b->logged = true;
    }
}

void bindMarioTexAnims(TMario *mario) {
    bindMarioTexAnimsInternal(mario, false, kUnknownArchiveSlot);
}

void bindMarioTexAnimsForSlot(TMario *mario, u8 archiveSlot) {
    bindMarioTexAnimsInternal(mario, false, archiveSlot);
}

void rebindMarioTexAnims(TMario *mario) {
    bindMarioTexAnimsInternal(mario, true, kUnknownArchiveSlot);
}

void rebindMarioTexAnimsForSlot(TMario *mario, u8 archiveSlot) {
    bindMarioTexAnimsInternal(mario, true, archiveSlot);
}

void retargetMarioTexAnimsForSlot(TMario *mario, u8 archiveSlot) {
    TexBinding *binding = findBinding(mario);
    if (binding)
        binding->archiveSlot = archiveSlot;
}

void updateMarioTexAnims(TMario *mario) {
    TexBinding *b = findBinding(mario);
    if (!b)
        return;

    // Shadow: keep trying MActor create if models weren't ready / heap was tight
    // on first bind. Remount this mario's pack — local mount usually has no BTKs.
    // Once mActorReady, do not remount every frame (retry interval only while pending).
    if (b->wantsShadowMActors && !b->mActorReady) {
        if (b->mActorRetryFrames > 0) {
            --b->mActorRetryFrames;
        } else {
            const bool created =
                withMountedPackForBinding(*b, tryCreateShadowMActorsThunk, mario);
            if (created)
                ensureShadowScreenTexture(*b, mario);
            if (!b->mActorReady)
                b->mActorRetryFrames = kMActorRetryIntervalFrames;
        }
    } else if (b->wantsShadowMActors && b->mActorReady && !b->screenTexApplied) {
        ensureShadowScreenTexture(*b, mario);
    }

    if (sMultiBtkUpdate) {
        for (u32 i = 0; i < b->trackCount; ++i) {
            if (b->tracks[i].multi)
                sMultiBtkUpdate(b->tracks[i].multi);
        }
    }

    frameUpdateShadowMActors(*b);
}

void updateAllMarioTexAnims(TMario *localMario) {
    for (u32 i = 0; i < sBindingCount; ++i) {
        TMario *mario = sBindings[i].mario;
        if (!mario)
            continue;
        // Local always updates. Remote BTK/MActor work follows the same
        // distance/visibility budget as its skeleton; parked bodies return false.
        if (mario != gpMarioAddress && mario != localMario &&
            !shouldUpdateRemoteMarioCosmetics(mario))
            continue;
        updateMarioTexAnims(mario);
    }
    (void)localMario;
}

bool marioHasShadowMActors(TMario *mario) {
    TexBinding *b = findBinding(mario);
    return b && b->mActorReady && b->mActorCount > 0;
}

void entryInMarioShadowMActors(TMario *mario) {
    TexBinding *b = findBinding(mario);
    if (!b || !b->mActorReady || b->bseOwnsMActors)
        return;
    for (u32 i = 0; i < kMaxMActors; ++i) {
        if (b->mActors[i])
            b->mActors[i]->entryIn();
    }
}

void entryOutMarioShadowMActors(TMario *mario) {
    TexBinding *b = findBinding(mario);
    if (!b || !b->mActorReady || b->bseOwnsMActors)
        return;
    for (u32 i = 0; i < kMaxMActors; ++i) {
        if (b->mActors[i])
            b->mActors[i]->entryOut();
    }
}

} // namespace smso

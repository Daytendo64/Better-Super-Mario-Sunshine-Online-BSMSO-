#include "mario_model_system.hpp"
#include "mario_tex_anim.hpp"
#include "comm_buffer.hpp"
#include "remote_actor.hpp"

#include <Dolphin/DVD.h>
#include <Dolphin/OS.h>
#include <Dolphin/MTX.h>
#include <Dolphin/printf.h>
#include <Dolphin/string.h>

#include <JSystem/J3D/J3DModel.hxx>
#include <JSystem/J3D/J3DShape.hxx>
#include <JSystem/JKernel/JKRFileLoader.hxx>
#include <JSystem/JKernel/JKRHeap.hxx>
#include <JSystem/JKernel/JKRMemArchive.hxx>
#include <SMS/M3DUtil/MActor.hxx>
#include <SMS/MSound/MSound.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/MarioCap.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>

#include <BetterSMS/module.hxx>

extern void *arcBufMario;
extern MSound *gpMSound;

// doldecomp TMarioEffect::init(TMario*) — effect object lives across initModel and
// still points at the previous body J3DModel until re-inited.
using TMarioEffectInitFn = void (*)(void *effect, TMario *mario);
static TMarioEffectInitFn sMarioEffectInit =
    reinterpret_cast<TMarioEffectInitFn>(SMS_PORT_REGION(0x802720C4, 0x80269E50, 0, 0));

// doldecomp TMBindShadowBody — constructed in TMario::initValues after initModel.
class TMBindShadowBody {
public:
    TMBindShadowBody(THitActor *owner, J3DModel *model, f32 scale);
};

namespace smso {
namespace {

// Cache one pack per player slot (local + remotes) so a 10-player lobby with
// unique models keeps all archives resident for remount-per-spawn.
constexpr u32 kMaxSlots = MAX_PLAYERS;
constexpr const char *kRetailPathArc = "/data/mario.arc";
constexpr const char *kRetailPathSzs = "/data/mario.szs";
constexpr const char *kPackDir = "/data/bsmso_models/";
// Pack RARC is ~1.4–1.9 MiB after in-place BMD/BTK patch. Soft-fail only when
// the expanded MEM1 arena cannot hold another pack; body headroom is reserved
// by the ~24 MiB dual-BAT arena budget (as many unique packs as fit + bodies),
// not per-load gating that would soft-fail the last few models needlessly.
constexpr u32 kMaxPackBytes = 0x00200000u; // 2 MiB upper bound
constexpr u32 kMinPackHeapFree = kMaxPackBytes + 0x00010000u; // pack + 64 KiB margin

struct SlotArchive {
    char modelId[MARIO_MODEL_ID_SIZE];
    void *buffer;
    bool loggedMissing;
};

static SlotArchive sSlots[kMaxSlots];

// Pack buffers are loaded into the expanded-MEM1 remote heap when it has
// pack+body headroom. They must NOT use sSystemHeap / stage heap — those OOMs
// abort the game. Soft-fail leaves retail mounted.
//
// Invariant: pack cache pointers are valid iff the remote actor heap that owns
// those buffers is still alive. Connected stage exits keep the heap + cache;
// disconnect / offline exit destroys the heap and must drop the cache first
// (clearMarioModelSystem(keepPackCache=false) before clearRemoteActors(false)).
static void *sRetailBuffer = nullptr;
static void *sPackCacheBuffers[kMaxSlots];
static char sPackCacheIds[kMaxSlots][MARIO_MODEL_ID_SIZE];
static u32 sPackCacheCount = 0;
static bool sBootstrapped = false;
static bool sInitialized = false;
static bool sLocalRebuildBusy = false;
static u32 sActiveSlot = 0xFFFFFFFFu;
// Last CommBuffer ids observed this stage (desired). Applied archives live in sSlots
// and are frozen at stage init / remote spawn — never remounted mid-stage.
static char sLastLocalId[MARIO_MODEL_ID_SIZE] = {};
static char sLastRemoteIds[kMaxSlots][MARIO_MODEL_ID_SIZE] = {};
static u32 sDeferLogCooldown = 0;
// Prefetch walk cursor + per-slot cooldown after a soft-fail so we do not
// SMSLoadArchive-spam missing packs every frame on hub / loading.
static u32 sPrefetchCursor = 0;
static u8 sPackPrefetchCooldown[kMaxSlots] = {};
constexpr u8 kPackPrefetchFailCooldownFrames = 120; // 2s @ 60 Hz

static bool idsEqual(const char a[MARIO_MODEL_ID_SIZE], const char b[MARIO_MODEL_ID_SIZE]) {
    return memcmp(a, b, MARIO_MODEL_ID_SIZE) == 0;
}

static void clearId(char id[MARIO_MODEL_ID_SIZE]) { memset(id, 0, MARIO_MODEL_ID_SIZE); }

static void copyId(char dest[MARIO_MODEL_ID_SIZE], const char src[MARIO_MODEL_ID_SIZE]) {
    if (!src) {
        clearId(dest);
        return;
    }
    memcpy(dest, src, MARIO_MODEL_ID_SIZE);
}

static void buildPackPath(char *dst, size_t dstSize, const char id[MARIO_MODEL_ID_SIZE]) {
    char hex[MARIO_MODEL_ID_SIZE + 1];
    u32 len = 0;
    for (; len < MARIO_MODEL_ID_SIZE && id[len] != '\0'; ++len)
        hex[len] = id[len];
    hex[len] = '\0';
    snprintf(dst, dstSize, "%s%s.arc", kPackDir, hex);
}

static void formatId(char out[MARIO_MODEL_ID_SIZE + 1], const char id[MARIO_MODEL_ID_SIZE]) {
    u32 len = 0;
    for (; len < MARIO_MODEL_ID_SIZE && id[len] != '\0'; ++len)
        out[len] = id[len];
    out[len] = '\0';
}

// Heap for SMSLoadArchive:
// - NEVER sSystemHeap: observed free space cannot hold ~1.4–1.9 MiB packs; load
//   returns nullptr and remount never runs (always retail Mario).
// - Prefer borrowRemoteActorHeap() (expanded MEM1 above arenaHi, ~24 MiB dual-BAT
//   or 7.5 MiB DBAT2 fallback) when free >= one more pack. Soft-fail to retail
//   otherwise — never load packs into the stage/system heap.
// - nullptr here means "do not load" (soft-fail to retail), NOT "use current heap".
static JKRHeap *archiveHeap() {
    JKRHeap *remote = borrowRemoteActorHeap();
    if (remote) {
        const u32 free = static_cast<u32>(remote->getTotalFreeSize());
        if (free >= kMinPackHeapFree)
            return remote;
        OSReport("[BSMSO] Remote pack heap low (free=%u need=%u); soft-fail retail\n", free,
                 kMinPackHeapFree);
        return nullptr;
    }
    OSReport("[BSMSO] No remote pack heap yet — soft-fail retail (avoid stage/system OOM)\n");
    return nullptr;
}

static void *loadArchivePath(const char *path, bool allowCurrentHeapFallback) {
    if (!path || path[0] == '\0')
        return nullptr;

    // DVDConvertPathToEntrynum takes char* in the SMS SDK; path is a mutable stack buffer
    // from callers (or a string literal — SDK casts internally on retail).
    char pathBuf[96];
    snprintf(pathBuf, sizeof(pathBuf), "%s", path);
    const s32 entry = DVDConvertPathToEntrynum(pathBuf);
    JKRHeap *heap = archiveHeap();

    if (entry < 0) {
        OSReport("[BSMSO] SMSLoadArchive FST miss: %s (file not on disc/DirectoryBlob)\n", path);
        return nullptr;
    }

    if (!heap && !allowCurrentHeapFallback) {
        OSReport("[BSMSO] SMSLoadArchive skipped (no safe pack heap): %s — retail fallback\n",
                 path);
        return nullptr;
    }

    void *buf = SMSLoadArchive(path, nullptr, 0, heap);
    if (!buf && heap != nullptr && allowCurrentHeapFallback) {
        OSReport("[BSMSO] SMSLoadArchive failed on heap=%p — retrying current heap\n", heap);
        buf = SMSLoadArchive(path, nullptr, 0, nullptr);
    }

    if (!buf)
        OSReport("[BSMSO] SMSLoadArchive failed: %s (heap=%p entry=%d — likely OOM)\n", path, heap,
                 entry);
    // Success: silent — try/ok spam on every cache-miss load flooded Dolphin logs.
    return buf;
}

static void *loadArchivePath(const char *path) { return loadArchivePath(path, false); }

static void *findCachedPack(const char id[MARIO_MODEL_ID_SIZE]) {
    if (marioModelIdIsEmpty(id))
        return nullptr;
    for (u32 i = 0; i < sPackCacheCount; ++i) {
        if (idsEqual(sPackCacheIds[i], id))
            return sPackCacheBuffers[i];
    }
    return nullptr;
}

static void cachePack(const char id[MARIO_MODEL_ID_SIZE], void *buffer) {
    if (!buffer || marioModelIdIsEmpty(id) || sPackCacheCount >= kMaxSlots)
        return;
    if (findCachedPack(id))
        return;
    copyId(sPackCacheIds[sPackCacheCount], id);
    sPackCacheBuffers[sPackCacheCount] = buffer;
    ++sPackCacheCount;
}

static bool marioPackBufferIsInitSafe(void *buffer);

static void dropPackCache() {
    for (u32 i = 0; i < kMaxSlots; ++i) {
        sPackCacheBuffers[i] = nullptr;
        clearId(sPackCacheIds[i]);
    }
    sPackCacheCount = 0;
}

static void *resolveBufferForId(const char id[MARIO_MODEL_ID_SIZE], bool *loggedMissing,
                                bool *outFromCache) {
    if (outFromCache)
        *outFromCache = false;

    if (marioModelIdIsEmpty(id))
        return sRetailBuffer;

    if (void *cached = findCachedPack(id)) {
        if (outFromCache)
            *outFromCache = true;
        return cached;
    }

    char path[96];
    buildPackPath(path, sizeof(path), id);
    void *buf = loadArchivePath(path);
    if (buf) {
        if (!marioPackBufferIsInitSafe(buf)) {
            OSReport("[BSMSO] Unsafe model pack %s (bad ma_mdl1/ma_cap joints) — "
                     "retail fallback (avoids remote initValues crash)\n",
                     path);
            // Leave the bad buffer on the remote heap; it is reclaimed with the
            // heap. Do not cache it — remotes must never initValues under it.
            if (loggedMissing && !*loggedMissing) {
                *loggedMissing = true;
            }
            return sRetailBuffer;
        }
        cachePack(id, buf);
        return buf;
    }

    if (loggedMissing && !*loggedMissing) {
        OSReport("[BSMSO] Missing/unloadable model pack %s — falling back to retail\n", path);
        *loggedMissing = true;
    }
    return sRetailBuffer;
}

static void *resolveBufferForId(const char id[MARIO_MODEL_ID_SIZE], bool *loggedMissing) {
    return resolveBufferForId(id, loggedMissing, nullptr);
}

// JKRMemArchive::fetchResource caches absolute pointers in each SDIFileEntry.mData
// field (offset +0x10), which lives INSIDE the RARC buffer. mountFixed/open does
// not clear those fields, so remounting a previously-used pack can return stale
// pointers from an earlier mount. Zero them after every successful mount.
static void clearArchiveCachedFilePointers(void *buffer) {
    if (!buffer)
        return;

    auto *hdr = reinterpret_cast<u8 *>(buffer);
    // SArcHeader: signature@0, file_length@4, header_length@8, file_data_offset@0xC
    const u32 headerLength = (static_cast<u32>(hdr[8]) << 24) | (static_cast<u32>(hdr[9]) << 16) |
                             (static_cast<u32>(hdr[10]) << 8) | static_cast<u32>(hdr[11]);
    if (headerLength < 0x20 || headerLength > 0x10000)
        return;

    u8 *info = hdr + headerLength;
    const u32 numEntries = (static_cast<u32>(info[8]) << 24) | (static_cast<u32>(info[9]) << 16) |
                           (static_cast<u32>(info[10]) << 8) | static_cast<u32>(info[11]);
    const u32 entryRel = (static_cast<u32>(info[12]) << 24) | (static_cast<u32>(info[13]) << 16) |
                         (static_cast<u32>(info[14]) << 8) | static_cast<u32>(info[15]);
    if (numEntries == 0 || numEntries > 4096 || entryRel > 0x100000)
        return;

    u8 *entries = info + entryRel;
    for (u32 i = 0; i < numEntries; ++i) {
        // SDIFileEntry is 0x14; mData at +0x10.
        u8 *mData = entries + i * 0x14 + 0x10;
        mData[0] = mData[1] = mData[2] = mData[3] = 0;
    }
}

// Read big-endian helpers for in-buffer RARC / BMD probes.
static u32 be32(const u8 *p) {
    return (static_cast<u32>(p[0]) << 24) | (static_cast<u32>(p[1]) << 16) |
           (static_cast<u32>(p[2]) << 8) | static_cast<u32>(p[3]);
}
static u16 be16(const u8 *p) {
    return static_cast<u16>((static_cast<u16>(p[0]) << 8) | static_cast<u16>(p[1]));
}

static s32 readBmdJointCount(const u8 *bmd, u32 size) {
    if (!bmd || size < 12)
        return -1;
    // Scan for JNT1; joint count is u16 at +8.
    for (u32 i = 0; i + 10 <= size; ++i) {
        if (bmd[i] == 'J' && bmd[i + 1] == 'N' && bmd[i + 2] == 'T' && bmd[i + 3] == '1')
            return static_cast<s32>(be16(bmd + i + 8));
    }
    return -1;
}

static bool rarcCStringEquals(const u8 *base, u32 absOff, u32 bufSize, const char *name) {
    if (!base || !name || absOff >= bufSize)
        return false;
    for (u32 i = 0; name[i] != '\0'; ++i) {
        if (absOff + i >= bufSize || base[absOff + i] != static_cast<u8>(name[i]))
            return false;
    }
    return absOff < bufSize && base[absOff + strlen(name)] == '\0';
}

// Locate a file by basename inside an uncompressed RARC buffer. Returns false
// when the name is missing or the entry looks corrupt.
static bool findRarcFileByBasename(void *buffer, const char *basename, const u8 **outPtr,
                                   u32 *outSize) {
    if (outPtr)
        *outPtr = nullptr;
    if (outSize)
        *outSize = 0;
    if (!buffer || !basename || basename[0] == '\0')
        return false;

    auto *hdr = reinterpret_cast<u8 *>(buffer);
    if (hdr[0] != 'R' || hdr[1] != 'A' || hdr[2] != 'R' || hdr[3] != 'C')
        return false;

    const u32 fileLength = be32(hdr + 4);
    const u32 headerLength = be32(hdr + 8);
    const u32 fileDataRel = be32(hdr + 0xC);
    if (fileLength < 0x40 || headerLength < 0x20 || headerLength > 0x10000 ||
        headerLength >= fileLength)
        return false;

    u8 *info = hdr + headerLength;
    const u32 numEntries = be32(info + 8);
    const u32 entryRel = be32(info + 12);
    const u32 stringRel = be32(info + 0x14);
    if (numEntries == 0 || numEntries > 4096 || entryRel > 0x100000 || stringRel > 0x100000)
        return false;

    const u32 absFileData = headerLength + fileDataRel;
    if (absFileData >= fileLength)
        return false;

    u8 *entries = info + entryRel;
    const u32 absString = headerLength + stringRel;
    for (u32 i = 0; i < numEntries; ++i) {
        u8 *entry = entries + i * 0x14;
        // On-disk SDIFileEntry: id@0, type@4, nameOff@6, dataOff@8, size@0xC
        // (mData @0x10 is a runtime cache filled by mountFixed — ignore here).
        const u16 id = be16(entry + 0);
        const u16 type = be16(entry + 4);
        const u16 nameRel = be16(entry + 6);
        const bool isDir =
            id == 0xFFFFu || (type & 0xFF00u) == 0x0200u || (type & 0x00FFu) == 0x02u;
        const bool isFile =
            (type & 0xFF00u) == 0x1100u || (type & 0x00FFu) == 0x11u;
        if (isDir && !isFile)
            continue;
        if (nameRel == 0 && (basename[0] == '.'))
            continue;
        if (!rarcCStringEquals(hdr, absString + nameRel, fileLength, basename))
            continue;

        // Skip "." / ".." directory stubs that share the file type check edge cases.
        if (basename[0] == '.' && (basename[1] == '\0' || basename[1] == '.'))
            continue;

        const u32 dataOff = be32(entry + 8);
        const u32 size = be32(entry + 0xC);
        const u32 abs = absFileData + dataOff;
        if (size == 0 || abs + size > fileLength)
            return false;
        if (outPtr)
            *outPtr = hdr + abs;
        if (outSize)
            *outSize = size;
        return true;
    }
    return false;
}

// Reject packs whose body/cap skeletons cannot survive TMario::initValues.
// Wrong ma_cap* joint counts (custom 1 vs retail 2/3) hard-crash remotes.
static bool marioPackBufferIsInitSafe(void *buffer) {
    if (!buffer)
        return false;

    const u8 *body = nullptr;
    u32 bodySize = 0;
    if (!findRarcFileByBasename(buffer, "ma_mdl1.bmd", &body, &bodySize))
        return false;
    if (readBmdJointCount(body, bodySize) != 29)
        return false;

    // Capless packs keep retail caps and stamp this marker — treat as safe.
    const u8 *hide = nullptr;
    u32 hideSize = 0;
    if (findRarcFileByBasename(buffer, "bsmso_hide_caps", &hide, &hideSize))
        return true;

    const u8 *cap1 = nullptr;
    u32 cap1Size = 0;
    const u8 *cap3 = nullptr;
    u32 cap3Size = 0;
    if (!findRarcFileByBasename(buffer, "ma_cap1.bmd", &cap1, &cap1Size) ||
        !findRarcFileByBasename(buffer, "ma_cap3.bmd", &cap3, &cap3Size))
        return false;
    if (readBmdJointCount(cap1, cap1Size) != 2)
        return false;
    if (readBmdJointCount(cap3, cap3Size) != 3)
        return false;
    return true;
}

static bool rarcContainsBasename(void *buffer, const char *basename) {
    const u8 *ptr = nullptr;
    u32 size = 0;
    return findRarcFileByBasename(buffer, basename, &ptr, &size);
}

static bool mountBuffer(void *buffer) {
    if (!buffer)
        return false;

    auto *archive = reinterpret_cast<JKRMemArchive *>(JKRFileLoader::getVolume("mario"));
    if (!archive) {
        // Volume missing (should be rare with early-init nops removed). Create it.
        OSReport("[BSMSO] mario volume missing — creating JKRMemArchive from %p\n", buffer);
        JKRHeap *prev = JKRHeap::sCurrentHeap;
        if (JKRHeap::sSystemHeap)
            JKRHeap::sSystemHeap->becomeCurrentHeap();
        archive = new JKRMemArchive(buffer, 0, UNK_0);
        if (prev)
            prev->becomeCurrentHeap();
        if (!archive) {
            OSReport("[BSMSO] Failed to create mario JKRMemArchive\n");
            return false;
        }
        arcBufMario = buffer;
        clearArchiveCachedFilePointers(buffer);
        return true;
    }

    if (arcBufMario == buffer) {
        // Still clear caches — a prior mount of this buffer may have left mData set.
        clearArchiveCachedFilePointers(buffer);
        OSReport("[BSMSO] mario volume already mounted @ %p\n", buffer);
        return true;
    }

    OSReport("[BSMSO] remountFixed mario %p -> %p\n", arcBufMario, buffer);
    archive->unmountFixed();
    arcBufMario = buffer;
    if (!archive->mountFixed(buffer, UNK_0)) {
        OSReport("[BSMSO] mountFixed failed; attempting retail fallback\n");
        if (buffer != sRetailBuffer && sRetailBuffer) {
            arcBufMario = sRetailBuffer;
            if (archive->mountFixed(sRetailBuffer, UNK_0)) {
                clearArchiveCachedFilePointers(sRetailBuffer);
                return true;
            }
        }
        return false;
    }
    clearArchiveCachedFilePointers(buffer);
    OSReport("[BSMSO] remountFixed ok @ %p\n", buffer);
    return true;
}

static void readSlotId(CommBuffer *buf, u32 slot, char out[MARIO_MODEL_ID_SIZE]) {
    if (!buf || slot >= kMaxSlots) {
        clearId(out);
        return;
    }
    if (slot == buf->localSlot)
        copyId(out, buf->localMarioModelId);
    else
        copyId(out, buf->remoteMarioModelIds[slot]);
}

static void syncIdsFromCommBuffer(CommBuffer *buf) {
    if (!buf)
        return;
    for (u32 i = 0; i < kMaxSlots; ++i)
        readSlotId(buf, i, sSlots[i].modelId);
}

// Prefer the game's already-mounted retail volume. Reloading /data/mario.arc is
// only a fallback — with early-init nops removed, retail mounts before title.
static bool ensureRetailLoaded() {
    if (sRetailBuffer)
        return true;

    auto *archive = reinterpret_cast<JKRMemArchive *>(JKRFileLoader::getVolume("mario"));
    if (archive && arcBufMario) {
        sRetailBuffer = arcBufMario;
        OSReport("[BSMSO] Adopted retail mario volume @ %p (arcBufMario)\n", sRetailBuffer);
        return true;
    }

    OSReport("[BSMSO] Retail mario volume not mounted yet — loading from disc\n");
    sRetailBuffer = loadArchivePath(kRetailPathArc, true);
    if (!sRetailBuffer)
        sRetailBuffer = loadArchivePath(kRetailPathSzs, true);

    if (!sRetailBuffer) {
        OSReport("[BSMSO] WARNING: failed to load retail mario archive (.arc/.szs)\n");
        return false;
    }
    return true;
}

// Stage init only needs the local player's pack mounted. Remote packs are
// resolved lazily in setActiveMarioArchive when a puppet is spawned.
static void ensureLocalBufferLoaded(CommBuffer *buf) {
    if (!ensureRetailLoaded())
        return;

    const u8 localSlot = buf ? buf->localSlot : 0;
    if (localSlot >= kMaxSlots)
        return;

    SlotArchive &slot = sSlots[localSlot];
    if (buf)
        readSlotId(buf, localSlot, slot.modelId);
    slot.buffer = resolveBufferForId(slot.modelId, &slot.loggedMissing);
    if (!slot.buffer)
        slot.buffer = sRetailBuffer;
}

static bool idsChanged(CommBuffer *buf) {
    if (!buf)
        return false;
    if (!idsEqual(sLastLocalId, buf->localMarioModelId))
        return true;
    for (u32 i = 0; i < kMaxSlots; ++i) {
        if (!idsEqual(sLastRemoteIds[i], buf->remoteMarioModelIds[i]))
            return true;
    }
    return false;
}

static void rememberIds(CommBuffer *buf) {
    if (!buf)
        return;
    copyId(sLastLocalId, buf->localMarioModelId);
    for (u32 i = 0; i < kMaxSlots; ++i)
        copyId(sLastRemoteIds[i], buf->remoteMarioModelIds[i]);
}

static void bootstrapOnce() {
    if (sBootstrapped)
        return;
    sBootstrapped = true;
    dropPackCache();
    OSReport("[BSMSO] Mario model system bootstrap (no early-init nops; retail mounts normally)\n");
}

// Full visual rebind after remount. Only safe during stage init / controlled
// refresh — never from the mid-stage CommBuffer id-change path (SMSLoadArchive
// + remountFixed + initModel during gameplay corrupts heaps / J3D state).
static bool rebuildLocalMarioVisuals(TMario *mario) {
    if (!mario)
        return false;
    if (sLocalRebuildBusy) {
        OSReport("[BSMSO] Local model rebuild skipped: already rebuilding\n");
        return false;
    }

    sLocalRebuildBusy = true;

    const u16 prevAnim = mario->mAnimationID;
    const u8 prevNozzle = mario->mFludd ? mario->mFludd->mCurrentNozzle : 0;
    const u8 prevSecond = mario->mFludd ? mario->mFludd->mSecondNozzle : 0;
    const s32 prevWater = mario->mFludd ? mario->mFludd->mCurrentWater : 0;
    const bool hadFludd = mario->mFludd != nullptr;

    // initModel() clears these; keep live stage attachments.
    void *savedSurfGesso = mario->mSurfGesso;
    MActor *savedTorocco = mario->mTorocco;
    MActor *savedPinnaRail = mario->mPinnaRail;
    MActor *savedKoopaRail = mario->mKoopaRail;
    const TVec3f savedPos = mario->mTranslation;
    const TVec3f savedRot = mario->mRotation;

    OSReport("[BSMSO] Local model rebuild begin (anim=%u nozzle=%u water=%d)\n", prevAnim,
             prevNozzle, prevWater);

    // Reloads body BMD, hands, anim tables, draw buffers, tremble from the
    // currently mounted "mario" volume. Old J3D allocations leak for the stage.
    mario->initModel();

    // Undo initModel's stage-special side effects (null / Pinna torocco rebuild).
    mario->mSurfGesso = savedSurfGesso;
    mario->mTorocco = savedTorocco;
    mario->mPinnaRail = savedPinnaRail;
    mario->mKoopaRail = savedKoopaRail;
    mario->mTranslation = savedPos;
    mario->mRotation = savedRot;

    if (!mario->mModelData || !mario->mModelData->mModel) {
        OSReport("[BSMSO] Local model rebuild failed: initModel left null mModelData\n");
        sLocalRebuildBusy = false;
        return false;
    }

    // Cap models (hat / wet / helm / glasses) load from /mario/bmd/* at construct.
    TMarioCap *oldCap = mario->mCap;
    TMarioCap *freshCap = new TMarioCap(mario);
    if (freshCap) {
        mario->mCap = freshCap;
        (void)oldCap; // leak for stage — cannot safely delete mid-frame
    } else {
        OSReport("[BSMSO] Local model rebuild: TMarioCap alloc failed — keeping old cap\n");
        mario->mCap = oldCap;
    }

    // FLUDD body + nozzle BMDs also resolve from the mounted pack.
    if (hadFludd) {
        TWaterGun *freshFludd = new TWaterGun(mario);
        if (freshFludd) {
            freshFludd->init();
            freshFludd->initInLoadAfter();
            freshFludd->mCurrentNozzle = prevNozzle;
            freshFludd->mSecondNozzle = prevSecond;
            freshFludd->mCurrentWater = prevWater;
            mario->mFludd = freshFludd;
        } else {
            OSReport("[BSMSO] Local model rebuild: TWaterGun alloc failed — keeping old FLUDD\n");
        }
    }

    // Bind-shadow body holds a pointer to the old J3DModel — retarget it.
    if (mario->mModelData->mModel) {
        mario->_390 = reinterpret_cast<u32>(
            new TMBindShadowBody(mario, mario->mModelData->mModel, 1.0f));
    }

    // Mirror of TMario::loadAfter visual finish — required after initModel.
    mario->finalDrawInitialize();
    mario->initMirrorModel();

    // TMarioEffect is allocated in initValues (not initModel) and caches the old
    // body model. Re-init against the live TMario so particles/VFX bind correctly.
    // SMS interface: _424 == mMarioEffect (after mMultiMtxEffect at _420).
    if (mario->_424 && sMarioEffectInit) {
        sMarioEffectInit(reinterpret_cast<void *>(mario->_424), mario);
        OSReport("[BSMSO] Local model rebuild: TMarioEffect re-inited @ %p\n",
                 reinterpret_cast<void *>(mario->_424));
    }

    // loadAfter stores Vec*/MtxPtr permanently — never pass stack temporaries.
    // Match TMario::loadAfter: member translation/speed + anmMtx(1).
    if (gpMSound && mario->mModelData && mario->mModelData->mModel &&
        mario->mModelData->mModel->mJointArray) {
        gpMSound->setPlayerInfo(reinterpret_cast<Vec *>(&mario->mTranslation),
                                reinterpret_cast<Vec *>(&mario->mSpeed),
                                mario->mModelData->mModel->mJointArray[1], true);
    }

    mario->changeHand(0);
    mario->setAnimation(static_cast<int>(prevAnim), 1.0f);

    // Custom packs may ship body/hand/cap BTKs (injected into /mario/btk/).
    CommBuffer *texBuf = getCommBuffer();
    const u8 localTexSlot = texBuf ? texBuf->localSlot : 0;
    rebindMarioTexAnimsForSlot(mario, localTexSlot);

    OSReport("[BSMSO] Local model rebuild ok (model=%p cap=%p fludd=%p)\n", mario->mModelData,
             mario->mCap, mario->mFludd);
    sLocalRebuildBusy = false;
    return true;
}

} // namespace

void initMarioModelSystem() {
    bootstrapOnce();
    sInitialized = true;
    sActiveSlot = 0xFFFFFFFFu;

    OSReport("[BSMSO] initMarioModelSystem begin (vol=%p arcBuf=%p retail=%p packs=%u)\n",
             JKRFileLoader::getVolume("mario"), arcBufMario, sRetailBuffer, sPackCacheCount);

    // Keep sRetailBuffer across stages (game-owned). Pack cache may still be
    // valid if loaded into expanded MEM1 that survived; otherwise reload.
    for (u32 i = 0; i < kMaxSlots; ++i) {
        clearId(sSlots[i].modelId);
        sSlots[i].buffer = nullptr;
        sSlots[i].loggedMissing = false;
    }

    CommBuffer *buf = getCommBuffer();
    syncIdsFromCommBuffer(buf);

    char localIdStr[MARIO_MODEL_ID_SIZE + 1] = {};
    if (buf)
        formatId(localIdStr, buf->localMarioModelId);
    OSReport("[BSMSO] Local mario model id='%s' (empty=retail)\n", localIdStr);

    ensureLocalBufferLoaded(buf);

    const u8 localSlot = buf ? buf->localSlot : 0;
    if (!setActiveMarioArchive(localSlot)) {
        // Never leave the volume unmounted — fall back to retail if possible.
        if (sRetailBuffer && mountBuffer(sRetailBuffer)) {
            sActiveSlot = localSlot;
            OSReport("[BSMSO] Mario model mount fell back to retail (localSlot=%u)\n", localSlot);
        } else if (JKRFileLoader::getVolume("mario")) {
            // Retail already mounted by the game; keep it and continue boot.
            OSReport("[BSMSO] Keeping game-mounted retail mario volume (localSlot=%u)\n",
                     localSlot);
            sActiveSlot = localSlot;
            if (!sRetailBuffer && arcBufMario)
                sRetailBuffer = arcBufMario;
        } else {
            OSReport("[BSMSO] ERROR: mario archive unavailable — continuing without remount\n");
        }
    }
    rememberIds(buf);
    OSReport("[BSMSO] Mario model system ready (localSlot=%u retail=%p packs=%u vol=%p arcBuf=%p)\n",
             localSlot, sRetailBuffer, sPackCacheCount, JKRFileLoader::getVolume("mario"),
             arcBufMario);
}

void clearMarioModelSystem(bool keepPackCache) {
    // Remount retail BEFORE any remote-heap destroy so the next stage's local
    // Mario init sees a valid volume. Slot bindings are always cleared; pack
    // cache survives only when the remote heap is also kept alive.
    if (sRetailBuffer && JKRFileLoader::getVolume("mario")) {
        if (arcBufMario != sRetailBuffer)
            mountBuffer(sRetailBuffer);
    }

    sInitialized = false;
    sLocalRebuildBusy = false;
    sDeferLogCooldown = 0;
    sActiveSlot = 0xFFFFFFFFu;
    sPrefetchCursor = 0;
    for (u32 i = 0; i < kMaxSlots; ++i) {
        sSlots[i].buffer = nullptr;
        sSlots[i].loggedMissing = false;
        clearId(sSlots[i].modelId);
        sPackPrefetchCooldown[i] = 0;
    }
    if (!keepPackCache)
        dropPackCache();
    // Connected keep-alive: remote pool bodies survive — keep their TexAnim
    // bindings (allocated on the remote heap). Wiping them here left J3D models
    // with dangling MaterialAnm/MActor state and crashed as more remotes drew.
    clearMarioTexAnims(/*keepRemoteBindings=*/keepPackCache);
    OSReport("[BSMSO] clearMarioModelSystem (retail retained=%p packs=%s count=%u)\n",
             sRetailBuffer, keepPackCache ? "kept" : "cleared", sPackCacheCount);
}

void ensureMarioTexAnimsBound(TMario *mario) {
    if (!mario || !mario->mBodyModelData)
        return;
    CommBuffer *buf = getCommBuffer();
    const u8 localSlot = buf ? buf->localSlot : 0;
    bindMarioTexAnimsForSlot(mario, localSlot);
}

void updateMarioModelSystem(TMarDirector *director) {
    (void)director;
    if (!sInitialized)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return;

    if (sDeferLogCooldown > 0)
        --sDeferLogCooldown;

    if (!idsChanged(buf))
        return;

    // Local mid-stage id updates stay deferred to next stage init (local rebuild
    // is unsafe mid-frame). Remotes: request a same-stage body reapply when the
    // CommBuffer id changes — keep the current puppet visible until rebuild.

    if (!idsEqual(sLastLocalId, buf->localMarioModelId)) {
        char oldStr[MARIO_MODEL_ID_SIZE + 1] = {};
        char newStr[MARIO_MODEL_ID_SIZE + 1] = {};
        formatId(oldStr, sLastLocalId);
        formatId(newStr, buf->localMarioModelId);
        if (sDeferLogCooldown == 0) {
            OSReport("[BSMSO] Local model id '%s' -> '%s' deferred "
                     "(apply on next stage reload; no load/remount)\n",
                     oldStr, newStr);
            sDeferLogCooldown = 60;
        }
    }

    for (u32 i = 0; i < kMaxSlots; ++i) {
        if (i == buf->localSlot)
            continue;
        if (idsEqual(sLastRemoteIds[i], buf->remoteMarioModelIds[i]))
            continue;
        char oldStr[MARIO_MODEL_ID_SIZE + 1] = {};
        char newStr[MARIO_MODEL_ID_SIZE + 1] = {};
        formatId(oldStr, sLastRemoteIds[i]);
        formatId(newStr, buf->remoteMarioModelIds[i]);

        // Emptying an id mid-stage (disconnect glitch) keeps the current body.
        // Non-empty changes request a same-stage rebuild (pack may have just
        // appeared on disc after EnsurePackPresent).
        if (!marioModelIdIsEmpty(buf->remoteMarioModelIds[i])) {
            requestRemoteMarioModelReapply(static_cast<u8>(i));
            OSReport("[BSMSO] Remote model id slot=%u '%s' -> '%s' reapply requested "
                     "(same-stage body rebuild when pack/heap ready)\n",
                     i, oldStr, newStr);
        } else if (isRemoteMarioModelFrozen(static_cast<u8>(i))) {
            OSReport("[BSMSO] Remote model id slot=%u '%s' -> '' kept "
                     "(frozen body stays visible until dismiss/stage reload)\n",
                     i, oldStr);
        } else {
            OSReport("[BSMSO] Remote model id slot=%u '%s' -> '%s' pending "
                     "(first residency apply on body assign)\n",
                     i, oldStr, newStr);
        }
    }

    rememberIds(buf);
}

void readMarioModelIdForSlot(u32 slot, char out[MARIO_MODEL_ID_SIZE]) {
    if (!out)
        return;
    clearId(out);
    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return;
    readSlotId(buf, slot, out);
}

bool marioSlotHasCustomPack(u32 slot) {
    if (!sInitialized || slot >= kMaxSlots)
        return false;
    void *buffer = sSlots[slot].buffer;
    if (!buffer || buffer == sRetailBuffer)
        return false;
    return !marioModelIdIsEmpty(sSlots[slot].modelId);
}

bool isMarioModelPackCached(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return true; // retail — no pack load required
    return findCachedPack(id) != nullptr;
}

bool prefetchRemoteMarioPacks() {
    if (!sInitialized)
        return false;

    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return false;
    if ((buf->bridgeFlags & BF_CONNECTED) == 0)
        return false;

    // Ensure remote heap exists so archiveHeap() can accept a pack load.
    if (!borrowRemoteActorHeap())
        return true; // retry next frame once heap is ready

    for (u32 i = 0; i < kMaxSlots; ++i) {
        if (sPackPrefetchCooldown[i] > 0)
            --sPackPrefetchCooldown[i];
    }

    bool anyPending = false;
    for (u32 n = 0; n < kMaxSlots; ++n) {
        const u32 slot = (sPrefetchCursor + n) % kMaxSlots;
        if (slot == buf->localSlot)
            continue;

        char desired[MARIO_MODEL_ID_SIZE] = {};
        readSlotId(buf, slot, desired);
        if (marioModelIdIsEmpty(desired))
            continue;

        if (findCachedPack(desired)) {
            // Bind slot from cache without DVD so first-residency sees custom.
            if (!marioSlotHasCustomPack(slot) || !idsEqual(sSlots[slot].modelId, desired))
                syncRemoteMarioArchiveSlot(slot);
            continue;
        }

        if (sPackPrefetchCooldown[slot] > 0) {
            anyPending = true;
            continue;
        }

        // Budget: one SMSLoadArchive attempt per call.
        sPrefetchCursor = (slot + 1) % kMaxSlots;
        char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
        formatId(idStr, desired);
        OSReport("[BSMSO] prefetchRemoteMarioPacks slot=%u id='%s' (1 load budget)\n", slot,
                 idStr);
        syncRemoteMarioArchiveSlot(slot);
        if (!marioSlotHasCustomPack(slot)) {
            sPackPrefetchCooldown[slot] = kPackPrefetchFailCooldownFrames;
            anyPending = true;
        }
        // More slots may still need work even if this one succeeded.
        for (u32 j = 0; j < kMaxSlots; ++j) {
            if (j == buf->localSlot)
                continue;
            char id[MARIO_MODEL_ID_SIZE] = {};
            readSlotId(buf, j, id);
            if (!marioModelIdIsEmpty(id) && !findCachedPack(id)) {
                anyPending = true;
                break;
            }
        }
        return anyPending;
    }

    return anyPending;
}

bool setActiveMarioArchive(u32 slot) {
    if (!sInitialized)
        return false;
    if (slot >= kMaxSlots)
        slot = 0;

    if (!ensureRetailLoaded()) {
        // Soft-fail: if the game already mounted retail, treat as success.
        if (JKRFileLoader::getVolume("mario") && arcBufMario) {
            sRetailBuffer = arcBufMario;
            sActiveSlot = slot;
            return true;
        }
        return false;
    }

    // Stage-applied archives live in sSlots. Do NOT re-read CommBuffer when a
    // buffer is already bound — mid-stage id swaps only update sLast* ids, and
    // restoreLocalMarioArchive must remount the stage-applied local pack, not a
    // pending hot-swap id. Late-join remotes call syncRemoteMarioArchiveSlot
    // first to rebind sSlots before spawn/rebuild.
    if (!sSlots[slot].buffer) {
        CommBuffer *buf = getCommBuffer();
        if (buf)
            readSlotId(buf, slot, sSlots[slot].modelId);
        bool fromCache = false;
        sSlots[slot].buffer = resolveBufferForId(sSlots[slot].modelId,
                                                 &sSlots[slot].loggedMissing, &fromCache);
        // Cache hits are the common path — do not OSReport every remount.
        (void)fromCache;
    }

    void *buffer = sSlots[slot].buffer;
    if (!buffer)
        buffer = sRetailBuffer;
    if (!buffer)
        return false;

    // Defense in depth: never remount a pack that would crash initValues.
    if (buffer != sRetailBuffer && !marioPackBufferIsInitSafe(buffer)) {
        OSReport("[BSMSO] setActiveMarioArchive(%u) rejecting unsafe pack — retail fallback\n",
                 slot);
        buffer = sRetailBuffer;
        sSlots[slot].buffer = sRetailBuffer;
    }

    // Same RARC already mounted (common during retail prewarm across empty slots,
    // and pack-cache remount hits) — skip unmountFixed/mountFixed churn.
    if (arcBufMario == buffer) {
        sActiveSlot = slot;
        return true;
    }

    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatId(idStr, sSlots[slot].modelId);
    OSReport("[BSMSO] setActiveMarioArchive(%u) id='%s' buf=%p retail=%p\n", slot, idStr, buffer,
             sRetailBuffer);

    if (!mountBuffer(buffer)) {
        // Soft-fail: never leave the volume unmounted.
        OSReport("[BSMSO] setActiveMarioArchive(%u) mount failed — attempting retail fallback\n",
                 slot);
        if (sRetailBuffer && sRetailBuffer != buffer && mountBuffer(sRetailBuffer)) {
            sSlots[slot].buffer = sRetailBuffer;
            sActiveSlot = slot;
            return true;
        }
        OSReport("[BSMSO] setActiveMarioArchive(%u) mount failed\n", slot);
        return false;
    }

    sActiveSlot = slot;
    return true;
}

void syncRemoteMarioArchiveSlot(u32 slot) {
    if (!sInitialized || slot >= kMaxSlots)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return;
    if (slot == buf->localSlot)
        return;

    if (!ensureRetailLoaded())
        return;

    char desired[MARIO_MODEL_ID_SIZE] = {};
    readSlotId(buf, slot, desired);

    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatId(idStr, desired);

    // Already bound to this id with a live *custom* buffer — remount only.
    // Soft-fail leaves modelId=desired but buffer=retail; that must NOT early-out
    // or a late EnsurePackPresent / freed heap can never retry SMSLoadArchive.
    const bool alreadyCustom =
        sSlots[slot].buffer && sSlots[slot].buffer != sRetailBuffer &&
        idsEqual(sSlots[slot].modelId, desired);
    if (alreadyCustom) {
        // Already bound — silent (hot path during prewarm / residency).
        return;
    }

    const bool retryingSoftFail =
        !marioModelIdIsEmpty(desired) && sSlots[slot].buffer == sRetailBuffer &&
        idsEqual(sSlots[slot].modelId, desired);

    copyId(sSlots[slot].modelId, desired);
    // Allow another missing-pack log after a soft-fail retry succeeds or fails again.
    if (!retryingSoftFail)
        sSlots[slot].loggedMissing = false;

    bool fromCache = false;
    void *buffer = resolveBufferForId(desired, &sSlots[slot].loggedMissing, &fromCache);
    if (!buffer)
        buffer = sRetailBuffer;
    sSlots[slot].buffer = buffer;

    if (fromCache) {
        // Silent cache hit.
    } else if (marioModelIdIsEmpty(desired) || buffer == sRetailBuffer) {
        if (retryingSoftFail)
            OSReport("[BSMSO] syncRemoteMarioArchiveSlot(%u) id='%s' still retail (retry)\n", slot,
                     idStr);
        else
            OSReport("[BSMSO] syncRemoteMarioArchiveSlot(%u) id='%s' retail\n", slot, idStr);
    } else
        OSReport("[BSMSO] syncRemoteMarioArchiveSlot(%u) id='%s' loaded @ %p\n", slot, idStr,
                 buffer);

    // Invalidate active-slot short-circuit so the next setActiveMarioArchive
    // remounts even if this slot was previously active under a different buffer.
    if (sActiveSlot == slot)
        sActiveSlot = 0xFFFFFFFFu;
}

bool restoreLocalMarioArchive() {
    CommBuffer *buf = getCommBuffer();
    const u8 localSlot = buf ? buf->localSlot : 0;
    return setActiveMarioArchive(localSlot);
}

bool restoreLocalMarioArchiveGuarded() {
    if (restoreLocalMarioArchive())
        return true;

    OSReport("[BSMSO] restoreLocalMarioArchive failed — mounting retail fallback\n");
    if (mountRetailMarioArchive())
        return true;

    OSReport("[BSMSO] retail fallback also failed — mario volume may be wrong\n");
    return false;
}

bool mountRetailMarioArchive() {
    if (!sInitialized)
        return false;
    if (!ensureRetailLoaded()) {
        if (JKRFileLoader::getVolume("mario") && arcBufMario) {
            sRetailBuffer = arcBufMario;
            return true;
        }
        return false;
    }
    if (!sRetailBuffer)
        return false;
    if (arcBufMario == sRetailBuffer)
        return true;
    return mountBuffer(sRetailBuffer);
}

bool refreshLocalMarioModel(TMario *mario) {
    if (!sInitialized || !mario)
        return false;
    if (!restoreLocalMarioArchive())
        return false;
    return rebuildLocalMarioVisuals(mario);
}

bool marioModelIdWantsHiddenCaps(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return false;

    // Prefer the live pack marker (any import that kept retail caps).
    if (void *cached = findCachedPack(id)) {
        if (rarcContainsBasename(cached, "bsmso_hide_caps"))
            return true;
    }

    // Legacy hardcoded ids for packs shipped before the marker existed.
    static const char kBirdoId[MARIO_MODEL_ID_SIZE] = {'1', 'b', '6', '8', '3', 'f', 'c', '7'};
    static const char kYoshiId[MARIO_MODEL_ID_SIZE] = {'f', '1', '3', '0', 'b', '2', '5', 'e'};
    return idsEqual(id, kBirdoId) || idsEqual(id, kYoshiId);
}

void squashHiddenCapDrawInstance(TMario *mario) {
    if (!mario || !mario->mCap)
        return;

    // Kill TMultiMtxEffect lag-ghosts (otherwise unbound/zeroed hats still float
    // in the stage via the cap effect system).
    mario->mCap->mtxEffectHide();

    // Per-J3DModel matrices only — never mutate shared J3DShape draw flags on
    // ma_cap1/ma_cap3 model data (that poisons every Mario using retail caps).
    auto squash = [](J3DModel *model) {
        if (!model)
            return;
        MTXScale(model->mBaseMtx, 0.0f, 0.0f, 0.0f);
        model->calc();
    };
    squash(mario->mCap->mCap1);
    squash(mario->mCap->mCap3);
}

void maintainLocalHiddenCaps(TMario *mario, u32 performFlags) {
    // 0x2 = calcAnim / TMarioCap::perform(2); 0x4 = viewCalc; 0x200 = entryModels.
    // Squash before draw (0x200) and after calc so retail rebinds cannot leave
    // visible orphan hats for the next entryModels pass.
    if (!mario || (performFlags & (0x2u | 0x4u | 0x200u)) == 0)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return;
    if (!marioModelIdWantsHiddenCaps(buf->localMarioModelId))
        return;

    squashHiddenCapDrawInstance(mario);
}

} // namespace smso

// Early-init nops REMOVED (Option A).
// Previously SMS_WRITE_32 at 0x802A6C4C / 0x802A7148 / 0x802A71A8 prevented the
// game from mounting /data/mario.arc. Combined with SMSLoadArchive(..., sRootHeap)
// failing, the volume never existed and boot froze before the title screen.
// Pack remount uses unmountFixed + mountFixed on the already-mounted volume.

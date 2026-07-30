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
#include <JSystem/JSupport/JSUMemoryStream.hxx>
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

// Cache only packs named by the live roster/local selection while preserving
// body-construction headroom. Buffers cannot be individually freed:
// live J3D graphs keep pointers into them, so this is a session-lifetime cache.
constexpr u32 kMaxSlots = MAX_PLAYERS;
// Ten simultaneous player identities plus two same-stage swap identities. The
// heap gate normally binds first; this hard cap prevents untracked allocations.
constexpr u32 kMaxPackCacheEntries = MAX_PLAYERS + 2;
constexpr const char *kRetailPathArc = "/data/mario.arc";
constexpr const char *kRetailPathSzs = "/data/mario.szs";
constexpr const char *kPackDir = "/data/bsmso_models/";
// Pack RARC is ~1.4–1.9 MiB after in-place BMD/BTK patch. The dedicated pack
// heap prevents archive blocks from fragmenting body/J3D allocations.
constexpr u32 kMaxPackBytes = 0x00200000u; // 2 MiB upper bound
constexpr u32 kPackAllocationMargin = 0x00000100u; // heap block/header/alignment

constexpr bool archiveAdmissionFits(u32 totalFree, u32 largestFree,
                                    u32 archiveBytes) {
    const u32 archiveNeed = archiveBytes + kPackAllocationMargin;
    return largestFree >= archiveNeed && totalFree >= archiveNeed;
}
static_assert(archiveAdmissionFits(0x00300000u, 0x00200000u, 0x00180000u));
static_assert(!archiveAdmissionFits(0x00300000u, 0x00100000u, 0x00180000u));
static_assert(!archiveAdmissionFits(0x00100000u, 0x00100000u, 0x00180000u));

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
static void *sPackCacheBuffers[kMaxPackCacheEntries];
static u32 sPackCacheSizes[kMaxPackCacheEntries];
static char sPackCacheIds[kMaxPackCacheEntries][MARIO_MODEL_ID_SIZE];
// Newly loaded archives are cache-visible immediately for remount bookkeeping,
// but body construction waits for this countdown to expire. Stage init and the
// first stage update can occur in one video frame, so two ticks guarantee at
// least one complete frame between DVD I/O and TMario::initValues.
static u8 sPackCacheBodyReadyDelay[kMaxPackCacheEntries];
constexpr u8 kPackBodyReadyDelayTicks = 2;
static u32 sPackCacheCount = 0;
static bool sPackCacheFullLogged = false;
static bool sPackCacheRecycleRequested = false;
// A cached pack can still be rejected by JKRMemArchive::mountFixed even after
// structural validation passed. Each retry costs a remount plus a full
// TMario::initValues, so attempts are bounded per identity; past the limit the
// id is served as retail. dropPackCache() re-arms the table, which is also the
// point at which a replaced pack file on disc becomes visible again.
constexpr u8 kMaxPackMountFailures = 3;
static char sPackMountFailIds[kMaxPackCacheEntries][MARIO_MODEL_ID_SIZE];
static u8 sPackMountFailCounts[kMaxPackCacheEntries];
static u32 sPackMountFailEntryCount = 0;
static bool sBootstrapped = false;
static bool sInitialized = false;
static bool sLocalRebuildBusy = false;
static u32 sActiveSlot = 0xFFFFFFFFu;
// Once per Mario instance: pack-local BodyAngleFree.prm → mBodyAngleFreeParams.
static TMario *sBodyAngleAppliedMario = nullptr;
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

enum class PackLoadState : u8 {
    Idle,
    OpenAndSize,
    Allocate,
    SubmitRead,
    Reading,
    Complete,
    Validate,
    Publish,
};

struct PackLoadJob {
    PackLoadState state;
    char id[MARIO_MODEL_ID_SIZE];
    char path[96];
    u8 slot;
    u8 priority;
    DVDFileInfo file;
    JKRHeap *heap;
    void *buffer;
    u32 fileBytes;
    u32 allocationBytes;
    OSTime startedAt;
    volatile bool callbackDone;
    volatile s32 callbackResult;
    bool fileOpen;
    bool valid;
};

static PackLoadJob sPackLoadJob{};
static u32 sAsyncReadCount = 0;
static u32 sAsyncReadFailureCount = 0;
static u32 sAsyncReadMilliseconds = 0;
static u32 sPackValidationCount = 0;
static u32 sPackValidationMilliseconds = 0;
static u32 sPackCacheHits = 0;
static u32 sPackCacheMisses = 0;
static u32 sPackDeferredCount = 0;
static u32 sPackDiagnosticsFrame = 0;

static void packReadCallback(u32 result, DVDFileInfo *info) {
    // DVD callback context: publish only the scalar completion result. The
    // static DVDFileInfo and destination remain alive until the main thread
    // observes this flag, closes the file, validates, and publishes the cache.
    if (info != &sPackLoadJob.file)
        return;
    sPackLoadJob.callbackResult = static_cast<s32>(result);
    sPackLoadJob.callbackDone = true;
}

constexpr bool modelPreparePriorityMatches(u32 priority, bool local,
                                           bool activeRequest, bool newlyConnected,
                                           bool activeRoster) {
    if (local)
        return priority == 3;
    if (priority == 0)
        return activeRequest;
    if (priority == 1)
        return !activeRequest && newlyConnected;
    if (priority == 2)
        return !activeRequest && !newlyConnected && activeRoster;
    return false;
}
static_assert(modelPreparePriorityMatches(0, false, true, false, true));
static_assert(modelPreparePriorityMatches(1, false, false, true, true));
static_assert(modelPreparePriorityMatches(2, false, false, false, true));
static_assert(modelPreparePriorityMatches(3, true, false, false, false));
static_assert(!modelPreparePriorityMatches(2, false, true, false, true));

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

static bool packMountIsQuarantined(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return false;
    for (u32 i = 0; i < sPackMountFailEntryCount; ++i) {
        if (idsEqual(sPackMountFailIds[i], id))
            return sPackMountFailCounts[i] >= kMaxPackMountFailures;
    }
    return false;
}

static u32 findOrInsertPackFailEntry(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return kMaxPackCacheEntries;
    for (u32 i = 0; i < sPackMountFailEntryCount; ++i) {
        if (idsEqual(sPackMountFailIds[i], id))
            return i;
    }
    // Table full: the pack heap already bounds how many identities can be
    // resident, so drop the record rather than evicting a live quarantine.
    if (sPackMountFailEntryCount >= kMaxPackCacheEntries)
        return kMaxPackCacheEntries;
    const u32 index = sPackMountFailEntryCount;
    copyId(sPackMountFailIds[index], id);
    sPackMountFailCounts[index] = 0;
    ++sPackMountFailEntryCount;
    return index;
}

static void notePackMountFailure(const char id[MARIO_MODEL_ID_SIZE]) {
    const u32 index = findOrInsertPackFailEntry(id);
    if (index >= kMaxPackCacheEntries)
        return;
    if (sPackMountFailCounts[index] >= kMaxPackMountFailures)
        return;

    ++sPackMountFailCounts[index];
    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatId(idStr, id);
    OSReport("[BSMSO] Pack mount rejected id='%s' attempt=%u/%u%s\n", idStr,
             static_cast<u32>(sPackMountFailCounts[index]),
             static_cast<u32>(kMaxPackMountFailures),
             sPackMountFailCounts[index] >= kMaxPackMountFailures
                 ? " — quarantined, serving retail until cache drop"
                 : "");
}

// Permanent session quarantine: DVD I/O errors, FST misses, and RARC validation
// rejects are immutable for this boot. Retrying them re-issues DirectoryBlob
// reads that can raise the retail "disc could not be read" fatal.
static void quarantinePackId(const char id[MARIO_MODEL_ID_SIZE], const char *reason) {
    const u32 index = findOrInsertPackFailEntry(id);
    if (index >= kMaxPackCacheEntries)
        return;
    if (sPackMountFailCounts[index] >= kMaxPackMountFailures)
        return;
    sPackMountFailCounts[index] = kMaxPackMountFailures;
    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatId(idStr, id);
    OSReport("[BSMSO] Pack id='%s' quarantined (%s) — retail fallback, no DVD retry\n",
             idStr, reason ? reason : "failed");
}

// Immutable pack allocations use their own expanded-MEM1 heap. nullptr means
// "defer/retail fallback", never "use current stage/system heap".
static JKRHeap *archiveHeap(u32 archiveBytes) {
    JKRHeap *pack = borrowRemoteActorPackHeap();
    if (pack) {
        const u32 totalFree = static_cast<u32>(pack->getTotalFreeSize());
        const u32 largestFree = static_cast<u32>(pack->getFreeSize());
        if (archiveAdmissionFits(totalFree, largestFree, archiveBytes))
            return pack;
        OSReport("[BSMSO] Pack admission deferred (total=%u largest=%u "
                 "archive=%u margin=%u capacity=%u)\n",
                 totalFree, largestFree, archiveBytes, kPackAllocationMargin,
                 remoteActorPackHeapCapacityBytes());
        return nullptr;
    }
    OSReport("[BSMSO] No dedicated pack heap yet — soft-fail retail\n");
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
    if (entry < 0) {
        OSReport("[BSMSO] SMSLoadArchive FST miss: %s (file not on disc/DirectoryBlob)\n", path);
        return nullptr;
    }

    // Use the actual RARC length instead of a fixed 2 MiB upper bound. This
    // admits smaller packs late in a session while still checking the largest
    // contiguous block, so fragmented total-free space cannot masquerade as a
    // usable reservation.
    u32 archiveBytes = kMaxPackBytes;
    DVDFileInfo info{};
    if (DVDFastOpen(entry, &info)) {
        archiveBytes = info.mLen;
        DVDClose(&info);
    }
    if (!allowCurrentHeapFallback && archiveBytes > kMaxPackBytes) {
        OSReport("[BSMSO] Model pack exceeds bounded archive limit: %s bytes=%u max=%u\n",
                 path, archiveBytes, kMaxPackBytes);
        return nullptr;
    }
    JKRHeap *heap = archiveHeap(archiveBytes);

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

static u32 findCachedPackSize(const char id[MARIO_MODEL_ID_SIZE]) {
    if (marioModelIdIsEmpty(id))
        return 0;
    for (u32 i = 0; i < sPackCacheCount; ++i) {
        if (idsEqual(sPackCacheIds[i], id))
            return sPackCacheSizes[i];
    }
    return 0;
}

static bool tryUnpinUnreferencedPack();

static void cachePack(const char id[MARIO_MODEL_ID_SIZE], void *buffer, u32 size) {
    if (!buffer || marioModelIdIsEmpty(id))
        return;
    if (findCachedPack(id))
        return;
    if (sPackCacheCount >= kMaxPackCacheEntries) {
        while (sPackCacheCount >= kMaxPackCacheEntries && tryUnpinUnreferencedPack()) {
        }
    }
    if (sPackCacheCount >= kMaxPackCacheEntries)
        return;
    copyId(sPackCacheIds[sPackCacheCount], id);
    sPackCacheBuffers[sPackCacheCount] = buffer;
    sPackCacheSizes[sPackCacheCount] = size;
    sPackCacheBodyReadyDelay[sPackCacheCount] = kPackBodyReadyDelayTicks;
    ++sPackCacheCount;
}

static bool marioPackBufferIsInitSafe(void *buffer, u32 bufferSize);

static void dropPackCache() {
    for (u32 i = 0; i < kMaxPackCacheEntries; ++i) {
        sPackCacheBuffers[i] = nullptr;
        sPackCacheSizes[i] = 0;
        clearId(sPackCacheIds[i]);
        sPackCacheBodyReadyDelay[i] = 0;
        clearId(sPackMountFailIds[i]);
        sPackMountFailCounts[i] = 0;
    }
    sPackMountFailEntryCount = 0;
    sPackCacheCount = 0;
    sPackCacheFullLogged = false;
    sPackCacheRecycleRequested = false;
}

static bool packBufferIsMounted(void *buffer) {
    return buffer && (buffer == arcBufMario || buffer == sRetailBuffer);
}

static bool packIdPinnedBySlotMount(const char id[MARIO_MODEL_ID_SIZE]) {
    if (marioModelIdIsEmpty(id))
        return false;
    for (u32 i = 0; i < kMaxSlots; ++i) {
        if (idsEqual(sSlots[i].modelId, id) && sSlots[i].buffer)
            return true;
    }
    return false;
}

// LRU-ish: drop the oldest cache entry that is not referenced by any live/ready
// body graph, outstanding request, or mounted slot. Frees the pack heap block
// so a new identity can admit mid-stage instead of waiting for a warp recycle.
static bool tryUnpinUnreferencedPack() {
    JKRHeap *packHeap = borrowRemoteActorPackHeap();
    for (u32 i = 0; i < sPackCacheCount; ++i) {
        if (marioModelIdIsEmpty(sPackCacheIds[i]) || !sPackCacheBuffers[i])
            continue;
        if (packBufferIsMounted(sPackCacheBuffers[i]))
            continue;
        if (packIdPinnedBySlotMount(sPackCacheIds[i]))
            continue;
        if (remoteActorReferencesModelId(sPackCacheIds[i]))
            continue;

        void *buf = sPackCacheBuffers[i];
        char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
        for (u32 c = 0; c < MARIO_MODEL_ID_SIZE; ++c)
            idStr[c] = sPackCacheIds[i][c] ? sPackCacheIds[i][c] : '\0';

        for (u32 j = i; j + 1 < sPackCacheCount; ++j) {
            sPackCacheBuffers[j] = sPackCacheBuffers[j + 1];
            sPackCacheSizes[j] = sPackCacheSizes[j + 1];
            copyId(sPackCacheIds[j], sPackCacheIds[j + 1]);
            sPackCacheBodyReadyDelay[j] = sPackCacheBodyReadyDelay[j + 1];
        }
        --sPackCacheCount;
        sPackCacheBuffers[sPackCacheCount] = nullptr;
        sPackCacheSizes[sPackCacheCount] = 0;
        clearId(sPackCacheIds[sPackCacheCount]);
        sPackCacheBodyReadyDelay[sPackCacheCount] = 0;

        if (packHeap && buf)
            packHeap->free(buf);

        sPackCacheFullLogged = false;
        sPackCacheRecycleRequested = false;
        OSReport("[BSMSO] Pack cache unpinned unreferenced id='%s' (cache=%u/%u)\n", idStr,
                 sPackCacheCount, kMaxPackCacheEntries);
        return true;
    }
    return false;
}

static void *resolveBufferForId(const char id[MARIO_MODEL_ID_SIZE], bool *loggedMissing,
                                bool *outFromCache, bool allowSynchronousLoad) {
    if (outFromCache)
        *outFromCache = false;

    if (marioModelIdIsEmpty(id))
        return sRetailBuffer;

    // A quarantined id still sits in the cache (its buffer backs no graph), but
    // binding a slot to it would only produce another failed mount.
    if (packMountIsQuarantined(id)) {
        if (loggedMissing && !*loggedMissing) {
            char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
            formatId(idStr, id);
            OSReport("[BSMSO] Pack id='%s' quarantined (mount kept failing) — retail\n", idStr);
            *loggedMissing = true;
        }
        return sRetailBuffer;
    }

    if (void *cached = findCachedPack(id)) {
        ++sPackCacheHits;
        if (outFromCache)
            *outFromCache = true;
        return cached;
    }
    ++sPackCacheMisses;

    // Every loaded pack must remain discoverable for the lifetime of any body
    // that retains J3D pointers into its RARC. When the bounded cache is full,
    // unpin the oldest pack that no live/ready/request still references.
    if (sPackCacheCount >= kMaxPackCacheEntries) {
        while (sPackCacheCount >= kMaxPackCacheEntries && tryUnpinUnreferencedPack()) {
        }
        if (sPackCacheCount >= kMaxPackCacheEntries) {
            if (!sPackCacheFullLogged) {
                OSReport("[BSMSO] Pack cache full (%u entries) — all entries pinned; "
                         "retail fallback until a body releases a pack or stage recycle\n",
                         sPackCacheCount);
                sPackCacheFullLogged = true;
            }
            sPackCacheRecycleRequested = true;
            return sRetailBuffer;
        }
    }

    if (!allowSynchronousLoad)
        return sRetailBuffer;

    char path[96];
    buildPackPath(path, sizeof(path), id);
    OSReport("[BSMSO] Loading-screen synchronous pack fallback: %s\n", path);
    void *buf = loadArchivePath(path);
    if (buf) {
        const u8 *hdr = reinterpret_cast<const u8 *>(buf);
        const u32 loadedSize =
            hdr[0] == 'R' && hdr[1] == 'A' && hdr[2] == 'R' && hdr[3] == 'C'
                ? (static_cast<u32>(hdr[4]) << 24) |
                      (static_cast<u32>(hdr[5]) << 16) |
                      (static_cast<u32>(hdr[6]) << 8) |
                      static_cast<u32>(hdr[7])
                : 0;
        if (!marioPackBufferIsInitSafe(buf, loadedSize)) {
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
        cachePack(id, buf, loadedSize);
        return buf;
    }

    if (loggedMissing && !*loggedMissing) {
        OSReport("[BSMSO] Missing/unloadable model pack %s — falling back to retail\n", path);
        *loggedMissing = true;
    }
    return sRetailBuffer;
}

static void *resolveBufferForId(const char id[MARIO_MODEL_ID_SIZE], bool *loggedMissing,
                                bool allowSynchronousLoad = false) {
    return resolveBufferForId(id, loggedMissing, nullptr, allowSynchronousLoad);
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
                                   u32 *outSize, u32 bufferSize = 0) {
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
        headerLength >= fileLength || fileLength - headerLength < 0x20 ||
        (bufferSize != 0 && fileLength > bufferSize))
        return false;

    u8 *info = hdr + headerLength;
    const u32 numEntries = be32(info + 8);
    const u32 entryRel = be32(info + 12);
    const u32 stringRel = be32(info + 0x14);
    if (numEntries == 0 || numEntries > 4096 ||
        entryRel > fileLength - headerLength ||
        stringRel > fileLength - headerLength ||
        numEntries * 0x14u > fileLength - headerLength - entryRel)
        return false;

    if (fileDataRel > fileLength - headerLength)
        return false;
    const u32 absFileData = headerLength + fileDataRel;

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
        if (dataOff > fileLength - absFileData)
            return false;
        const u32 abs = absFileData + dataOff;
        if (size == 0 || size > fileLength - abs)
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
static bool marioPackBufferIsInitSafe(void *buffer, u32 bufferSize) {
    if (!buffer || bufferSize < 0x40 || bufferSize > kMaxPackBytes)
        return false;

    const u8 *body = nullptr;
    u32 bodySize = 0;
    if (!findRarcFileByBasename(buffer, "ma_mdl1.bmd", &body, &bodySize, bufferSize))
        return false;
    if (readBmdJointCount(body, bodySize) != 29)
        return false;

    // Capless packs keep retail caps and stamp this marker — treat as safe.
    const u8 *hide = nullptr;
    u32 hideSize = 0;
    if (findRarcFileByBasename(buffer, "bsmso_hide_caps", &hide, &hideSize, bufferSize))
        return true;

    const u8 *cap1 = nullptr;
    u32 cap1Size = 0;
    const u8 *cap3 = nullptr;
    u32 cap3Size = 0;
    if (!findRarcFileByBasename(buffer, "ma_cap1.bmd", &cap1, &cap1Size, bufferSize) ||
        !findRarcFileByBasename(buffer, "ma_cap3.bmd", &cap3, &cap3Size, bufferSize))
        return false;
    if (readBmdJointCount(cap1, cap1Size) != 2)
        return false;
    if (readBmdJointCount(cap3, cap3Size) != 3)
        return false;
    return true;
}

static void resetPackLoadJob(bool freeUnpublishedBuffer) {
    if (sPackLoadJob.fileOpen) {
        // SDK contract: DVDClose synchronously cancels an unfinished async
        // transfer before closing. Only reset/free the static job after it
        // returns, so neither the drive nor its callback can still reference
        // DVDFileInfo or the destination buffer when the job storage is reused.
        DVDClose(&sPackLoadJob.file);
        sPackLoadJob.fileOpen = false;
    }
    if (freeUnpublishedBuffer && sPackLoadJob.buffer && sPackLoadJob.heap)
        sPackLoadJob.heap->free(sPackLoadJob.buffer);
    sPackLoadJob = {};
    sPackLoadJob.state = PackLoadState::Idle;
}

enum class PackFailKind : u8 {
    // Heap pressure / cache full — retry after cooldown when RAM frees.
    Transient,
    // Disc I/O, missing FST entry, corrupt/unsafe RARC — never retry this boot.
    Permanent,
};

static void failPackLoadJob(const char *reason,
                            PackFailKind kind = PackFailKind::Permanent) {
    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatId(idStr, sPackLoadJob.id);
    OSReport("[BSMSO] Async pack failed state=%u slot=%u id='%s': %s\n",
             static_cast<u32>(sPackLoadJob.state), sPackLoadJob.slot, idStr,
             reason ? reason : "unknown");
    ++sAsyncReadFailureCount;
    if (kind == PackFailKind::Permanent)
        quarantinePackId(sPackLoadJob.id, reason);
    if (sPackLoadJob.slot < kMaxSlots)
        sPackPrefetchCooldown[sPackLoadJob.slot] =
            kind == PackFailKind::Permanent ? 0xFFu
                                            : kPackPrefetchFailCooldownFrames;
    resetPackLoadJob(true);
}

static bool beginPackLoadJob(const char id[MARIO_MODEL_ID_SIZE], u8 slot,
                             u8 priority) {
    if (sPackLoadJob.state != PackLoadState::Idle || !id ||
        marioModelIdIsEmpty(id))
        return false;
    sPackLoadJob = {};
    sPackLoadJob.state = PackLoadState::OpenAndSize;
    copyId(sPackLoadJob.id, id);
    buildPackPath(sPackLoadJob.path, sizeof(sPackLoadJob.path), id);
    sPackLoadJob.slot = slot;
    sPackLoadJob.priority = priority;
    sPackLoadJob.startedAt = OSGetTime();
    return true;
}

static void advancePackLoadJob(bool *outReadStarted) {
    if (outReadStarted)
        *outReadStarted = false;

    switch (sPackLoadJob.state) {
    case PackLoadState::Idle:
        return;
    case PackLoadState::OpenAndSize: {
        char path[96];
        snprintf(path, sizeof(path), "%s", sPackLoadJob.path);
        const s32 entry = DVDConvertPathToEntrynum(path);
        if (entry < 0 || !DVDFastOpen(entry, &sPackLoadJob.file)) {
            failPackLoadJob("FST/open miss");
            return;
        }
        sPackLoadJob.fileOpen = true;
        sPackLoadJob.fileBytes = sPackLoadJob.file.mLen;
        if (sPackLoadJob.fileBytes < 0x40 ||
            sPackLoadJob.fileBytes > kMaxPackBytes) {
            failPackLoadJob("archive size outside bounded limit");
            return;
        }
        // DVD length must be a 32-byte multiple and must NEVER exceed the FST
        // length. Rounding up past mLen makes DirectoryBlob read past the host
        // file and raises the retail disc-error fatal. Reject unaligned FST
        // sizes instead of over-reading.
        if ((sPackLoadJob.fileBytes & 31u) != 0) {
            failPackLoadJob("archive size not 32-byte aligned");
            return;
        }
        sPackLoadJob.allocationBytes = sPackLoadJob.fileBytes;
        sPackLoadJob.state = PackLoadState::Allocate;
        return;
    }
    case PackLoadState::Allocate:
        sPackLoadJob.heap = archiveHeap(sPackLoadJob.allocationBytes);
        if (!sPackLoadJob.heap) {
            ++sPackDeferredCount;
            failPackLoadJob("dedicated pack heap has no contiguous capacity",
                            PackFailKind::Transient);
            return;
        }
        sPackLoadJob.buffer =
            sPackLoadJob.heap->alloc(sPackLoadJob.allocationBytes, 0x20);
        if (!sPackLoadJob.buffer) {
            failPackLoadJob("aligned pack allocation failed",
                            PackFailKind::Transient);
            return;
        }
        sPackLoadJob.state = PackLoadState::SubmitRead;
        return;
    case PackLoadState::SubmitRead:
        sPackLoadJob.callbackDone = false;
        sPackLoadJob.callbackResult = DVD_ERROR_FATAL;
        DCInvalidateRange(sPackLoadJob.buffer, sPackLoadJob.allocationBytes);
        if (!DVDReadAsyncPrio(&sPackLoadJob.file, sPackLoadJob.buffer,
                              // Exact FST length (already 32-byte aligned). Never
                              // round up — DirectoryBlob EOF becomes disc-fatal.
                              static_cast<s32>(sPackLoadJob.fileBytes), 0,
                              packReadCallback, 2)) {
            failPackLoadJob("DVDReadAsyncPrio queue rejected",
                            PackFailKind::Transient);
            return;
        }
        sPackLoadJob.state = PackLoadState::Reading;
        ++sAsyncReadCount;
        if (outReadStarted)
            *outReadStarted = true;
        return;
    case PackLoadState::Reading: {
        const bool interrupts = OSDisableInterrupts();
        const bool done = sPackLoadJob.callbackDone;
        const s32 result = sPackLoadJob.callbackResult;
        OSRestoreInterrupts(interrupts);
        if (!done)
            return;
        if (result < 0 ||
            static_cast<u32>(result) != sPackLoadJob.fileBytes) {
            // DVD DEINT / short read: quarantining prevents a 10-player lobby
            // from re-issuing the same fatal DirectoryBlob EOF every 2s.
            failPackLoadJob("short/error async read");
            return;
        }
        // DVD auto-invalidation is enabled in this runtime, but explicitly
        // invalidate before CPU validation for coherent behavior across builds.
        DCInvalidateRange(sPackLoadJob.buffer, sPackLoadJob.allocationBytes);
        DVDClose(&sPackLoadJob.file);
        sPackLoadJob.fileOpen = false;
        sPackLoadJob.state = PackLoadState::Complete;
        sAsyncReadMilliseconds += static_cast<u32>(
            OSTicksToMilliseconds(OSGetTime() - sPackLoadJob.startedAt));
        return;
    }
    case PackLoadState::Complete:
        sPackLoadJob.state = PackLoadState::Validate;
        return;
    case PackLoadState::Validate: {
        const OSTime start = OSGetTime();
        sPackLoadJob.valid = marioPackBufferIsInitSafe(
            sPackLoadJob.buffer, sPackLoadJob.fileBytes);
        sPackValidationMilliseconds +=
            static_cast<u32>(OSTicksToMilliseconds(OSGetTime() - start));
        ++sPackValidationCount;
        if (!sPackLoadJob.valid) {
            failPackLoadJob("RARC/body/cap validation rejected");
            return;
        }
        sPackLoadJob.state = PackLoadState::Publish;
        return;
    }
    case PackLoadState::Publish: {
        // Rapid intent changes do not invalidate an immutable completed pack;
        // cache it for bounded reuse, but never mount or build in this phase.
        if (findCachedPack(sPackLoadJob.id)) {
            resetPackLoadJob(true);
            return;
        }
        if (sPackCacheCount >= kMaxPackCacheEntries) {
            while (sPackCacheCount >= kMaxPackCacheEntries && tryUnpinUnreferencedPack()) {
            }
        }
        if (sPackCacheCount >= kMaxPackCacheEntries) {
            sPackCacheRecycleRequested = true;
            failPackLoadJob("bounded pack cache full", PackFailKind::Transient);
            return;
        }
        void *published = sPackLoadJob.buffer;
        const u32 publishedBytes = sPackLoadJob.fileBytes;
        char publishedId[MARIO_MODEL_ID_SIZE] = {};
        copyId(publishedId, sPackLoadJob.id);
        const u8 slot = sPackLoadJob.slot;
        sPackLoadJob.buffer = nullptr;
        cachePack(publishedId, published, publishedBytes);
        sPackLoadJob = {};
        sPackLoadJob.state = PackLoadState::Idle;
        if (slot < kMaxSlots)
            syncRemoteMarioArchiveSlot(slot);
        return;
    }
    }
}

enum class MountOutcome : u8 {
    // Volume is unusable / was left unmounted.
    Failed,
    // The requested buffer is the live "mario" volume.
    Mounted,
    // The requested buffer was rejected; retail is the live volume instead.
    // The volume is usable, but callers holding per-slot custom state MUST
    // rebind that slot to retail — otherwise the slot reads as "already on its
    // pack" while every resource resolves from retail, and it never upgrades.
    RetailFallback,
};

static MountOutcome mountBufferChecked(void *buffer) {
    if (!buffer)
        return MountOutcome::Failed;

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
            return MountOutcome::Failed;
        }
        arcBufMario = buffer;
        clearArchiveCachedFilePointers(buffer);
        return MountOutcome::Mounted;
    }

    if (arcBufMario == buffer) {
        // Continuously mounted: SDIFileEntry.mData is live for this buffer.
        // Clearing/re-reporting here is only needed after a real remountFixed
        // (below). Matches setActiveMarioArchive's arcBufMario==buffer skip.
        return MountOutcome::Mounted;
    }

    OSReport("[BSMSO] remountFixed mario %p -> %p\n", arcBufMario, buffer);
    archive->unmountFixed();
    arcBufMario = buffer;
    // New pack may carry BodyAngleFree.prm — force re-apply on next stage tick.
    sBodyAngleAppliedMario = nullptr;
    if (!archive->mountFixed(buffer, UNK_0)) {
        OSReport("[BSMSO] mountFixed failed; attempting retail fallback\n");
        if (buffer != sRetailBuffer && sRetailBuffer) {
            arcBufMario = sRetailBuffer;
            if (archive->mountFixed(sRetailBuffer, UNK_0)) {
                clearArchiveCachedFilePointers(sRetailBuffer);
                return MountOutcome::RetailFallback;
            }
        }
        return MountOutcome::Failed;
    }
    clearArchiveCachedFilePointers(buffer);
    OSReport("[BSMSO] remountFixed ok @ %p\n", buffer);
    return MountOutcome::Mounted;
}

// For callers that only need a usable "mario" volume (retail remount, teardown,
// boot fallback). Callers that care *which* archive is live must use
// mountBufferChecked and handle RetailFallback.
static bool mountBuffer(void *buffer) {
    return mountBufferChecked(buffer) != MountOutcome::Failed;
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
static bool ensureRetailLoaded(bool allowSynchronousFallback = false) {
    if (sRetailBuffer)
        return true;

    auto *archive = reinterpret_cast<JKRMemArchive *>(JKRFileLoader::getVolume("mario"));
    if (archive && arcBufMario) {
        sRetailBuffer = arcBufMario;
        OSReport("[BSMSO] Adopted retail mario volume @ %p (arcBufMario)\n", sRetailBuffer);
        return true;
    }

    if (!allowSynchronousFallback)
        return false;

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
    if (!ensureRetailLoaded(/*allowSynchronousFallback=*/true))
        return;

    const u8 localSlot = buf ? buf->localSlot : 0;
    if (localSlot >= kMaxSlots)
        return;

    SlotArchive &slot = sSlots[localSlot];
    if (buf)
        readSlotId(buf, localSlot, slot.modelId);
    slot.buffer = resolveBufferForId(slot.modelId, &slot.loggedMissing,
                                     /*allowSynchronousLoad=*/true);
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

    // Soft same-stage reloads (buggy ep-0xFF moveStage) skip exitStageCallbacks.
    // Force the same teardown stageExit would have run so Shadow TexAnim bindings
    // and pack mounts cannot leak into the next Mario construct.
    if (sInitialized) {
        OSReport("[BSMSO] initMarioModelSystem: prior stage still live — forcing cleanup "
                 "(exit callback skipped)\n");
        clearMarioModelSystem(/*keepPackCache=*/false);
    }

    sInitialized = true;
    sActiveSlot = 0xFFFFFFFFu;
    sBodyAngleAppliedMario = nullptr;

    OSReport("[BSMSO] initMarioModelSystem begin (vol=%p arcBuf=%p retail=%p packs=%u)\n",
             JKRFileLoader::getVolume("mario"), arcBufMario, sRetailBuffer, sPackCacheCount);

    // Keep sRetailBuffer across stages (game-owned). Connected transitions also
    // retain the bounded custom-pack cache with its owning remote heap; slot
    // bindings are rebuilt below and resolve directly to those warm buffers.
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

bool marioModelSystemIsLive() { return sInitialized; }

void clearMarioModelSystem(bool keepPackCache) {
    // Quiesce DVD callback ownership before any stage-exit decision can destroy
    // the pack heap. Pending (unpublished) storage may be individually freed.
    if (sPackLoadJob.state != PackLoadState::Idle)
        resetPackLoadJob(true);

    // Remount retail BEFORE any remote-heap destroy so the next stage's local
    // Mario init sees a valid volume. Slot bindings are always cleared; pack
    // cache survives only when the remote heap is also kept alive.
    if (sRetailBuffer && JKRFileLoader::getVolume("mario")) {
        if (arcBufMario != sRetailBuffer)
            mountBuffer(sRetailBuffer);
    }

    sInitialized = false;
    sLocalRebuildBusy = false;
    sBodyAngleAppliedMario = nullptr;
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

void ensureLocalBodyAngleFreeParams(TMario *mario) {
    if (!mario || sBodyAngleAppliedMario == mario)
        return;

    // TParams::load("/Mario/BodyAngleFree.prm") reads params.szs only — JKR
    // getResource on the mario volume does not see pack-root PRMs reliably.
    // Scan the mounted RARC buffer by basename (same path as hide-caps).
    if (!arcBufMario) {
        // Remount may still be pending; retry next frame.
        return;
    }

    const u8 *prm = nullptr;
    u32 prmSize = 0;
    if (!findRarcFileByBasename(arcBufMario, "BodyAngleFree.prm", &prm, &prmSize))
        findRarcFileByBasename(arcBufMario, "bodyanglefree.prm", &prm, &prmSize);

    if (!prm || prmSize == 0) {
        // Retail / packs without override keep construction-time params.szs values.
        sBodyAngleAppliedMario = mario;
        OSReport("[BSMSO] No pack BodyAngleFree.prm in mario RARC — keeping params.szs lean\n");
        return;
    }

    JSUMemoryInputStream stream(const_cast<u8 *>(prm), prmSize);
    mario->mBodyAngleFreeParams.load(stream);
    sBodyAngleAppliedMario = mario;
    OSReport("[BSMSO] Reloaded mBodyAngleFreeParams from pack BodyAngleFree.prm (%u bytes)\n",
             static_cast<unsigned>(prmSize));
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

        // Apply explicit changes while the remote snapshot is still connected,
        // including custom -> retail (empty id). A disconnected/empty slot keeps
        // its parked body until normal dismiss so a transient roster clear does
        // not cause pointless rebuild work.
        const bool remoteConnected = buf->remoteSnapshots[i].connected != 0;
        if (!marioModelIdIsEmpty(buf->remoteMarioModelIds[i]) || remoteConnected) {
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

bool isMarioModelPackReadyForBodyInit(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return true;
    // Resident but unmountable: no body path may spend an initValues budget on
    // it, and acquirePoolBodyForSlot may hand this slot a retail spare again.
    if (packMountIsQuarantined(id))
        return false;
    for (u32 i = 0; i < sPackCacheCount; ++i) {
        if (idsEqual(sPackCacheIds[i], id))
            return sPackCacheBuffers[i] != nullptr && sPackCacheBodyReadyDelay[i] == 0;
    }
    return false;
}

u32 marioModelPackCacheCount() { return sPackCacheCount; }

bool readMarioModelPackCacheId(u32 index, char out[MARIO_MODEL_ID_SIZE]) {
    if (!out || index >= sPackCacheCount || !sPackCacheBuffers[index])
        return false;
    copyId(out, sPackCacheIds[index]);
    return true;
}

bool mountCachedMarioModelPack(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!sInitialized || !id || marioModelIdIsEmpty(id))
        return mountRetailMarioArchive();
    if (packMountIsQuarantined(id))
        return false;
    void *buffer = findCachedPack(id);
    if (!buffer || !marioPackBufferIsInitSafe(buffer, findCachedPackSize(id)))
        return false;
    // Only "the requested pack is live" counts. A retail fallback here would
    // build a ready body from retail assets under a custom identity.
    const MountOutcome outcome = mountBufferChecked(buffer);
    if (outcome != MountOutcome::Mounted) {
        if (outcome == MountOutcome::RetailFallback)
            notePackMountFailure(id);
        return false;
    }
    sActiveSlot = 0xFFFFFFFFu;
    return true;
}

bool marioModelPackCacheNeedsRecycleOnStageExit() {
    // The cache and every J3D graph that references it share the remote-heap
    // lifetime. Keeping both across a connected stage transition is safe and
    // removes repeated 1.4–1.9 MiB DVD reads. On-disk replacement while Dolphin
    // is running is intentionally adopted only after disconnect/relaunch, when
    // clearMarioModelSystem(false) drops pointers before heap destruction.
    return sPackCacheRecycleRequested;
}

bool prefetchRemoteMarioPacks(bool *outLoadStarted) {
    if (outLoadStarted)
        *outLoadStarted = false;
    if (!sInitialized)
        return false;

    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return false;
    if ((buf->bridgeFlags & BF_CONNECTED) == 0)
        return false;

    // Ensure the dedicated archive heap exists before opening a DVD job.
    if (!borrowRemoteActorPackHeap())
        return true;

    for (u32 i = 0; i < sPackCacheCount; ++i) {
        if (sPackCacheBodyReadyDelay[i] > 0)
            --sPackCacheBodyReadyDelay[i];
    }

    for (u32 i = 0; i < kMaxSlots; ++i) {
        if (sPackPrefetchCooldown[i] > 0)
            --sPackPrefetchCooldown[i];
    }

    if (++sPackDiagnosticsFrame >= 600) {
        sPackDiagnosticsFrame = 0;
        OSReport("[BSMSO] Pack async diag reads=%u failures=%u readMs=%u "
                 "validations=%u validationMs=%u hits=%u misses=%u deferred=%u "
                 "cache=%u/%u job=%u packFree=%u/%u\n",
                 sAsyncReadCount, sAsyncReadFailureCount,
                 sAsyncReadMilliseconds, sPackValidationCount,
                 sPackValidationMilliseconds, sPackCacheHits,
                 sPackCacheMisses, sPackDeferredCount, sPackCacheCount,
                 kMaxPackCacheEntries, static_cast<u32>(sPackLoadJob.state),
                 static_cast<u32>(borrowRemoteActorPackHeap()->getTotalFreeSize()),
                 remoteActorPackHeapCapacityBytes());
    }

    if (sPackLoadJob.state != PackLoadState::Idle) {
        advancePackLoadJob(outLoadStarted);
        return true;
    }

    bool anyPending = false;
    // Four bounded priority passes:
    //   0 live body waiting for a model change/first custom apply
    //   1 connected snapshot (join/first appearance)
    //   2 announced roster id without a snapshot yet
    //   3 imminent local selection (warms the next-stage local mount)
    // There is deliberately no installed-library speculation.
    for (u32 priority = 0; priority < 4; ++priority) {
        for (u32 n = 0; n < kMaxSlots; ++n) {
            const u32 slot = (sPrefetchCursor + n) % kMaxSlots;
            const bool local = slot == buf->localSlot;

            char desired[MARIO_MODEL_ID_SIZE] = {};
            readSlotId(buf, slot, desired);
            if (marioModelIdIsEmpty(desired))
                continue;

            // Quarantined identities are immutable failures for this boot —
            // never open another DVD job (10-player stampede amplifier).
            if (packMountIsQuarantined(desired))
                continue;

            if (!local) {
                const bool hasBody =
                    hasRemoteBodyForSlotLoose(static_cast<u8>(slot));
                const bool activeRequest =
                    hasBody && !isRemoteMarioModelFrozen(static_cast<u8>(slot));
                const bool connected = buf->remoteSnapshots[slot].connected != 0;
                const bool newlyConnected = connected && !hasBody;
                const bool activeRoster = connected ||
                                          !marioModelIdIsEmpty(desired);
                if (!modelPreparePriorityMatches(priority, false, activeRequest,
                                                 newlyConnected, activeRoster))
                    continue;
            } else if (!modelPreparePriorityMatches(priority, true, false, false,
                                                    false)) {
                continue;
            }

            if (findCachedPack(desired)) {
                ++sPackCacheHits;
                // A quarantined id is cached but will never bind; re-syncing it
                // every frame only re-resolves back to retail.
                if (!local && !packMountIsQuarantined(desired) &&
                    (!marioSlotHasCustomPack(slot) || !idsEqual(sSlots[slot].modelId, desired)))
                    syncRemoteMarioArchiveSlot(slot);
                continue;
            }

            anyPending = true;
            if (sPackPrefetchCooldown[slot] > 0)
                continue;

            ++sPackCacheMisses;
            // Exactly one demand job may be active. Open/size, allocation,
            // submission, completion, validation, and publication advance on
            // separate main-thread updates.
            sPrefetchCursor = (slot + 1) % kMaxSlots;
            char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
            formatId(idStr, desired);
            OSReport("[BSMSO] Demand async pack priority=%u slot=%u id='%s'\n",
                     priority, slot, idStr);
            beginPackLoadJob(desired, static_cast<u8>(slot),
                             static_cast<u8>(priority));
            return true;
        }
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
        sSlots[slot].buffer = resolveBufferForId(
            sSlots[slot].modelId, &sSlots[slot].loggedMissing, &fromCache,
            /*allowSynchronousLoad=*/false);
        // Cache hits are the common path — do not OSReport every remount.
        (void)fromCache;
    }

    void *buffer = sSlots[slot].buffer;
    if (!buffer)
        buffer = sRetailBuffer;
    if (!buffer)
        return false;

    // Defense in depth: never remount a pack that would crash initValues.
    if (buffer != sRetailBuffer &&
        !marioPackBufferIsInitSafe(buffer, findCachedPackSize(sSlots[slot].modelId))) {
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

    const MountOutcome outcome = mountBufferChecked(buffer);
    if (outcome == MountOutcome::Failed) {
        // Soft-fail: never leave the volume unmounted.
        if (buffer != sRetailBuffer)
            notePackMountFailure(sSlots[slot].modelId);
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

    if (outcome == MountOutcome::RetailFallback) {
        // mountBufferChecked already remounted retail, so the volume is usable
        // and this stays a soft-fail. But the slot must now read as retail:
        // leaving the custom buffer bound makes marioSlotHasCustomPack() true,
        // and spawnRemoteBody would stamp the puppet as a finished custom body
        // built from retail geometry — permanently stuck, never re-swapped.
        // modelId is deliberately left on the desired pack so the desired-vs-live
        // reconciliation (and syncRemoteMarioArchiveSlot's soft-fail retry) still
        // see this slot as wanting its pack.
        notePackMountFailure(sSlots[slot].modelId);
        sSlots[slot].buffer = sRetailBuffer;
        OSReport("[BSMSO] setActiveMarioArchive(%u) id='%s' fell back to retail — slot rebound "
                 "(body will build retail and upgrade later)\n",
                 slot, idStr);
        sActiveSlot = slot;
        return true;
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
    void *buffer = resolveBufferForId(
        desired, &sSlots[slot].loggedMissing, &fromCache,
        /*allowSynchronousLoad=*/false);
    if (!buffer)
        buffer = sRetailBuffer;
    sSlots[slot].buffer = buffer;

    if (fromCache) {
        // Silent cache hit.
    } else if (marioModelIdIsEmpty(desired) || buffer == sRetailBuffer) {
        if (retryingSoftFail) {
            // Prefetch re-syncs this slot every frame while it wants a pack it
            // does not have; log the retry once per identity, not per frame.
            if (!sSlots[slot].loggedMissing) {
                OSReport("[BSMSO] syncRemoteMarioArchiveSlot(%u) id='%s' still retail (retry)\n",
                         slot, idStr);
                sSlots[slot].loggedMissing = true;
            }
        } else
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
        const u8 *marker = nullptr;
        u32 markerSize = 0;
        if (findRarcFileByBasename(cached, "bsmso_hide_caps", &marker, &markerSize,
                                   findCachedPackSize(id)))
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

#include "collectible_scan.hpp"

#include <SMS/Manager/ObjManager.hxx>
#include <SMS/macros.h>

namespace smso {

bool isValidMapObjPtr(const void *ptr) {
    const u32 addr = reinterpret_cast<u32>(ptr);
    return addr >= 0x80000000u && addr < 0x81800000u;
}

static void scanObjManager(TObjManager *mgr, MapObjVisitorFn visitor, void *ctx) {
    if (!mgr || !isValidMapObjPtr(mgr))
        return;
    if (mgr->mMaxObjs == 0 || mgr->mMaxObjs > 512 || mgr->mObjCount > mgr->mMaxObjs)
        return;
    if (!mgr->mObjAry || !isValidMapObjPtr(mgr->mObjAry))
        return;

    for (size_t i = 0; i < mgr->mObjCount; ++i) {
        auto *view = mgr->mObjAry[i];
        if (!view || !isValidMapObjPtr(view))
            continue;
        auto *obj = reinterpret_cast<TMapObjBase *>(view);
        if (!isValidMapObjPtr(obj))
            continue;
        if (visitor(obj, ctx))
            return;
    }
}

void forEachManagedMapObj(MapObjVisitorFn visitor, void *ctx) {
    scanObjManager(reinterpret_cast<TObjManager *>(gpItemManager), visitor, ctx);
    scanObjManager(reinterpret_cast<TObjManager *>(gpMapObjManager), visitor, ctx);
    scanObjManager(*reinterpret_cast<TObjManager *const *>(
                       SMS_PORT_REGION(0x8040DF7C, 0x80405644, 0, 0)),
                   visitor, ctx);
    scanObjManager(*reinterpret_cast<TObjManager *const *>(
                       SMS_PORT_REGION(0x8040DF18, 0x804055E0, 0, 0)),
                   visitor, ctx);
    scanObjManager(*reinterpret_cast<TObjManager *const *>(
                       SMS_PORT_REGION(0x8040DF54, 0x8040561C, 0, 0)),
                   visitor, ctx);
    scanObjManager(*reinterpret_cast<TObjManager *const *>(
                       SMS_PORT_REGION(0x8040DF80, 0x80405648, 0, 0)),
                   visitor, ctx);
    scanObjManager(*reinterpret_cast<TObjManager *const *>(
                       SMS_PORT_REGION(0x8040DF40, 0x80405608, 0, 0)),
                   visitor, ctx);
}

} // namespace smso

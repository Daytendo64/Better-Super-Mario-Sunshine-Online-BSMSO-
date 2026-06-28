#pragma once

#include <SMS/MapObj/MapObjBase.hxx>

struct TMapObjManager;
struct TItemManager;

extern TItemManager *gpItemManager;
extern TMapObjManager *gpMapObjManager;

namespace smso {

using MapObjVisitorFn = bool (*)(TMapObjBase *, void *);

bool isValidMapObjPtr(const void *ptr);
void forEachManagedMapObj(MapObjVisitorFn visitor, void *ctx);

} // namespace smso

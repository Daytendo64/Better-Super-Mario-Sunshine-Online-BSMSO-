# Remote Crowd LOD

## Problem

With MaxPlayers=10 (1 local + 9 remotes), FPS drops once ~5+ full TMario remotes share the camera. SMSCoop-style local co-op only needs 2 bodies; BSMSO must keep **full TMario remotes** (FLUDD, cap, hands, shadow, Yoshi, particles) — bare J3D puppets were already rejected.

## Research summary (other mods + Dolphin)

| Source | Takeaway | Citation |
|--------|----------|----------|
| [SMSCoop](https://github.com/TheAzack9/SMSCoop) (`players.cpp`) | Local split-screen: `new TMario()` + `load()` stream, max **2** full players; `setActiveMario` swaps `gpMarioOriginal`. No networked puppet LOD — both are live controllers. | [players.cpp](https://github.com/TheAzack9/SMSCoop/blob/main/src/patches/players.cpp), [renderDistance.cpp](https://github.com/TheAzack9/SMSCoop/blob/main/src/patches/renderDistance.cpp) (water cull expand for P2 only; frustum multi-cam hooks commented out) |
| [sm64coopdx](https://github.com/coop-deluxe/sm64coopdx) / sm64ex-coop | Up to 16 `gMarioStates[]`; remotes are real Mario objects with network-synced state. Local-only paths gate on `playerIndex == 0` (inputs, camera, some audio). Engine interpolates objects/nametags; not a featureless mesh puppet. | [globals.md](https://github.com/coop-deluxe/sm64coopdx/blob/main/docs/lua/globals.md), [mario.c](https://github.com/coop-deluxe/sm64coopdx/blob/main/src/game/mario.c) (`playerIndex != 0` early-outs) |
| Dolphin Dual Core / FIFO | Extra GX work increases CPU↔GPU coupling; Dual Core can amplify hitching when the FIFO is saturated (plaza crowds). | [Performance Guide](https://dolphin-emu.org/docs/guides/performance-guide/), [FIFO Progress Report](https://dolphin-emu.org/blog/2014/12/01/dolphin-progress-report-november-2014/) |
| JSystem BMD/BDL + EVP1 | Weighted envelopes run per skinned character on each pose rebuild; `J3DSkinDeform` (when present) additionally writes CPU-deformed vertices into `J3DModel::mVtxBuffer`. Dolphin walks GX display lists per shape; many characters ≈ many draw calls / FIFO pressure. | SMS J3D / prior BSMSO builds 40–41 |

### What they do that BSMSO does not

- **SMSCoop:** only 2× full TMario; no 9-remote network visual schedule.
- **sm64coopdx:** PC port can afford 16 full Marios; gates expensive local-only work by `playerIndex`, not distance LOD of remotes.
- **Unsafe to copy into BSMSO:** bare BMD/J3D puppets without TMario/FLUDD/Yoshi (docs forbid — feature parity). SMSCoop's `gpMarioOriginal` swap is for local co-op authority, not remote display.

## Ranked bottlenecks (evidence-based)

1. **Per-remote `remoteCalcAnim` / `J3DModelData::perform(2)`** — BCK + joint callbacks + 5 hand models + cap + Yoshi + surf. Scales linearly with visual tick rate. Distance tiers alone never helped a plaza pile-up, because *every* co-located remote sat inside the full-rate radius and therefore ran at 60 Hz.
2. **LOD-skip pose re-root (`updateRemoteRootTransform`)** — `MTXInverse` + one `MTXConcat` per joint + `calcWeightEnvelopeMtx()` + 7 accessory `J3DModel::calc()` calls, every non-calcAnim frame, even for a body that had not moved at all.
3. **`entryModels` / `calcView` / accessory GX** — full Mario + FLUDD + Yoshi shapes per drawn remote (GPU/FIFO). Frustum-culled and appear-hidden remotes still paid `calcView`.
4. **Shadows (`TMBindShadowBody` + ground probe)** — a `gpMap->checkGround` query plus an extra draw per caster, for remotes inside XZ 2000 and |Y| 10000 (AND).
5. **`isRemoteBody`** — a ~54-entry table scan (9 pool + 9 variant + 9 actor + 16 ready + 9 spare + arenas) run from every Mario perform pass, `receiveMessage`, `canTake`, `checkCollision`, `damageExec` and the FLUDD nozzle patches.
6. **Looping particles** — FLUDD spray stays 60 Hz by design; slide/swim/blur were still mid/far expensive.
7. **Bridge/UDP** — already SequenceEqual-coalesced @ 60 Hz; not the primary on-screen FPS cliff vs GX/CPU skinning.

## What the module actually does now

All of the following live in `module/src/remote_actor.cpp` unless noted.

### Visual tier selection — `selectRemoteVisualInterval`

| Tier | Interval | Enter / exit (distance from local Mario) |
|------|----------|------------------------------------------|
| Full 60 Hz | 1 | ≤2600 enter, ≤3400 exit |
| Mid 30 Hz | 2 | between the full and far bands |
| Far 20 Hz | 3 | ≥5200 enter, ≥4600 exit |
| Off-screen 15 Hz | 4 | conservative frustum reject + 6-frame grace |

All four boundaries are hysteretic, and the `static_assert` block below the function pins both directions of every transition.

### Crowd anim budget — `updateRemoteLodRankBudget` + `updateRemoteVisualSchedule`

`updateRemoteLodRankBudget()` runs once per game update, before the per-slot loop in `updateRemoteActors`. It ranks visible remotes by `slot.lodDistanceSq` (sampled by the previous frame's schedule pass — one frame of staleness is imperceptible for LOD and avoids a second position sweep) and publishes two cutoffs:

- `gRemoteCrowdFullRateCutoffSq` — the `kRemoteCrowdFullRateBudget`-th (4th) nearest full-rate-eligible remote. Only published when there are at least `kRemoteCrowdBudgetMinCandidates` (5) candidates, so 2–4 player sessions behave exactly as before.
- `gRemoteShadowRankCutoffSq` — the `kRemoteShadowCasterBudget`-th (4th) nearest shadow candidate.

`updateRemoteVisualSchedule` then demotes any full-rate remote past the cutoff to interval 2. Demoted bodies keep the existing `(gRemoteVisualFrame + networkSlot) % interval` stagger, so they do not all tick on the same frame. `kRemoteCrowdRankHysteresisSq` (1.30 on squared distance) keeps an already-full-rate body in budget when two remotes swap rank by a hair.

Exemptions that still override the budget, in the order they are applied:

- Off-screen cosmetic tail (Yoshi / blooper / hover) — 30 Hz while inside the off-screen grace window. Now explicitly scoped to `!onScreen`, which is what the old `interval > 2` test implied before the far tier existed.
- Spin-jump playback — forced back to 60 Hz whenever draw-visible, both here and in the LOD-skip branch of `TMario_perform_remote`.
- FLUDD ModelWater spray — never touched by the scheduler at all; it has a dedicated 60 Hz tick outside `renderVisible` and outside the interval logic.

### Pose re-root — `updateRemoteRootTransform`

- **Settled-body early-out.** If `calcBaseMtx` produces the same root as `cachedPoseRoot` (per-element tolerance: 1/512 world units on translation, 1/8192 on the 3×3), the whole re-root is skipped: no inverse, no per-joint concat, no envelope rebuild, no accessory rebind. A crowd standing in the plaza converges to this within about a second of the display-motion smoothing settling, so it is the largest single LOD-skip saving. The error cannot accumulate because a skipped frame also leaves `cachedPoseRoot` untouched — the comparison is always against the root the joints were actually built for.
- **Lazy inverse cache.** `cachedPoseRootInv` / `cachedPoseRootInvState` compute `MTXInverse(cachedPoseRoot)` at most once per cached root instead of once per skip frame. It is only ever consulted while `cachedPoseRootValid`, so every existing invalidation site (`resetRemoteRuntimeState`, the two body-swap paths, the null-model and singular-matrix branches) already covers it.
- **Envelopes and soft-skin stay.** `calcWeightEnvelopeMtx()` **and** `mSkinDeform->deform()` still run on every frame where joints actually move, including the singular-`MTXInverse` rebuild path. Dropping the deform on skip frames was evaluated and rejected: `J3DSkinDeform::deform` writes world-space soft-skin vertices into `J3DModel::mVtxBuffer`, so skipping it after moving the joint array would freeze the skinned mesh at the previous root while the joint-bound cap/hands moved — the same class of visible tear as the build 40/41 rubber-hose bug. It is also a no-op whenever `mSkinDeform` is null, which is the common case, so there was nothing to win.

### Draw gating

`mario->calcView(graphics)` on the 0x4 pass now runs only when `drawBody`. Every consumer of those view matrices was already gated: `performRemoteYoshiDraw` early-outs on `!drawBody`, the FLUDD 0x4 perform is inside a `drawBody` check, and the 0x200 block still calls `calcView` itself when a body becomes drawable on a pass without the 0x4 bit. Hide & Seek grace suppression is unaffected — it returns from `TMario_perform_remote` long before this point, and `drawBody` (`isRemoteBodyDrawVisible`) checks it too.

### Shadows

Horizontal **2000** and vertical **10000** are independent (AND cylinder) — not 3D
distance and not an ellipse:

- Cast when `xz <= 2000` **and** `|y| <= 10000` (keep 2400 / 10400).
- Height does **not** shrink the ground footprint; XZ offset does **not** burn the
  height budget.
- Nearest-4 ranks by **XZ only** (`lodShadowHorizDistanceSq`), never by 3D
  `lodDistanceSq` or an anisotropic score.
- Shadow binds under the remote’s standing floor. Draw may temporarily clamp body Y
  into TMBindShadowBody’s comfort band above that floor so elevated casters are not
  faded — it must **not** re-probe plaza/lower ground (that snapped shadows off Mario).

`syncRemoteShadowGround` adaptive probe lift is capped at **10000**. Swimming remotes
still skip dry `checkGround`.

### `isRemoteBody`

Three O(1) gates in front of the table scan, all strict supersets so a false negative — which would drop a puppet into retail `TMario::perform` and crash on missing BetterSMS player data — remains impossible:

1. `mario == gpMarioAddress` → false. The local player is never a puppet, and this is by far the hottest caller.
2. Address window `[gRemoteBodyAddrLo, gRemoteBodyAddrHi)`. `spawnRemoteBody` is the only TMario construction site in the module and always allocates from `gRemoteActorHeap` or a child `JKRExpHeap` carved out of it, so `noteRemoteBodyAllocationRange` records both before each `new`. The window only widens while graphs are alive; `destroyRemoteActorHeap` resets it once they are all gone.
3. An 8-entry direct-mapped positive memo. Mid-stage policy never frees a TMario graph, so an accepted pointer stays accepted; the memo is cleared in `teardownRemoteBodyGraph` and `destroyRemoteActorHeap`. Negatives are deliberately never cached — a body can join a table at any time.

`shouldUpdateRemoteMarioCosmetics` reuses gate 2 before its slot sweep.

### Per-slot loop cost

- `dismissRemoteBody` skips the runtime-state / carried-fruit / voice resets for a slot that is already parked. The model-request generation bookkeeping above that guard still runs unconditionally, so its semantics are unchanged.
- `applySnapshotToBody`'s teleport check compares squared distance instead of calling `sqrtf`.
- `remote_mario.cpp` skips fully-disconnected, already-cleared nametag slots, so a 2-player session does not evaluate nine tags (projection, camera distance, Hide & Seek queries) per frame.
- `nametag_system.cpp` `drawAll` builds one `J2DPrint` per frame and reuses it for every tag and every outline pass instead of constructing one per tag.

FLUDD ModelWater spray remains LOD-exempt at 60 Hz. Full TMario accessories stay.

## Tunables

| Constant | Value | Effect |
|----------|-------|--------|
| `kRemoteCrowdFullRateBudget` | 4 | How many remotes keep 60 Hz `remoteCalcAnim` |
| `kRemoteCrowdBudgetMinCandidates` | 5 | Crowd size at which the budget starts applying |
| `kRemoteCrowdRankHysteresisSq` | 1.30 | Rank-swap slack (squared distance) |
| `kRemoteFarRateEnterDistanceSq` / `Exit` | 4600² / 5200² | 20 Hz far band |
| `kRemoteShadowHorizDistance` / `Keep` | 2000 / 2400 | Shadow XZ radius (AND) |
| `kRemoteShadowVertDistance` / `Keep` | 10000 / 10400 | Shadow |Y| radius (AND) |
| `kRemoteShadowCasterBudget` | 4 | Max simultaneous remote shadow casters |
| `kPoseRootTranslationEpsilon` / `Rotation` | 1/512, 1/8192 | Settled-body re-root threshold |

## Deferred / rejected

- **Skipping `mSkinDeform->deform()` on re-root frames** — rejected, see above.
- Soft LOD that drops FLUDD mesh / particles for distant remotes (noticeable fidelity loss).
- Nametag glyph atlas / cheaper outline (minor vs body draw).
- Skipping the nametag update for `!renderVisible` remotes — the tag state would freeze rather than fade, and the projection test already rejects off-screen anchors.
- Slot-index map for `mario_tex_anim.cpp findBinding` — a ≤12-entry pointer scan per body per frame is not measurable next to the body draw.
- Compact connected-slot index array for `updateRemoteActors` — the loop must still notice newly-valid snapshots on every slot, so the only real saving was the redundant dismiss work, which the guard above already removes.
- Further bridge write coalescing (already SequenceEqual-skips).
- GPU skinning (Dolphin/host change — out of module scope).
- Lowering the bridge snapshot flush rate for far remotes (network pose is already interpolated; module LOD is the win).

## Honesty bar

A packed plaza with **all 9 remotes spraying + Yoshi + blur** can still be GPU/CPU bound on weak hosts: the anim budget caps CPU pose work, but every drawn body still submits its full GX shape set. These changes target the common case — many standing or running remotes on screen without every body paying 60 Hz `calcAnim`, a full re-root, and a ground-probed shadow.

## Verify

1. Host 10 players in Delfino Plaza and gather them in one camera shot.
2. Dolphin OSD: compare FPS / frame time against the previous build with identical settings.
3. Nearest ~4 remotes: animation, FLUDD, Yoshi and spin-jump look full rate. Others visibly run at 30 Hz pose but must not stutter in *position* — translation is still re-rooted at render rate.
4. Walk toward and away from a crowd: the 60/30 Hz boundary must not visibly ping-pong (rank hysteresis).
5. Watch for rubber-hose / exploded-bone stretch on any LOD-skip frame, especially right after a body stops moving (the settled-body early-out) and right after a mid-stage model swap.
6. Shadows: at most 4; XZ <= 2000 AND |Y| <= 10000 (independent). Rank by XZ.
   Overhead remotes must keep a shadow under their feet until |Y| ~10000 — not cut
   at the 2000 ground radius. No plaza snap when elevated.
7. Far remotes past ~5200 units: 20 Hz pose is acceptable, spray must still be continuous while firing.
8. Turn the camera away from a crowd: FPS should rise (`calcView` and `entryModels` skipped for culled remotes).
9. Hide & Seek: seekers must still see nothing at all of hiders during Start Tag grace; nametags and the seeker glasses behave as before.
10. Nametags: no flicker or popping when remotes join, leave, or move between fade bands.

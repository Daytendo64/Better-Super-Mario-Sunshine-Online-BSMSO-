# Flags and Sync

NTSC-U save region base: `0x578940` (RAScript).

Host toggles sync categories via launcher **Sync Flags** (default on). **Authoritative bitsets / sparse maps on the server are the only durable source of truth.** Clients recover from compact `WorldProgressSnapshot` heals — never from unbounded event history. **Graffiti/goop sync is permanently disabled** (was durable `GraffitiCleaned`; flooded history/TCP/mailbox and starved progress — see `12-graffiti-clean-sync.md`).

Architecture alternatives (why authority-first vs lockstep vs full mirror): see `13-sync-architecture-alternatives.md`.

## Phase A — TCP durable-only (ModBuildId 28)

**Goal:** 10-player 120-shine clears without TCP floods. Inspired by SMO Online (server-authoritative collectibles) + delta/snapshot games.

### What rides TCP

| Lane | Traffic | Delivery |
|------|---------|----------|
| **Ownership / progress** | Shine, blue, story, trigger, secret, session reset | Coalesced `WorldProgressSnapshot` push (~**200 ms** idle / **500 ms** under load via adaptive `ProgressPushCoalescer`). `SessionProgressReset` remains a live WorldEvent (host wipe). |
| **Stage mission** | Red coin, NPC cleaned | Authority mutation + same coalesced snapshot (mission bits filtered to stage on apply). **No per-coin live WorldEvent fanout.** |
| **Ephemeral** | Fruit, NpcReact, HipDrop, Yoshi fruit, gold | **Not networked** (module never publishes; server drops; bridge hard-drops; launcher ignores). Chosen for 120% reliability — cosmetic only. |

Live ownership WorldEvents are **not** fanout: remotes apply within the coalesce window from the ownership-push snapshot (FlagManager + HUD). Actor FX still stage-gates as before.

### TCP anti-flood (ModBuildId 36)

**Symptom (2026-07-22 logs):** mid-run flag sync death with `progressSeq` racing to **476** while only ~46 shines collected. Stage-enter / coop-start / best-effort refresh each sent `WorldProgressRequest seq=0`, and the server **force-bumped** global seq on every such request ([Gaffer](https://gafferongames.com/post/snapshot_compression/)-style lesson: don't raise snapshot rate / generation without need).

**Fix:**
1. Force-full (`clientSeq==0`) still delivers a **body** (never Unchanged) but **does not bump** `progressSeq` — same-seq reheal via client `PushProgressSnapshot(moduleApplied=0)`.
2. Cache restage completes heal with **no** best-effort seq=0 TCP refresh.
3. Adaptive ownership-push coalesce: 200 ms idle → 500 ms when ≥4 flushes in 2 s.

### Efficiency

- Ownership push coalesce **adaptive 200→500 ms**; full compact snapshot is OK while coalesced (diff snapshots deferred to Phase B).
- Server high-pri TCP bound 128 DropOldest; low-pri leftover lane **8** DropOldest (ephemeral should never enqueue).
- Progress frames park latest-wins so DropOldest cannot eat force-bumps.
- Module mission abandon after **~4.5 s** (3 stuck bumps); fruit/gold never occupy `localPendingMission`.

### Reliability (unchanged pillars)

- `AuthorityHealGovernor` + continuous ownership push + force watchdog.
- Stage-enter force restages from authority cache; must not hang (watchdog / expand).
- Dual outbound / dual inbound Comm v14 lanes kept.

### Phase B ideas (not implemented)

1. **Diff snapshots** — send only changed shine bits / blue courses / story flags since last peer seq (xor / sparse delta) to cut mid-run payload size.
2. **Interest management** — ownership-push only to peers that need the bits (same stage for mission; plaza for hub triggers); full heal on stage-enter / join.
3. **UDP unreliable cosmetic channel** — if fruit/react ever returns, put them on UDP same-stage only with DropOldest, never TCP.
4. **Per-stage mission coalesce timer** — dedicated red/NPC mask push to same-stage peers only (lighter than lobby-wide full snapshot).

---

## Publish-ack contract (client → server durable path)

**Why it exists:** `WorldProgressSnapshot` heals *from* server authority, so anything the server never received is unrecoverable for the rest of the session. Before this contract the client treated *local enqueue* as *published*: `MaybePublishLocalWorldEvent` cleared the Dolphin `localPending` lane and advanced the last-published sequence before the TCP write happened, the drain's `catch` dropped the event, `SendWorldEventAsync` was fire-and-forget, `SendTcpAsync` silently no-oped on a null stream, and connect/disconnect wiped the outbound queue. Field symptoms: peers stuck on the pre-flood plaza (`0x103AE`), a missing Bowser 120th shine (`0x77`), missing red coins — permanently.

### The contract

An event is **published** only when the socket write completed. Nothing observable advances before that.

| Stage | Owner | Rule |
|-------|-------|------|
| Dolphin lane occupied | module | `localPending{Ownership,Mission}` holds the event; the module keeps it there. |
| Enqueued | `BridgeWorker.MaybePublishLocalWorldEvent` (`BridgeWorker.cs:1994`) | Sets `_publishedUncleared*Sequence`, enqueues an `OutboundWorldEvent`, and **returns without clearing the lane**. |
| Sent | `SendOutboundWorldEventAsync` (`:2373`) → `SessionCoordinator.SendLocalWorldEventAsync` (`SessionCoordinator.cs:1736`) → `NetClient.TrySendWorldEventAsync` (`NetClient.cs:422`) | Returns `bool`. `SendTcpAsync` now throws on a null stream instead of no-oping. |
| Acked | `AckOutboundWorldEvent` (`:2396`) | Records the lane sequence; the **poll thread** performs the actual clear on its next tick (mailbox writes stay single-owner, no `_bufferLock` across a network send). |
| Failed | `HandleOutboundWorldEventFailure` (`:2433`) | Requeues at the **front** (FIFO within a lane) with 5 bounded attempts, 100 ms → 2 s backoff. Backoff is armed off-drain so latest-wins snapshots keep flowing. |
| Exhausted / disconnected | `RetainOutboundWorldEvent` (`:2521`), `RetainOutboundWorldEventsForReconnect` (`:2581`), `FlushRetainedWorldEvents` (`:2615`) | Moved to a bounded (64) retention list keyed by `(type, course, episode, payload0, payload1)`. The lane is released (using the sequence captured **before** retention detaches it) so the module's ownership queue keeps draining. |
| Retained → retried | `MaybeRetryRetainedWorldEvents` (`:2558`), driven from the poll loop (`:1761`) | Retention drains every **5 s** for as long as the session is connected, plus the `SetConnected(true)` reconnect flush. |

Duplicate sends are safe by design: every server accept (`TryAcceptStory` / shine / red coin) is grow-only and idempotent.

**Retention never sits idle.** Draining only on reconnect meant a session that never dropped — a transient stream fault, a brief server hiccup, five fast failures during a stage load — kept the mutation forever while the UI looked healthy, i.e. the exact loss this contract exists to prevent. The poll loop therefore re-attempts retention every 5 s while connected (session-scoped, so it runs even when Dolphin is detached, and outside `_bufferLock`). Guards:

- A retained entry whose key is **already queued** is absorbed rather than re-added, so the same mutation can never be in flight twice; the queued copy returns to retention by itself if it also fails.
- Replayed items are flagged, so a permanently failing event logs once per attempt burst at most — the periodic drain itself logs at most once per 60 s.
- The 64-entry keyed bound and keyed dedupe are unchanged, so a long outage still cannot grow the list.

### Module side — published ≠ confirmed

The module used to set its "already published" caches (`sAuthorityCardBits`, `markAuthorityShine`) on a successful *local enqueue*, so `queueUnpublishedDurableCardSets` skipped those bits forever and never retried. Publication and confirmation are now separate:

- `sPendingConfirmCardBits` (`story_flag_sync.cpp:124`) / `sPendingConfirmShineBits` (`world_sync.cpp:134`) mark *locally published, server not yet echoed*.
- `sAuthorityCardBits` / `sAuthorityShineBits` are set only from a server-sourced apply (incoming WorldEvent or `WorldProgressSnapshot`).
- Stage-enter re-queues still-unconfirmed bits, bounded by `kMaxCardConfirmRetryPasses` / `kMaxShineConfirmRetryPasses` (3 passes) so a server that legitimately never confirms cannot cause an endless republish loop.

Deferring the cache was chosen over "reset published state on a bridge-reported failure": the failure signal only covers the client's own socket, while the deferred cache also covers a send that succeeded locally but was dropped server-side, and it needs no new bridge→module channel.

### Queue coalescing (no silent drops)

- Module ownership queue (16 deep): `coalesceOwnershipQueueDuplicates` (`world_sync.cpp:1544`) collapses duplicate keys before any drop, and `ownershipKeyQueued` (`:1530`) refuses to enqueue a key already waiting. The ownership lane is still **never** abandoned (unlike the mission lane).
- Red-coin deferred queue: `storeDeferredRedCoinEvent` (`red_coin_sync.cpp:166`) coalesces by `(course, episode, index)` instead of spilling the oldest distinct coin.

### Related resilience

- **Poll-loop watchdog** (`BridgeWorker.cs:306`): a fault outside the per-tick guard used to stop every Dolphin read/write silently while the UI showed a healthy session. The supervisor restarts the loop up to 8 times (250 ms → 5 s backoff), then logs loudly.
- **UDP health** (`NetClient.cs:70`): 4 s warm-up grace, warn at 6 s of inbound silence, disconnect (`Timeout`) at 20 s or 300 consecutive send failures. Transient loss never triggers it; `UdpReadLoop` now survives ICMP port-unreachable instead of exiting.
- **Bounded TCP resync** (`NetClient.cs:88`): byte-at-a-time resync on bad magic/version is capped at 4096 skipped bytes, then the session is declared unrecoverable.

---

## Phase 2 continuous ownership push (ModBuildId 24)

Build **28** (2026-07-21 TCP durable-only): live ownership/mission WorldEvent fanout removed; fruit/NpcReact/HipDrop/gold never networked; ownership-push coalesce 125 ms; ephemeral TCP/mailbox DropOldest 8.

Build **26** (2026-07-21 evening soft-death): gold/yellow coin network sync **disabled** (module no longer publishes; server drops; launcher ignores) — 714 `GoldCoinCollected` lines preceded stage-enter hang. Cache restage uses **real `ProgressSeq`** as mailbox hostSeq (legacy `0x60000000` band poisoned `periodic-catchup` advertise as `seq=1610612814`). Cache-path force **re-arms await + watchdog** so silent TCP cannot end at `stage-enter … seq=0 force` with zero follow-up. Remote publish hold has a **3s max** so `clearPuppets`/remount flicker cannot suppress remotes forever. NetClient detaches world-event/progress handlers off the TCP read loop; server parks latest progress frames so `DropOldest` cannot eat force-bumps.

Build **25** follow-ups (no module change): never Clear on stale cache TTL; heal latch clears on `applied >= hostSeq` or `==0`; force-await clears only after successful mailbox/expand (not on cache Note).

Authority-first rewrite (build 18–23) still soft-died mid-run because catch-up was **request/response** (stage-enter / periodic) and session work ran **inline on the bridge poll thread**.

### 2026-07-21 evening soft-death (build 26 closes)

**Smoking guns (`smso-2026-07-21.log` Instance 1 + `dolphin.log`, ~19:28–19:31):**
- Massive `GoldCoinCollected` spam course=5/5 (payload1 13→100) then shine 33
- `19:30:49` `cacheHeal=78 restaged authority (stage-enter 13/3) hostSeq=1610612814` + `requesting progress resync (stage-enter 13/3) seq=0 force`
- **No** `force-bump` / `force-timeout` / `tcpForceRetry` / `circuitOpen`; next lines are `periodic-catchup seq=1610612814` (synthetic) with **no server reply**; fruit/blue `sending` without `slot` echo
- Dolphin: `remountFixed` + `Remote actors cleared` / `Stage exit` around the same transitions; `localPendingMission` type=7 (gold) stuck bumps earlier in the run
- `force-timeout` count in log: **0** (cache restage left governor Idle — watchdog never armed)

**Root class:** (1) gold TCP/mission volume under multiplayer; (2) synthetic cache hostSeq advertised as proof seq; (3) stage-enter TCP refresh with no watchdog after cache restage; (4) remote hold + clearPuppets without a hard republish ceiling.

### 2026-07-21 afternoon soft-death (build 24 closed)

**Smoking guns (`smso-2026-07-21.log` Instance 1 + `dolphin.log`):**
- `17:33:24` last `ShineCollected` send/apply + `progress snapshot → mailbox seq=221`
- `17:33:28` `cacheHeal=62 restaged authority (stage-enter 1/8)` then `requesting progress resync (stage-enter 1/8) seq=0 force`
- **No** subsequent `progress snapshot → mailbox` / `force-bump` / `force-timeout` / `tcpForceRetry` / `circuitOpen` until Dolphin stop at `17:34:36`
- Dolphin `33:28:873` bulk-applied the cacheHeal body; `33:54:550` `blue publish course=1/8 idx=5` with **no** launcher `sending type=BlueCoinCollected`
- `33:56`–`34:34` `localPendingMission` / `localPendingOwnership stuck — bumped` (type=7 gold, type=2 blue) then `localPendingAbandon lane=mission`

**Root class:** after stage-enter force, the bridge poll path stopped draining `localPending` (session callback hung inline). Ownership heals depended on that same path for force-timeout + periodic catch-up. Live events and stage-enter TCP were not a continuous primary heal.

### Target model (build 24–28)

1. **Server primary path:** every authority mutation (`NoteProgressChanged`) schedules a coalesced lobby-wide `WorldProgressSnapshot` push (~125 ms via `ProgressPushCoalescer`). Live ownership/mission WorldEvents are **not** fanout (build 28).
2. **Cache restage uses real ProgressSeq** and **arms force-await watchdog** for TCP refresh (build 26). Mailbox is already filled — never clear-then-wait.
3. **Stage-enter force:** restage from cache (no clear); on mailbox write miss, expand-from-cache in the same call. Watchdog must fire `force-timeout` / retry / circuitOpen.
4. **Bridge + NetClient isolation:** session callbacks off poll/read threads so hung TCP cannot wedge `localPending` or stall progress replies.
5. **Gold / fruit / NpcReact / HipDrop:** not networked (build 26 gold; build 28 all ephemeral).
6. **Remote republish:** Active grace + **3s max hold** after `clearPuppets`.

Telemetry: `ownership-push →`, `cacheHeal=`, `force-timeout`, `force-heal watchdog`, `tcpForceRetry=`, `circuitOpen=`, `localPendingAbandon=`, `snapshot-only`.

---

## Phase 1 authority-first rewrite (ModBuildId 18)

### Target model

1. **Server authorities** (shine / blue / red / NPC / story) are the only durable heal source.
2. **Live deltas** remain best-effort; every peer must recover from a compact snapshot.
3. **Ownership isolated** from mission/ephemeral (`incomingOwnership` + TCP high-priority — Comm v13).
4. **No soft-death single queues:** if a heal lane wedges, abandon + restage from authority within seconds.
5. **Aggressive prune:** diagnostic history capped at 16 ownership events; mission tracker masks pruned to the current stage; red/NPC never inflate durable history.

### `AuthorityHealGovernor` (launcher)

Force-full **must not** `ClearProgressSnapshot` then wait forever on TCP when an authority cache exists. `PushProgressSnapshot` already stages `moduleApplied=0`, so same-seq reheal works without clearing.

| Event | Behavior |
|-------|----------|
| First force (no cache) | Clear mailbox + TCP + watchdog timer (only safe path) |
| Force with cache (any age) | Restage mailbox from cache with **real ProgressSeq** + TCP refresh — **AwaitingForce armed for watchdog** (build 26; never synthetic 0x60000000 hostSeq) |
| Force timeout + cache | Restage again, clear await — **no retry storm** |
| Force timeout, no cache | TCP retry up to 5, then circuit-open 8s |
| Unchanged while awaiting | Ignored (never clears await); refreshes cache stamp when not awaiting (build 25) |
| Changed snapshot received | Cache body immediately; **force-await clears only after successful mailbox write / expand** (build 25) |
| Authority mutation | Server coalesced `ownership-push` snapshot to all peers (build 24) |

### Log evidence (soft-death this rewrite closes)

**Note:** `smso-2026-07-21*.log` / `dolphin.log` were empty at investigation time (`%AppData%\SMSO\logs` cleared). Evidence below is from the prior mid-run failure captured in `Downloads\smso-2026-07-20.log` (build 14) plus the documented host Dolphin OSReport pattern.

**Smoking guns (`smso-2026-07-20.log`, Instance 1):**
- `21:26:01` last successful `progress snapshot → mailbox seq=155`
- `21:26:27` `stage-enter 1/8` force-full → mailbox cleared, TCP requested
- `21:26:29`–`21:30:00` **104×** `force-progress-retry` with **zero** subsequent mailbox writes
- `21:27:39` `sending type=ShineCollected` still works outbound while heal is dead
- `21:29:06` / `21:29:26` / `21:29:46` `periodic-catchup seq=155` cannot unstick the empty lane
- Red coins earlier queued with episode mismatch (`local 13/1, event 13/2`) — mission backlog + force-clear compounded the wedge

**Root class:** clear-mailbox-then-await-TCP with no local authority restage. Phase 1 makes that class impossible when any snapshot has been received.

### 2026-07-20 mid-run heal / outbound soft-death follow-ups

Additional fixes on top of the shine-36 / force-full root causes:

1. **Force-full await** — `_awaitingForceProgressReply` clears only after successful mailbox write or expand fallback (serialize failure keeps the 2s retry armed). **Superseded in build 18 by `AuthorityHealGovernor`.**
2. **Deferred episode queue** — progress heals no longer `Clear()` pending gold/fruit/triggers or cancel an in-flight stage-enter drain; they flush matching entries after a successful apply.
3. **`NoteLiveEvent`** — only after a real bridge enqueue (queued-off-stage mission events stay eligible for heal expand).
4. **Outbound mark-after-enqueue** — shine / blue / gold / story / red / NPC trackers advance only after `publishLocalWorldEvent` / enqueue succeeds so a full queue can retry.
5. **Incoming drain** — bridge re-queues at the front when `TryWriteIncomingWorldEventOnly` fails (no silent dequeue loss).
6. **Heal expand red/NPC** — `filterOwnership:false` no longer suppresses noted-but-unapplied mission bits.

## Live-first ownership pattern (reusable)

Durable **ownership** flags use a two-layer apply. This is the future-proof model for shine, blue coin, and story/secret bits:

1. **Immediate live apply** — write the `TFlagManager` bit and refresh HUD (`countShine` / `countBlueCoin`) as soon as the event reaches the module, **regardless of current course/episode**. Never defer the flag write itself to plaza load or stage match.
2. **Visual reconcile** — actor hide / particles / get-star anim only when the relevant actors exist on the current stage; otherwise log `defer-visual` and remember ids/masks for hide-on-settle. Visuals may wait; **flags never wait**.

| Layer | Shine | Blue coin | Story / secret |
|-------|-------|-----------|----------------|
| Ownership (always) | `setShineFlag` + HUD | `setBlueCoinFlag(course, idx)` + HUD | `setBool` on card/game banks |
| Visual (stage-gated) | remote get-star / hide actor | hide + FX on **matching course** | MapEvent `watch()` / geometry wake when actors exist |

Episode-scoped **mission** events (red coin, NPC cleaned, gold coin, hip-drop, fruit) still defer at the launcher when off-stage. Their module apply must also **free the durable mailbox** after recording ownership/mask state — never hold the single `incoming` slot waiting for actors/settle. Holding that slot previously blocked shine/blue until the next plaza `WorldProgressRequest`.

**Episode equivalence (2026-07-20):** Compact heal mission filter (`WithMissionFilteredToStage`), live apply / pending flush matchers, and server `StagesEquivalent` all share `LevelCatalog.EpisodesEquivalent` — plaza hub, casino catalog↔mission, and `NormalizeEpisodeFromGame` for hotel / Ricco / Pinna. Catalog-normalized authority keys therefore keep matching director mission ids in the local snapshot (no silent strip / stuck queue). **Module `sameStage` now mirrors this** via `episode_equiv.hpp` (used by `world_sync`, `red_coin_sync`, `monte_clean_sync`) so catalog-keyed heals/replays apply while the director still shows mission ids.

Bridge maintains **separate ownership / mission / ephemeral queues** and a **dedicated `incomingOwnership` mailbox lane** (CommBuffer v13) so shine/blue/story never share the mission/ephemeral slot. Compact `WorldProgressSnapshot` heals write a **latest-wins progress mailbox** that the module bulk-applies in one tick. Ephemeral pending is cleared on heal; ownership / mission already queued in the bridge is preserved. **Phase A (build 28): ephemeral WorldEvents are hard-dropped and never networked** — fruit/NpcReact/HipDrop/gold cannot wedge. Heals must **not** wipe the launcher `_pendingEpisodeWorldEvents` queue for deferred mission/triggers. Server TCP fanout uses a **high-priority channel** for ownership/progress/control and a **bounded DropOldest 8** low-priority leftover lane (should stay empty).

## Recovery (full-run durability)

Event-only delivery is lossy under 9-player load (single mailbox slot + ephemeral flood). Recovery paths:

1. **Compact `WorldProgressSnapshot` heal** — server serializes authority bitsets / sparse flag sets into `TcpPacketId.WorldProgressSnapshot` (**FormatVersion 2**: 256-bit / 32-byte shine ownership; v1 128-bit is a hard cut gated by `ModBuildId`). Clients write a filtered payload (ownership + current-stage mission) into `ProgressSnapshotMailbox`; the module bulk-applies FlagManager + HUD. Used for join, stage-enter / `WorldProgressRequest`, co-op catch-up, sync-reenable, **and a cheap ~20s client catch-up** (`periodic-catchup` with last `progressSeq` — unchanged ack when authority is current). Lobby-wide periodic ~45s full *broadcast* remains **disabled** (that flooded every peer). Force-full requests (`clientSeq==0`) **bump server `progressSeq`** and are **never** server-debounced to silence — the client prefers authority-cache restage and only clears its progress mailbox when no cache exists.
2. **Idempotent re-apply** — module re-applies durable collectibles even when `eventId <= lastAppliedEventId`, and retries (keeps incoming slot) only if FlagManager itself is missing. Live ownership uses `incomingOwnership`; mission/ephemeral use `incoming`. Heal expand (fallback only) uses synthetic ids in `0x70000000+` and **does not filter ownership** against optimistic live notes — a noted-but-unapplied shine must still heal. Heal expand does **not** advance live `_nextEventId`.
3. **Outbound `localPending` handshake** — bridge publishes once per sequence and only advances last-seq after a successful Dolphin clear. Failed clear retries without skipping (the old advance-before-clear path permanently stalled the slot after `markShineSet`). Module bumps stuck `localPending` sequence after ~1.5s so a skipped duplicate-seq is re-observed. Full-buffer bridge writes preserve live `localPending` / both incoming lanes / progress ack lanes so they cannot stomp an in-flight ownership event.
4. **Red-coin switch arming** — local-only (`mRedCoinSwitchPressed` / Type5 `0x50009` excluded from durable TriggerFlag sync). Collection uses an index+mask model; remotes never press the switch for you — see `07-red-coin-episodes.md`.
5. **Monotonic flag merge** — clients baseline-publish existing durable set bits at one event per frame. The server accepts only `0→1`; local `resetStage`/`resetGame` clears never enter authority. Remote writes update local trackers, so they do not echo through normal polling.
6. **Same-episode reload recovery** — the stage-init callback runs even when course/episode IDs do not change. It preserves and reapplies authoritative Type5 bits after vanilla reset, then resets polling baselines.
7. **Authority cache restage (build 18)** — launcher keeps the last changed `WorldProgressSnapshot` and restages it on force timeout / stage-enter without depending on a TCP round-trip.

### 2026-07-20 RedCoin `localPending` soft-death (type=9)

**Smoking guns (this run):**
- Host Dolphin: `26:15` red-coin collect → `26:18` `localPending stuck — bumped seq=87 type=9` then **208** consecutive type=9 bumps; host shine publish `id=15`/`id=16` after the wedge never reached launcher `sending`.
- Client: `21:26:25` / `21:27:39` `sending type=ShineCollected` while host applied nothing after `19:26:04` `eventId=234`; `force-progress-retry` looped **104** times with no mailbox reply.
- Root hole: full-buffer writes preserved stale `_workingBuffer.LocalPending` when live was **empty** after clear — resurrecting RedCoin and wedging the single outbound slot so ownership could never flush. Bumping seq alone could not recover while the stomp kept reoccupying the lane.

**Fixes:** (1) always adopt live `LocalPending` including empty on full write + splice live lane in `TryWriteBuffer`, (2) module splits card ownership vs mission, soft-caps mission queue, promotes ownership on flush, preempts/abandons wedged mission `localPending`, (3) bridge incoming reorders ownership → mission → ephemeral and caps mission pending.

### 2026-07-20 volume soft-death follow-up (ModBuildId 17 / Comm v13)

**Still broken after 16:** (1) single shared `incoming` mailbox — a wedged red/gold/fruit occupant still blocked live shine/blue even with bridge reorder; (2) unbounded server TCP send channel — ownership frames sat behind thousands of ephemeral fruit/NPC packets; (3) unbounded `_pendingEpisodeWorldEvents` + 5s/event drain pacing; (4) durable history still retained red/NPC (authorities already heal them).

**Fixes:** dual `incomingOwnership` + `incoming` mailbox; bridge separate never-drop ownership / capped mission / capped ephemeral queues; server high-priority TCP (**bounded 128 DropOldest** in build 18) + bounded DropOldest low-priority; ownership-only diagnostic history (48→**16** in build 18); pending-episode prune/coalesce; tracker mission masks replaced from heals + **pruned to current stage**; drain pace 120ms.

## Comm buffer v14 (dual outbound + dual inbound + `ProgressSnapshotMailbox`)

| Field | Size | Direction |
|-------|------|-----------|
| `worldSync.localPendingOwnership` | 19 | module → bridge (shine/blue/story/secret/trigger/episode/reset) |
| `worldSync.localPendingMission` | 19 | module → bridge (red/NPC/gold/fruit/hip-drop/NPC react) |
| `worldSync.incomingOwnership` | 19 | bridge → module (shine/blue/story/secret/trigger/reset) |
| `worldSync.incoming` | 19 | bridge → module (mission + ephemeral) |
| `worldSync.lastAppliedEventId` | 4 | module |
| `progressSnapshot.hostSeq` | 4 | bridge (latest-wins heal) |
| `progressSnapshot.moduleAppliedSeq` | 4 | module ack |
| `progressSnapshot.payloadLen` + payload | ≤4096+2 | LE `WorldProgressSnapshot` body |

**ModBuildId 20 / Comm v14:** dual outbound localPending (mirrors v13 inbound split). Phase 1 `AuthorityHealGovernor` retained. Telemetry grep tags: `cacheHeal=`, `tcpForceRetry=`, `circuitOpen=`, `localPendingAbandon=`. Diagnostic durable history ring reduced to **4** ownership events. Pending-episode: hard-drop fruit; coalesce red/NPC; ownership never deferred.

## Event types

| Type | Hook | Payload |
|------|------|---------|
| ShineCollected | **live** `setShineFlag` + HUD; get-star anim when actor exists | payload0 = global shine id; reserved = collector slot; payload1 = packed world position. All shine types: episode, secret, blue-coin reward, 100-coin, etc. Position captured from `mCollectedShine` during collection and stage snapshot at settle. **Bowser epilogue** shine `0x77` (119) is latched in movie context — Build 53 force-publishes it (session authority cache + movie/stage-exit emit) so stageInit cannot swallow it. |
| BlueCoinCollected | **live** `setBlueCoinFlag` + HUD; actor hide/FX on matching course | courseId + payload0 = `TMapObjBase::mMapObjID` (vanilla blue-coin flag index 0..49). Hide matches **only** that ID — never `TCoin::_154` (red-coin HUD slot / unused on TCoinBlue; often 0 and formerly false-matched every graffiti spawn after index-0 collect). |
| GoldCoinCollected | **NOT NETWORKED** (build 26+) | Local only; never publish / fanout |
| RedCoinCollected | authority + coalesced snapshot (build 28) | reserved=stableIndex; payload0=(count<<4)\|hudSlot; payload1=authoritative 8-bit collected mask. Hide by bound actor pointer only — see `07-red-coin-episodes.md` |
| NpcCleaned | authority + coalesced snapshot (build 28) | reserved=stableIndex; payload0=(count<<4)|index; payload1=packed position. Mask recorded immediately; actor unsink reconciled when NPCs exist — mailbox never waits on settle. |
| GraffitiCleaned | **DISABLED** — not published / not durable | Legacy enum 18; server ignores; module no-ops. See `12-graffiti-clean-sync.md`. |
| Fruit / NpcReact / HipDrop | **NOT NETWORKED** (build 28 Phase A) | Cosmetic only; module never enqueues `localPendingMission`; server/bridge hard-drop. |
| Yoshi (riding) | UDP `PlayerSnapshot` | `nozzleId` low=Yoshi nozzle + high=color; `movementState` high 5 bits=juice; requires `VFX_NO_FLUDD` + Yoshi nozzle — see `08-yoshi-sync.md` |
| StoryFlag / TriggerFlag / SecretComplete | polling `TFlagManager` durable bools | payload1 = flag id; payload0 is always 1. Persistent card story bits exclude shine/blue ownership. Type5 is resetStage scratch; only verified durable MapEvent latches `0x50001`, `0x50002`, `0x50004` are admitted (plaza-only). Server `StoryFlagAuthority` coalesces those plaza Type5 bits to hub episode **`0xFF` (255)** so every dolpic scenario shares one key. Flag bits apply live (launcher does **not** episode-defer plaza hub TriggerFlags — same as StoryFlag/SecretComplete); module admits overlay off-plaza and writes FlagManager on plaza. **No plaza soft-reload**. |

Module applies idempotent flag writes when sync toggles enabled. Remote shine/blue/red coin apply also hides the world actor when present (same safe pattern as red coins: `makeObjDead` + vtable→`TCoinEmpty`; never calls `taken()`). Remote red/blue coin apply replays pickup particles for all clients **on the same course** (blue) / **same stage** (red) via coalesced ownership-push snapshot apply — **not** per-collect TCP fanout. Pickup SFX (`MSD_SE_SY_RED_COIN_GET` / `MSD_SE_SY_BLUE_COIN_GET`) only when the local camera is within JAI `distanceMax` (`checkSoundArea` + `getDistPowFromCamera`) so distant players do not hear a global jingle. Blue-coin FX requires `event.courseId == local area`, resolves actor position **before** hide (post-hide lookup always failed → `blue defer-fx`), and gates on first-time apply that was not locally tracked (snapshot `reserved=0` is ambiguous with host slot 0). Red-coin FX requires a live actor was hidden (`hidLiveCoin`) and must not OR the stage-wide `payload1` mask mid per-index expand (that skipped sibling FX). Local collectors still get audio from vanilla `taken()`.

**Episode-scoped** events (gold / red / NPC cleaned / hip-drop / fruit / NPC react / non-hub Type5) received while on another stage are deferred in the launcher until the matching course/episode loads. **Shine / blue / story / secret / plaza hub TriggerFlag (episode 255)** are not episode-scoped — ownership applies immediately on any stage. Hooks skip re-broadcast while applying remote events. Blue HUD refreshes via `countBlueCoin` after ownership apply. Shine HUD: on each newly set shine flag, re-arm `startAppearStar` and spin `countShine` until the console displayed total (`+0x64`) catches FlagManager `0x40000` (one-shot appear + single `countShine` only fixed the first shine per stage).

### Coin collect FX (ModBuildId 62)

**Symptom:** Remotes saw blue/red coins disappear (ownership) but often heard/saw no pickup FX; dolphin.log showed `blue defer-fx … (actor not found)` after every remote blue.

**Root cause:** (1) `applyBlueCoinVisualReconcile` hid the actor then looked it up with `isCollectibleBlueCoin` (live+untaken) — always miss. (2) FX also required `reserved != localSlot`, but ownership-push snapshots leave `reserved=0`, so host slot 0 never played FX. (3) Red per-index snapshot expand OR'd the full stage mask on the first index, marking siblings `already` and skipping their FX.

**Fix:** resolve blue pos before hide; FX on `!alreadySet && !locallyTracked`; stop OR-ing red `payload1` mask during apply. Still snapshot-only (no TCP per-collect spam).

OSReport diagnostics (shine/blue): `shine publish` / `shine apply-flag` / `shine hud-refresh` / `shine defer-visual`; `blue publish` / `blue apply-flag` / `blue defer-visual` / `blue defer-fx`.

### Shine HUD cross-stage / multi-shine (2026-07-18)

**Symptom (1):** Remote shine collect sets the flag (`shine apply-flag … changed=1`) but the on-screen shine counter stays stale until returning to Delfino Plaza.

**Symptom (2):** First synced shine bumps the HUD; a **second** shine in the same stage logs `shine hud-refresh count=2` but the digits stay at **1** until plaza refresh.

**Root cause:** `TGCConsole2::startAppearStar` is one-shot (`console+0x34`). While armed, `perform` drives `countShine` across ~250 frames; `countShine` commits the shown total to `console+0x64` and advances timer `+0x8A`. Calling `startAppearStar` + one `countShine` per apply arms the card for the first shine (later frames finish digits) but the second shine early-returns from `startAppearStar` (flag already set / pane settled), so only an orphan `countShine` runs and digits stall.

**Fix:** On each newly set shine flag: clear the appear one-shot + appear frame, call `startAppearStar`, force displayed cache behind the FlagManager total (`0x40000`), zero the count timer, then **spin `countShine` until `displayed >= count`** (cap 320) so every shine bumps digits immediately on any stage. Log: `shine hud-refresh count=%d displayed=%d timer=%u armed=%u`.

### Shine HUD snapshot snap race (2026-07-22)

**Symptom:** Collecting a shine sometimes leaves the star-card / counter half-loaded or flicker-interrupted.

**Root cause (ModBuildId 28+ TCP durable-only):** coalesced `WorldProgressSnapshot` heals (~125 ms) always ended with `snapHudCountersToFlagManager`, which disarms `startAppearStar` and forces a pane refresh — even when `shineChanged==0` (local bit already set) or right after `refreshShineHudLive`. That cuts off retail `perform` mid-appear.

**Fix:** Snapshot bulk-apply uses `HudSnapMode::PreserveShineAppear`: skip the shine hard-snap while appear is armed or digits already match FlagManager; only live-refresh when digits are stuck behind with no appear; still snap blue. Session reset keeps `ForceBoth`.

### Shine / coin HUD diagonal stagger (2026-07-25)

**Symptom (3):** After a co-op shine collect (local or remote), the top-left shine / blue / gold HUD rows sometimes stagger diagonally and individual digits within a number sit at different Y positions.

**Root cause:** Same mid-`TGCConsole2::perform` overlay class as ModBuildId 44/48. `gx_hud_fence` restored GX scissor/viewport/projection but wrote logical widescreen rects into `J2DOrthoGraph::mBounds` without restoring `mOrtho`. Later retail pane `setPort` / `scissorBounds` then used mismatched bounds↔ortho power — worst during `startAppearStar` when digit `TBlendPane`s (`s_n*` / `c_n*` / `b_n*`) redraw. Not a `refreshShineHudLive` / `snapHudCountersToFlagManager` digit-cache rewrite (PreserveShineAppear already protects appear).

**Fix (ModBuildId 54):** Fence snapshots and restores full graf fields, applies HUD-safe GX without calling `setPort` on logical bounds, and leaves `mBounds`/`mOrtho` paired for the rest of `perform`.

Visual reconcile (`defer-visual` / get-star anim) stays stage-local.

## Delfino Plaza (dolpic) story state

Plaza visuals are driven by:

1. **Which `dolpic` scenario loads** (shine count + story progress — see `DelfinoPlazaMapping`).
2. **Card bools** such as `0x10384` (Bianco king gate), nozzle rights `0x10366+`, Pinna unlock `0x10389` (`dolpic8`), and Corona visited `0x103AE` (`dolpic10` post-flood).
3. **Stage bools** such as `0x50001` (Ricco tanuki house), `0x50002` (lighthouse), `0x50004` (MareGate / boat).

   **Durable Type5 allowlist:** `0x50001`, `0x50002`, `0x50004`, plaza-only, coalesced to episode **255 (`PlazaHubEpisode`)**. All other Type5 bits are resetStage scratch (including graffiti/timers/session switches); `0x50009` (`mRedCoinSwitchPressed`) remains local-only. Launcher applies hub TriggerFlags immediately (any stage); flushing queued episode events on plaza also drains episode-255 hub triggers.

   **Excluded from durable story/game sync:** `0x30001` / `0x30004` — one-shot spawn directors consumed by `decideMarioPosIdx`. Pinna unlock FMV sets `0x30004` so the post-cutscene plaza entry uses the cannon spawn; vanilla clears it after use. Durable sync used to re-apply `0x30004` on every plaza enter (authority snapshot after stageInit), so returns from Ricco/Bianco/etc. always spawned at the Pinna cannon. Durable Pinna unlock progress is card bool `0x10389` (`decideNextScenario` → `dolpic8`), which still syncs.

Shine sync alone is not enough: remotes can share shine counts while gates/boats/flood props stay stale. BSMSO polls persistent card progression plus the verified stage-trigger allowlist, emits set-only `StoryFlag` / `TriggerFlag`, and includes them in sparse authority snapshots. Runtime Type3 is never durable: decomp confirms `resetGame` clears the whole bank, while stable outcomes live in card flags. Remote geometry flags are written into `TFlagManager` immediately.

### Snapshot semantics

Story snapshots are authoritative **sparse set snapshots**, not full bitmaps. Presence means set; absence does not command a clear. This is intentional because clearing an absent bit could erase local save progress or let a vanilla stage reset roll back the session. Connected clients merge their initial durable card/stage set into authority gradually (one event per frame), so differing local saves converge to their union. Server snapshots then deterministically heal missed packets, reconnects, and late joins. A new session clears the module's cached authority overlay after the disconnected mailbox state is observed.

**Host Reset Progress** (launcher Server Actions, formerly "Reset Flags") is the intentional clear path for a mid-session **new-file** wipe: server empties Shine / Blue / Red / NpcClean / Graffiti / Story authorities, clears all durable world-event history, and broadcasts non-durable `SessionProgressReset` (type 19, legacy alias `ShineBlueProgressReset`). Modules clear the full card bool bank (`0x10000`–`0x103B3`: shines, blues, nozzles, story, secrets), card ints (lives/records/water — restored by `correctFlag`), game ints `0x40000`/`0x40001`/`0x40002`, **BSE Type6 extra-shine ownership bits for ids 120..255** (`setFlag(0x60040+(id-0x78), 0)` — card-bank wipe alone leaves `BETTER_SMS_EXTRA_SHINES` set), plaza Type5 allowlist `0x50001`/`0x50002`/`0x50004`, and spawn directors `0x30001`/`0x30004`, then call `correctFlag()` (re-applies always-set `0x1039A`/`0x1039D`, min lives, FLUDD water). Type3 cutscene-watched bits are **kept** to avoid re-firing FMVs mid-session — do **not** call `firstStart()`/`resetCard()`. HUD snaps via `snapHudCountersToFlagManager` (shine `+0x64` + pane timer 252; blue `+0x168` seeded at count−1/−1 then one `countBlueCoin`) — never `refreshShineHudLive` on a decrease (`countBlueCoin` increments whenever caches disagree, so a stale blue cache walks **up** after reset). Collected world actors / graffiti visuals respawn only after stage re-enter. A 3s server grace rejects durable re-publishes while peers apply the clear. Late joiners with progressed local saves can still re-merge grow-only flags unless they also receive a clear while connected.

### Live mid-visit (no stage reload)

MapEvents / scripts that actively `watch()` FlagManager bits update as soon as the bit is applied:

| Example | Flag | Behavior |
|---------|------|----------|
| Shine count / ownership | shine id 0..255 | HUD + `getShineFlag` update on any stage |
| Blue coin ownership | course + index 0..49 | HUD + flag update on any stage; actor hide only on matching course |
| Bianco king gate | `0x10384` | Opens/updates when the card bool is set remotely |
| Ricco tanuki house | `0x50001` | Watched MapEvent reacts live |
| Lighthouse / Gelato | `0x50002` | Watched MapEvent reacts live |
| Other watched story/trigger bits | card/stage/game banks | Same — flag write is enough |

### Needs natural leave / re-enter

Vanilla binds some plaza layout to the **loaded `dolpicN` archive** and to `loadAfter` (not to a continuous watch). BSMSO does **not** soft-reload the hub to force these — players stay in place; the next natural exit/re-entry (pipe, warp, episode change) loads the correct archive via `decideNextScenario`:

| Visual / prop | Why stale until re-enter |
|---------------|--------------------------|
| Hub archive switch (e.g. Pinna unlock `dolpic6` → `dolpic7`) | Scenario archive encodes MapObj sets, NPC placements, Pinna cannon layout |
| MareGate / boat (`0x50004`) | `TMareGate::loadAfter` only; MapEvent watch cannot spawn it mid-visit |
| Flood / post-flood layout | Bound to `dolpic9` / `dolpic10` archives — never force-reload (old soft-reload looped the flood cutscene). **Flooded unlock** = all seven Shadow Mario episode shines (`6/16/26/36/46/56/66`) via ShineAuthority. **Post-flood unlock** = card bool `0x103AE` (latched on Corona Mountain enter / flooded→Corona FMV). Build 52: stageInit tracker reseed no longer swallows `0x103AE` — module force-publishes unpublished durable card bits after stage enter and prefers immediate `0x103AE` emit so one peer's Corona visit unlocks final plaza for everyone on their next natural plaza re-enter. |
| Bowser / epilogue shine (`0x77` / 119) | Vanilla `TMovieDirector::decideNextMode` latches `setShineFlag(0x77)` when **epilogue.thp** (movie 14) ends — while stage callbacks are idle. Build 53: session shine authority cache + movie-loop / stage-exit force-publish so stageInit reseed cannot swallow the 0→1 edge; peers heal via ShineAuthority ownership push (same class as `0x103AE`). |
| Shine / blue **actors** on another course | Ownership + HUD already live; world actor hide waits until that course is loaded |

Shine count, blue ownership, and story bits sync live mid-visit; only archive-bound MapObj sets stay on the scenario that was entered.

## Phase 3 (landed with ModBuildId 20)

1. Dual outbound `localPendingOwnership` / `localPendingMission` — eliminates module-side preempt.
2. Per-stage mission authority push on enter only (no live red/NPC event dependence for catch-up).
3. Diagnostic durable history ring reduced to 4 ownership events — authorities are SoT.
4. Telemetry: `cacheHeal` / `tcpForceRetry` / `circuitOpen` / `localPendingAbandon` in launcher logs.

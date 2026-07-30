namespace SMSO.Net;

public static class ProtocolConstants
{
    public const uint Magic = 0x534D534F;
    // v2 adds coalesced UDP SnapshotBatch server fanout.
    public const ushort ProtocolVersion = 2;
    public const ushort CommVersion = 15;
    /// <summary>
    /// Release build gate for multiplayer. Bump on every zip build so mismatched
    /// clients are rejected with <see cref="JoinRejectReason.VersionMismatch"/>.
    /// Independent of <see cref="CommVersion"/> / <see cref="ProtocolVersion"/>.
    /// </summary>
    /// <remarks>
    /// Build 60: launcher music volume slider â†’ CommBuffer â†’ MSBgm track slot 3
    /// (BGM-only; SFX untouched) + BSE AudioStreamer max/current volume.
    /// Build 58: remote shadow Y-clamp fix; undersea warp gated to episode 0 only.
    /// Build 57: stability + crowd performance sweep.
    /// Durable publish is now ack-gated: the Dolphin ownership/mission lane is held
    /// until the TCP write completes, failures requeue at the front (5 tries, 100ms
    /// to 2s), a disconnect retains up to 64 keyed events for replay on reconnect,
    /// and the module keeps published-but-unechoed bits in sPendingConfirm*Bits so
    /// only a server-sourced apply sets the authority cache â€” a dropped send no
    /// longer leaves peers permanently stuck on the pre-flood plaza or missing the
    /// Bowser 120th shine. Ownership and deferred red-coin queues coalesce duplicate
    /// keys instead of dropping. The bridge poll loop self-restarts (8 tries) instead
    /// of dying silently with the UI still showing a healthy session. UDP silence and
    /// TCP resync corruption now degrade the session instead of freezing remotes
    /// until the 15s watchdog. Server IsHost is pinned to the hosting launcher's
    /// in-process self-join (slot 0 reserved 10s) so a fast joiner can no longer
    /// hijack SyncSettings/warp-all; dedicated ServerHost opts out of the
    /// reservation. Full-lobby rejects close the socket; HandleClient logs faults.
    /// Launcher instances claim a lock file instead of falling back to index 0 and
    /// sharing config/logs. Hide &amp; Seek: SetGameMode(Normal) clears round state so
    /// Start Tag re-arms the hide grace, a reclaimed slot no longer inherits the
    /// departed player's seeker role, and a tag auto-ends when the last hider or the
    /// last seeker disconnects. Remote crowd LOD: nearest 4 keep 60Hz calcAnim at 5+
    /// visible remotes (hysteresis, spin-jump/FLUDD tails still override), settled
    /// bodies skip the whole re-root and moving ones cache the pose-root inverse,
    /// calcView is gated on drawBody, shadows cap at the nearest 4 within 4500u
    /// (4900 hysteresis; horizontal/XZ distance only so vertical separation does
    /// not demote a caster), a 20Hz tier lands past 4600u, isRemoteBody rejects
    /// via an address window before the 54-entry scan, and nametags share one
    /// J2DPrint per draw.
    /// Build 56: remote custom models in a full lobby â€” build 51's baseline prewarm
    /// built all MaxPlayers-1 puppets under the RETAIL mario volume before pack
    /// prefetch published, and mid-stage policy never frees a TMario. Nine wasted
    /// ~612 KiB graphs left only ~2 MiB of the 7.375 MiB body heap, so after the two
    /// ping-pong staging arenas just two remotes could ever be upgraded to their
    /// pack and the rest rendered retail. Prewarm now skips (and revisits) a slot
    /// that announced a custom model until its pack is ready for initValues, with a
    /// 900-frame deadline before a retail fallback; acquirePoolBodyForSlot refuses to
    /// hand a retail spare to a slot whose pack is already loaded so the body is
    /// built correct once; demoted main-heap graphs record their identity and are
    /// re-adopted instead of leaking for the stage. Launcher UpdateSessionStatusColor
    /// resolves theme brushes leniently (shutdown FindResource no longer throws).
    /// Build 55: Hide &amp; Seek death reload freeze â€” post-reload death-finish no
    /// longer calls TMarDirector::setMario (re-entered waitingStart/demo after intro
    /// skip already succeeded; sticky retry was cleared with death flags). Finish now
    /// restores control in-place + keeps s_postDeathEnsurePlayable sticky until
    /// playable; intro skip / forceSkip also avoid setMario. 10p Hide&amp;Seek role
    /// capacity tests lock MaxPlayers arrays.
    /// Build 54: mid-HUD gx_hud_fence save/restores full J2DOrthoGraph fields
    /// (mBounds/mScissor/mOrtho) and no longer writes logical widescreen rects
    /// into mBounds â€” that desynced scissorBounds power / setPort so shine,
    /// blue-coin, and gold-coin digit panes staggered diagonally (worse during
    /// startAppearStar). Still re-applies ReInitializeGX + 640x480 viewport +
    /// BSE widescreen ortho after nametag / Hide&amp;Seek / Connected overlays.
    /// Build 53: Bowser epilogue shine 0x77 (119) sync â€” vanilla latches the final
    /// shine in TMovieDirector::decideNextMode when epilogue.thp (movie 14) ends,
    /// while stageUpdate is idle; stageInit tracker reseed then absorbed the 0â†’1
    /// edge (same class as floodedâ†’post-flood 0x103AE). Module now keeps a session
    /// shine authority cache, force-publishes unpublished FlagManager shines after
    /// stage enter, and prefers immediate 0x77 emit from movie loop / stage exit
    /// so one peer beating Bowser syncs the shine to everyone via ShineAuthority.
    /// Build 52: floodedâ†’post-flood plaza sync â€” Corona visited card bool 0x103AE
    /// (decideNextScenario â†’ dolpic10) was swallowed by stageInit tracker reseed
    /// when latched during Corona load / floodedâ†’Corona FMV; module now force-
    /// publishes unpublished durable card bits after stage enter and prefers
    /// immediate 0x103AE emit so one peer's Corona visit unlocks final plaza for
    /// all (flooded unlock remains the seven Shadow Mario shines via ShineAuthority).
    /// Build 51: 10-player body pool â€” module baseline prewarm fills all
    /// MaxPlayers-1 remote TMario puppets (was a 3-body spare + lazy mid-stage);
    /// heap-exhausted prewarm soft-completes so custom ready work is not starved;
    /// capacity tests lock MaxPlayers/array lengths; Hide&amp;Seek role lists scroll
    /// for a full lobby. Build 50 rehost hold/republish paths unchanged.
    /// Build 50: rehost remotes â€” SetConnected drops session before adopt and clears
    /// HoldRemotePublish / remotes on disconnect + fresh connect; TryWriteBuffer
    /// invalidates remote-sync skip cache; roster always arms _activeRosterSlots;
    /// module notifyRemoteActorsConnectionChanged parks/clears on BF_CONNECTED fall
    /// and re-arms heap residency on rise so remotes spawn without Dolphin restart.
    /// Build 49: Hide &amp; Seek Start Tag â€” BridgeWorker falls back to
    /// TryWriteGameModeStateOnly when the bundled remote-sync payload write fails
    /// during Active play so GMF_GRACE_ACTIVE / roles still reach Dolphin (seeker
    /// freeze no longer waits for the ~1s TickGrace rebroadcast); module sticky
    /// seeker pad-lock holds across brief grace mailbox gaps.
    /// Build 48: nametag mid-HUD fence restores widescreen scissor / viewport /
    /// ortho (not only TEV) so FLUDD water-meter no longer shows a blue outline
    /// while remote nametags are on screen.
    /// Build 47: BridgeWorker LocalSnapshotReady ordered latest-wins handoff (monotonic seq)
    /// on the shared session-outbound drain â€” same-tick snapshot / stage-enter side effects
    /// before world-event TCP; stale detached snapshot callbacks dropped so PublishSnapshot
    /// / _lastLocalSnapshot cannot move backwards.
    /// Build 46: 1.0 harden â€” remove AgentDebugLog disk I/O from UDP/session hot paths;
    /// exclusive TCP/UDP bind (no silent dual-host via SO_REUSEADDR); NetClient TCP read
    /// + roster parse allocation cuts; BridgeWorker per-slot remote name buffer reuse
    /// (60 Hz ingest/flush); docs CommBuffer/ModBuildId brought current.
    /// Build 45: connect/disconnect/rehost harden â€” ordered localPending world-event handoff
    /// (clear only after durable queue), GameServer bind retry + linger-0 rehost, Handshake
    /// ModBuildId early reject, NetClient connect retry / ForceDispose, HostAsync waits for
    /// listener, clearer VersionMismatch UX (launcher vs ServerHost vs disc module).
    /// Build 44: HUD-safe GX fencing around mid-GCConsole overlays (nametags /
    /// Hide&amp;Seek grace / Connected PostDraw) â€” restores ReInitializeGX +
    /// ReInitTevStages after custom J2D so Z-menu blue-coin digits and radar are
    /// not painted with leftover TexObj/TEV; LOD skip rebinds hands/cap from
    /// re-rooted body joints (plaza accessory stretch harden).
    /// Build 43: Hide &amp; Seek death reload no longer softlocks on stage-entry
    /// intros (force end demo camera / entrance demo / STATE_NORMAL on reload entry;
    /// sticky retry while death recovery is live).
    /// Build 42: multi-mounted Yoshi crash â€” enemy-group tongue remove used UTF-8 name
    /// (retail is Shift-JIS) so puppet tongues stayed on æ•µã‚°ãƒ«ãƒ¼ãƒ— movement/findTarget;
    /// HoldRemotePublishMaxDuration no longer skips Active grace after long loads.
    /// Build 41: remote LOD re-root envelope/skin on all paths; never release remote
    /// publish hold into Loading (plaza stretch from freed J3D graphs).
    /// Build 40: remote LOD pose re-root rebuilds weight-envelope + skin deform after
    /// moving joint matrices (fixes intermittent rubber-hose / vertex stretch, mainly
    /// Delfino Plaza distance LOD); body-swap paths invalidate cachedPoseRoot.
    /// Build 39: Bowser ending movie auto-skip re-enabled; stage_guard still redirects
    /// corona boss (60) collapsed leaves (title/15 or 0xFF non-plaza) to plaza hub so
    /// skip cannot Delay-8 freeze. Flooded-plazaâ†’Corona loading FMV stays skip-blocked.
    /// Build 38: Bowser clear â€” next=15/255 no longer Delay-8 freezes (title/options
    /// destination + coronaâ†’plaza redirect); movie auto-skip temporarily blocked for
    /// boss ending FMV (lifted in 39).
    /// Build 68: Hide &amp; Seek death softlock â€” do not clear sticky ensure on a one-shot
    /// "playable" at death-finish (build 67 log: playable then no control retry); re-assert
    /// pad/BSE restore after collisionContext each Mario perform; abort stuck wipe-warp;
    /// require sustained playable / stick accept before clearing sticky.
    /// Build 67: Hide &amp; Seek death reload â€” always clear BSE mIsDisableInput when
    /// restoring pad (collisionContext mirrors it onto mReadInput every frame); sticky
    /// post-death ensure until control is actually playable (Pianta Village softlock).
    /// Build 66: Mecha-Bowser (area 58) 3+ player crash â€” strip STATUS_TOROCCO on
    /// remotes, null mTorocco/mPinnaRail/mKoopaRail, spoof mAreaID during remote
    /// initValues so initModel skips Torocco+rail alloc (calcBaseMtx null-deref).
    /// Build 65: Gecko 0426078C 60000000 â€” nop @ 0x8026078C (NTSC-U cutscene_skip).
    /// Build 62: red/blue remote collect FX â€” resolve blue coin pos before hide; stop
    /// OR-ing full red mask mid snapshot-expand (sibling FX was skipped). Still snapshot-only.
    /// Build 37: progress frames latest-wins only (wake pulse, no DropOldest body dup);
    /// module skips noop shine/blue apply walks on ownership-push echoes.
    /// Build 36: TCP anti-flood â€” force-full rehals no longer bump progressSeq; cache restage
    /// skips best-effort seq=0 TCP; ownership-push adaptive coalesce 200â†’500ms under load.
    /// Build 34: progress heal epoch in snapshot flags so stale applied&gt;=host acks cannot
    /// soft-skip rehals; cache-restage stage-enter no longer logs alarming seq=0 force.
    /// Build 33: cache restage completes the heal without arming force-await â€” silent TCP
    /// after stage-enter no longer storms 2s watchdog restages (bulk-apply soft-death).
    /// Build 29: fresh-connect clears progress snapshot mailbox (stale moduleAppliedSeq
    /// no longer no-ops join heals); ownership pending hard-cap drop-oldest after coalesce.
    /// Build 28: TCP durable-only â€” shine/blue/story/trigger/secret/red/NPC via coalesced
    /// WorldProgressSnapshot (125ms); no live ownership/mission WorldEvent fanout; fruit /
    /// NpcReact / HipDrop / gold never networked; ephemeral TCP DropOldest cap 8.
    /// Build 27: serialize SessionCoordinator world-progress / stage-enter / snapshot
    /// mutations on one lock (LocalSnapshotReady thread-pool vs TCP snapshot race); force-
    /// timeout cache restage and progress-serialize failures expand via events like
    /// RequestWorldProgressResync (no hung force-await / skipped heal).
    /// Build 26: gold coin network sync disabled (10p TCP/mission flood); cache restage uses
    /// real ProgressSeq; remote publish hold has a 3s max so clearPuppets cannot soft-kill
    /// remotes forever. (Build 26 also armed await on cache restage â€” reverted in 33.)
    /// Build 25: heal soft-death follow-ups on build 24 - never Clear mailbox when any
    /// authority cache exists (TTL ignored); Unchanged refreshes cache stamp; heal latch
    /// clears on moduleApplied &gt;= hostSeq OR ==0; force-await clears only after successful
    /// mailbox write / expand (not on NoteAuthoritySnapshot).
    /// Build 83: Module Update consistency — path-normalized exe/BaseDirectory
    /// preference, never take _BSMSO.kxe from AppData, verify kxe bytes before any
    /// ModBuildId stamp (extracted + ISO), install-guide matches Install-only overlays.
    /// Build 82: Update module prefers _BSMSO.kxe beside the launcher (newest fallback);
    /// never stamp ModBuildId unless installed kxe bytes match the Install source;
    /// publish copies .kxe next to BSMSO.Launcher.exe.
    /// Build 81: Force Fast Disc Speed on Launch; reject wrong-size BSE/Moveset at
    /// Install/Launch; Install mutex against half-patched Kuribo trees (black screen).
    /// Build 80: One-shot NetClient transport teardown; HostAsync holds lock for
    /// stop→bind→self-join; SessionLifecyclePhase gates UI; GameServer.Stop awaits loops.
    /// Build 79: Patch BSE moveset ON matches release zip — install Moveset.kxe only;
    /// never inject better_movement.prm (that PRM raised gravity/jump multipliers and
    /// made jumps feel heavier than release). Install always strips leftover PRM.
    /// Build 78: Moveset strip is definitive (mutex + loose PRM + verify/Launch warn);
    /// HnS keeps in-flight death remount after RoundComplete (no death-scene softlock);
    /// round-end fanfare uses system SE + RoundFanfare on first complete broadcast.
    /// Build 77: Install-only disc sync (no auto kxe/PRM on open/Launch); Connected
    /// Players Model column; HnS hotel death remount preserves load+mission (pin +
    /// sticky leave arm hotel sync); death settle skips cinematic from frame 1 and
    /// completes at STATE_NORMAL (no hotel 120f demo stack / Ricco intro stall).
    /// Build 76: HnS — scrub sticky death BEFORE warp consume on tag-off; keep
    /// launcher authorize latch until stageInit (not mid-moveStage); Sirena hotel
    /// death reload re-arms mission override + episode_equiv isSameStage so remotes
    /// return after respawn; PatchBseMoveset off strips better_movement.prm leftovers.
    /// Build 75: HnS — never redirect launcher warps when tag is off (sticky death no
    /// longer rewrites next=1/2 → death stage; remotes were invisible after local stayed
    /// behind); authorizeLauncherStageMove covers BF_WARP_PENDING-cleared moveStage;
    /// death timeout ~3s@30Hz (was ~14s); fresh Start Tag arms 8s death-edge immunity;
    /// PatchBseMoveset API defaults false so Install removes Moveset.kxe.
    /// Build 74: HnS reliability — faster death reload (min ~1.3s + early cinematic
    /// skip); keep remote heap/pool on soft stageInit so remotes reattach after death;
    /// launcher warps no longer redirected when tag/round is over (allow flag survived
    /// through moveStage; sticky death only blocks plaza hub 1/0xFF); Start Tag resume
    /// absorbs stale VFX_DEAD during 8s proximity immunity + always-seed death baseline;
    /// Noki Undersea removed from random levels; Patch BSE moveset defaults to off
    /// (retail Mario movement).
    /// Build 73: HnS — lifetime seekers keep 0:00 after death; Start Tag resume arms
    /// proximity immunity (no clustered false promotes); warp-all no longer aborted when
    /// one peer is loading; Start Tag seeds death baseline against stale VFX_DEAD.
    /// Build 72: Hide &amp; Seek death/tag freeze is monotonic — never let a post-reload
    /// director stopwatch (~0) clobber the hider's accumulated TIME; capture before stage
    /// teardown and show frozenCs on seeker remount.
    /// Build 71: Hide &amp; Seek death-reload TIME HUD retries appear until retail
    /// mIsTimerCard is live (build 70 one-shot remount skipped when GMF_TAG_ACTIVE
    /// flickered false; seeker promotion left a stale s_timerPanelVisible).
    /// Build 70: Hide &amp; Seek timer HUD remounts after mid-tag death reload (stale
    /// s_timerPanelVisible skipped appear on the new GCConsole2).
    /// Build 69: Hide &amp; Seek death retail-like same-stage moveStage settle (STATE_NORMAL
    /// then one-shot cinematic skip + control restore; no sticky pad / VerifyingMove wars).
    /// Build 24: continuous ownership push (server WorldProgressSnapshot on every authority
    /// change, 50ms coalesce) + bridge restage no longer arms force-await + bridge poll
    /// callbacks detached so a hung stage-enter path cannot wedge localPending.
    /// Build 21: Hide &amp; Seek death same-stage reload (no black-screen soft setMario),
    /// mid-round warp intro demo skip, launcher Connected Players scroll fix.
    /// Build 20: release zip of Comm v14 authority-first sync (dual outbound localPending
    /// ownership vs mission) + Phase 1 AuthorityHealGovernor.
    /// Build 81: black-screen after Install — FastDiscSpeed=True (Emulate Disc Speed
    /// OFF) for BSE boot; reject wrong-size BSE/Moveset at Install + Launch; install mutex.
    /// Fast Disc Speed later moved to recommended profile only (user choice when off).
    /// Build 80: reliable disconnect→reconnect / host→stop→rehost — UDP Dead one-shot
    /// teardown (no sticky Connected), SessionLifecyclePhase UI gating, HostAsync holds
    /// network lock across stop+bind+self-join, generation-gated NetClient callbacks,
    /// GameServer Stop awaits listen loops before exclusive rebind.
    /// Build 93: off-stage Mario model swap no longer leaves remotes invisible —
    /// park stale custom pool bodies and allow retail stand-in / unspawned ready-body
    /// prep so co-location can spawn then upgrade.
    /// Build 92: hide remote bodies when camera→chest collision LOS is blocked —
    /// fixes remotes showing through walls when the camera is jammed inside geometry
    /// (GPU Z hole on one-sided meshes). Shared collision_los with H&amp;S nametags.
    /// Build 94: BSMSO 2.0 line — GameProfileId on JoinRequest + ProfileMismatch reject,
    /// Super Mario Eclipse profile (additive-only Install, Eclipse-aware host/launch gates,
    /// warp pass-through, collectible sync hard-off until Eclipse maps are measured).
    /// Build 91: Sirena hotel Ep4/Ep5 share delfino2 — arm mission on natural load=2
    /// entry and harden casino stash so unfinished Ep4 cannot force casino0 after
    /// selecting Ep5 (King Boo path).
    /// Build 90: revert closer content-sized HnS roster columns; restore equal-width
    /// dual columns (build 88 layout) with toasts still below the roster.
    /// Build 89: HnS role roster columns size to content and sit closer (8px gap).
    /// Build 88: HnS join/leave toasts draw below the role roster (no overlap).
    /// Build 87: inset HnS role roster from screen right; width-fit names; keep clear of
    /// Connected/Tag status so dual columns are not clipped off-screen.
    /// Build 86: Hide &amp; Seek HUD — compact top-right Hider/Seeker roster,
    /// gold Tag-is-going status, seeker-only CAN'T MOVE YET during grace lock.
    /// Build 85: while local player is a seeker, nametags keep distance scaling but
    /// skip far-distance fade/cull so teammate tags do not disappear at range.
    /// Build 84: Client Actions HnS status shows warp destination (seeker fixed-scale
    /// nametag experiment reverted in 85).
    /// </remarks>
    public const ushort ModBuildId = 94;
    public const int DefaultPort = 27015;
    public const int StableMaxPlayers = 10;
    public const int MaxPlayers = 10;
    public const int MaxRemoteSlots = 10;
    public const int MarioModelIdSize = 8;
    public const int MarioModelIntentSize = 4 + MarioModelIdSize;
    public const int MarioVoiceEventSize = 12;
    public const int CommMarioVoiceEventsOffset = 862;
    public const int CommMarioVoiceEventsSize = MarioVoiceEventSize * (MaxRemoteSlots + 1);
    // mode+flags+localRole+lastTagged+tagEventId+roundStartMs(4)+roleBySlot[N]+graceRemainingMs(2)
    public const int CommGameModeStateSize = 11 + MaxPlayers;
    public const int CommGameModeStateOffset = CommMarioVoiceEventsOffset + CommMarioVoiceEventsSize;
    public const int CommWorldEventSize = 19;
    /// <summary>
    /// Dual outbound (ownership + mission) + dual inbound + lastAppliedEventId.
    /// Comm v14 mirrors the v13 inbound split on the outbound path so a wedged
    /// red/gold/fruit localPending can never starve shine/blue/story.
    /// </summary>
    public const int CommWorldSyncSize = CommWorldEventSize * 4 + 4;
    public const int CommWorldSyncOffset = CommGameModeStateOffset + CommGameModeStateSize;
    public const int CommLocalPendingOwnershipOffset = CommWorldSyncOffset;
    public const int CommLocalPendingMissionOffset =
        CommWorldSyncOffset + CommWorldEventSize;
    public const int CommIncomingOwnershipWorldEventOffset =
        CommWorldSyncOffset + CommWorldEventSize * 2;
    public const int CommIncomingWorldEventOffset =
        CommWorldSyncOffset + CommWorldEventSize * 3;
    public const int CommRosterHudEventSize = 20;
    // One slot per player so a full-lobby connect/disconnect wave cannot overwrite unread HUD events.
    public const int CommRosterHudRingSlots = MaxPlayers;
    public const int CommRosterHudSyncSize = 2 + CommRosterHudEventSize * CommRosterHudRingSlots;
    public const int CommRosterHudOffset = CommWorldSyncOffset + CommWorldSyncSize;
    public const int CommMarioModelIdsOffset = CommRosterHudOffset + CommRosterHudSyncSize;
    public const int CommMarioModelIdsSize = MarioModelIdSize * (MaxRemoteSlots + 1);
    /// <summary>
    /// Max shine ownership id+1 synced via bitsets / live ShineCollected payload0.
    /// 256 fits the live event byte id, BSE EXTRA_SHINES FlagManager path, and keeps
    /// heal payloads compact (32-byte bitset). Vanilla SMS uses 120; ids 0..255.
    /// </summary>
    public const int ShineBitCapacity = 256;
    public const int ShineBitsByteCount = ShineBitCapacity / 8;

    /// <summary>
    /// Latest-wins compact progress heal blob (after model ids). Sized for full ownership
    /// + current-stage mission bits; off-stage red/NPC are filtered before write.
    /// Header: hostSeq(4)+moduleAppliedSeq(4)+payloadLen(2)+flags(1)+reserved(1).
    /// </summary>
    public const int CommProgressSnapshotHeaderSize = 12;
    // 4096 still covers worst-case ownership (256-shine bitset + blues + ~378 story +
    // plaza triggers + secrets) plus one stage of red/NPC mission bits with headroom.
    public const int CommProgressSnapshotMaxPayload = 4096;
    public const int CommProgressSnapshotSize =
        CommProgressSnapshotHeaderSize + CommProgressSnapshotMaxPayload;
    public const int CommProgressSnapshotOffset = CommMarioModelIdsOffset + CommMarioModelIdsSize;
    /// <summary>Launcher-authored in-game BGM volume percent (0â€“100), after progress snapshot.</summary>
    public const int CommMusicVolumeOffset = CommProgressSnapshotOffset + CommProgressSnapshotSize;
    public const int CommMusicVolumeSize = 1;
    public const byte CommMusicVolumeDefault = 100;
    public const int CommBufferSize = CommMusicVolumeOffset + CommMusicVolumeSize;
    public const int RosterEntrySize = 30; // slot(1)+name(16)+stage(1)+ep(1)+state(1)+ping(2)+modelId(8)
    // name(16)+modelId(8)+modBuildId(2)+gameProfileId(2)
    public const int JoinRequestSize = 16 + MarioModelIdSize + 2 + 2;
    public const int WorldEventClientPayloadSize = 15;
    public const int WorldEventBroadcastPayloadSize = 17;
    public const int CommNameTagAppearancesOffset = 752;
    public const int CommNameTagAppearancesSize = 10 * (MaxRemoteSlots + 1);
    public const int CommBridgeControlOffset = 6;
    public const int CommBridgeControlSize = 26;
    public const int CommRemoteSnapshotsOffset = 112;
    public const int CommRemoteSnapshotsSize = PlayerSnapshotSize * MaxRemoteSlots;
    public const int UdpSnapshotPayloadOffset = 10;
    public const int PlayerSnapshotSize = 64;
    // Batched server fanout: magic(4)+id(1)+count(1), then
    // slot(1)+source sequence(4)+snapshot(64) per player.
    public const int UdpSnapshotBatchHeaderSize = 6;
    public const int UdpSnapshotBatchEntrySize = 1 + 4 + PlayerSnapshotSize;
    public const int UdpSnapshotBatchMaxSize =
        UdpSnapshotBatchHeaderSize + UdpSnapshotBatchEntrySize * StableMaxPlayers;
    public const int UdpPingPayloadSize = 8;
    // Must fit the worst-case sparse authority snapshot (collectibles + every durable
    // card bit + stage-scoped trigger bits). 32 KiB truncated the tail â€” exactly where
    // story flags are serialized. The frame length is ushort, so retain safe headroom.
    public const int MaxTcpPayloadSize = 60000;
    /// <summary>
    /// Lobby-wide periodic full heal is disabled as a reliability mechanism (was 45s).
    /// Join / client-request / sync-reenable still send compact progress snapshots.
    /// Kept as a large sentinel so any residual watchdog call is effectively off.
    /// </summary>
    public const int WorldProgressResyncIntervalMs = int.MaxValue / 4;
    public const int WorldProgressRequestClientSeqSize = 4;
    public const uint DefaultMailboxAddress = 0x817FC000;
    public const int SnapshotRateHz = 60;
    public const int BridgePollMs = 1000 / SnapshotRateHz;
    public const int UdpSnapshotIntervalMs = 1000 / SnapshotRateHz;
    public const int HeartbeatIntervalMs = 2000;
    public const int StaleTimeoutMs = 10000;
    public const int DisconnectTimeoutMs = 15000;
    public const int ConnectTimeoutMs = 10000;
    /// <summary>
    /// Transient TCP connect failures (host still binding / AcceptLoop not scheduled /
    /// brief TIME_WAIT after rehost) retry this many times before surfacing an error.
    /// </summary>
    public const int ConnectRetryCount = 6;
    /// <summary>Base delay for connect retries; attempt N waits N * this many ms.</summary>
    public const int ConnectRetryBaseDelayMs = 75;
    /// <summary>GameServer.Start bind retries when the prior host left the port in TIME_WAIT.</summary>
    public const int ServerBindRetryCount = 8;
    /// <summary>Base delay for server bind retries; attempt N waits N * this many ms.</summary>
    public const int ServerBindRetryBaseDelayMs = 50;
    public const int RosterBroadcastIntervalMs = 200;
    public const int ReconnectWindowMs = 30000;
    /// <summary>
    /// Unnamed handshake sessions older than this are reclaimable so abandoned
    /// TCP connects cannot permanently block joins when the lobby is at capacity.
    /// </summary>
    public const int AbandonedHandshakeGraceMs = 5000;
    /// <summary>
    /// The game profile this build/session joins with. Clients with a different profile
    /// are rejected with <see cref="JoinRejectReason.ProfileMismatch"/>.
    /// </summary>
    public const ushort CurrentGameProfileId = (ushort)GameProfileId.VanillaSms;
    /// <summary>
    /// Guid (16) + ModBuildId (2). Older peers may still send Guid-only (16).
    /// </summary>
    public const int HandshakePayloadSize = 18;
    /// <summary>
    /// Legacy HandshakeAck is 17 bytes (slot at offset 16). Current acks append
    /// server <see cref="ModBuildId"/> at offset 17 so clients can fail fast.
    /// </summary>
    public const int HandshakeAckPayloadSize = 19;
    public const byte WarpNoTarget = 0xFC;
    public const byte WarpAllSlots = 0xFF;
}

public enum TcpPacketId : byte
{
    Handshake = 1,
    HandshakeAck = 2,
    JoinRequest = 3,
    JoinAccepted = 4,
    JoinRejected = 5,
    RosterSnapshot = 6,
    WarpRequest = 7,
    WarpCommand = 8,
    SyncSettings = 9,
    WorldEvent = 10,
    Disconnect = 11,
    Heartbeat = 12,
    PlayerLeft = 13,
    UdpRegister = 14,
    MarioVoiceEvent = 15,
    ClientTeleportSettings = 16,
    GameModeState = 17,
    WorldStateReplay = 18,
    /// <summary>Client asks server to rebroadcast authoritative collectible state.</summary>
    WorldProgressRequest = 19,
    /// <summary>
    /// Client immediately announces its desired Mario model. Unknown TCP ids are
    /// ignored by v2 peers, so heartbeat advertisement remains a safe fallback
    /// without changing protocol or CommBuffer versions.
    /// </summary>
    MarioModelIntent = 20,
    /// <summary>
    /// Compact authority heal (bitsets / sparse flags). Replaces exploding
    /// <see cref="WorldStateReplay"/> for join / stage-enter / catch-up.
    /// Unknown to older clients â€” they keep working via live WorldEvent only until updated.
    /// </summary>
    WorldProgressSnapshot = 21,
}

public enum UdpPacketId : byte
{
    PlayerSnapshot = 20,
    SnapshotBatch = 21,
    Ping = 22,
    Pong = 23,
}

public enum JoinRejectReason : byte
{
    None = 0,
    NameTaken = 1,
    Full = 2,
    InvalidName = 3,
    VersionMismatch = 4,
    /// <summary>Client and server target different BSE game profiles (e.g. vanilla vs Eclipse).</summary>
    ProfileMismatch = 5,
}

public enum DisconnectReason : byte
{
    UserRequest = 0,
    Timeout = 1,
    Kicked = 2,
    ServerShutdown = 3,
    DolphinClosed = 4,
}

[Flags]
public enum BridgeFlags : uint
{
    Connected = 1 << 0,
    Host = 1 << 1,
    WarpPending = 1 << 2,
    Loading = 1 << 3,
    SyncShine = 1 << 4,
    SyncBlueCoin = 1 << 5,
    SyncEvent = 1 << 6,
    SyncStory = 1 << 7,
    SyncMission = 1 << 8,
    SyncSecret = 1 << 9,
    SyncObjects = 1 << 10,
    SyncProgress = 1 << 11,
    /// Module requests an immediate WorldProgress snapshot (e.g. co-op same-stage death reload).
    RequestProgress = 1 << 12,
    WarpToPoint = 1 << 13,
    WarpAll = 1 << 14,
}

public enum DolphinState : byte
{
    None = 0,
    Booting = 1,
    Loading = 2,
    Active = 3,
    Warping = 4,
}

[Flags]
public enum VfxFlags : ushort
{
    WaterSpray = 1 << 0,
    Hover = 1 << 1,
    Rocket = 1 << 2,
    Turbo = 1 << 3,
    Dead = 1 << 4,
    FluddEmpty = 1 << 5, // spray trigger held with empty tank (dry pump)
    YCam = 1 << 6,
    NozzleSwitching = 1 << 7,
    WetSlide = 1 << 8,
    NoFludd = 1 << 9, // FLUDD pack hidden on Mario's back (stolen / on Yoshi)
    YoshiFruitMouth = 1 << 10, // fruit actor encode (1..7) in vfx bits 11..13
}

public enum RosterHudEventKind : byte
{
    None = 0,
    Connected = 1,
    Disconnected = 2,
}

public enum WorldEventType : byte
{
    ShineCollected = 1,
    BlueCoinCollected = 2,
    EpisodeComplete = 3,
    StoryFlag = 4,
    TriggerFlag = 5,
    SecretComplete = 6,
    GoldCoinCollected = 7,
    HipDropObject = 8,
    RedCoinCollected = 9,
    YoshiFruitTaken = 10,
    MarioFruitKicked = 11,
    MarioFruitPicked = 12,
    MarioFruitThrown = 13,
    MarioFruitDropped = 14,
    MarioFruitSync = 15,
    NpcReact = 16,
    NpcCleaned = 17,
    /// <summary>
    /// LEGACY â€” graffiti/goop sync permanently disabled. Enum kept for wire
    /// compatibility; server rejects; never enters durable history.
    /// </summary>
    GraffitiCleaned = 18,
    /// <summary>
    /// Host-only mid-session "new file" progress clear. Non-durable.
    /// Clears shine/blue/story/secret/plaza Type5 ownership on clients plus all
    /// durable collectible authorities on the server. Empty snapshots alone are set-only.
    /// </summary>
    SessionProgressReset = 19,
    /// <summary>Legacy alias for <see cref="SessionProgressReset"/>.</summary>
    ShineBlueProgressReset = SessionProgressReset,
}

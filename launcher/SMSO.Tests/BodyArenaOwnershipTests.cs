namespace SMSO.Tests;

/// <summary>
/// Host-side regression coverage for the remote body-arena ownership policy
/// mirrored by module/src/remote_actor.cpp. Double-assignment of one child
/// arena to two network slots must be impossible by construction.
/// Perform ownership: demoted/parked/ready graphs are module-owned and must
/// never fall through to retail TMario::perform.
/// Mid-stage arena freeAll of live pool graphs is forbidden. Displaced
/// main-heap retail is recycled one-for-one on custom claim; leftover spares
/// may reclaim at admit time. Soft-defer when the arena pool (heap stand-in)
/// cannot allocate another child — but soft-defer must not consume the shared
/// construction budget (ModBuildId 60). When admit fails, THAT slot's live
/// main-heap retail may be destroyed one-for-one so a parent-heap custom can
/// spawn without dual residency. Main-heap prewarm graphs park as spares only
/// when still referenced at recycle time.
/// </summary>
public class BodyArenaOwnershipTests
{
    private const int ArenaCapacity = 10;
    private const int MaxRemoteSlots = 10;
    private const int PingPongArenaCount = 2;
    private const int MainHeapSpareCapacity = MaxRemoteSlots;
    private const byte NoArena = 0xFF;
    private const uint ReclaimDelayTicks = 6;

    private enum ArenaState : byte
    {
        Free = 0,
        Building,
        Ready,
        Active,
        Retired,
    }

    private sealed class Arena
    {
        public ArenaState State;
        public byte OwnerSlot = NoArena;
        public uint Generation;
        public int? BodyId;
        /// <summary>Stand-in for releaseMarioTexAnims having run before freeAll.</summary>
        public bool TexReleased;
        /// <summary>Stand-in for removeBodyFromViewList before freeAll.</summary>
        public bool ViewDetached;
        /// <summary>Stand-in for Player-group removal before freeAll.</summary>
        public bool PlayerGroupDetached;
        /// <summary>Stand-in for ~TMario before freeAll.</summary>
        public bool DestructorRan;
        /// <summary>Stamp-only; mid-stage freeAll is disabled.</summary>
        public uint ReclaimAfterTick;
        /// <summary>Fixed ping-pong staging slot (0..1) or -1 for overflow arenas.</summary>
        public int PingPongIndex = -1;
    }

    private sealed class OwnershipModel
    {
        public readonly Arena[] Arenas = Enumerable.Range(0, ArenaCapacity)
            .Select(i => new Arena { PingPongIndex = i < PingPongArenaCount ? i : -1 }).ToArray();
        public readonly byte[] SlotArena = Enumerable.Repeat(NoArena, MaxRemoteSlots).ToArray();
        public readonly int?[] SlotBody = new int?[MaxRemoteSlots];
        /// <summary>Main-heap prewarm bodies (no child arena) parked mid-stage.</summary>
        public readonly List<int> MainHeapSpares = new();
        /// <summary>Bodies known to module ownership tables (pool/variant/ready/arena/spare).</summary>
        public readonly HashSet<int> ModuleOwnedBodies = new();
        public readonly HashSet<int> ViewListBodies = new();
        public readonly HashSet<int> PlayerGroupBodies = new();
        public readonly HashSet<int> VariantOrReadyBodies = new();
        /// <summary>Bodies that were scrubbed from cache without ~TMario (forbidden).</summary>
        public readonly HashSet<int> AbandonedWithoutTeardown = new();
        public int NextBodyId = 1;
        public uint ReclaimTick = 1;
        public int SoftDeferCount;

        public void AdvanceReclaimTick()
        {
            if (++ReclaimTick == 0)
                ReclaimTick = 1;
        }

        public uint SlotReferenceMask()
        {
            uint mask = 0;
            for (var slot = 0; slot < MaxRemoteSlots; slot++)
            {
                var index = SlotArena[slot];
                if (index < ArenaCapacity)
                    mask |= 1u << index;
            }
            return mask;
        }

        public int OccupiedArenaCount()
            => Arenas.Count(a => a.State != ArenaState.Free);

        public static int ChooseExclusiveFree(uint freeMask, uint excludedMask)
        {
            var candidates = freeMask & ~excludedMask;
            // Prefer fixed ping-pong arenas (low indices) for staging.
            for (var i = 0; i < ArenaCapacity; i++)
            {
                if ((candidates & (1u << i)) != 0)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Mirrors isRemoteBody + perform gating: module-owned bodies that are
        /// not the active visible slot body must no-op (never retail perform).
        /// </summary>
        public bool IsModuleOwnedRemote(int bodyId)
        {
            if (ModuleOwnedBodies.Contains(bodyId))
                return true;
            if (SlotBody.Any(b => b == bodyId))
                return true;
            if (VariantOrReadyBodies.Contains(bodyId))
                return true;
            if (MainHeapSpares.Contains(bodyId))
                return true;
            return Arenas.Any(a => a.BodyId == bodyId);
        }

        public bool WouldCallRetailPerform(int bodyId, int? activeVisibleSlotBody)
        {
            if (!IsModuleOwnedRemote(bodyId))
                return true; // local / unknown — retail path
            _ = activeVisibleSlotBody;
            return false;
        }

        public int ActivateMainHeapPrewarm(byte slot)
        {
            var bodyId = NextBodyId++;
            SlotBody[slot] = bodyId;
            SlotArena[slot] = NoArena;
            ModuleOwnedBodies.Add(bodyId);
            ViewListBodies.Add(bodyId);
            return bodyId;
        }

        public bool ParkMainHeapSpare(int bodyId)
        {
            if (MainHeapSpares.Contains(bodyId))
                return true;
            if (MainHeapSpares.Count >= MainHeapSpareCapacity)
                return false;
            ViewListBodies.Remove(bodyId);
            PlayerGroupBodies.Remove(bodyId);
            VariantOrReadyBodies.Remove(bodyId);
            MainHeapSpares.Add(bodyId);
            ModuleOwnedBodies.Add(bodyId);
            return true;
        }

        public void ScrubMainHeapWithoutTeardown(int bodyId)
        {
            VariantOrReadyBodies.Remove(bodyId);
            ModuleOwnedBodies.Remove(bodyId);
            AbandonedWithoutTeardown.Add(bodyId);
        }

        public int BeginBuild(byte slot, uint generation)
        {
            Assert.True(generation != 0);
            Assert.False(HasDuplicateArenaOwnership());
            Assert.False(HasDuplicateBodyOwnership());

            // Cancel this slot's leftover staging by parking (never freeAll mid-stage).
            for (var i = 0; i < ArenaCapacity; i++)
            {
                if (Arenas[i].OwnerSlot != slot)
                    continue;
                if (Arenas[i].State == ArenaState.Building)
                    AbortBuild(i);
                else if (Arenas[i].State == ArenaState.Ready)
                    Retire(i, slot);
            }

            // Mid-stage: never reclaim. Soft-defer when no free arena remains.
            uint freeMask = 0;
            var excluded = SlotReferenceMask();
            for (var i = 0; i < ArenaCapacity; i++)
            {
                if (Arenas[i].State != ArenaState.Free)
                {
                    excluded |= 1u << i;
                    continue;
                }
                if (Arenas[i].BodyId is null)
                    freeMask |= 1u << i;
                else
                    excluded |= 1u << i;
            }

            var selected = ChooseExclusiveFree(freeMask, excluded);
            if (selected < 0)
            {
                SoftDeferCount++;
                return -1;
            }

            Arenas[selected].OwnerSlot = slot;
            Arenas[selected].Generation = generation;
            Arenas[selected].State = ArenaState.Building;
            Arenas[selected].TexReleased = false;
            Arenas[selected].ViewDetached = false;
            Arenas[selected].PlayerGroupDetached = false;
            Arenas[selected].DestructorRan = false;
            Arenas[selected].ReclaimAfterTick = 0;
            return selected;
        }

        public void AbortBuild(int index)
        {
            var arena = Arenas[index];
            Assert.Equal(ArenaState.Building, arena.State);
            arena.State = ArenaState.Retired;
            arena.ReclaimAfterTick = 0;
        }

        public void CompleteReady(int index, byte slot, uint generation)
        {
            var arena = Arenas[index];
            Assert.Equal(ArenaState.Building, arena.State);
            Assert.Equal(slot, arena.OwnerSlot);
            arena.BodyId = NextBodyId++;
            arena.Generation = generation;
            arena.State = ArenaState.Ready;
            arena.ReclaimAfterTick = 0;
            ModuleOwnedBodies.Add(arena.BodyId.Value);
            VariantOrReadyBodies.Add(arena.BodyId.Value);
        }

        public void CompleteReadyUnowned(int index, uint generation)
        {
            var arena = Arenas[index];
            Assert.Equal(ArenaState.Building, arena.State);
            arena.BodyId = NextBodyId++;
            arena.Generation = generation;
            arena.OwnerSlot = NoArena;
            arena.State = ArenaState.Ready;
            arena.ReclaimAfterTick = 0;
            ModuleOwnedBodies.Add(arena.BodyId.Value);
            VariantOrReadyBodies.Add(arena.BodyId.Value);
        }

        public void ActivateFromPrewarm(int index, byte slot, uint generation)
        {
            var arena = Arenas[index];
            Assert.Equal(ArenaState.Building, arena.State);
            arena.BodyId = NextBodyId++;
            arena.State = ArenaState.Active;
            arena.OwnerSlot = slot;
            arena.Generation = generation;
            SlotBody[slot] = arena.BodyId;
            SlotArena[slot] = (byte)index;
            ModuleOwnedBodies.Add(arena.BodyId.Value);
            if (arena.BodyId is int body)
                ViewListBodies.Add(body);
        }

        public bool CommitReady(byte slot, int readyIndex)
        {
            var ready = Arenas[readyIndex];
            Assert.Equal(ArenaState.Ready, ready.State);
            Assert.Equal(slot, ready.OwnerSlot);

            var previous = SlotArena[slot];
            if (previous >= ArenaCapacity || previous == readyIndex)
                return false;
            if (Arenas[previous].State != ArenaState.Active || Arenas[previous].OwnerSlot != slot)
                return false;
            for (var other = 0; other < MaxRemoteSlots; other++)
            {
                if (other == slot)
                    continue;
                if (SlotArena[other] == previous || SlotArena[other] == readyIndex)
                    return false;
            }

            var prevBody = SlotBody[slot];
            SlotBody[slot] = ready.BodyId;
            SlotArena[slot] = (byte)readyIndex;
            ready.State = ArenaState.Active;
            if (ready.BodyId is int newBody)
            {
                ViewListBodies.Add(newBody);
                VariantOrReadyBodies.Remove(newBody);
            }
            RetireFromLive(previous, slot);
            if (prevBody is int demoted)
            {
                Assert.True(IsModuleOwnedRemote(demoted));
                Assert.False(WouldCallRetailPerform(demoted, SlotBody[slot]));
            }
            return !HasDuplicateArenaOwnership() && !HasDuplicateBodyOwnership();
        }

        public void Retire(int index, byte slot)
        {
            var arena = Arenas[index];
            Assert.Equal(slot, arena.OwnerSlot);
            Assert.True(arena.State is ArenaState.Active or ArenaState.Ready);
            if (arena.BodyId is int body)
                VariantOrReadyBodies.Add(body);
            arena.State = ArenaState.Retired;
        }

        public void RetireFromLive(int index, byte slot)
        {
            var arena = Arenas[index];
            Assert.Equal(slot, arena.OwnerSlot);
            Assert.Equal(ArenaState.Active, arena.State);
            if (arena.BodyId is int body)
            {
                ViewListBodies.Remove(body);
                PlayerGroupBodies.Remove(body);
                VariantOrReadyBodies.Add(body);
                ModuleOwnedBodies.Add(body);
            }
            arena.ViewDetached = true;
            arena.PlayerGroupDetached = true;
            arena.State = ArenaState.Retired;
            var earliest = ReclaimTick + ReclaimDelayTicks;
            if (arena.ReclaimAfterTick < earliest)
                arena.ReclaimAfterTick = earliest;
        }

        public bool BodyIsLiveOwner(int? bodyId)
        {
            if (bodyId is null)
                return false;
            if (SlotBody.Any(body => body == bodyId))
                return true;
            return ViewListBodies.Contains(bodyId.Value);
        }

        public bool ArenaIsLivePool(int index)
        {
            return SlotArena.Any(slotIndex => slotIndex == index);
        }

        /// <summary>Mid-stage no-op — mirrors reclaimInactiveBodyGraphs stage-only policy.</summary>
        public bool TryReclaimUnreferencedReady() => false;

        /// <summary>Mid-stage no-op — parked graphs stay until StageRecycle.</summary>
        public void ReclaimRetired()
        {
            // Intentionally empty: mid-stage freeAll is forbidden.
        }

        /// <summary>
        /// Stage-boundary recycle: ~TMario + freeAll for non-live parked graphs.
        /// </summary>
        public void StageRecycle()
        {
            for (var i = 0; i < ArenaCapacity; i++)
            {
                var arena = Arenas[i];
                if (arena.State is not (ArenaState.Retired or ArenaState.Ready))
                    continue;
                if (ArenaIsLivePool(i) || SlotArena.Any(index => index == i))
                    continue;
                if (BodyIsLiveOwner(arena.BodyId))
                    continue;
                if (!arena.ViewDetached && arena.BodyId is int body)
                {
                    ViewListBodies.Remove(body);
                    arena.ViewDetached = true;
                }
                if (!arena.PlayerGroupDetached && arena.BodyId is int pgBody)
                {
                    PlayerGroupBodies.Remove(pgBody);
                    arena.PlayerGroupDetached = true;
                }
                arena.TexReleased = true;
                arena.DestructorRan = true;
                if (arena.BodyId is int freedBody)
                {
                    ModuleOwnedBodies.Remove(freedBody);
                    VariantOrReadyBodies.Remove(freedBody);
                }
                arena.BodyId = null;
                arena.OwnerSlot = NoArena;
                arena.Generation = 0;
                arena.ReclaimAfterTick = 0;
                arena.State = ArenaState.Free;
            }
        }

        public bool TryClaimReadyExclusive(byte slot, int readyIndex)
        {
            var ready = Arenas[readyIndex];
            if (ready.State != ArenaState.Ready)
                return false;
            if (ready.OwnerSlot != NoArena && ready.OwnerSlot != slot)
                return false;
            if (SlotBody.Any(body => body == ready.BodyId))
                return false;

            ready.OwnerSlot = slot;
            var previous = SlotArena[slot];
            var prevBody = SlotBody[slot];
            SlotBody[slot] = ready.BodyId;
            SlotArena[slot] = (byte)readyIndex;
            ready.State = ArenaState.Active;
            if (ready.BodyId is int newBody)
            {
                ViewListBodies.Add(newBody);
                VariantOrReadyBodies.Remove(newBody);
            }

            if (previous < ArenaCapacity && previous != readyIndex)
            {
                RetireFromLive(previous, slot);
                Arenas[previous].TexReleased = false;
                Arenas[previous].DestructorRan = false;
            }
            else if (previous == NoArena && prevBody is int mainHeapBody)
            {
                Assert.True(ParkMainHeapSpare(mainHeapBody));
                Assert.DoesNotContain(mainHeapBody, AbandonedWithoutTeardown);
            }
            if (prevBody is int demoted)
                Assert.False(WouldCallRetailPerform(demoted, SlotBody[slot]));
            return !HasDuplicateArenaOwnership() && !HasDuplicateBodyOwnership();
        }

        public bool HasDuplicateArenaOwnership()
        {
            for (var a = 0; a < MaxRemoteSlots; a++)
            {
                if (SlotArena[a] >= ArenaCapacity)
                    continue;
                for (var b = a + 1; b < MaxRemoteSlots; b++)
                {
                    if (SlotArena[a] == SlotArena[b])
                        return true;
                }
            }
            return false;
        }

        public bool HasDuplicateBodyOwnership()
        {
            for (var a = 0; a < MaxRemoteSlots; a++)
            {
                if (SlotBody[a] is null)
                    continue;
                for (var b = a + 1; b < MaxRemoteSlots; b++)
                {
                    if (SlotBody[a] == SlotBody[b])
                        return true;
                }
            }
            return false;
        }

        public bool AnyReclaimTargetsLivePoolBody()
        {
            for (var i = 0; i < ArenaCapacity; i++)
            {
                var arena = Arenas[i];
                if (arena.State != ArenaState.Free)
                    continue;
                if (arena.BodyId is null)
                    continue;
                if (BodyIsLiveOwner(arena.BodyId))
                    return true;
            }
            return false;
        }

        public bool AnyFreeAllOfLivePoolArena()
        {
            for (var i = 0; i < ArenaCapacity; i++)
            {
                if (Arenas[i].State != ArenaState.Free)
                    continue;
                if (ArenaIsLivePool(i))
                    return true;
            }
            return false;
        }

        public bool AnyMainHeapAbandonedWithoutTeardown()
            => AbandonedWithoutTeardown.Count > 0;
    }

    [Fact]
    public void ChooseExclusiveFree_IgnoresExcludedAndNonFreeBits()
    {
        Assert.Equal(1, OwnershipModel.ChooseExclusiveFree(0b0011, 0b0001));
        Assert.Equal(-1, OwnershipModel.ChooseExclusiveFree(0b0001, 0b0001));
        Assert.Equal(-1, OwnershipModel.ChooseExclusiveFree(0, 0));
    }

    [Fact]
    public void ModelSwitch_RetiresPrevious_OverflowAllocatesNewArena()
    {
        var model = new OwnershipModel();

        var a0 = model.BeginBuild(slot: 1, generation: 1);
        Assert.Equal(0, a0);
        model.ActivateFromPrewarm(a0, slot: 1, generation: 1);

        var staging = model.BeginBuild(slot: 1, generation: 2);
        Assert.Equal(1, staging);
        model.CompleteReady(staging, slot: 1, generation: 2);
        Assert.True(model.CommitReady(slot: 1, readyIndex: staging));
        Assert.Equal((byte)staging, model.SlotArena[1]);
        Assert.Equal(ArenaState.Retired, model.Arenas[a0].State);

        // Mid-stage reclaim must not free a0 — next build takes overflow arena 2.
        Assert.False(model.TryReclaimUnreferencedReady());
        model.ReclaimRetired();
        Assert.Equal(ArenaState.Retired, model.Arenas[a0].State);

        var other = model.BeginBuild(slot: 2, generation: 1);
        Assert.Equal(2, other);
        model.ActivateFromPrewarm(other, slot: 2, generation: 1);
        Assert.NotEqual(model.SlotArena[1], model.SlotArena[2]);
        Assert.NotEqual(model.SlotBody[1], model.SlotBody[2]);
        Assert.False(model.HasDuplicateArenaOwnership());
        Assert.False(model.HasDuplicateBodyOwnership());
    }

    [Fact]
    public void FreeArenaStillIndexed_CannotBeChosenOrReclaimed()
    {
        var model = new OwnershipModel();
        var a0 = model.BeginBuild(slot: 0, generation: 1);
        model.ActivateFromPrewarm(a0, slot: 0, generation: 1);

        model.Arenas[a0].State = ArenaState.Free;
        model.Arenas[a0].BodyId = null;

        var stolen = model.BeginBuild(slot: 1, generation: 1);
        Assert.NotEqual(a0, stolen);
        Assert.True(stolen < 0 || stolen != a0);

        model.Arenas[a0].State = ArenaState.Retired;
        model.Arenas[a0].ReclaimAfterTick = 0;
        model.ReclaimRetired();
        Assert.Equal(ArenaState.Retired, model.Arenas[a0].State);
        Assert.Equal((byte)a0, model.SlotArena[0]);

        model.StageRecycle();
        // Still live-indexed — stage recycle must refuse freeAll.
        Assert.Equal(ArenaState.Retired, model.Arenas[a0].State);
        Assert.Equal((byte)a0, model.SlotArena[0]);
    }

    [Fact]
    public void TwoSlots_NeverShareArenaIndex_AfterSequentialSwitches()
    {
        var model = new OwnershipModel();
        foreach (byte slot in new byte[] { 0, 1, 2 })
        {
            var index = model.BeginBuild(slot, generation: 1);
            Assert.True(index >= 0);
            model.ActivateFromPrewarm(index, slot, generation: 1);
        }

        for (uint gen = 2; gen <= 4; gen++)
        {
            var staging = model.BeginBuild(slot: 1, generation: gen);
            Assert.True(staging >= 0);
            model.CompleteReady(staging, slot: 1, generation: gen);
            Assert.True(model.CommitReady(slot: 1, readyIndex: staging));
            Assert.False(model.HasDuplicateArenaOwnership());
            Assert.False(model.HasDuplicateBodyOwnership());
            Assert.False(model.AnyFreeAllOfLivePoolArena());
        }

        Assert.NotEqual(model.SlotArena[0], model.SlotArena[1]);
        Assert.NotEqual(model.SlotArena[1], model.SlotArena[2]);
        Assert.NotEqual(model.SlotBody[0], model.SlotBody[1]);
    }

    [Fact]
    public void OverflowStaging_AllowsBeyondPingPong_UntilSoftDefer()
    {
        var model = new OwnershipModel();
        for (byte slot = 0; slot < ArenaCapacity; slot++)
        {
            var index = model.BeginBuild(slot, generation: 1);
            Assert.True(index >= 0, $"slot={slot} should allocate overflow arena");
            model.CompleteReady(index, slot, generation: 1);
        }

        Assert.Equal(ArenaCapacity, model.OccupiedArenaCount());
        var deferred = model.BeginBuild(slot: 0, generation: 2);
        Assert.Equal(-1, deferred);
        Assert.True(model.SoftDeferCount >= 1);
        Assert.False(model.TryReclaimUnreferencedReady());
        Assert.Equal(ArenaCapacity, model.OccupiedArenaCount());
    }

    [Fact]
    public void MidStageReclaim_NeverFrees_EvenWhenReadyFull()
    {
        var model = new OwnershipModel();

        var live = model.BeginBuild(slot: 1, generation: 1);
        model.ActivateFromPrewarm(live, slot: 1, generation: 1);

        var readyA = model.BeginBuild(slot: 2, generation: 1);
        Assert.True(readyA >= 0);
        model.CompleteReady(readyA, slot: 2, generation: 1);
        var readyB = model.BeginBuild(slot: 3, generation: 1);
        Assert.True(readyB >= 0);
        model.CompleteReady(readyB, slot: 3, generation: 1);

        Assert.False(model.TryReclaimUnreferencedReady());
        model.ReclaimRetired();
        Assert.Equal(ArenaState.Ready, model.Arenas[readyA].State);
        Assert.Equal(ArenaState.Ready, model.Arenas[readyB].State);
        Assert.False(model.Arenas[readyA].DestructorRan);
        Assert.False(model.Arenas[readyB].DestructorRan);

        // Overflow still admits without freeing parked ready graphs.
        var readyC = model.BeginBuild(slot: 1, generation: 2);
        Assert.True(readyC >= 0);
        Assert.NotEqual(readyA, readyC);
        Assert.NotEqual(readyB, readyC);
        model.CompleteReady(readyC, slot: 1, generation: 2);
        Assert.True(model.CommitReady(slot: 1, readyIndex: readyC));
        Assert.False(model.HasDuplicateArenaOwnership());
    }

    [Fact]
    public void StageRecycle_ReleasesTexBeforeFree_AndNeverTargetsLivePoolBody()
    {
        var model = new OwnershipModel();
        var live = model.BeginBuild(slot: 0, generation: 1);
        model.ActivateFromPrewarm(live, slot: 0, generation: 1);
        var liveBody = model.SlotBody[0];

        var ready = model.BeginBuild(slot: 0, generation: 2);
        model.CompleteReady(ready, slot: 0, generation: 2);

        model.Arenas[ready].BodyId = liveBody;
        model.StageRecycle();
        Assert.Equal(ArenaState.Ready, model.Arenas[ready].State);
        Assert.False(model.AnyReclaimTargetsLivePoolBody());

        model.Arenas[ready].BodyId = model.NextBodyId++;
        model.ModuleOwnedBodies.Add(model.Arenas[ready].BodyId!.Value);
        model.VariantOrReadyBodies.Add(model.Arenas[ready].BodyId!.Value);
        model.StageRecycle();
        Assert.True(model.Arenas[ready].State == ArenaState.Free);
        Assert.True(model.Arenas[ready].TexReleased);
        Assert.True(model.Arenas[ready].DestructorRan);
        Assert.Equal(liveBody, model.SlotBody[0]);
        Assert.False(model.AnyReclaimTargetsLivePoolBody());
        Assert.False(model.AnyFreeAllOfLivePoolArena());
    }

    [Fact]
    public void ExclusiveReadyClaim_SecondSlotCannotOwnSameBody()
    {
        var model = new OwnershipModel();
        var a0 = model.BeginBuild(slot: 1, generation: 1);
        model.ActivateFromPrewarm(a0, slot: 1, generation: 1);
        var a1 = model.BeginBuild(slot: 0, generation: 1);
        model.ActivateFromPrewarm(a1, slot: 0, generation: 1);

        var ready = model.BeginBuild(slot: 1, generation: 2);
        model.CompleteReady(ready, slot: 1, generation: 2);

        Assert.False(model.TryClaimReadyExclusive(slot: 0, readyIndex: ready));
        Assert.True(model.TryClaimReadyExclusive(slot: 1, readyIndex: ready));
        Assert.False(model.TryClaimReadyExclusive(slot: 0, readyIndex: ready));
        Assert.NotEqual(model.SlotBody[0], model.SlotBody[1]);
        Assert.False(model.HasDuplicateBodyOwnership());
    }

    [Fact]
    public void ExclusiveReadyClaim_UnownedPrewarm_FirstClaimerWins()
    {
        var model = new OwnershipModel();
        var a0 = model.BeginBuild(slot: 0, generation: 1);
        model.ActivateFromPrewarm(a0, slot: 0, generation: 1);
        var a1 = model.BeginBuild(slot: 1, generation: 1);
        model.ActivateFromPrewarm(a1, slot: 1, generation: 1);

        var build = model.BeginBuild(slot: 2, generation: 1);
        Assert.True(build >= 0);
        model.CompleteReadyUnowned(build, generation: 1);

        Assert.True(model.TryClaimReadyExclusive(slot: 0, readyIndex: build));
        Assert.False(model.TryClaimReadyExclusive(slot: 1, readyIndex: build));
        Assert.Equal(model.Arenas[build].BodyId, model.SlotBody[0]);
        Assert.NotEqual(model.SlotBody[0], model.SlotBody[1]);
        Assert.False(model.HasDuplicateBodyOwnership());
    }

    [Fact]
    public void ExclusiveReadyClaim_RefusesBodyAlreadyLiveInAnotherSlot()
    {
        var model = new OwnershipModel();
        var live = model.BeginBuild(slot: 1, generation: 1);
        model.ActivateFromPrewarm(live, slot: 1, generation: 1);
        var other = model.BeginBuild(slot: 0, generation: 1);
        model.ActivateFromPrewarm(other, slot: 0, generation: 1);

        var ready = model.BeginBuild(slot: 0, generation: 2);
        model.CompleteReady(ready, slot: 0, generation: 2);
        model.Arenas[ready].BodyId = model.SlotBody[1];

        Assert.False(model.TryClaimReadyExclusive(slot: 0, readyIndex: ready));
        Assert.False(model.HasDuplicateBodyOwnership());
    }

    [Fact]
    public void MidStage_NeverFreesDemotedLive_EvenAfterDelay()
    {
        var model = new OwnershipModel();
        var live = model.BeginBuild(slot: 1, generation: 1);
        model.ActivateFromPrewarm(live, slot: 1, generation: 1);
        var liveBody = model.SlotBody[1];

        var ready = model.BeginBuild(slot: 1, generation: 2);
        model.CompleteReady(ready, slot: 1, generation: 2);
        Assert.True(model.TryClaimReadyExclusive(slot: 1, readyIndex: ready));

        Assert.Equal(ArenaState.Retired, model.Arenas[live].State);
        Assert.True(model.Arenas[live].ViewDetached);
        Assert.True(model.Arenas[live].PlayerGroupDetached);
        Assert.DoesNotContain(liveBody!.Value, model.ViewListBodies);
        Assert.False(model.WouldCallRetailPerform(liveBody.Value, model.SlotBody[1]));

        while (model.ReclaimTick < model.Arenas[live].ReclaimAfterTick + 10)
            model.AdvanceReclaimTick();
        model.ReclaimRetired();
        Assert.False(model.TryReclaimUnreferencedReady());
        Assert.Equal(ArenaState.Retired, model.Arenas[live].State);
        Assert.Equal(liveBody, model.Arenas[live].BodyId);
        Assert.False(model.Arenas[live].DestructorRan);
    }

    [Fact]
    public void StageRecycle_FreesParkedReady_AfterHardDetach()
    {
        var model = new OwnershipModel();
        var live = model.BeginBuild(slot: 0, generation: 1);
        model.ActivateFromPrewarm(live, slot: 0, generation: 1);
        var body = model.SlotBody[0]!.Value;

        var ready = model.BeginBuild(slot: 0, generation: 2);
        model.CompleteReady(ready, slot: 0, generation: 2);
        Assert.True(model.TryClaimReadyExclusive(slot: 0, readyIndex: ready));

        Assert.DoesNotContain(body, model.ViewListBodies);
        Assert.True(model.Arenas[live].ViewDetached);
        Assert.True(model.Arenas[live].PlayerGroupDetached);
        Assert.Equal(ArenaState.Retired, model.Arenas[live].State);

        model.StageRecycle();
        Assert.Equal(ArenaState.Free, model.Arenas[live].State);
        Assert.True(model.Arenas[live].ViewDetached);
        Assert.True(model.Arenas[live].TexReleased);
        Assert.True(model.Arenas[live].DestructorRan);
        Assert.False(model.AnyFreeAllOfLivePoolArena());
    }

    [Fact]
    public void PerformOwnership_DemotedVariantNeverCallsRetailPerform()
    {
        var model = new OwnershipModel();
        var live = model.BeginBuild(slot: 1, generation: 1);
        model.ActivateFromPrewarm(live, slot: 1, generation: 1);
        var demoted = model.SlotBody[1]!.Value;

        var ready = model.BeginBuild(slot: 1, generation: 2);
        model.CompleteReady(ready, slot: 1, generation: 2);
        Assert.True(model.TryClaimReadyExclusive(slot: 1, readyIndex: ready));

        Assert.True(model.IsModuleOwnedRemote(demoted));
        Assert.Contains(demoted, model.VariantOrReadyBodies);
        Assert.False(model.WouldCallRetailPerform(demoted, model.SlotBody[1]));
        Assert.False(model.WouldCallRetailPerform(model.SlotBody[1]!.Value, model.SlotBody[1]));
    }

    [Fact]
    public void FiveSequentialSwaps_OverflowWithoutMidStageFreeAll()
    {
        var model = new OwnershipModel();
        var live = model.BeginBuild(slot: 0, generation: 1);
        model.ActivateFromPrewarm(live, slot: 0, generation: 1);

        for (uint gen = 2; gen <= 6; gen++)
        {
            var staging = model.BeginBuild(slot: 0, generation: gen);
            Assert.True(staging >= 0, $"gen={gen} should admit via overflow allocate (no freeAll)");
            model.CompleteReady(staging, slot: 0, generation: gen);
            Assert.True(model.TryClaimReadyExclusive(slot: 0, readyIndex: staging));
            Assert.False(model.AnyFreeAllOfLivePoolArena());
            Assert.False(model.TryReclaimUnreferencedReady());

            var demoted = model.Arenas.FirstOrDefault(a =>
                a.State == ArenaState.Retired && a.BodyId is not null);
            if (demoted?.BodyId is int demotedBody)
                Assert.False(model.WouldCallRetailPerform(demotedBody, model.SlotBody[0]));
        }

        Assert.True(model.OccupiedArenaCount() >= 6);
        Assert.False(model.HasDuplicateArenaOwnership());
        Assert.False(model.HasDuplicateBodyOwnership());
    }

    [Fact]
    public void MainHeapDemotion_ParksAsSpare_NeverAbandonsWithoutTeardown()
    {
        var model = new OwnershipModel();
        var prewarm = model.ActivateMainHeapPrewarm(slot: 0);
        Assert.Equal(NoArena, model.SlotArena[0]);

        var staging = model.BeginBuild(slot: 0, generation: 1);
        Assert.True(staging >= 0);
        model.CompleteReady(staging, slot: 0, generation: 1);
        Assert.True(model.TryClaimReadyExclusive(slot: 0, readyIndex: staging));

        Assert.Contains(prewarm, model.MainHeapSpares);
        Assert.True(model.IsModuleOwnedRemote(prewarm));
        Assert.False(model.WouldCallRetailPerform(prewarm, model.SlotBody[0]));
        Assert.False(model.AnyMainHeapAbandonedWithoutTeardown());
        Assert.False(model.AnyFreeAllOfLivePoolArena());
    }

    [Fact]
    public void MainHeapScrubWithoutTeardown_IsDetectedAsPolicyViolation()
    {
        var model = new OwnershipModel();
        var prewarm = model.ActivateMainHeapPrewarm(slot: 0);
        model.SlotBody[0] = null;
        model.SlotArena[0] = NoArena;
        model.ViewListBodies.Remove(prewarm);
        model.ScrubMainHeapWithoutTeardown(prewarm);
        Assert.True(model.AnyMainHeapAbandonedWithoutTeardown());
        Assert.False(model.IsModuleOwnedRemote(prewarm));
    }

    [Fact]
    public void MainHeapThenFiveArenaSwaps_NeverAbandonMainHeap_NoMidStageFree()
    {
        var model = new OwnershipModel();
        var prewarm = model.ActivateMainHeapPrewarm(slot: 0);

        for (uint gen = 1; gen <= 5; gen++)
        {
            var staging = model.BeginBuild(slot: 0, generation: gen);
            Assert.True(staging >= 0, $"gen={gen} must admit via overflow");
            model.CompleteReady(staging, slot: 0, generation: gen);
            Assert.True(model.TryClaimReadyExclusive(slot: 0, readyIndex: staging));
            Assert.False(model.AnyFreeAllOfLivePoolArena());
            Assert.False(model.AnyMainHeapAbandonedWithoutTeardown());
            Assert.False(model.TryReclaimUnreferencedReady());
        }

        Assert.Contains(prewarm, model.MainHeapSpares);
        Assert.True(model.IsModuleOwnedRemote(prewarm));
        Assert.False(model.WouldCallRetailPerform(prewarm, model.SlotBody[0]));
    }

    /// <summary>
    /// Regression (ModBuildId 57): a mount soft-fail that leaves the volume on
    /// retail while sSlots still points at a custom pack must NOT stamp
    /// isCustom / freeze first-residency. Authenticity requires the
    /// construction-time pack buffer to match the desired cached pack.
    /// </summary>
    [Fact]
    public void FirstResidency_FalseCustomStamp_RequiresRebuild()
    {
        // Mirrors applyRemoteBodyModelOnFirstResidency authenticity gate.
        static bool AuthenticCustom(
            bool packReady,
            bool poolIsCustom,
            IntPtr buildBuffer,
            IntPtr wantBuffer) =>
            packReady && poolIsCustom && wantBuffer != IntPtr.Zero &&
            buildBuffer == wantBuffer;

        var customPack = new IntPtr(0x818100A0);
        var retail = new IntPtr(0x8083E340);

        // Happy path: body was built under the desired pack.
        Assert.True(AuthenticCustom(
            packReady: true,
            poolIsCustom: true,
            buildBuffer: customPack,
            wantBuffer: customPack));

        // Soft-fail stamp: isCustom true but geometry came from retail mount.
        Assert.False(AuthenticCustom(
            packReady: true,
            poolIsCustom: true,
            buildBuffer: retail,
            wantBuffer: customPack));

        // Keep-alive before buildBuffer tracking: null build buf is untrusted.
        Assert.False(AuthenticCustom(
            packReady: true,
            poolIsCustom: true,
            buildBuffer: IntPtr.Zero,
            wantBuffer: customPack));

        // Mount reported success while volume sat on retail — same as null/mismatch.
        Assert.False(AuthenticCustom(
            packReady: true,
            poolIsCustom: true,
            buildBuffer: IntPtr.Zero,
            wantBuffer: customPack));
    }

    [Fact]
    public void MountBuffer_RetailFallback_MustReportFailureForCustomRequest()
    {
        // Mirrors mountBuffer contract after ModBuildId 57: when mountFixed(custom)
        // fails and retail is remounted for volume safety, the function returns
        // false so callers rebind sSlots to retail and do not stamp isCustom.
        static bool MountBufferReportsSuccess(bool requestedRetail, bool mountFixedOk,
            bool retailFallbackOk)
        {
            if (mountFixedOk)
                return true;
            if (!requestedRetail && retailFallbackOk)
                return false; // volume safe on retail; request failed
            return false;
        }

        Assert.True(MountBufferReportsSuccess(
            requestedRetail: false, mountFixedOk: true, retailFallbackOk: false));
        Assert.False(MountBufferReportsSuccess(
            requestedRetail: false, mountFixedOk: false, retailFallbackOk: true));
        Assert.False(MountBufferReportsSuccess(
            requestedRetail: true, mountFixedOk: false, retailFallbackOk: false));
    }

    /// <summary>
    /// Regression (ModBuildId 60/61/62): after ping-pong arenas are occupied, soft-defer
    /// on an early slot must not spend the shared construction budget — later
    /// slots still get a chance. Live body for the upgrading slot (retail or
    /// custom) may be destroyed one-for-one so a replacement can admit. Build 61+:
    /// if spawn cannot complete after reclaim, retail must be restored so the
    /// slot never stays body-null.
    /// </summary>
    [Fact]
    public void SoftDefer_DoesNotBlockLaterSlot_LiveRetailReclaimAllowsParentHeap()
    {
        // Mirrors prewarmRequestedCustomBodyStep + queueRequestedReadyBody budget.
        static bool TryQueue(bool admitOk, bool canReclaimLiveBody, bool spawnOk,
            out bool usedBudget, out bool softDeferred, out bool liveReclaimed,
            out bool retailRestored)
        {
            usedBudget = false;
            softDeferred = false;
            liveReclaimed = false;
            retailRestored = false;
            if (admitOk)
            {
                usedBudget = true;
                return true;
            }

            if (!canReclaimLiveBody)
            {
                softDeferred = true;
                usedBudget = false; // critical: soft-defer is not a spent initValues
                return false;
            }

            liveReclaimed = true;
            if (!spawnOk)
            {
                retailRestored = true; // ModBuildId 61: never leave pool/actor empty
                softDeferred = true;
                usedBudget = false;
                return false;
            }

            usedBudget = true; // parent-heap / arena one-for-one spawn
            return true;
        }

        // Slot 2 soft-defers (no live body to reclaim) — budget remains for slot 3.
        Assert.False(TryQueue(admitOk: false, canReclaimLiveBody: false, spawnOk: true,
            out var budget2, out var defer2, out var reclaim2, out var restore2));
        Assert.True(defer2);
        Assert.False(budget2);
        Assert.False(reclaim2);
        Assert.False(restore2);

        Assert.True(TryQueue(admitOk: false, canReclaimLiveBody: true, spawnOk: true,
            out var budget3, out var defer3, out var reclaim3, out var restore3));
        Assert.False(defer3);
        Assert.True(budget3);
        Assert.True(reclaim3);
        Assert.False(restore3);

        // Build 61: reclaim + failed spawn restores retail (no empty slot).
        Assert.False(TryQueue(admitOk: false, canReclaimLiveBody: true, spawnOk: false,
            out var budget4, out var defer4, out var reclaim4, out var restore4));
        Assert.True(defer4);
        Assert.False(budget4);
        Assert.True(reclaim4);
        Assert.True(restore4);
    }

    /// <summary>
    /// Regression (ModBuildId 62): one-for-one reclaim is decided by whether THIS
    /// slot has a live body to destroy — not by pre-reclaim bodyFree. Destroying
    /// the upgrading slot's retail (~612 KiB) or custom/arena is what creates the
    /// admit hole; build 61's pre-reclaim hopeless gate blocked late joins.
    /// </summary>
    [Fact]
    public void LiveReclaim_AttemptsWheneverSlotHasLiveBody()
    {
        static bool ShouldReclaim(bool hasLiveBodyForSlot, uint bodyFree) =>
            hasLiveBodyForSlot; // bodyFree is informational only after ModBuildId 62

        Assert.True(ShouldReclaim(hasLiveBodyForSlot: true, bodyFree: 685552));
        Assert.True(ShouldReclaim(hasLiveBodyForSlot: true, bodyFree: 117476)); // late join
        Assert.True(ShouldReclaim(hasLiveBodyForSlot: true, bodyFree: 655424)); // custom→custom
        Assert.True(ShouldReclaim(hasLiveBodyForSlot: true, bodyFree: 75716));
        Assert.False(ShouldReclaim(hasLiveBodyForSlot: false, bodyFree: 702592));
    }

    /// <summary>
    /// Regression (ModBuildId 62): custom / arena-backed live bodies must be
    /// reclaimable for mid-session model changes (A→B). Build 61 refused both.
    /// </summary>
    [Fact]
    public void LiveReclaim_AllowsCustomAndArenaBackedBodies()
    {
        static bool CanReclaimLiveBody(bool hasBody, bool isCustom, bool hasArena) =>
            hasBody; // identity / arena no longer block one-for-one upgrade

        Assert.True(CanReclaimLiveBody(hasBody: true, isCustom: false, hasArena: false));
        Assert.True(CanReclaimLiveBody(hasBody: true, isCustom: true, hasArena: false));
        Assert.True(CanReclaimLiveBody(hasBody: true, isCustom: true, hasArena: true));
        Assert.False(CanReclaimLiveBody(hasBody: false, isCustom: true, hasArena: true));
    }

    // Body-heap budget mirrored from module/src/remote_actor.cpp: the expanded
    // MEM1 arena is split 16.5 MiB packs / 7.375 MiB bodies, one TMario graph
    // costs ~612 KiB, and a staging arena for a replacement graph costs 768 KiB.
    private const int BodyHeapBytes = 0x0076_0000;
    private const int BodyGraphBytes = 612 * 1024;
    private const int StagingArenaBytes = 0x000C_0000;

    /// <summary>
    /// Counts how many of <paramref name="remoteCount"/> remotes end up wearing
    /// their own pack when prewarm builds every puppet under retail before the
    /// packs publish. Each upgrade must construct a second graph inside a staging
    /// arena while the retail graph stays resident — mid-stage never frees a
    /// TMario — so the heap runs out long before every remote is served.
    /// </summary>
    private static int RemotesWearingTheirPack(int remoteCount, bool prewarmUnderRetail)
    {
        var free = BodyHeapBytes;
        if (!prewarmUnderRetail)
        {
            // Build 56: prewarm waits for the pack, so each remote costs one graph.
            var served = 0;
            while (served < remoteCount && free >= BodyGraphBytes)
            {
                free -= BodyGraphBytes;
                served++;
            }
            return served;
        }

        var prewarmed = 0;
        while (prewarmed < remoteCount && free >= BodyGraphBytes)
        {
            free -= BodyGraphBytes;
            prewarmed++;
        }

        // Ping-pong staging arenas are carved at stage start, then overflow
        // arenas are created per additional identity.
        var upgraded = 0;
        for (var i = 0; i < PingPongArenaCount && upgraded < prewarmed; i++)
        {
            if (free < StagingArenaBytes)
                break;
            free -= StagingArenaBytes;
            upgraded++;
        }
        while (upgraded < prewarmed && free >= StagingArenaBytes)
        {
            free -= StagingArenaBytes;
            upgraded++;
        }
        return upgraded;
    }

    /// <summary>
    /// Regression (ModBuildId 56): build 51 raised baseline prewarm to all
    /// MaxPlayers-1 puppets, but prewarm ran before pack prefetch published and
    /// therefore built them under the retail mario volume. Since mid-stage never
    /// frees a TMario, those nine graphs left too little body heap for the
    /// replacement graphs, and only the first couple of remotes ever received
    /// their custom pack. Waiting for the pack fits all nine.
    /// </summary>
    [Fact]
    public void FullLobbyDistinctPacks_RetailPrewarmStarvesUpgrades_PackGatedPrewarmDoesNot()
    {
        const int remotes = MaxRemoteSlots - 1;

        var retailFirst = RemotesWearingTheirPack(remotes, prewarmUnderRetail: true);
        Assert.True(retailFirst < remotes,
            "build 51 retail-first prewarm must be shown to starve custom upgrades");
        Assert.True(retailFirst <= PingPongArenaCount + 1);

        Assert.Equal(remotes, RemotesWearingTheirPack(remotes, prewarmUnderRetail: false));
    }

    private enum PrewarmAction
    {
        SkipEmptySlot,
        WaitForPack,
        BuildCustom,
        BuildRetail,
    }

    /// <summary>Mirrors prewarmRemoteBodyPoolStep's per-slot decision.</summary>
    private static PrewarmAction DecidePrewarm(bool connected, bool announcedCustom,
        bool packReady, int framesWaited, int deadline)
    {
        if (!connected && !announcedCustom)
            return PrewarmAction.SkipEmptySlot;
        if (!announcedCustom)
            return PrewarmAction.BuildRetail;
        if (packReady)
            return PrewarmAction.BuildCustom;
        return framesWaited < deadline ? PrewarmAction.WaitForPack : PrewarmAction.BuildRetail;
    }

    /// <summary>
    /// Regression (ModBuildId 56): prewarm never spends a body graph on a slot
    /// that has no player, and skips (rather than consumes) a slot whose
    /// announced pack is not ready yet. The wait is bounded so a pack that never
    /// publishes still yields a visible body.
    /// </summary>
    [Fact]
    public void PrewarmPackGate_SkipsEmptySlots_DefersPendingPacks_FallsBackAfterDeadline()
    {
        const int deadline = 900;

        // Empty slot: a speculative retail graph would steal the eventual
        // occupant's RAM and could never be freed mid-stage.
        Assert.Equal(PrewarmAction.SkipEmptySlot,
            DecidePrewarm(connected: false, announcedCustom: false, packReady: false, 0, deadline));
        // Connected player genuinely on retail Mario.
        Assert.Equal(PrewarmAction.BuildRetail,
            DecidePrewarm(connected: true, announcedCustom: false, packReady: false, 0, deadline));
        Assert.Equal(PrewarmAction.BuildCustom,
            DecidePrewarm(connected: true, announcedCustom: true, packReady: true, 0, deadline));
        // Announced before the first snapshot — still occupied.
        Assert.Equal(PrewarmAction.BuildCustom,
            DecidePrewarm(connected: false, announcedCustom: true, packReady: true, 0, deadline));
        Assert.Equal(PrewarmAction.WaitForPack,
            DecidePrewarm(connected: true, announcedCustom: true, packReady: false, 0, deadline));
        Assert.Equal(PrewarmAction.WaitForPack,
            DecidePrewarm(connected: true, announcedCustom: true, packReady: false, deadline - 1, deadline));
        // Deadline reached: build retail so the player is never bodyless.
        Assert.Equal(PrewarmAction.BuildRetail,
            DecidePrewarm(connected: true, announcedCustom: true, packReady: false, deadline, deadline));
    }

    /// <summary>
    /// Regression (ModBuildId 56): a ten-player lobby with ten distinct packs must
    /// end up with exactly one graph per remote — no wasted retail graph, and no
    /// staging arena, because every body is built under the right archive first.
    /// </summary>
    [Fact]
    public void FullLobbyDistinctPacks_BuildsOneGraphPerRemote_WithinBodyHeap()
    {
        const int remotes = MaxRemoteSlots - 1;
        const int deadline = 900;

        var graphs = 0;
        for (var slot = 0; slot < remotes; slot++)
        {
            // Packs publish one at a time; prewarm revisits the slot until then.
            Assert.Equal(PrewarmAction.WaitForPack,
                DecidePrewarm(connected: true, announcedCustom: true, packReady: false, 0, deadline));
            Assert.Equal(PrewarmAction.BuildCustom,
                DecidePrewarm(connected: true, announcedCustom: true, packReady: true, 1, deadline));
            graphs++;
        }

        Assert.Equal(remotes, graphs);
        Assert.True(graphs * BodyGraphBytes <= BodyHeapBytes);
        // Headroom must remain for mid-session model changes (staging arenas).
        Assert.True(BodyHeapBytes - graphs * BodyGraphBytes >= StagingArenaBytes * PingPongArenaCount);
    }

    /// <summary>
    /// Regression (ModBuildId 56): a slot whose pack is already resident must not
    /// be handed another slot's retail spare — that forces the replace-and-abandon
    /// upgrade. Adopting a parked graph with a matching identity is always free.
    /// </summary>
    [Fact]
    public void AcquirePoolBody_PrefersMatchingSpare_RefusesRetailSpareWhenPackReady()
    {
        // Mirrors acquirePoolBodyForSlot after the build 56 gate.
        static string Acquire(bool ownPoolBodyFree, bool matchingSpare, bool wantsCustom,
            bool packReady) =>
            ownPoolBodyFree ? "own"
            : matchingSpare ? "adopt"
            : wantsCustom && packReady ? "build-correct"
            : "retail-spare";

        Assert.Equal("own", Acquire(true, false, true, true));
        Assert.Equal("adopt", Acquire(false, true, true, true));
        Assert.Equal("build-correct", Acquire(false, false, true, true));
        // Pack still loading: a retail spare keeps the player visible now.
        Assert.Equal("retail-spare", Acquire(false, false, true, false));
        Assert.Equal("retail-spare", Acquire(false, false, false, true));
    }

    /// <summary>
    /// Regression (ModBuildId 56): demoted main-heap graphs record the identity
    /// they were built under so a later slot can adopt them. Without the identity
    /// the spare table is write-only and every stage leaks its demoted graphs.
    /// </summary>
    [Fact]
    public void MainHeapSpare_IsAdoptableByIdentity()
    {
        static bool CanAdopt(string spareId, bool spareIsCustom, string wantedId) =>
            spareId == wantedId && spareIsCustom == (wantedId.Length > 0);

        Assert.True(CanAdopt("", false, ""));
        Assert.True(CanAdopt("841192a3", true, "841192a3"));
        Assert.False(CanAdopt("841192a3", true, "36de327c"));
        // Soft-failed custom mount left retail geometry — not the wanted identity.
        Assert.False(CanAdopt("841192a3", false, "841192a3"));
    }
}

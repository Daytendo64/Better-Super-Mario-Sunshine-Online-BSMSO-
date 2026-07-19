namespace SMSO.Tests;

/// <summary>
/// Host-side regression coverage for the remote body-arena ownership policy
/// mirrored by module/src/remote_actor.cpp. Double-assignment of one child
/// arena to two network slots must be impossible by construction.
/// Perform ownership: demoted/parked/ready graphs are module-owned and must
/// never fall through to retail TMario::perform.
/// Mid-stage reclaim never freeAlls / ~TMario — only stage-boundary recycle
/// tears down parked graphs. Soft-defer when the arena pool (heap stand-in)
/// cannot allocate another child. Main-heap prewarm graphs park as spares.
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
}

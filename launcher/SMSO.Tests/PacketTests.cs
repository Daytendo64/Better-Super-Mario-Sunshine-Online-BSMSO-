using System.Buffers.Binary;
using SMSO.Net;
using SMSO.Net.MarioPack;

namespace SMSO.Tests;

public class PacketTests
{
    [Fact]
    public void TcpWrap_Unwrap_RoundTrip()
    {
        var payload = new byte[] { 1, 2, 3 };
        var frame = PacketSerializer.WrapTcp(TcpPacketId.Heartbeat, payload);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var restored));
        Assert.Equal(TcpPacketId.Heartbeat, id);
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void TcpUnwrap_RejectsCorruptCrc()
    {
        var frame = PacketSerializer.WrapTcp(TcpPacketId.Heartbeat, new byte[] { 1, 2, 3 });
        frame[^1] ^= 0xFF;

        Assert.False(PacketSerializer.TryUnwrapTcp(frame, out _, out _));
    }

    [Fact]
    public void Crc32_IsDeterministic()
    {
        var data = new byte[] { 0x53, 0x4D, 0x53, 0x4F };
        Assert.Equal(Crc32.Compute(data), Crc32.Compute(data));
    }

    [Fact]
    public void SnapshotBytes_RoundTrip_PreservesFields()
    {
        var snap = new PlayerSnapshot
        {
            Position = new Vec3 { X = 1.25f, Y = -2.5f, Z = 3.75f },
            Velocity = new Vec3 { X = 4.5f, Y = -5.5f, Z = 6.5f },
            RotationY = 12345f,
            AnimId = 0x1234,
            NozzleId = 0x45,
            Water = 200,
            Health = 0x3C,
            StageId = 7,
            EpisodeId = 2,
            MovementState = 0x91,
            ActionId = 0xBEEF,
            VfxFlags = 0x01FF,
            Connected = 1,
            Slot = 9,
            PingMs = 0xCAFE,
            Name = new byte[16],
            AnimFrame = 0x2222,
            ActionIdHi = 0x3333,
        };
        snap.SetNameTagAppearance(12, 34, 56, 90, 100, 110, 0, 0, 0, gradientEnabled: false);

        var restored = PacketSerializer.SnapshotFromBytes(PacketSerializer.SnapshotToBytes(snap));

        Assert.Equal(snap.Position.X, restored.Position.X);
        Assert.Equal(snap.Velocity.Z, restored.Velocity.Z);
        Assert.Equal(snap.RotationY, restored.RotationY);
        Assert.Equal(snap.AnimId, restored.AnimId);
        Assert.Equal(snap.NozzleId, restored.NozzleId);
        Assert.Equal(snap.VfxFlags, restored.VfxFlags);
        Assert.True(NameTagColorCodec.TryDecodeAppearance(restored.Name, out var appearance));
        Assert.Equal(12, appearance.TextTopR);
        Assert.Equal(NameTagColorCodec.ExtendedMarker, restored.Name[15]);
        Assert.Equal(snap.ActionIdHi, restored.ActionIdHi);
    }

    [Fact]
    public void SnapshotFromBytes_ReusesProvidedNameBufferWithoutPerPacketAllocation()
    {
        var snap = new PlayerSnapshot { Name = new byte[16], Connected = 1, Slot = 3 };
        snap.SetName("Mario");
        var payload = PacketSerializer.SnapshotToBytes(snap);
        var nameBuffer = new byte[16];

        _ = PacketSerializer.SnapshotFromBytes(payload, nameBuffer);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        PlayerSnapshot restored = default;
        for (int i = 0; i < 1_000; i++)
            restored = PacketSerializer.SnapshotFromBytes(payload, nameBuffer);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Same(nameBuffer, restored.Name);
        Assert.Equal("Mario", restored.GetName());
        Assert.True(allocated <= 128, $"Reusable snapshot decode allocated {allocated} bytes.");
    }

    [Fact]
    public void UdpSnapshotBatch_RoundTripsIndependentSequencesAndNames()
    {
        var first = new PlayerSnapshot { Connected = 1, Slot = 2, Name = new byte[16] };
        first.SetName("Mario");
        first.Position = new Vec3 { X = 10, Y = 20, Z = 30 };
        var second = new PlayerSnapshot { Connected = 1, Slot = 7, Name = new byte[16] };
        second.SetName("Luigi");
        second.AnimId = 0x48;

        var batch = new byte[ProtocolConstants.UdpSnapshotBatchMaxSize];
        PacketSerializer.WriteUdpSnapshotBatchHeader(batch, 2);
        PacketSerializer.WriteUdpSnapshotBatchEntry(batch, 0, 2, 100, first);
        PacketSerializer.WriteUdpSnapshotBatchEntry(batch, 1, 7, 205, second);
        var length = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                     2 * ProtocolConstants.UdpSnapshotBatchEntrySize;

        Assert.True(PacketSerializer.TryReadUdpSnapshotBatchEntry(
            batch.AsSpan(0, length), 0, new byte[16],
            out var firstSlot, out var firstSeq, out var firstRestored));
        Assert.True(PacketSerializer.TryReadUdpSnapshotBatchEntry(
            batch.AsSpan(0, length), 1, new byte[16],
            out var secondSlot, out var secondSeq, out var secondRestored));

        Assert.Equal((byte)2, firstSlot);
        Assert.Equal(100u, firstSeq);
        Assert.Equal("Mario", firstRestored.GetName());
        Assert.Equal(10f, firstRestored.Position.X);
        Assert.Equal((byte)7, secondSlot);
        Assert.Equal(205u, secondSeq);
        Assert.Equal("Luigi", secondRestored.GetName());
        Assert.Equal((ushort)0x48, secondRestored.AnimId);
    }

    [Fact]
    public void UdpSnapshotBatch_RejectsTruncatedPayload()
    {
        var batch = new byte[ProtocolConstants.UdpSnapshotBatchHeaderSize];
        PacketSerializer.WriteUdpSnapshotBatchHeader(batch, 1);

        Assert.False(PacketSerializer.TryReadUdpSnapshotBatchEntry(
            batch, 0, new byte[16], out _, out _, out _));
    }

    [Fact]
    public void UdpSnapshotBatch_InvalidEntryDoesNotDiscardLaterFixedEntry()
    {
        var malformed = new PlayerSnapshot { Connected = 1, Slot = byte.MaxValue, Name = new byte[16] };
        malformed.SetName("Bad");
        var valid = new PlayerSnapshot { Connected = 1, Slot = 2, Name = new byte[16] };
        valid.SetName("StillHere");
        valid.Position = new Vec3 { X = 42, Y = 7, Z = -3 };

        var batch = new byte[ProtocolConstants.UdpSnapshotBatchHeaderSize +
                             2 * ProtocolConstants.UdpSnapshotBatchEntrySize];
        PacketSerializer.WriteUdpSnapshotBatchHeader(batch, 2);
        PacketSerializer.WriteUdpSnapshotBatchEntry(batch, 0, byte.MaxValue, 10, malformed);
        PacketSerializer.WriteUdpSnapshotBatchEntry(batch, 1, 2, 11, valid);

        using var client = new NetClient();
        var received = new List<(byte Slot, PlayerSnapshot Snapshot)>();
        client.SnapshotReceived += (slot, snapshot) => received.Add((slot, snapshot));

        client.HandleUdpSnapshotBatch(batch);

        var item = Assert.Single(received);
        Assert.Equal((byte)2, item.Slot);
        Assert.Equal("StillHere", item.Snapshot.GetName());
        Assert.Equal(42f, item.Snapshot.Position.X);
    }

    [Fact]
    public void UdpSnapshotBatch_TruncationDoesNotPublishPartialPrefix()
    {
        var snapshot = new PlayerSnapshot { Connected = 1, Slot = 2, Name = new byte[16] };
        var full = new byte[ProtocolConstants.UdpSnapshotBatchHeaderSize +
                            2 * ProtocolConstants.UdpSnapshotBatchEntrySize];
        PacketSerializer.WriteUdpSnapshotBatchHeader(full, 2);
        PacketSerializer.WriteUdpSnapshotBatchEntry(full, 0, 2, 1, snapshot);

        using var client = new NetClient();
        var received = 0;
        client.SnapshotReceived += (_, _) => received++;

        client.HandleUdpSnapshotBatch(
            full.AsSpan(0, ProtocolConstants.UdpSnapshotBatchHeaderSize +
                           ProtocolConstants.UdpSnapshotBatchEntrySize));

        Assert.Equal(0, received);
    }

    [Fact]
    public void MarioVoiceEvent_RoundTrip_PreservesFields()
    {
        var voiceEvent = new MarioVoiceEvent
        {
            SoundId = 0x000078B6,
            Sequence = 7,
            Flags = 1,
            Health = 2,
            StageId = 3,
            EpisodeId = 1,
        };

        var frame = PacketSerializer.BuildMarioVoiceEvent(4, voiceEvent);

        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.MarioVoiceEvent, id);
        Assert.True(PacketSerializer.TryReadMarioVoiceEvent(payload, out var slot, out var restored));
        Assert.Equal(4, slot);
        Assert.Equal(voiceEvent.SoundId, restored.SoundId);
        Assert.Equal(voiceEvent.Sequence, restored.Sequence);
        Assert.Equal(voiceEvent.Health, restored.Health);
        Assert.Equal(voiceEvent.StageId, restored.StageId);
        Assert.Equal(voiceEvent.EpisodeId, restored.EpisodeId);
    }

    [Fact]
    public void WorldEventRequest_RoundTrip_PreservesFields()
    {
        var request = new WorldEventRequest(42, WorldEventType.ShineCollected, 5, 2, 17, 3, 0);

        var frame = PacketSerializer.BuildWorldEventRequest(request);

        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldEvent, id);
        Assert.True(PacketSerializer.TryReadWorldEventRequest(payload, out var restored));
        Assert.Equal(42, restored.Sequence);
        Assert.Equal(WorldEventType.ShineCollected, restored.Type);
        Assert.Equal((byte)5, restored.CourseId);
        Assert.Equal((byte)17, restored.Payload0);
        Assert.Equal((byte)3, restored.Reserved);
    }

    [Fact]
    public void WorldEventBroadcast_RoundTrip_PreservesFields()
    {
        var packet = new WorldEventPacket(99, WorldEventType.GoldCoinCollected, 3, 1, 0, 0, 42);

        var frame = PacketSerializer.BuildWorldEventBroadcast(packet);

        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldEvent, id);
        Assert.True(PacketSerializer.TryReadWorldEventBroadcast(payload, out var restored));
        Assert.Equal(99u, restored.EventId);
        Assert.Equal(WorldEventType.GoldCoinCollected, restored.Type);
        Assert.Equal(42u, restored.Payload1);
    }

    [Fact]
    public void WorldEventBroadcast_RedCoinEvents_RoundTrip()
    {
        var coinEvent = new WorldEventPacket(101, WorldEventType.RedCoinCollected, 2, 3, 0x24, 2, 0x205);
        var coinFrame = PacketSerializer.BuildWorldEventBroadcast(coinEvent);
        Assert.True(PacketSerializer.TryUnwrapTcp(coinFrame, out _, out var coinPayload));
        Assert.True(PacketSerializer.TryReadWorldEventBroadcast(coinPayload, out var restoredCoin));
        Assert.Equal(WorldEventType.RedCoinCollected, restoredCoin.Type);
        Assert.Equal((byte)0x24, restoredCoin.Payload0);
        Assert.Equal((byte)2, restoredCoin.Reserved);
        Assert.Equal(0x205u, restoredCoin.Payload1);

        var shineEvent = new WorldEventPacket(102, WorldEventType.ShineCollected, 1, 2, 117, 0, 0xABCDEF);
        var shineFrame = PacketSerializer.BuildWorldEventBroadcast(shineEvent);
        Assert.True(PacketSerializer.TryUnwrapTcp(shineFrame, out _, out var shinePayload));
        Assert.True(PacketSerializer.TryReadWorldEventBroadcast(shinePayload, out var restoredShine));
        Assert.Equal((byte)117, restoredShine.Payload0);
        Assert.Equal((byte)0, restoredShine.Reserved);
        Assert.Equal(0xABCDEFu, restoredShine.Payload1);
    }

    [Fact]
    public void WorldEventBroadcast_HipDropObject_RoundTrip()
    {
        // payload0 = object mMapObjID, reserved = pounder slot, payload1 = packed world pos.
        var pound = new WorldEventPacket(7, WorldEventType.HipDropObject, 2, 1, 0x3A, 4, 0x123456);

        var frame = PacketSerializer.BuildWorldEventBroadcast(pound);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadWorldEventBroadcast(payload, out var restored));

        Assert.Equal(WorldEventType.HipDropObject, restored.Type);
        Assert.Equal((byte)0x3A, restored.Payload0);
        Assert.Equal((byte)4, restored.Reserved);
        Assert.Equal(0x123456u, restored.Payload1);
    }

    [Fact]
    public void WorldEventBroadcast_NpcReact_RoundTrip()
    {
        // payload0 = reaction kind (1=wet), reserved = actor slot, payload1 = packed NPC pos,
        // payload2 = retail message id (HIT_MESSAGE_SPRAYED_BY_WATER = 0xF).
        var react = new WorldEventPacket(16, WorldEventType.NpcReact, 1, 0, 1, 3, 0xABCDEF, 0xF);

        var frame = PacketSerializer.BuildWorldEventBroadcast(react);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadWorldEventBroadcast(payload, out var restored));

        Assert.Equal(WorldEventType.NpcReact, restored.Type);
        Assert.Equal((byte)1, restored.CourseId);
        Assert.Equal((byte)0, restored.EpisodeId);
        Assert.Equal((byte)1, restored.Payload0);
        Assert.Equal((byte)3, restored.Reserved);
        Assert.Equal(0xABCDEFu, restored.Payload1);
        Assert.Equal(0xFu, restored.Payload2);
    }

    [Fact]
    public void WorldEventRequest_NpcReact_PreservesActingSlot()
    {
        // reserved = acting slot must survive request encode/decode so server dedup can key on it.
        var request = new WorldEventRequest(22, WorldEventType.NpcReact, 1, 0, 1, 0, 0x112233u, 0xFu);
        var frame = PacketSerializer.BuildWorldEventRequest(request);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadWorldEventRequest(payload, out var restored));

        Assert.Equal(WorldEventType.NpcReact, restored.Type);
        Assert.Equal((byte)1, restored.Payload0);
        Assert.Equal((byte)0, restored.Reserved);
        Assert.Equal(0x112233u, restored.Payload1);
        Assert.Equal(0xFu, restored.Payload2);
    }

    [Fact]
    public void WorldEventBroadcast_MarioFruitActions_RoundTrip()
    {
        var kicked = new WorldEventPacket(8, WorldEventType.MarioFruitKicked, 3, 2, 4, 1, 0xABCDEF);
        var kickedFrame = PacketSerializer.BuildWorldEventBroadcast(kicked);
        Assert.True(PacketSerializer.TryUnwrapTcp(kickedFrame, out _, out var kickedPayload));
        Assert.True(PacketSerializer.TryReadWorldEventBroadcast(kickedPayload, out var restoredKick));
        Assert.Equal(WorldEventType.MarioFruitKicked, restoredKick.Type);
        Assert.Equal((byte)4, restoredKick.Payload0);
        Assert.Equal((byte)1, restoredKick.Reserved);

        var thrown = new WorldEventPacket(9, WorldEventType.MarioFruitThrown, 3, 2, 2, 1, 0x123456);
        var thrownFrame = PacketSerializer.BuildWorldEventBroadcast(thrown);
        Assert.True(PacketSerializer.TryUnwrapTcp(thrownFrame, out _, out var thrownPayload));
        Assert.True(PacketSerializer.TryReadWorldEventBroadcast(thrownPayload, out var restoredThrow));
        Assert.Equal(WorldEventType.MarioFruitThrown, restoredThrow.Type);
    }

    [Fact]
    public void WorldStateReplay_RoundTrip_PreservesEvents()
    {
        var events = new[]
        {
            new WorldEventPacket(1, WorldEventType.RedCoinCollected, 2, 3, 0x14, 2, 0x205),
            new WorldEventPacket(2, WorldEventType.ShineCollected, 2, 3, 17, 0, 0xABCDEF),
            new WorldEventPacket(3, WorldEventType.BlueCoinCollected, 2, 3, 5, 0, 0),
        };

        var payload = new byte[2 + events.Length * ProtocolConstants.WorldEventBroadcastPayloadSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)events.Length);
        var offset = 2;
        foreach (var packet in events)
        {
            var frame = PacketSerializer.BuildWorldEventBroadcast(packet);
            Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var eventPayload));
            eventPayload.AsSpan().CopyTo(payload.AsSpan(offset));
            offset += ProtocolConstants.WorldEventBroadcastPayloadSize;
        }

        Assert.True(PacketSerializer.TryReadWorldStateReplay(payload, out var restored));
        Assert.Equal(events.Length, restored.Length);
        Assert.Equal(WorldEventType.RedCoinCollected, restored[0].Type);
        Assert.Equal((byte)17, restored[1].Payload0);
        Assert.Equal((byte)0, restored[1].Reserved);
        Assert.Equal(0xABCDEFu, restored[1].Payload1);
        Assert.Equal((byte)5, restored[2].Payload0);
    }

    [Fact]
    public void WorldProgressSnapshot_RoundTrip_PreservesBitsets()
    {
        var shineBits = new byte[WorldProgressSnapshot.ShineBitsByteCount];
        WorldProgressSnapshot.SetShineBit(shineBits, 7);
        WorldProgressSnapshot.SetShineBit(shineBits, 120);
        WorldProgressSnapshot.SetShineBit(shineBits, 200);
        WorldProgressSnapshot.SetShineBit(shineBits, 255);
        var snapshot = new WorldProgressSnapshot
        {
            ProgressSeq = 99,
            ShineBits = shineBits,
            BlueCourses = new[] { ((byte)2, 1ul << 5) },
            StoryFlags = new[] { (0x10384u, (byte)1) },
            TriggerFlags = new[] { ((byte)1, (byte)0xFF, 0x50001u, (byte)1) },
            SecretFlags = new[] { (0x10390u, (byte)1) },
            RedStages = new[]
            {
                new WorldProgressSnapshot.RedStageMask(3, 4, 0x05,
                    new uint[] { 0x111, 0, 0x222, 0, 0, 0, 0, 0 }),
            },
            NpcCleanStages = new[] { ((byte)8, (byte)5, (ushort)0x0008) },
        };

        var frame = PacketSerializer.BuildWorldProgressSnapshot(snapshot);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.WorldProgressSnapshot, id);
        Assert.True(PacketSerializer.TryReadWorldProgressSnapshot(payload, out var restored));
        Assert.Equal(99u, restored.ProgressSeq);
        Assert.Equal(WorldProgressSnapshot.FormatVersion, payload[0]);
        Assert.Equal(WorldProgressSnapshot.ShineBitsByteCount, restored.ShineBits.Length);
        Assert.True(WorldProgressSnapshot.TestBit(restored.ShineBits, 7));
        Assert.True(WorldProgressSnapshot.TestBit(restored.ShineBits, 120));
        Assert.True(WorldProgressSnapshot.TestBit(restored.ShineBits, 200));
        Assert.True(WorldProgressSnapshot.TestBit(restored.ShineBits, 255));
        Assert.Single(restored.BlueCourses);
        Assert.Equal(2, restored.BlueCourses[0].CourseId);
        Assert.Equal(1ul << 5, restored.BlueCourses[0].Mask);
        Assert.Equal(0x10384u, restored.StoryFlags[0].FlagId);
        Assert.Equal(0x50001u, restored.TriggerFlags[0].FlagId);
        Assert.Equal((byte)0xFF, restored.TriggerFlags[0].EpisodeId);
        Assert.Equal(0x05, restored.RedStages[0].Mask);
        Assert.Equal(0x111u, restored.RedStages[0].PackedPos[0]);
        Assert.Equal((ushort)0x0008, restored.NpcCleanStages[0].Mask);

        var unchanged = PacketSerializer.BuildWorldProgressSnapshot(
            WorldProgressSnapshot.CreateUnchanged(99));
        Assert.True(PacketSerializer.TryUnwrapTcp(unchanged, out _, out var unchangedPayload));
        Assert.True(PacketSerializer.TryReadWorldProgressSnapshot(unchangedPayload, out var u));
        Assert.True(u.Unchanged);
        Assert.Equal(99u, u.ProgressSeq);
    }

    [Fact]
    public void ClientTeleportSettings_RoundTrip()
    {
        var frame = PacketSerializer.BuildClientTeleportSettings(true);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.ClientTeleportSettings, id);
        Assert.Single(payload);
        Assert.Equal(1, payload[0]);
    }

    [Fact]
    public void NameTagAppearance_RoundTrip_WithGradient()
    {
        var name = new byte[16];
        NameTagColorCodec.SetNameTagAppearance(name, 255, 128, 64, 16, 32, 48, 0, 0, 0, gradientEnabled: true);

        Assert.True(NameTagColorCodec.TryReadTextTopColor(name, out var tr, out var tg, out var tb));
        Assert.True(NameTagColorCodec.TryReadTextBottomColor(name, out var br, out var bg, out var bb));
        Assert.True(NameTagColorCodec.TryReadOutlineColor(name, out var or, out var og, out var ob));
        Assert.True(NameTagColorCodec.IsGradientEnabled(name));
        Assert.Equal(255, tr);
        Assert.Equal(16, br);
        Assert.Equal(0, or);
        Assert.Equal(NameTagColorCodec.GradientMarker, name[15]);
    }

    [Fact]
    public void WireAppearance_DecodeThenSetName_KeepsColorsAndFullName()
    {
        var wire = new PlayerSnapshot { Name = new byte[16] };
        wire.SetName("Player");
        wire.SetNameTagAppearance(210, 11, 22, 33, 44, 55, 1, 2, 3, gradientEnabled: true);

        Assert.True(NameTagColorCodec.TryDecodeAppearance(wire.Name, out var appearance));
        Assert.Equal(210, appearance.TextTopR);
        Assert.Equal(33, appearance.TextBottomR);
        Assert.True(appearance.GradientEnabled);

        // Receiver strips overlay for display while keeping decoded sidecar colors.
        wire.SetName("Player");
        Assert.Equal("Player", wire.GetName());
        Assert.False(NameTagColorCodec.HasAppearanceMarker(wire.Name[15]));
        Assert.Equal(210, appearance.TextTopR);
    }

    [Fact]
    public void GetPureName_RespectsLegacyGradientTextLimit()
    {
        var snap = new PlayerSnapshot { Name = new byte[16] };
        snap.SetName("Player");
        snap.SetNameTagAppearance(10, 20, 30, 40, 50, 60, 1, 2, 3, gradientEnabled: true);

        // Gradient overlay overwrites bytes 5+; pure text must stop at 5 chars.
        Assert.Equal("Playe", snap.GetPureName());
        Assert.Equal(NameTagColorCodec.NameTextBytesWithGradient,
            NameTagColorCodec.GetNameTextByteLimit(NameTagColorCodec.GradientMarker));
    }

    [Fact]
    public void SetName_AfterAppearance_RestoresFullDisplayName()
    {
        var snap = new PlayerSnapshot { Name = new byte[16] };
        snap.SetName("Player");
        snap.SetNameTagAppearance(10, 20, 30, 40, 50, 60, 1, 2, 3, gradientEnabled: true);
        snap.SetName("Player");

        Assert.Equal("Player", snap.GetName());
        Assert.False(NameTagColorCodec.HasAppearanceMarker(snap.Name[15]));
    }

    [Fact]
    public void SetNameTagAppearance_DoesNotDependOnDisplayName()
    {
        var snap = new PlayerSnapshot { Name = new byte[16] };
        snap.SetNameTagAppearance(10, 20, 30, 40, 50, 60, 1, 2, 3, gradientEnabled: true);

        Assert.True(NameTagColorCodec.TryDecodeAppearance(snap.Name, out var appearance));
        Assert.Equal(1, appearance.OutlineR);
        Assert.True(appearance.GradientEnabled);
    }

    [Fact]
    public void JoinRequest_RoundTrip_PreservesMarioModelId()
    {
        var frame = PacketSerializer.BuildJoinRequest("Player1", "4ef21b6e");
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.JoinRequest, id);
        Assert.True(PacketSerializer.TryReadJoinRequest(payload, out var name, out var modelId, out var buildId));
        Assert.Equal("Player1", name);
        Assert.Equal("4ef21b6e", modelId);
        Assert.Equal(ProtocolConstants.ModBuildId, buildId);
    }

    [Fact]
    public void JoinRequest_RoundTrip_PreservesExplicitModBuildId()
    {
        var frame = PacketSerializer.BuildJoinRequest("Player1", "4ef21b6e", modBuildId: 99);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadJoinRequest(payload, out _, out _, out var buildId));
        Assert.Equal((ushort)99, buildId);
    }

    [Fact]
    public void JoinRequest_RoundTrip_PreservesGameProfileId()
    {
        var frame = PacketSerializer.BuildJoinRequest(
            "Player1", "4ef21b6e", gameProfileId: (ushort)GameProfileId.MarioEclipse);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadJoinRequest(
            payload, out _, out _, out var buildId, out var profileId));
        Assert.Equal(ProtocolConstants.ModBuildId, buildId);
        Assert.Equal((ushort)GameProfileId.MarioEclipse, profileId);
    }

    [Fact]
    public void JoinRequest_DefaultProfile_IsVanilla()
    {
        var frame = PacketSerializer.BuildJoinRequest("Player1", null);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadJoinRequest(payload, out _, out _, out _, out var profileId));
        Assert.Equal((ushort)GameProfileId.VanillaSms, profileId);
        Assert.Equal(ProtocolConstants.CurrentGameProfileId, profileId);
    }

    [Fact]
    public void JoinRequest_LegacyPayloadWithoutBuildId_ReadsAsZero()
    {
        var legacy = new byte[16 + ProtocolConstants.MarioModelIdSize];
        System.Text.Encoding.UTF8.GetBytes("Legacy").CopyTo(legacy, 0);
        CharacterPack.EncodeModelId("4ef21b6e").CopyTo(legacy.AsSpan(16));
        Assert.True(PacketSerializer.TryReadJoinRequest(legacy, out var name, out var modelId, out var buildId));
        Assert.Equal("Legacy", name);
        Assert.Equal("4ef21b6e", modelId);
        Assert.Equal((ushort)0, buildId);
    }

    [Fact]
    public void Handshake_RoundTrip_PreservesModBuildId()
    {
        var frame = PacketSerializer.BuildHandshake(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.Handshake, id);
        Assert.Equal(ProtocolConstants.HandshakePayloadSize, payload.Length);
        Assert.True(PacketSerializer.TryReadHandshakeModBuildId(payload, out var buildId));
        Assert.Equal(ProtocolConstants.ModBuildId, buildId);
    }

    [Fact]
    public void HandshakeAck_RoundTrip_PreservesSlotAndServerBuild()
    {
        var frame = PacketSerializer.BuildHandshakeAck(3);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.HandshakeAck, id);
        Assert.True(PacketSerializer.TryReadHandshakeAck(payload, out var slot, out var serverBuild));
        Assert.Equal((byte)3, slot);
        Assert.Equal(ProtocolConstants.ModBuildId, serverBuild);
    }

    [Fact]
    public void Handshake_LegacyGuidOnly_HasNoModBuildId()
    {
        var legacy = Guid.NewGuid().ToByteArray();
        Assert.False(PacketSerializer.TryReadHandshakeModBuildId(legacy, out _));
    }

    [Fact]
    public void HeartbeatPayload_WithModelId_IsDecodable()
    {
        var payload = new byte[10 + ProtocolConstants.MarioModelIdSize];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), 123456789L);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), 42);
        CharacterPack.EncodeModelId("4ef21b6e").CopyTo(payload, 10);

        var frame = PacketSerializer.BuildHeartbeat(payload);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var restored));
        Assert.Equal(TcpPacketId.Heartbeat, id);
        Assert.Equal(10 + ProtocolConstants.MarioModelIdSize, restored.Length);
        Assert.Equal("4ef21b6e",
            CharacterPack.DecodeModelId(restored.AsSpan(10, ProtocolConstants.MarioModelIdSize)));
    }

    [Fact]
    public void MarioModelIntent_RoundTrip_RequiresExactPayload()
    {
        var frame = PacketSerializer.BuildMarioModelIntent("4ef21b6e", 17);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.MarioModelIntent, id);
        Assert.True(PacketSerializer.TryReadMarioModelIntent(
            payload, out var sequence, out var modelId));
        Assert.Equal(17u, sequence);
        Assert.Equal("4ef21b6e", modelId);
        Assert.False(PacketSerializer.TryReadMarioModelIntent(payload.AsSpan(0, 11), out _));

        var legacyPayload = CharacterPack.EncodeModelId("aabbccdd");
        Assert.True(PacketSerializer.TryReadMarioModelIntent(
            legacyPayload, out var legacySequence, out var legacyId));
        Assert.Equal(0u, legacySequence);
        Assert.Equal("aabbccdd", legacyId);

        var retail = PacketSerializer.BuildMarioModelIntent(null);
        Assert.True(PacketSerializer.TryUnwrapTcp(retail, out _, out var retailPayload));
        Assert.True(PacketSerializer.TryReadMarioModelIntent(retailPayload, out var retailId));
        Assert.Equal(string.Empty, retailId);
    }

    [Fact]
    public void CommBufferEndian_RoundTrip_PreservesMarioModelIds()
    {
        var buf = CommBuffer.CreateDefault();
        CharacterPack.EncodeModelId("4ef21b6e").CopyTo(buf.LocalMarioModelId, 0);
        CharacterPack.EncodeModelId("aabbccdd")
            .CopyTo(buf.RemoteMarioModelIds, 1 * ProtocolConstants.MarioModelIdSize);

        var restored = CommBufferEndian.FromDolphinBytes(CommBufferEndian.ToDolphinBytes(buf));
        Assert.Equal("4ef21b6e", CharacterPack.DecodeModelId(restored.LocalMarioModelId));
        Assert.Equal("aabbccdd",
            CharacterPack.DecodeModelId(
                restored.RemoteMarioModelIds.AsSpan(1 * ProtocolConstants.MarioModelIdSize,
                    ProtocolConstants.MarioModelIdSize)));
    }
}

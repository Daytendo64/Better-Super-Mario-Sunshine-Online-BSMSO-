using System.Runtime.InteropServices;
using SMSO.Net;

namespace SMSO.Tests;

public class CommBufferTests
{
    [Fact]
    public void CommBuffer_Size_MatchesProtocol()
    {
        Assert.Equal(ProtocolConstants.CommBufferSize, Marshal.SizeOf<CommBuffer>());
    }

    [Fact]
    public void PlayerSnapshot_Size_Is64()
    {
        Assert.Equal(64, Marshal.SizeOf<PlayerSnapshot>());
    }

    [Fact]
    public void UdpSnapshotInterval_Matches60Hz()
    {
        Assert.Equal(60, ProtocolConstants.SnapshotRateHz);
        Assert.Equal(16, ProtocolConstants.UdpSnapshotIntervalMs);
        Assert.Equal(16, ProtocolConstants.BridgePollMs);
    }

    [Fact]
    public void PlayerLimit_FitsReservedRemoteSlots()
    {
        Assert.Equal(10, ProtocolConstants.StableMaxPlayers);
        Assert.Equal(ProtocolConstants.StableMaxPlayers, ProtocolConstants.MaxPlayers);
        Assert.True(ProtocolConstants.MaxRemoteSlots >= ProtocolConstants.MaxPlayers);
        Assert.Equal(ProtocolConstants.MaxPlayers, ProtocolConstants.CommRosterHudRingSlots);
        Assert.Equal(11, ProtocolConstants.CommVersion);
    }

    [Fact]
    public void BlooperSurf_WaterByte_EncodesGessoTypeForAllRemoteSlots()
    {
        // Module packs mSurfGessoID & 0x03 into snap.water while surfing.
        // Remotes must have a slot for every non-local player (MaxPlayers-1) and the
        // array itself is MaxRemoteSlots wide so slot index == network slot is safe.
        Assert.Equal(10, ProtocolConstants.MaxRemoteSlots);
        Assert.Equal(ProtocolConstants.MaxPlayers, ProtocolConstants.MaxRemoteSlots);

        var buf = CommBuffer.CreateDefault();
        // Simulate remotes surfing different gesso colors (0/1/2) across all remote slots.
        for (byte slot = 0; slot < ProtocolConstants.MaxRemoteSlots; slot++)
        {
            buf.RemoteSnapshots[slot].Connected = 1;
            buf.RemoteSnapshots[slot].Slot = slot;
            buf.RemoteSnapshots[slot].Name ??= new byte[16];
            // Surf ride state 0x810446 — low u16 + high u16.
            buf.RemoteSnapshots[slot].ActionId = 0x0446;
            buf.RemoteSnapshots[slot].ActionIdHi = 0x0081;
            buf.RemoteSnapshots[slot].Water = (byte)(slot % 3);
            buf.RemoteSnapshots[slot].AnimId = 0x6D; // ride shell
        }

        var dolphinBytes = CommBufferEndian.ToDolphinBytes(buf);
        var restored = CommBufferEndian.FromDolphinBytes(dolphinBytes);
        for (byte slot = 0; slot < ProtocolConstants.MaxRemoteSlots; slot++)
        {
            Assert.Equal(1, restored.RemoteSnapshots[slot].Connected);
            Assert.Equal((byte)(slot % 3), restored.RemoteSnapshots[slot].Water);
            Assert.Equal(0x0446, restored.RemoteSnapshots[slot].ActionId);
            Assert.Equal(0x0081, restored.RemoteSnapshots[slot].ActionIdHi);
            Assert.Equal(0x6D, restored.RemoteSnapshots[slot].AnimId);
        }
    }

    [Fact]
    public void RoundTrip_PreservesMagic()
    {
        var buf = CommBuffer.CreateDefault();
        buf.Magic = ProtocolConstants.Magic;
        buf.SetLocalPlayerName("TestPlayer");
        var bytes = CommBufferMarshal.ToBytes(buf);
        Assert.Equal(ProtocolConstants.CommBufferSize, bytes.Length);
        var restored = CommBufferMarshal.FromBytes(bytes);
        Assert.Equal(ProtocolConstants.Magic, restored.Magic);
        Assert.Equal("TestPlayer", restored.GetLocalPlayerName());
    }

    [Fact]
    public void DolphinEndian_RoundTrip_PreservesRotationY()
    {
        var buf = CommBuffer.CreateDefault();
        buf.Magic = ProtocolConstants.Magic;
        buf.LocalSnapshot.RotationY = 12000f;

        var dolphinBytes = CommBufferEndian.ToDolphinBytes(buf);
        var restored = CommBufferEndian.FromDolphinBytes(dolphinBytes);
        Assert.Equal(12000f, restored.LocalSnapshot.RotationY, precision: 3);
    }

    [Fact]
    public void DolphinEndian_RoundTrip_PreservesFullMarioState()
    {
        var buf = CommBuffer.CreateDefault();
        buf.LocalSnapshot.ActionId = 0x0201;
        buf.LocalSnapshot.ActionIdHi = 0x0C40;

        var dolphinBytes = CommBufferEndian.ToDolphinBytes(buf);
        var restored = CommBufferEndian.FromDolphinBytes(dolphinBytes);
        Assert.Equal(0x0201, restored.LocalSnapshot.ActionId);
        Assert.Equal(0x0C40, restored.LocalSnapshot.ActionIdHi);
    }

    [Fact]
    public void DolphinEndian_RoundTrip_PreservesMarioVoiceEvent()
    {
        var buf = CommBuffer.CreateDefault();
        buf.LocalMarioVoiceEvent = new MarioVoiceEvent
        {
            SoundId = 0x000078AB,
            Sequence = 44,
            Flags = 1,
            Health = 2,
            StageId = 1,
            EpisodeId = 3,
        };
        buf.RemoteMarioVoiceEvents[2] = new MarioVoiceEvent
        {
            SoundId = 0x0000790E,
            Sequence = 12,
            Health = 1,
            StageId = 4,
            EpisodeId = 0,
        };

        var restored = CommBufferEndian.FromDolphinBytes(CommBufferEndian.ToDolphinBytes(buf));

        Assert.Equal(0x000078ABu, restored.LocalMarioVoiceEvent.SoundId);
        Assert.Equal(44, restored.LocalMarioVoiceEvent.Sequence);
        Assert.Equal(0x0000790Eu, restored.RemoteMarioVoiceEvents[2].SoundId);
        Assert.Equal(12, restored.RemoteMarioVoiceEvents[2].Sequence);
    }

    [Fact]
    public void DolphinEndian_RoundTrip_PreservesWorldSyncState()
    {
        var buf = CommBuffer.CreateDefault();
        buf.WorldSync.LocalPending = new CommWorldEvent
        {
            Sequence = 9,
            Type = WorldEventType.BlueCoinCollected,
            CourseId = 2,
            EpisodeId = 1,
            Payload0 = 7,
            Payload1 = 0,
        };
        buf.WorldSync.Incoming = new CommWorldEvent
        {
            EventId = 55,
            Type = WorldEventType.ShineCollected,
            CourseId = 2,
            EpisodeId = 1,
            Payload0 = 3,
            Payload1 = 0,
        };
        buf.WorldSync.LastAppliedEventId = 54;

        var restored = CommBufferEndian.FromDolphinBytes(CommBufferEndian.ToDolphinBytes(buf));

        Assert.Equal(9, restored.WorldSync.LocalPending.Sequence);
        Assert.Equal(WorldEventType.BlueCoinCollected, restored.WorldSync.LocalPending.Type);
        Assert.Equal(55u, restored.WorldSync.Incoming.EventId);
        Assert.Equal(54u, restored.WorldSync.LastAppliedEventId);
    }

    [Fact]
    public void DolphinEndian_RoundTrip_PreservesRosterHudSync()
    {
        var buf = CommBuffer.CreateDefault();
        buf.RosterHud.LatestSequence = 2;
        buf.RosterHud.Events[1] = new CommRosterHudEvent
        {
            Sequence = 2,
            Kind = RosterHudEventKind.Disconnected,
            Slot = 3,
            Name = new byte[16],
        };
        buf.RosterHud.Events[1].SetPlayerName("Luigi");

        var restored = CommBufferEndian.FromDolphinBytes(CommBufferEndian.ToDolphinBytes(buf));

        Assert.Equal(2, restored.RosterHud.LatestSequence);
        Assert.Equal(RosterHudEventKind.Disconnected, restored.RosterHud.Events[1].Kind);
        Assert.Equal(3, restored.RosterHud.Events[1].Slot);
        Assert.Equal("Luigi", restored.RosterHud.Events[1].GetPlayerName());
    }

    [Fact]
    public void DolphinEndian_RosterHudOnly_RoundTrip_PreservesHighSlotJoin()
    {
        var sync = CommRosterHudSync.CreateDefault();
        sync.LatestSequence = 9;
        sync.Events[8] = new CommRosterHudEvent
        {
            Sequence = 9,
            Kind = RosterHudEventKind.Connected,
            Slot = 8,
            Name = new byte[16],
        };
        sync.Events[8].SetPlayerName("Player9");

        var bytes = CommBufferEndian.ToRosterHudSyncDolphinBytes(sync);
        Assert.Equal(ProtocolConstants.CommRosterHudSyncSize, bytes.Length);

        var buf = CommBuffer.CreateDefault();
        // Reconstruct via full buffer write path at the roster offset.
        var full = CommBufferEndian.ToDolphinBytes(buf);
        bytes.CopyTo(full.AsSpan(ProtocolConstants.CommRosterHudOffset));
        var restored = CommBufferEndian.FromDolphinBytes(full);

        Assert.Equal(9, restored.RosterHud.LatestSequence);
        Assert.Equal(RosterHudEventKind.Connected, restored.RosterHud.Events[8].Kind);
        Assert.Equal(8, restored.RosterHud.Events[8].Slot);
        Assert.Equal("Player9", restored.RosterHud.Events[8].GetPlayerName());
    }

    [Fact]
    public void DolphinEndian_RoundTrip_PreservesAllRemoteSlots()
    {
        var buf = CommBuffer.CreateDefault();
        for (byte slot = 1; slot <= ProtocolConstants.MaxRemoteSlots - 1; slot++)
        {
            buf.RemoteSnapshots[slot] = new PlayerSnapshot
            {
                Connected = 1,
                Slot = slot,
                StageId = 1,
                EpisodeId = slot,
                Position = new Vec3 { X = slot * 10.0f, Y = 50.0f, Z = slot * 20.0f },
                Name = new byte[16],
            };
            buf.RemoteNameTagAppearances[slot] = new NameTagAppearance
            {
                TextTopR = slot,
                TextTopG = (byte)(slot + 10),
                TextTopB = (byte)(slot + 20),
                TextBottomR = 136,
                TextBottomG = 136,
                TextBottomB = 136,
                OutlineR = 0,
                OutlineG = 0,
                OutlineB = slot,
                Flags = NameTagAppearance.FlagValid,
            };
            buf.RemoteMarioVoiceEvents[slot] = new MarioVoiceEvent
            {
                SoundId = 0x7800u + slot,
                Sequence = slot,
                StageId = 1,
                EpisodeId = slot,
            };
        }

        var restored = CommBufferEndian.FromDolphinBytes(CommBufferEndian.ToDolphinBytes(buf));

        for (byte slot = 1; slot <= ProtocolConstants.MaxRemoteSlots - 1; slot++)
        {
            Assert.Equal(1, restored.RemoteSnapshots[slot].Connected);
            Assert.Equal(slot, restored.RemoteSnapshots[slot].Slot);
            Assert.Equal(slot * 10.0f, restored.RemoteSnapshots[slot].Position.X, precision: 3);
            Assert.Equal(slot, restored.RemoteNameTagAppearances[slot].TextTopR);
            Assert.Equal((uint)(0x7800u + slot), restored.RemoteMarioVoiceEvents[slot].SoundId);
            Assert.Equal(slot, restored.RemoteMarioVoiceEvents[slot].Sequence);
        }
    }

    [Fact]
    public void DolphinEndian_RoundTrip_PreservesMagicAndName()
    {
        var buf = CommBuffer.CreateDefault();
        buf.Magic = ProtocolConstants.Magic;
        buf.Version = ProtocolConstants.CommVersion;
        buf.SetLocalPlayerName("Mario");
        buf.LocalSnapshot.StageId = 4;
        buf.LocalSnapshot.EpisodeId = 2;

        var dolphinBytes = CommBufferEndian.ToDolphinBytes(buf);
        Assert.Equal(0x53, dolphinBytes[0]);
        Assert.Equal(0x4F, dolphinBytes[3]);

        var restored = CommBufferEndian.FromDolphinBytes(dolphinBytes);
        Assert.Equal(ProtocolConstants.Magic, restored.Magic);
        Assert.Equal("Mario", restored.GetLocalPlayerName());
        Assert.Equal(4, restored.LocalSnapshot.StageId);
        Assert.Equal(2, restored.LocalSnapshot.EpisodeId);
    }
}

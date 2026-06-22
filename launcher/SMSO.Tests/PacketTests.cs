using SMSO.Net;

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
    public void SetNameTagAppearance_DoesNotDependOnDisplayName()
    {
        var snap = new PlayerSnapshot { Name = new byte[16] };
        snap.SetNameTagAppearance(10, 20, 30, 40, 50, 60, 1, 2, 3, gradientEnabled: true);

        Assert.True(NameTagColorCodec.TryDecodeAppearance(snap.Name, out var appearance));
        Assert.Equal(1, appearance.OutlineR);
        Assert.True(appearance.GradientEnabled);
    }
}

using SMSO.Net;

namespace SMSO.Tests;

public class RemoteInterpolationTests
{
    [Fact]
    public void Advance_FollowsLatestAnimFrameFromPackets()
    {
        var interp = new RemoteInterpolation();
        interp.PushPacket(1, MakeSnapshot(animId: 0xF4, animFrame: 1000));
        interp.Advance(1);

        interp.PushPacket(1, MakeSnapshot(animId: 0xF4, animFrame: 1500));
        var display = interp.Advance(1);

        Assert.Equal((ushort)0xF4, display.AnimId);
        Assert.Equal((ushort)1500, display.AnimFrame);
    }

    [Fact]
    public void Advance_DoesNotLerpAnimFrameAcrossAnimChange()
    {
        var interp = new RemoteInterpolation();

        interp.PushPacket(1, MakeSnapshot(animId: 0x48, animFrame: 1000));
        interp.PushPacket(1, MakeSnapshot(animId: 0xF4, animFrame: 500));

        var display = interp.Advance(1);
        Assert.Equal((ushort)0xF4, display.AnimId);
        Assert.Equal((ushort)500, display.AnimFrame);
    }

    [Fact]
    public void Advance_UsesLatestRawAnimFrameNotDelayedLerp()
    {
        var interp = new RemoteInterpolation();

        interp.PushPacket(1, MakeSnapshot(animId: 0xF4, animFrame: 0));
        Thread.Sleep(55);
        interp.PushPacket(1, MakeSnapshot(animId: 0xF4, animFrame: 256));
        Thread.Sleep(90);

        var display = interp.Advance(1);
        Assert.Equal((ushort)0xF4, display.AnimId);
        Assert.Equal((ushort)256, display.AnimFrame);
    }

    [Fact]
    public void Advance_SnapsRotationDuringTurnAnim()
    {
        var interp = new RemoteInterpolation();

        var prev = MakeSnapshot(animId: 0xBC, animFrame: 0);
        prev.RotationY = 0f;
        var next = MakeSnapshot(animId: 0xBC, animFrame: 256);
        next.RotationY = 1000f;

        interp.PushPacket(1, prev);
        interp.PushPacket(1, next);

        Thread.Sleep(90);
        var display = interp.Advance(1);
        Assert.Equal(1000f, display.RotationY);
    }

    [Fact]
    public void Advance_InterpolatesAnimFrameBetweenMatchingPackets()
    {
        var interp = new RemoteInterpolation();

        interp.PushPacket(1, MakeSnapshot(animId: 0x48, animFrame: 0));
        Thread.Sleep(20);
        interp.PushPacket(1, MakeSnapshot(animId: 0x48, animFrame: 512));

        var display = interp.Advance(1);
        Assert.Equal((ushort)0x48, display.AnimId);
        Assert.InRange(display.AnimFrame, (ushort)200, (ushort)512);
    }

    [Fact]
    public void Advance_ExtrapolatesAnimFrameWhenOnlyOnePacket()
    {
        var interp = new RemoteInterpolation();
        interp.PushPacket(1, MakeSnapshot(animId: 0x48, animFrame: 0, pingMs: 64));
        Thread.Sleep(50);

        var display = interp.Advance(1);
        Assert.Equal((ushort)0x48, display.AnimId);
        Assert.True(display.AnimFrame > 0);
    }

    [Fact]
    public void Advance_SnapsRotationDuringSideFlipAnim()
    {
        var interp = new RemoteInterpolation();

        var prev = MakeSnapshot(animId: 0xBF, animFrame: 0);
        prev.RotationY = 0f;
        var next = MakeSnapshot(animId: 0xBF, animFrame: 256);
        next.RotationY = 5000f;

        interp.PushPacket(1, prev);
        interp.PushPacket(1, next);

        Thread.Sleep(90);
        var display = interp.Advance(1);
        Assert.Equal(5000f, display.RotationY);
    }

    [Fact]
    public void DecodeAnimRate_UsesLowByteOnly()
    {
        Assert.Equal(1.0f, RemoteInterpolation.DecodeAnimRate(64));
        Assert.Equal(1.0f, RemoteInterpolation.DecodeAnimRate((ushort)(64 | (200 << 8))));
    }

    private static PlayerSnapshot MakeSnapshot(ushort animId, ushort animFrame, ushort pingMs = 64)
    {
        return new PlayerSnapshot
        {
            Position = new Vec3 { X = 1f, Y = 2f, Z = 3f },
            Velocity = new Vec3(),
            RotationY = 0f,
            AnimId = animId,
            AnimFrame = animFrame,
            PingMs = pingMs,
            Connected = 1,
            Slot = 1,
        };
    }
}

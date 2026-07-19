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

    [Fact]
    public void Advance_HermiteBlendsVelocityBetweenPackets()
    {
        var interp = new RemoteInterpolation();

        var first = MakeSnapshot(animId: 0x48, animFrame: 0);
        first.Position = new Vec3 { X = 0f, Y = 0f, Z = 0f };
        first.Velocity = new Vec3 { X = 10f, Y = 0f, Z = 0f };

        interp.PushPacket(1, first);
        Thread.Sleep(20);

        var second = MakeSnapshot(animId: 0x48, animFrame: 256);
        second.Position = new Vec3 { X = 1f, Y = 0f, Z = 0f };
        second.Velocity = new Vec3 { X = 10f, Y = 0f, Z = 0f };

        interp.PushPacket(1, second);
        // Render delay is 33 ms; wait long enough that render time lands mid-span.
        Thread.Sleep(25);

        var display = interp.Advance(1);
        Assert.InRange(display.Position.X, 0.1f, 1.0f);
        Assert.NotEqual(0f, display.Position.X);
    }

    [Fact]
    public void Advance_NormalMovementDoesNotTriggerTeleportSnap()
    {
        double now = 0;
        var interp = new RemoteInterpolation(() => now);
        var first = MakeSnapshot(animId: 0x48, animFrame: 0);
        first.Position = new Vec3 { X = 0f, Y = 0f, Z = 0f };
        first.Velocity = new Vec3 { X = 1000f, Y = 0f, Z = 0f };
        interp.PushPacket(1, first);

        now = 16;
        var second = MakeSnapshot(animId: 0x48, animFrame: 256);
        second.Position = new Vec3 { X = 100f, Y = 0f, Z = 0f };
        second.Velocity = first.Velocity;
        interp.PushPacket(1, second);

        now = 40; // delayed render time = 7 ms, within the 0..16 ms sample span
        var display = interp.Advance(1);

        Assert.InRange(display.Position.X, 1f, 99f);
    }

    [Fact]
    public void Advance_ChangesPositionOnEachRenderTickBetweenSamples()
    {
        double now = 0;
        var interp = new RemoteInterpolation(() => now);
        var first = MakeSnapshot(animId: 0x48, animFrame: 0);
        first.Position = new Vec3 { X = 0f, Y = 0f, Z = 0f };
        first.Velocity = new Vec3 { X = 1000f, Y = 0f, Z = 0f };
        interp.PushPacket(1, first);

        now = 16;
        var second = MakeSnapshot(animId: 0x48, animFrame: 256);
        second.Position = new Vec3 { X = 100f, Y = 0f, Z = 0f };
        second.Velocity = first.Velocity;
        interp.PushPacket(1, second);

        now = 38;
        var frameA = interp.Advance(1);
        now = 42;
        var frameB = interp.Advance(1);

        Assert.True(frameB.Position.X > frameA.Position.X);
        Assert.NotEqual(frameA.Position.X, frameB.Position.X);
    }

    [Fact]
    public void Advance_ExtrapolatesAcrossDelayedCoalescedBatch()
    {
        double now = 0;
        var interp = new RemoteInterpolation(() => now);
        var first = MakeSnapshot(animId: 0x48, animFrame: 0);
        first.Position = new Vec3 { X = 0f, Y = 0f, Z = 0f };
        first.Velocity = new Vec3 { X = 1000f, Y = 0f, Z = 0f };
        interp.PushPacket(1, first);

        now = 16;
        var second = MakeSnapshot(animId: 0x48, animFrame: 256);
        second.Position = new Vec3 { X = 16f, Y = 0f, Z = 0f };
        second.Velocity = first.Velocity;
        interp.PushPacket(1, second);

        now = 80; // render time 47 ms: 31 ms beyond the latest coalesced sample
        var display = interp.Advance(1);

        Assert.True(display.Position.X > second.Position.X);
        Assert.InRange(display.Position.X, 40f, 50f);
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

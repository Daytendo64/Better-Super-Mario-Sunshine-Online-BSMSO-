using System.Buffers.Binary;
using SMSO.Net;

namespace SMSO.Tests;

public class CommBufferEndianTests
{
    [Fact]
    public void ApplyWarpIntentToControlSpan_SetsWarpFields()
    {
        var control = new byte[ProtocolConstants.CommBridgeControlSize];
        BinaryPrimitives.WriteUInt32BigEndian(control, (uint)BridgeFlags.Connected);

        CommBufferEndian.ApplyWarpIntentToControlSpan(control, 0xFF, 2, 0, setHostFlag: true, setWarpAll: true);

        var flags = (BridgeFlags)BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(0, 4));
        Assert.True(flags.HasFlag(BridgeFlags.WarpPending));
        Assert.True(flags.HasFlag(BridgeFlags.WarpAll));
        Assert.True(flags.HasFlag(BridgeFlags.Host));
        Assert.True(flags.HasFlag(BridgeFlags.Connected));
        Assert.Equal(0xFF, control[7]);
        Assert.Equal(2, control[8]);
        Assert.Equal(0, control[9]);
    }

    [Fact]
    public void ApplyWarpIntentToControlSpan_SetsWarpToPointFields()
    {
        var control = new byte[ProtocolConstants.CommBridgeControlSize];

        CommBufferEndian.ApplyWarpIntentToControlSpan(
            control,
            0xFF,
            2,
            1,
            setHostFlag: false,
            setWarpPending: false,
            setWarpToPoint: true,
            warpPosX: 100f,
            warpPosY: 200f,
            warpPosZ: 300f,
            warpFacingY: 45f);

        var flags = (BridgeFlags)BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(0, 4));
        Assert.False(flags.HasFlag(BridgeFlags.WarpPending));
        Assert.True(flags.HasFlag(BridgeFlags.WarpToPoint));
        Assert.Equal(100f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(control.AsSpan(10, 4))));
        Assert.Equal(200f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(control.AsSpan(14, 4))));
        Assert.Equal(300f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(control.AsSpan(18, 4))));
        Assert.Equal(45f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(control.AsSpan(22, 4))));
    }
}

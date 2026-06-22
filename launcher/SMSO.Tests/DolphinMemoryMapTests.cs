using System.Buffers.Binary;
using SMSO.Bridge;
using SMSO.Net;

namespace SMSO.Tests;

public class DolphinMemoryMapTests
{
    [Fact]
    public void AnchorGuestOffset_MatchesDefaultMailbox()
    {
        var offset = ProtocolConstants.DefaultMailboxAddress - DolphinMemoryMap.Mem1GuestBase;
        Assert.Equal(0x17FC000UL, offset);
    }

    [Fact]
    public void DolphinMemLayout_LogicalBase_IsEightGiBFromArena()
    {
        Assert.Equal(0x200000000UL, DolphinMemLayout.LogicalBaseOffset);
        Assert.Equal(0x80000000UL, DolphinMemLayout.PhysicalBaseOffset);
    }

    [Fact]
    public void DolphinMemLayout_AnchorHostOffset_FromLogicalView()
    {
        var anchorGuestOffset = ProtocolConstants.DefaultMailboxAddress - DolphinMemoryMap.Mem1GuestBase;
        var expected = DolphinMemLayout.LogicalBaseOffset + anchorGuestOffset;
        Assert.Equal(0x2017FC000UL, expected);
    }

    [Fact]
    public void ParseAnchor_AcceptsValidIndirectionBlock()
    {
        var anchor = new byte[12];
        anchor[0] = 0x53;
        anchor[1] = 0x4D;
        anchor[2] = 0x53;
        anchor[3] = 0x4F;
        BinaryPrimitives.WriteUInt16BigEndian(anchor.AsSpan(4, 2), ProtocolConstants.CommVersion);
        BinaryPrimitives.WriteUInt32BigEndian(anchor.AsSpan(8, 4), 0x803A1234U);

        Assert.True(DolphinMemoryMapTestHooks.TryParseAnchor(anchor, out var bufferGuest));
        Assert.Equal(0x803A1234U, bufferGuest);
    }

    [Fact]
    public void ParseAnchor_RejectsInvalidPointer()
    {
        var anchor = new byte[12];
        anchor[0] = 0x53;
        anchor[1] = 0x4D;
        anchor[2] = 0x53;
        anchor[3] = 0x4F;
        BinaryPrimitives.WriteUInt16BigEndian(anchor.AsSpan(4, 2), ProtocolConstants.CommVersion);
        BinaryPrimitives.WriteUInt32BigEndian(anchor.AsSpan(8, 4), 0x12345678U);

        Assert.False(DolphinMemoryMapTestHooks.TryParseAnchor(anchor, out _));
    }
}

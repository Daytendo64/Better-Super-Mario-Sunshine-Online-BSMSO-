namespace SMSO.Bridge;

/// <summary>Test-only surface for mailbox anchor parsing.</summary>
internal static class DolphinMemoryMapTestHooks
{
    public static bool TryParseAnchor(ReadOnlySpan<byte> anchor, out uint bufferGuest) =>
        DolphinMemoryMap.TryParseAnchorForTests(anchor, out bufferGuest);
}

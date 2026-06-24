using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SMSO.Net;

namespace SMSO.Bridge;

internal static class DolphinMemoryMap
{
    public const uint Mem1GuestBase = 0x80000000;
    public const int Mem1GuestSize = 0x01800000; // 24 MiB hardware MEM1
    public const int Mem1MappedSize = 0x02000000; // 32 MiB Dolphin cached view
    public const int CommMailboxAnchorSize = 12;

    private static readonly byte[] MagicBytes = { 0x53, 0x4D, 0x53, 0x4F }; // "SMSO" big-endian

    private const uint MemCommit = 0x1000;
    private const int ScanChunkSize = 0x40000; // 256 KiB — fewer syscalls than 64 KiB
    private const int ScanStride = 0x10;
    private const ulong MaxBackgroundScanBytes = 0x40000000; // 1 GiB cap for fallback scan

    private static readonly object RegionCacheLock = new();
    private static int _cachedProcessId;
    private static List<(UIntPtr Base, ulong Size)> _cachedRegions = new();
    private static DateTime _regionCacheUtc = DateTime.MinValue;
    private static ulong? _cachedArenaBase;
    private static ulong? _cachedViewBaseOffset;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public ushort Unused;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(
        IntPtr hProcess,
        UIntPtr lpAddress,
        out MemoryBasicInformation lpBuffer,
        uint dwLength);

    public static void InvalidateCache()
    {
        lock (RegionCacheLock)
        {
            _cachedProcessId = 0;
            _cachedRegions.Clear();
            _regionCacheUtc = DateTime.MinValue;
            _cachedArenaBase = null;
            _cachedViewBaseOffset = null;
        }
    }

    /// <summary>Sub-millisecond path: probe known Dolphin fastmem anchor offsets on the arena region.</summary>
    public static bool TryResolveMailboxFast(IntPtr processHandle, uint guestMailbox, out UIntPtr hostAddress)
    {
        hostAddress = UIntPtr.Zero;
        if (processHandle == IntPtr.Zero)
            return false;

        var anchorGuestOffset = (ulong)(guestMailbox - Mem1GuestBase);
        var regions = GetReadableRegions(processHandle);

        if (_cachedArenaBase is ulong arenaBase &&
            _cachedViewBaseOffset is ulong viewOffset &&
            TryAnchorAtOffset(processHandle, arenaBase, viewOffset, anchorGuestOffset, out hostAddress))
        {
            return true;
        }

        foreach (var (regionBase, regionSize) in regions)
        {
            if (regionSize < DolphinMemLayout.MinFastmemArenaSize)
                continue;

            var baseVal = regionBase.ToUInt64();
            if (TryFastmemAnchorOffsets(processHandle, baseVal, anchorGuestOffset, out hostAddress, out var usedOffset))
            {
                lock (RegionCacheLock)
                {
                    _cachedArenaBase = baseVal;
                    _cachedViewBaseOffset = usedOffset;
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>Bounded fallback scan for non-fastmem or unusual Dolphin builds. Run off the hot poll thread.</summary>
    public static bool TryResolveMailboxScan(IntPtr processHandle, uint guestMailbox, out UIntPtr hostAddress)
    {
        hostAddress = UIntPtr.Zero;
        if (processHandle == IntPtr.Zero)
            return false;

        var anchorGuestOffset = (ulong)(guestMailbox - Mem1GuestBase);
        foreach (var (regionBase, regionSize) in GetReadableRegions(processHandle))
        {
            if (TryScanRegion(processHandle, regionBase, regionSize, anchorGuestOffset, out hostAddress))
                return true;
        }

        return false;
    }

    public static bool TryResolveMailbox(IntPtr processHandle, uint guestMailbox, out UIntPtr hostAddress) =>
        TryResolveMailboxFast(processHandle, guestMailbox, out hostAddress) ||
        TryResolveMailboxScan(processHandle, guestMailbox, out hostAddress);

    public static int CountReadableRegions(IntPtr processHandle) =>
        GetReadableRegions(processHandle).Count;

    private static bool TryFastmemAnchorOffsets(
        IntPtr processHandle,
        ulong arenaBase,
        ulong anchorGuestOffset,
        out UIntPtr hostAddress,
        out ulong usedViewOffset)
    {
        hostAddress = UIntPtr.Zero;
        usedViewOffset = 0;

        foreach (var viewOffset in DolphinMemLayout.Mem1ViewBaseOffsets)
        {
            if (TryAnchorAtOffset(processHandle, arenaBase, viewOffset, anchorGuestOffset, out hostAddress))
            {
                usedViewOffset = viewOffset;
                return true;
            }
        }

        return false;
    }

    private static bool TryAnchorAtOffset(
        IntPtr processHandle,
        ulong arenaBase,
        ulong viewBaseOffset,
        ulong anchorGuestOffset,
        out UIntPtr hostAddress)
    {
        hostAddress = UIntPtr.Zero;
        var anchorHost = (UIntPtr)(arenaBase + viewBaseOffset + anchorGuestOffset);
        return TryResolveFromAnchor(processHandle, anchorHost, anchorGuestOffset, out hostAddress);
    }

    private static bool TryScanRegion(
        IntPtr processHandle,
        UIntPtr regionBase,
        ulong regionSize,
        ulong anchorGuestOffset,
        out UIntPtr hostAddress)
    {
        hostAddress = UIntPtr.Zero;
        var scanLimit = Math.Min(regionSize, MaxBackgroundScanBytes);
        if (scanLimit < (ulong)ProtocolConstants.CommBufferSize)
            return false;

        var chunk = new byte[ScanChunkSize + ProtocolConstants.CommBufferSize];
        for (ulong offset = 0; offset < scanLimit; offset += ScanChunkSize)
        {
            var readSize = (int)Math.Min((ulong)chunk.Length, scanLimit - offset);
            var chunkAddress = new UIntPtr(regionBase.ToUInt64() + offset);
            if (!ReadMemory(processHandle, chunkAddress, chunk, readSize, out int read) || read < 16)
                continue;

            var limit = Math.Min(read - 16, read - CommMailboxAnchorSize);
            for (var i = 0; i <= limit; i += ScanStride)
            {
                if (!MatchesMagic(chunk, i))
                    continue;

                var probeAddress = new UIntPtr(regionBase.ToUInt64() + offset + (ulong)i);

                if (LooksLikeCommBuffer(processHandle, probeAddress))
                {
                    hostAddress = probeAddress;
                    return true;
                }

                if (TryResolveFromAnchor(processHandle, probeAddress, anchorGuestOffset, out hostAddress))
                    return true;
            }
        }

        return false;
    }

    private static bool TryResolveFromAnchor(
        IntPtr processHandle,
        UIntPtr anchorHost,
        ulong anchorGuestOffset,
        out UIntPtr hostAddress)
    {
        hostAddress = UIntPtr.Zero;
        var anchor = new byte[CommMailboxAnchorSize];
        if (!ReadMemory(processHandle, anchorHost, anchor, anchor.Length, out int read) || read != anchor.Length)
            return false;

        if (!TryParseAnchor(anchor, out var bufferGuest))
            return false;

        var logicalBase = anchorHost.ToUInt64() - anchorGuestOffset;
        var bufferHost = (UIntPtr)(logicalBase + (bufferGuest - Mem1GuestBase));
        if (!LooksLikeCommBuffer(processHandle, bufferHost))
            return false;

        hostAddress = bufferHost;
        return true;
    }

    private static bool TryParseAnchor(ReadOnlySpan<byte> anchor, out uint bufferGuest)
    {
        bufferGuest = 0;
        if (anchor.Length < CommMailboxAnchorSize)
            return false;
        if (!anchor[..4].SequenceEqual(MagicBytes))
            return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(anchor.Slice(4, 2)) == 0)
            return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(anchor.Slice(6, 2)) != 0)
            return false;

        bufferGuest = BinaryPrimitives.ReadUInt32BigEndian(anchor.Slice(8, 4));
        return bufferGuest >= Mem1GuestBase &&
               bufferGuest < Mem1GuestBase + (uint)Mem1MappedSize;
    }

    private static bool LooksLikeCommBuffer(IntPtr processHandle, UIntPtr address)
    {
        var header = new byte[ProtocolConstants.CommBridgeControlOffset + ProtocolConstants.CommBridgeControlSize];
        if (!ReadMemory(processHandle, address, header, header.Length, out int read) ||
            read != header.Length)
        {
            return false;
        }

        if (!header.AsSpan(0, 4).SequenceEqual(MagicBytes))
            return false;

        var version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        if (version == 0)
            return false;

        if (TryParseAnchor(header.AsSpan(0, CommMailboxAnchorSize), out _))
            return false;

        return true;
    }

    internal static bool TryParseAnchorForTests(ReadOnlySpan<byte> anchor, out uint bufferGuest) =>
        TryParseAnchor(anchor, out bufferGuest);

    private static bool MatchesMagic(byte[] buffer, int offset) =>
        offset + 4 <= buffer.Length &&
        buffer[offset] == MagicBytes[0] &&
        buffer[offset + 1] == MagicBytes[1] &&
        buffer[offset + 2] == MagicBytes[2] &&
        buffer[offset + 3] == MagicBytes[3];

    private static bool ReadMemory(IntPtr processHandle, UIntPtr address, byte[] buffer, int size, out int read) =>
        NativeMethods.ReadProcessMemory(processHandle, address, buffer, size, out read) && read > 0;

    private static List<(UIntPtr Base, ulong Size)> GetReadableRegions(IntPtr processHandle)
    {
        var processId = NativeMethods.GetProcessId(processHandle);
        lock (RegionCacheLock)
        {
            if (_cachedProcessId == processId &&
                (DateTime.UtcNow - _regionCacheUtc).TotalMilliseconds < 1000 &&
                _cachedRegions.Count > 0)
            {
                // Return a snapshot — callers may enumerate while another thread invalidates the cache.
                return new List<(UIntPtr Base, ulong Size)>(_cachedRegions);
            }
        }

        var regions = EnumerateReadableRegions(processHandle);
        lock (RegionCacheLock)
        {
            _cachedProcessId = processId;
            _cachedRegions = regions;
            _regionCacheUtc = DateTime.UtcNow;
        }

        return new List<(UIntPtr Base, ulong Size)>(regions);
    }

    private static List<(UIntPtr Base, ulong Size)> EnumerateReadableRegions(IntPtr processHandle)
    {
        var regions = new List<(UIntPtr, ulong)>();
        var seen = new HashSet<ulong>();
        var address = UIntPtr.Zero;
        var infoSize = (uint)Marshal.SizeOf<MemoryBasicInformation>();

        for (var scanned = 0; scanned < 4096; scanned++)
        {
            if (VirtualQueryEx(processHandle, address, out var info, infoSize) == 0)
                break;

            var baseVal = (ulong)info.BaseAddress;
            var sizeVal = info.RegionSize.ToUInt64();
            if (sizeVal == 0)
                break;

            if (info.State == MemCommit &&
                IsReadable(info.Protect) &&
                sizeVal >= (ulong)ProtocolConstants.CommBufferSize &&
                seen.Add(baseVal))
            {
                regions.Add(((UIntPtr)baseVal, sizeVal));
            }

            var next = baseVal + sizeVal;
            if (next <= baseVal)
                break;

            address = new UIntPtr(next);
        }

        regions.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return regions;
    }

    private static bool IsReadable(uint protect)
    {
        var page = protect & 0xFF;
        return page is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    }
}

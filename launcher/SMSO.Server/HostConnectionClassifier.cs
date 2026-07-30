using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SMSO.Server;

/// <summary>
/// How a TCP peer relates to the process running this server. Host privileges follow
/// this instead of "first connection wins", which let any client that beat the hosting
/// launcher's own self-join take over sync settings and warps.
/// </summary>
internal enum HostConnectionKind
{
    /// <summary>Off-box peer — never the launcher that owns this server.</summary>
    Remote,

    /// <summary>Loopback peer whose owning process could not be determined.</summary>
    LoopbackUnverified,

    /// <summary>Loopback peer owned by another process (e.g. a second launcher instance).</summary>
    LoopbackOtherProcess,

    /// <summary>Loopback peer owned by this process — the hosting launcher's self-join.</summary>
    SameProcess,
}

/// <summary>
/// Identifies the hosting launcher's own loopback connection by looking up the owning
/// process of the peer's socket. Falls back to <see cref="HostConnectionKind.LoopbackUnverified"/>
/// whenever the lookup is unavailable so hosting still works off the loopback heuristic.
/// </summary>
internal static class HostConnectionClassifier
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint ErrorInsufficientBuffer = 122;

    private static volatile bool _ownerLookupSupported = true;

    public static HostConnectionKind Classify(TcpClient tcp, int listenPort)
    {
        IPEndPoint? remote;
        try
        {
            remote = tcp.Client.RemoteEndPoint as IPEndPoint;
        }
        catch
        {
            return HostConnectionKind.Remote;
        }

        if (remote == null || !IPAddress.IsLoopback(remote.Address))
            return HostConnectionKind.Remote;

        if (!TryGetOwningProcessId(remote.Port, listenPort, out var pid))
            return HostConnectionKind.LoopbackUnverified;

        return pid == Environment.ProcessId
            ? HostConnectionKind.SameProcess
            : HostConnectionKind.LoopbackOtherProcess;
    }

    /// <summary>
    /// Owning process of the loopback socket at <paramref name="peerPort"/> that is
    /// connected to <paramref name="listenPort"/>.
    /// </summary>
    private static bool TryGetOwningProcessId(int peerPort, int listenPort, out int pid)
    {
        pid = 0;
        if (!_ownerLookupSupported || !OperatingSystem.IsWindows() || listenPort <= 0)
            return false;

        var buffer = IntPtr.Zero;
        try
        {
            var size = 0;
            var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet,
                TcpTableOwnerPidConnections, 0);
            if (status != ErrorInsufficientBuffer && status != 0)
                return false;
            if (size <= 4)
                return false;

            buffer = Marshal.AllocHGlobal(size);
            status = GetExtendedTcpTable(buffer, ref size, false, AfInet,
                TcpTableOwnerPidConnections, 0);
            if (status != 0)
                return false;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var cursor = buffer + sizeof(int);
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
                cursor += rowSize;
                if (ToHostPort(row.LocalPort) != peerPort)
                    continue;
                if (ToHostPort(row.RemotePort) != listenPort)
                    continue;

                pid = (int)row.OwningPid;
                return true;
            }

            return false;
        }
        catch (DllNotFoundException)
        {
            _ownerLookupSupported = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _ownerLookupSupported = false;
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Ports live in the low two bytes of the DWORD in network byte order.</summary>
    private static int ToHostPort(uint port) => (int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);
}

using System.Net;
using System.Net.Sockets;

namespace SMSO.Tests;

/// <summary>
/// Hands out loopback ports for tests that stand up a real <c>GameServer</c>.
/// </summary>
/// <remarks>
/// The obvious approach — bind port 0, read what the OS picked, close, then bind it again for
/// real — races itself. xUnit runs test classes in parallel, so two tests can be handed the same
/// number in the gap between the probe closing and the server binding, and the loser dies with
/// "Only one usage of each socket address". Because <c>GameServer</c> binds exclusively (no
/// SO_REUSEADDR, deliberately, so a stale listener can never shadow a rehost) that collision is a
/// hard failure rather than a silent share.
///
/// Instead every caller in the run draws from one monotonic counter, so no number is ever handed
/// out twice; the only remaining contender is an unrelated process on the machine, which the
/// bindability probe skips past.
/// </remarks>
internal static class TestPortAllocator
{
    // Above the product default (27015) and below the Windows dynamic range (49152+), so we
    // collide with neither a real session nor the OS's own ephemeral allocations.
    private const int BasePort = 28300;
    private const int MaxAttempts = 50;

    private static int _next = BasePort;

    /// <summary>Reserves a loopback port number that no other test in this run will receive.</summary>
    public static int Next()
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var port = Interlocked.Increment(ref _next);
            if (IsBindable(port))
                return port;
        }

        throw new InvalidOperationException(
            $"No bindable test port found in {MaxAttempts} attempts from {BasePort}.");
    }

    private static bool IsBindable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            // Match GameServer: no reuse, and drop the socket immediately on close so the number
            // is usable again on the next line rather than after TIME_WAIT.
            try { listener.Server.LingerState = new LingerOption(true, 0); } catch { /* platform */ }
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try { listener.Server.Close(); } catch { /* ignore */ }
            try { listener.Stop(); } catch { /* ignore */ }
        }
    }
}

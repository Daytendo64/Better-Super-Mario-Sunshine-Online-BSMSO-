namespace SMSO.Net;

/// <summary>
/// Explicit session lifecycle for Host/Connect/Disconnect UI and socket teardown.
/// Socket flags (<c>TcpClient.Connected</c>, <c>GameServer.IsRunning</c>) are racy on
/// Windows after remote close — UI must follow this phase, not socket probes.
/// </summary>
public enum SessionLifecyclePhase : byte
{
    Idle = 0,
    Connecting = 1,
    Connected = 2,
    Disconnecting = 3,
    /// <summary>Binding / starting the embedded GameServer.</summary>
    Hosting = 4,
    /// <summary>Server listening and host self-join completed.</summary>
    Hosted = 5,
    /// <summary>Stopping the embedded GameServer (exclusive bind release).</summary>
    Stopping = 6,
}

/// <summary>Pure helpers so UI and tests agree on button enablement.</summary>
public static class SessionLifecycle
{
    public static bool CanHostOrConnect(SessionLifecyclePhase phase) =>
        phase == SessionLifecyclePhase.Idle;

    public static bool CanDisconnect(SessionLifecyclePhase phase) =>
        phase is SessionLifecyclePhase.Connected or SessionLifecyclePhase.Hosted;

    public static bool IsTransient(SessionLifecyclePhase phase) =>
        phase is SessionLifecyclePhase.Connecting
            or SessionLifecyclePhase.Disconnecting
            or SessionLifecyclePhase.Hosting
            or SessionLifecyclePhase.Stopping;

    public static string ToLogLabel(SessionLifecyclePhase phase) => phase switch
    {
        SessionLifecyclePhase.Idle => "Idle",
        SessionLifecyclePhase.Connecting => "Connecting",
        SessionLifecyclePhase.Connected => "Connected",
        SessionLifecyclePhase.Disconnecting => "Disconnecting",
        SessionLifecyclePhase.Hosting => "Hosting",
        SessionLifecyclePhase.Hosted => "Hosted",
        SessionLifecyclePhase.Stopping => "Stopping",
        _ => phase.ToString(),
    };
}

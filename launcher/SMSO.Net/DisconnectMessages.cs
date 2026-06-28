namespace SMSO.Net;

public static class DisconnectMessages
{
    public static string GetUserMessage(DisconnectReason reason) => reason switch
    {
        DisconnectReason.UserRequest => "You left the session.",
        DisconnectReason.Timeout => "Lost connection to the server (timed out). Check your network and try again.",
        DisconnectReason.Kicked => "You were removed from the session by the host.",
        DisconnectReason.ServerShutdown => "The host ended the session.",
        DisconnectReason.DolphinClosed => "Disconnected because Dolphin or the game was closed.",
        _ => "Disconnected from the session.",
    };
}

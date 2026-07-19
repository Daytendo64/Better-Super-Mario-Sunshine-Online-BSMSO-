namespace SMSO.Net;

public sealed class NetJoinRejectedException : Exception
{
    public JoinRejectReason Reason { get; }

    public NetJoinRejectedException(JoinRejectReason reason)
        : base(GetUserMessage(reason))
    {
        Reason = reason;
    }

    public static string GetUserMessage(JoinRejectReason reason) => reason switch
    {
        JoinRejectReason.VersionMismatch =>
            "This BSMSO build does not match the server. Download the latest zip, replace your launcher files, and try again.",
        JoinRejectReason.NameTaken =>
            "Join rejected: that username is already in use — set a unique name in Settings.",
        JoinRejectReason.Full =>
            "Join rejected: the server is full.",
        JoinRejectReason.InvalidName =>
            "Join rejected: invalid username.",
        _ => $"Join rejected: {reason}",
    };
}

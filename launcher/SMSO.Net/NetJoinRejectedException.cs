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
            "BSMSO build mismatch (VersionMismatch). Update ALL of: launcher, " +
            $"disc/_BSMSO.kxe (re-install modules), and dedicated BSMSO.ServerHost.exe if used. " +
            $"This client is build {ProtocolConstants.ModBuildId}.",
        JoinRejectReason.ProfileMismatch =>
            "Game profile mismatch (ProfileMismatch). Host and client must point at the same game " +
            "— vanilla SMS and Super Mario Eclipse sessions cannot mix. Check Paths → Game ISO on every player.",
        JoinRejectReason.NameTaken =>
            "Join rejected: that username is already in use — set a unique name in Settings.",
        JoinRejectReason.Full =>
            "Join rejected: the server is full (or still releasing a slot — retry in a few seconds).",
        JoinRejectReason.InvalidName =>
            "Join rejected: invalid username.",
        _ => $"Join rejected: {reason}",
    };
}

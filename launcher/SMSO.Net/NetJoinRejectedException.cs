namespace SMSO.Net;

public sealed class NetJoinRejectedException : Exception
{
    public JoinRejectReason Reason { get; }

    public NetJoinRejectedException(JoinRejectReason reason)
        : base($"Join rejected: {reason}")
    {
        Reason = reason;
    }
}

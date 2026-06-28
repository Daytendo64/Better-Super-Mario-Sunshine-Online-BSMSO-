namespace SMSO.Server;

public sealed class ShineAuthority
{
    private readonly HashSet<byte> _collected = new();

    public bool TryAccept(byte shineId)
    {
        if (_collected.Contains(shineId))
            return false;

        _collected.Add(shineId);
        return true;
    }
}

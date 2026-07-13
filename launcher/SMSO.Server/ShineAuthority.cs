using System.Collections.Generic;

namespace SMSO.Server;

public sealed class ShineAuthority
{
    private readonly HashSet<byte> _collected = new();
    private readonly object _gate = new();

    public bool TryAccept(byte shineId)
    {
        lock (_gate)
        {
            if (_collected.Contains(shineId))
                return false;

            _collected.Add(shineId);
            return true;
        }
    }

    public IReadOnlyCollection<byte> Collected
    {
        get { lock (_gate) return _collected.ToArray(); }
    }

    public void Reset()
    {
        lock (_gate)
            _collected.Clear();
    }
}

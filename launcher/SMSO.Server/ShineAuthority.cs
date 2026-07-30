using System.Collections.Generic;
using SMSO.Net;

namespace SMSO.Server;

public sealed class ShineAuthority
{
    /// <summary>
    /// Vanilla Bowser ending shine. <c>TMovieDirector::decideNextMode</c> latches this
    /// when epilogue.thp (movie 14 / 0xE) finishes — FlagManager id <c>0x77</c> (119),
    /// the 120th vanilla shine (ids 0..119 / card bools <c>0x10000</c>..<c>0x10077</c>).
    /// </summary>
    public const byte BowserEpilogueShineId = 0x77;

    private readonly HashSet<byte> _collected = new();
    private readonly object _gate = new();

    /// <summary>
    /// Accept a newly collected shine id (0..<see cref="ProtocolConstants.ShineBitCapacity"/>-1).
    /// Live ShineCollected uses payload0 (byte), so the natural wire max is 255.
    /// </summary>
    public bool TryAccept(byte shineId)
    {
        // shineId is byte (0..255) which matches ShineBitCapacity == 256.
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

using System.Collections.Generic;

namespace SMSO.Server;

/// <summary>
/// Authoritative blue-coin state keyed by course. Must retain every course for the whole
/// session — players routinely split across stages, and resetting on course change caused
/// duplicate accepts plus permanent desync for anyone who missed the original broadcast.
/// Indices match vanilla TFlagManager blue-coin IDs (0..49 per shine stage / mMapObjID).
/// </summary>
public sealed class BlueCoinAuthority
{
    public const byte MaxIndexExclusive = 50;

    private readonly Dictionary<byte, ulong> _collectedByCourse = new();
    private readonly object _gate = new();

    public bool TryAccept(byte courseId, byte coinIndex)
    {
        if (coinIndex >= MaxIndexExclusive)
            return false;

        lock (_gate)
        {
            if (!_collectedByCourse.TryGetValue(courseId, out var mask))
                mask = 0;

            var bit = 1ul << coinIndex;
            if ((mask & bit) != 0)
                return false;

            _collectedByCourse[courseId] = mask | bit;
            return true;
        }
    }

    public ulong MaskForCourse(byte courseId)
    {
        lock (_gate)
            return _collectedByCourse.TryGetValue(courseId, out var mask) ? mask : 0ul;
    }

    public IReadOnlyDictionary<byte, ulong> AllCourses
    {
        get { lock (_gate) return new Dictionary<byte, ulong>(_collectedByCourse); }
    }

    public void Reset()
    {
        lock (_gate)
            _collectedByCourse.Clear();
    }
}

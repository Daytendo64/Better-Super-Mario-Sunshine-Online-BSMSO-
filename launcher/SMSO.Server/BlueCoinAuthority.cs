namespace SMSO.Server;

public sealed class BlueCoinAuthority
{
    private byte _courseId = 0xFF;
    private uint _collectedMask;

    public bool TryAccept(byte courseId, byte coinIndex)
    {
        ResetIfCourseChanged(courseId);
        if (coinIndex >= 30)
            return false;

        if ((_collectedMask & (1u << coinIndex)) != 0)
            return false;

        _collectedMask |= 1u << coinIndex;
        return true;
    }

    private void ResetIfCourseChanged(byte courseId)
    {
        if (_courseId == courseId)
            return;

        _courseId = courseId;
        _collectedMask = 0;
    }
}

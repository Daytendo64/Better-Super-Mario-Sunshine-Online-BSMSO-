namespace SMSO.Net;

/// <summary>
/// Compile-time feature switches for alternate launcher packages.
/// Lite builds set <c>BSMSO_CLIENT_LITE</c> via <c>-p:BSMSOClientLite=true</c>.
/// </summary>
public static class BuildFeatures
{
#if BSMSO_CLIENT_LITE
    public const bool ClientLite = true;
#else
    public const bool ClientLite = false;
#endif
}

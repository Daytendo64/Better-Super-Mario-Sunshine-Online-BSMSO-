namespace SMSO.Server;

/// <summary>
/// Hide &amp; Seek tag reach derived from SMS TMario collision dimensions (doldecomp/sms).
/// Snapshots export mTranslation (feet pivot), so tag checks use center-to-center distance
/// between two body cylinders, not a single point radius.
/// </summary>
public static class HideSeekTagConstants
{
    /// <summary>Horizontal collision probe radius used by retail Mario wall/ground checks.</summary>
    public const float MarioBodyRadius = 50f;

    /// <summary>Approximate standing height from feet pivot to head (world units).</summary>
    public const float MarioStandingHeight = 165f;

    /// <summary>
    /// Forgiveness beyond visual body contact for UDP position error and client-side interpolation.
    /// </summary>
    public const float TouchSlack = 22f;

    /// <summary>Extra vertical reach while jumping over a hider.</summary>
    public const float VerticalSlack = 35f;

    /// <summary>Max horizontal pivot-to-pivot distance: ~1.25 body-widths of contact + slack.</summary>
    public const float MaxHorizontalReach = MarioBodyRadius * 2.5f + TouchSlack;

    /// <summary>Max feet-to-feet vertical separation for a valid tag.</summary>
    public const float MaxVerticalSeparation = MarioStandingHeight + VerticalSlack;
}

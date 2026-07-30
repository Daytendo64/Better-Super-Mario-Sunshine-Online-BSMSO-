namespace SMSO.Net;

/// <summary>
/// Which BSE/SMS game profile a session targets. Wire id for join gating —
/// clients with different ids must never share a lobby (stage/flag semantics differ).
/// </summary>
public enum GameProfileId : ushort
{
    /// <summary>Legacy / missing profile field (rejected by current servers).</summary>
    Unspecified = 0,
    /// <summary>Vanilla Super Mario Sunshine + official BSE runtime.</summary>
    VanillaSms = 1,
    /// <summary>Super Mario Eclipse (untouched Eclipse module stack).</summary>
    MarioEclipse = 2,
}

public static class GameProfileIds
{
    public static string DisplayName(GameProfileId id) => id switch
    {
        GameProfileId.VanillaSms => "Super Mario Sunshine",
        GameProfileId.MarioEclipse => "Super Mario Eclipse",
        _ => "Unknown game",
    };

    /// <summary>Parses "vanilla"/"sms", "eclipse"/"sme" (case-insensitive) for CLI/config use.</summary>
    public static bool TryParse(string? value, out GameProfileId id)
    {
        id = GameProfileId.Unspecified;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "vanilla":
            case "sms":
            case "vanillasms":
                id = GameProfileId.VanillaSms;
                return true;
            case "eclipse":
            case "sme":
            case "marioeclipse":
                id = GameProfileId.MarioEclipse;
                return true;
            default:
                return false;
        }
    }
}

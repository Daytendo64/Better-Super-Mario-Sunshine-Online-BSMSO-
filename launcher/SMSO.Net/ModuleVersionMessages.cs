namespace SMSO.Net;

public static class ModuleVersionMessages
{
    public const string ModuleFileName = "_BSMSO.kxe";
    public const string LegacyModuleFileName = "_SMSO.kxe";
    public const string BseModuleFileName = "BetterSunshineEngine.kxe";
    public const string BseDownloadUrl = "https://github.com/DotKuribo/BetterSunshineEngine";
    public const string BseReleaseUrl = "https://github.com/DotKuribo/BetterSunshineEngine/releases";

    public static string MissingModuleFile(string path) =>
        $"BSMSO module not found at {path}. Build with tools\\build.ps1 and copy {ModuleFileName} into files/Kuribo!/Mods/ on your BSE ISO.";
}

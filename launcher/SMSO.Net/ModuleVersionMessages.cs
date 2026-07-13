namespace SMSO.Net;

public static class ModuleVersionMessages
{
    public const string ModuleFileName = "_BSMSO.kxe";
    public const string LegacyModuleFileName = "_SMSO.kxe";
    public const string BseModuleFileName = "BetterSunshineEngine.kxe";
    public const string MovesetModuleFileName = "BetterSunshineMoveset.kxe";
    public const string BseDownloadUrl = "https://github.com/DotKuribo/BetterSunshineEngine";
    public const string BseReleaseUrl = "https://github.com/DotKuribo/BetterSunshineEngine/releases";

    public static string MissingModuleFile(string path) =>
        $"BSMSO module not found at {path}. In Settings → Game modules, use Install / patch modules to install Kuribo System (KuriboKernel.bin), BSE main.dol/boot.bin, {BseModuleFileName}, {MovesetModuleFileName}, and {ModuleFileName} (extracted folder or .iso/.gcm patch).";
}

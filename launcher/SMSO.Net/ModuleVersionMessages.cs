namespace SMSO.Net;

public static class ModuleVersionMessages
{
    public const string ModuleFileName = "_BSMSO.kxe";
    public const string LegacyModuleFileName = "_SMSO.kxe";
    public const string BseModuleFileName = "BetterSunshineEngine.kxe";
    public const string MovesetModuleFileName = "BetterSunshineMoveset.kxe";
    public const string BseDownloadUrl = "https://github.com/DotKuribo/BetterSunshineEngine";
    public const string BseReleaseUrl = "https://github.com/DotKuribo/BetterSunshineEngine/releases";

    /// <summary>
    /// Sidecar written beside <c>Kuribo!/Mods</c> (in the <c>Kuribo!</c> folder — NOT inside
    /// Mods). Kuribo loads every file under Mods as a module; a marker there black-screens boot.
    /// Disc images use a sibling sidecar via <see cref="ModuleInstaller.GetDiscImageModBuildIdMarkerPath"/>.
    /// </summary>
    public const string ModBuildIdMarkerFileName = ".bsmso-mod-build-id";

    public const string InstallModuleButtonLabel = "Install / patch modules";
    public const string UpdateModuleButtonLabel = "Update module";

    public const string RestartRequiredForUpdate =
        "Updated BSMSO module installed — close Dolphin and Launch Dolphin again before hosting or connecting.";

    public const string UpdateRequired =
        "Outdated BSMSO module — press Update module to install the bundled _BSMSO.kxe.";

    public const string LauncherUpdateRequiredGeneric =
        "Download the latest BSMSO zip and replace BSMSO.Launcher.exe and _BSMSO.kxe (then run Update module).";

    /// <summary>
    /// Shown when installed modules match this launcher's ModBuildId and no launcher update is required.
    /// </summary>
    public const string EverythingUpToDateReadyToPlay =
        "Everything is up to date — ready to play";

    public static string EverythingUpToDateReadyToPlayWithBuild(ushort buildId) =>
        $"{EverythingUpToDateReadyToPlay} (build {buildId}).";

    public static string LauncherUpdateRequired(ushort localBuild, ushort remoteBuild) =>
        $"Outdated BSMSO launcher (build {localBuild}) — latest is build {remoteBuild}. " +
        LauncherUpdateRequiredGeneric;

    public static string MissingModuleFile(string path) =>
        $"BSMSO module not found at {path}. Settings → Game modules → Install / patch modules to install Kuribo, BSE main.dol/boot.bin, {BseModuleFileName}, {MovesetModuleFileName}, and {ModuleFileName}.";
}

using System;
using System.Collections.Generic;
using System.IO;
using SMSO.Net;

namespace SMSO.Launcher;

public enum GameProfileKind
{
    Unknown,
    VanillaSms,
    MarioEclipse,
}

public sealed class GameProfile
{
    public required GameProfileKind Kind { get; init; }
    public required GameProfileId Id { get; init; }
    public required string DisplayName { get; init; }
    public required string? GameId { get; init; }
    /// <summary>Eclipse-specific files detected (module / runtime / disc signature).</summary>
    public required IReadOnlyList<string> Evidence { get; init; }
    /// <summary>Hard blockers before an additive Eclipse install can proceed.</summary>
    public required IReadOnlyList<string> BlockingIssues { get; init; }
    public required string StatusMessage { get; init; }

    public bool IsEclipse => Kind == GameProfileKind.MarioEclipse;
}

/// <summary>
/// Detects which BSE game profile a Game ISO path belongs to (vanilla SMS vs Super Mario
/// Eclipse) from the boot game id plus Eclipse-only module files. Detection never writes
/// anything — Install uses it to switch from full-runtime install to additive-only.
/// </summary>
internal static class GameProfileDetector
{
    public const string EclipseModuleFileName = "SuperMarioEclipse.kxe";
    public const string EclipseMirrorModuleFileName = "MirrorMode.kxe";
    /// <summary>Eclipse-bundled BSE build (differs from the official v4.0.0 BSMSO pins).</summary>
    public const long EclipseBseSizeBytes = 571_104;
    public const long EclipseMovesetSizeBytes = 43_520;

    private const string KuriboSystemProbeFile = "KuriboKernel.bin";
    private const string KuriboSystemProbeDirectory = @"Kuribo!\System";

    public static GameProfile Detect(string? gamePath)
    {
        var evidence = new List<string>();
        var blocking = new List<string>();
        string? gameId = null;

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return new GameProfile
            {
                Kind = GameProfileKind.Unknown,
                Id = GameProfileId.Unspecified,
                DisplayName = "Unknown",
                GameId = null,
                Evidence = evidence,
                BlockingIssues = blocking,
                StatusMessage = "Set Game ISO path to detect the game profile.",
            };
        }

        var trimmed = gamePath.Trim().Trim('"');
        var kind = ModuleInstallValidator.ClassifyInstallTarget(trimmed);

        if (GameIdentity.TryResolveBootBinPath(trimmed, out var bootPath) &&
            GameIdentity.TryReadGameId(bootPath, out var readId))
        {
            gameId = readId;
            if (string.Equals(readId, GameIdentity.MarioEclipseGameId, StringComparison.Ordinal))
                evidence.Add($"game id {readId}");
        }

        var modsDir = ResolveModsDirectory(trimmed, kind);
        var eclipseModulePath = modsDir != null
            ? Path.Combine(modsDir, EclipseModuleFileName)
            : null;
        var hasEclipseModule = eclipseModulePath != null && File.Exists(eclipseModulePath);
        if (hasEclipseModule)
            evidence.Add($"{EclipseModuleFileName} ({new FileInfo(eclipseModulePath!).Length} bytes)");

        var hasEclipseGameId = string.Equals(gameId, GameIdentity.MarioEclipseGameId, StringComparison.Ordinal);

        if (!hasEclipseModule && !hasEclipseGameId)
        {
            var vanilla = new GameProfile
            {
                Kind = gameId != null ? GameProfileKind.VanillaSms : GameProfileKind.Unknown,
                Id = GameProfileId.VanillaSms,
                DisplayName = GameProfileIds.DisplayName(GameProfileId.VanillaSms),
                GameId = gameId,
                Evidence = evidence,
                BlockingIssues = blocking,
                StatusMessage = gameId != null
                    ? $"Vanilla SMS path (game id {gameId}). Full BSE runtime Install applies."
                    : "Game profile not recognized — vanilla Install path applies once a valid SMS target is set.",
            };
            return vanilla;
        }

        if (hasEclipseModule && !hasEclipseGameId)
            evidence.Add("no GMSE04 game id (module-only Eclipse tree)");

        // Eclipse path: additive install must never replace Eclipse's own runtime.
        if (kind == ModuleInstallTargetKind.ExtractedFolder && modsDir != null)
        {
            var bsePath = Path.Combine(modsDir, ModuleVersionMessages.BseModuleFileName);
            if (!File.Exists(bsePath))
                blocking.Add($"{ModuleVersionMessages.BseModuleFileName} missing from Eclipse Mods — run the official Eclipse installer first.");
            else
                evidence.Add($"Eclipse {ModuleVersionMessages.BseModuleFileName} ({new FileInfo(bsePath).Length} bytes — kept untouched)");

            var movesetPath = Path.Combine(modsDir, ModuleVersionMessages.MovesetModuleFileName);
            if (File.Exists(movesetPath))
                evidence.Add($"Eclipse {ModuleVersionMessages.MovesetModuleFileName} ({new FileInfo(movesetPath).Length} bytes — kept untouched)");

            if (File.Exists(Path.Combine(modsDir, EclipseMirrorModuleFileName)))
                evidence.Add(EclipseMirrorModuleFileName);

            if (trimmed.Length > 0 && !Directory.Exists(Path.Combine(trimmed, "files")))
            {
                // sys-only probe: Kuribo runtime cannot be verified.
            }
            else if (!File.Exists(Path.Combine(trimmed, "files", KuriboSystemProbeDirectory, KuriboSystemProbeFile)))
            {
                blocking.Add($"files\\{KuriboSystemProbeDirectory}\\{KuriboSystemProbeFile} missing — Eclipse Kuribo runtime is incomplete; re-run the official Eclipse installer.");
            }

            var sysMainDol = Path.Combine(trimmed, "sys", "main.dol");
            if (Directory.Exists(trimmed) && !File.Exists(sysMainDol))
                blocking.Add("sys\\main.dol missing — Eclipse runtime is incomplete; re-run the official Eclipse installer.");
        }

        var display = GameProfileIds.DisplayName(GameProfileId.MarioEclipse);
        var status = blocking.Count > 0
            ? $"{display} detected, but the Eclipse runtime is incomplete:\n- {string.Join("\n- ", blocking)}\n" +
              "BSMSO will NOT overwrite Eclipse's BSE — fix the Eclipse install first."
            : $"{display} detected ({string.Join(", ", evidence)}).\n" +
              "Install is additive-only: _BSMSO.kxe + Mario packs are added; Eclipse's BSE, Moveset, main.dol and boot.bin are never touched.";

        return new GameProfile
        {
            Kind = GameProfileKind.MarioEclipse,
            Id = GameProfileId.MarioEclipse,
            DisplayName = display,
            GameId = gameId,
            Evidence = evidence,
            BlockingIssues = blocking,
            StatusMessage = status,
        };
    }

    private static string? ResolveModsDirectory(string trimmed, ModuleInstallTargetKind kind)
    {
        if (kind == ModuleInstallTargetKind.ExtractedFolder &&
            ModuleInstallValidator.TryResolveGameRoot(trimmed, out var gameRoot) &&
            gameRoot != null)
        {
            return Path.Combine(gameRoot, "files", "Kuribo!", "Mods");
        }

        if (kind == ModuleInstallTargetKind.DiscImage &&
            ModuleInstallValidator.TryResolveGameRoot(trimmed, out var siblingRoot) &&
            siblingRoot != null)
        {
            // A sibling extracted Eclipse tree (if any) carries the module evidence.
            var mods = Path.Combine(siblingRoot, "files", "Kuribo!", "Mods");
            if (Directory.Exists(mods))
                return mods;
        }

        return null;
    }
}

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SMSO.Net;

namespace SMSO.Launcher;

/// <summary>
/// Compares this process's <see cref="ProtocolConstants.ModBuildId"/> against
/// <see cref="LauncherUpdateManifest"/> shipped beside the launcher in the
/// release zip (<c>latest-build.json</c>). Optional HTTP(S) override via env
/// <c>BSMSO_UPDATE_MANIFEST_URL</c> or config <c>UpdateManifestUrl</c>.
/// </summary>
public static class LauncherUpdateChecker
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    static LauncherUpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"BSMSO-Launcher/{ProtocolConstants.ModBuildId}");
    }

    /// <summary>
    /// Optional remote URL. Empty means use the bundled zip sidecar
    /// (<see cref="ResolveBundledManifestPath"/>).
    /// </summary>
    public static string? ResolveManifestUrl(string? configuredUrl = null)
    {
        var env = Environment.GetEnvironmentVariable("BSMSO_UPDATE_MANIFEST_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();
        if (!string.IsNullOrWhiteSpace(configuredUrl))
            return configuredUrl.Trim();
        return null;
    }

    /// <summary>
    /// Looks for <see cref="LauncherUpdateManifest.FileName"/> next to the exe,
    /// then under <c>assets/</c> (both are packaged in the release zip).
    /// </summary>
    public static string? ResolveBundledManifestPath(string? baseDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        var candidates = new[]
        {
            Path.Combine(root, LauncherUpdateManifest.FileName),
            Path.Combine(root, "assets", LauncherUpdateManifest.FileName),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Reads <c>versionLabel</c> from the zip-bundled <c>latest-build.json</c>
    /// so the same binary can ship as 1.1 and 2.0 packages.
    /// </summary>
    public static string ResolveProductVersionLabel(
        string? baseDirectory = null,
        string fallback = LauncherUpdateManifest.DefaultVersionLabel)
    {
        var path = ResolveBundledManifestPath(baseDirectory);
        if (path == null)
            return fallback;

        try
        {
            var json = File.ReadAllText(path);
            if (LauncherUpdateManifest.TryParse(json, out var manifest) &&
                !string.IsNullOrWhiteSpace(manifest?.VersionLabel))
            {
                return manifest!.VersionLabel!.Trim();
            }
        }
        catch
        {
            // Fall through — missing/unreadable sidecar must not break startup.
        }

        return fallback;
    }

    public static async Task<LauncherUpdateCheckResult> CheckAsync(
        string? configuredUrl = null,
        CancellationToken cancellationToken = default)
    {
        var url = ResolveManifestUrl(configuredUrl);
        if (!string.IsNullOrWhiteSpace(url))
            return await CheckFromUrlAsync(url, cancellationToken).ConfigureAwait(false);

        var path = ResolveBundledManifestPath();
        if (path == null)
        {
            return LauncherUpdateCheckResult.Unavailable(
                "No bundled latest-build.json next to the launcher (extract the full BSMSO zip)");
        }

        return await CheckFromFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LauncherUpdateCheckResult> CheckFromUrlAsync(
        string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return LauncherUpdateCheckResult.Unavailable(
                    $"Update check HTTP {(int)response.StatusCode} from {url}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return EvaluateManifestJson(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail open — offline / bad URL must not block play.
            return LauncherUpdateCheckResult.Unavailable(ex.Message);
        }
    }

    private static async Task<LauncherUpdateCheckResult> CheckFromFileAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return EvaluateManifestJson(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LauncherUpdateCheckResult.Unavailable(ex.Message);
        }
    }

    private static LauncherUpdateCheckResult EvaluateManifestJson(string json)
    {
        if (!LauncherUpdateManifest.TryParse(json, out var manifest) || manifest == null)
        {
            return LauncherUpdateCheckResult.Unavailable(
                "Update check found an invalid latest-build.json");
        }

        var local = ProtocolConstants.ModBuildId;
        if (!manifest.IsNewerThan(local))
            return LauncherUpdateCheckResult.UpToDate(manifest);

        return LauncherUpdateCheckResult.UpdateAvailable(manifest, local);
    }
}

public sealed class LauncherUpdateCheckResult
{
    public bool CheckedSuccessfully { get; init; }
    public bool UpdateRequired { get; init; }
    public ushort LocalBuildId { get; init; }
    public LauncherUpdateManifest? Manifest { get; init; }
    public string? Detail { get; init; }

    public string UserMessage =>
        !UpdateRequired || Manifest == null
            ? string.Empty
            : ModuleVersionMessages.LauncherUpdateRequired(LocalBuildId, Manifest.ModBuildId);

    public static LauncherUpdateCheckResult UpToDate(LauncherUpdateManifest manifest) => new()
    {
        CheckedSuccessfully = true,
        UpdateRequired = false,
        LocalBuildId = ProtocolConstants.ModBuildId,
        Manifest = manifest,
    };

    public static LauncherUpdateCheckResult UpdateAvailable(
        LauncherUpdateManifest manifest, ushort localBuildId) => new()
    {
        CheckedSuccessfully = true,
        UpdateRequired = true,
        LocalBuildId = localBuildId,
        Manifest = manifest,
    };

    public static LauncherUpdateCheckResult Unavailable(string detail) => new()
    {
        CheckedSuccessfully = false,
        UpdateRequired = false,
        LocalBuildId = ProtocolConstants.ModBuildId,
        Detail = detail,
    };
}

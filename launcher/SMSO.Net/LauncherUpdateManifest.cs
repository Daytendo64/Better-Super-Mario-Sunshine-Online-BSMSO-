using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMSO.Net;

/// <summary>
/// "Latest build" document shipped in each release zip beside the launcher
/// (<see cref="FileName"/>) so an older exe can detect it is behind when a
/// newer json is dropped in from a newer zip. Optional HTTP override via env
/// <c>BSMSO_UPDATE_MANIFEST_URL</c> or config <c>UpdateManifestUrl</c>.
/// </summary>
public sealed class LauncherUpdateManifest
{
    public const string FileName = "latest-build.json";

    [JsonPropertyName("modBuildId")]
    public ushort ModBuildId { get; set; }

    [JsonPropertyName("versionLabel")]
    public string? VersionLabel { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    public bool IsNewerThan(ushort localBuildId) => ModBuildId > localBuildId;

    /// <summary>
    /// Product label for UI / zip branding. Falls back to <paramref name="fallback"/>
    /// when the bundled sidecar is missing or has no <c>versionLabel</c>.
    /// </summary>
    public const string DefaultVersionLabel = "2.0";

    public static bool TryParse(string json, out LauncherUpdateManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize(
                json,
                LauncherUpdateManifestJsonContext.Default.LauncherUpdateManifest);
            if (parsed == null || parsed.ModBuildId == 0)
                return false;
            manifest = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string BuildJson(ushort modBuildId, string versionLabel = DefaultVersionLabel,
        string? downloadUrl = null, string? message = null)
    {
        var manifest = new LauncherUpdateManifest
        {
            ModBuildId = modBuildId,
            VersionLabel = versionLabel,
            DownloadUrl = string.IsNullOrWhiteSpace(downloadUrl) ? null : downloadUrl,
            Message = string.IsNullOrWhiteSpace(message)
                ? ModuleVersionMessages.LauncherUpdateRequiredGeneric
                : message,
        };
        return JsonSerializer.Serialize(
            manifest,
            LauncherUpdateManifestJsonContext.Default.LauncherUpdateManifest) + Environment.NewLine;
    }
}

[JsonSerializable(typeof(LauncherUpdateManifest))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class LauncherUpdateManifestJsonContext : JsonSerializerContext
{
}

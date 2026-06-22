using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMSO.Net;

public sealed class EpisodeNameReference
{
    public string Region { get; set; } = "NTSC-U";
    public string Source { get; set; } = "wiki";
    public List<EpisodeNameEntry> Entries { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static EpisodeNameReference Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EpisodeNameReference>(json, JsonOptions) ?? new EpisodeNameReference();
    }

    public static string FormatDisplayName(EpisodeNameEntry entry)
    {
        return entry.Format switch
        {
            "hub" => entry.Title,
            "numbered" => $"Episode {entry.EpisodeId + 1} \u2014 {entry.Title}",
            _ => $"Episode 1 \u2014 {entry.Title}",
        };
    }

    public byte ResolveExpectedScenario(byte courseId, byte episodeId)
    {
        var entry = Entries.FirstOrDefault(e => e.CourseId == courseId && e.EpisodeId == episodeId);
        if (entry == null)
            return episodeId;
        return entry.ExpectedScenario ?? episodeId;
    }

    public int ApplyToLevels(string levelsPath)
    {
        var json = File.ReadAllText(levelsPath);
        var root = JsonSerializer.Deserialize<LevelRoot>(json, JsonOptions)
                   ?? throw new InvalidOperationException("Invalid levels JSON");

        var byKey = Entries.ToDictionary(e => (e.CourseId, e.EpisodeId));
        var updated = 0;

        foreach (var course in root.Courses)
        {
            foreach (var episode in course.Episodes)
            {
                if (!byKey.TryGetValue((course.CourseId, episode.EpisodeId), out var entry))
                    continue;

                var displayName = FormatDisplayName(entry);
                if (episode.DisplayName != displayName)
                {
                    episode.DisplayName = displayName;
                    updated++;
                }
            }
        }

        var writeOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(levelsPath, JsonSerializer.Serialize(root, writeOptions));
        return updated;
    }
}

public sealed class EpisodeNameEntry
{
    public byte CourseId { get; set; }
    public byte EpisodeId { get; set; }
    public string Title { get; set; } = "";
    public string Format { get; set; } = "numbered";

    [JsonPropertyName("expectedScenario")]
    public byte? ExpectedScenario { get; set; }
}

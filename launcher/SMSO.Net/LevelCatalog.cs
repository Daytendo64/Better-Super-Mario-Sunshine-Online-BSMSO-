using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMSO.Net;

public sealed class LevelCatalog
{
    public List<CourseEntry> Courses { get; set; } = new();

    public static LevelCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize<LevelRoot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        return new LevelCatalog { Courses = root?.Courses ?? new List<CourseEntry>() };
    }

    public CourseEntry? FindCourse(byte courseId) => Courses.FirstOrDefault(c => c.CourseId == courseId);

    public bool IsValidWarp(byte courseId, byte episodeId)
    {
        var course = FindCourse(courseId);
        if (course == null || !course.Warpable) return false;
        return course.Episodes.Any(e => e.EpisodeId == episodeId);
    }

    public static byte NormalizeEpisodeFromGame(byte courseId, byte gameScenarioId)
    {
        if (courseId == DelfinoPlazaMapping.AreaId &&
            DelfinoPlazaMapping.TryScenarioToCatalog(gameScenarioId, out var catalogId))
        {
            return catalogId;
        }

        return gameScenarioId;
    }

    public static byte ResolveEpisodeForWarp(byte courseId, byte catalogEpisodeId)
    {
        if (courseId == DelfinoPlazaMapping.AreaId &&
            DelfinoPlazaMapping.TryCatalogToScenario(catalogEpisodeId, out var scenarioId))
        {
            return scenarioId;
        }

        return catalogEpisodeId;
    }

    public string GetCourseName(byte courseId)
    {
        if (courseId == 15)
            return "Title / File Select";
        return FindCourse(courseId)?.DisplayName ?? $"Course {courseId}";
    }

    public string GetEpisodeDisplayName(byte courseId, byte catalogEpisodeId)
    {
        var course = FindCourse(courseId);
        return course?.Episodes.FirstOrDefault(e => e.EpisodeId == catalogEpisodeId)?.DisplayName
               ?? $"Episode {catalogEpisodeId + 1}";
    }
}

public sealed class LevelRoot
{
    public List<CourseEntry> Courses { get; set; } = new();
}

public sealed class CourseEntry
{
  public byte CourseId { get; set; }
  public string DisplayName { get; set; } = "";
  public bool Warpable { get; set; }
  public List<EpisodeEntry> Episodes { get; set; } = new();
}

public sealed class EpisodeEntry
{
  public byte EpisodeId { get; set; }
  public string DisplayName { get; set; } = "";
}

public sealed class PlayerRosterEntry
{
  public byte Slot { get; set; }
  public string Username { get; set; } = "";
  public byte StageId { get; set; }
  public byte EpisodeId { get; set; }
  public DolphinState State { get; set; }
  public ushort PingMs { get; set; }
  public bool Connected { get; set; } = true;
}

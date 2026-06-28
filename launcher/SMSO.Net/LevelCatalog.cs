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

        if (courseId == SirenaHotelInteriorMapping.AreaId &&
            SirenaHotelInteriorMapping.TryScenarioToCatalog(gameScenarioId, out catalogId))
        {
            return catalogId;
        }

        return gameScenarioId;
    }

    public static byte ResolveEpisodeForWarp(byte courseId, byte catalogEpisodeId)
    {
        if (TryResolveWarpDestination(courseId, catalogEpisodeId, out var areaId, out var missionScenario))
            return missionScenario;

        if (courseId == DelfinoPlazaMapping.AreaId &&
            DelfinoPlazaMapping.TryCatalogToScenario(catalogEpisodeId, out var scenarioId))
        {
            return scenarioId;
        }

        if (courseId == SirenaHotelInteriorMapping.AreaId &&
            SirenaHotelInteriorMapping.TryCatalogToScenario(catalogEpisodeId, out scenarioId))
        {
            return scenarioId;
        }

        return catalogEpisodeId;
    }

    /// <summary>
    /// Shadow Mario and hotel red coins play in area 7 even when selected from Sirena Beach (area 6).
    /// </summary>
    public static bool TryResolveWarpDestination(byte courseId, byte catalogEpisodeId, out byte areaId,
        out byte missionScenario)
    {
        if (courseId == 6 && (catalogEpisodeId == 6 || catalogEpisodeId == 7) &&
            SirenaHotelInteriorMapping.TryCatalogToMissionScenario(catalogEpisodeId, out missionScenario))
        {
            areaId = SirenaHotelInteriorMapping.AreaId;
            return true;
        }

        areaId = courseId;
        missionScenario = catalogEpisodeId;
        return false;
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

    /// <summary>Warpable courses sorted for teleport dropdowns (story → plaza → secrets → minigames).</summary>
    public IReadOnlyList<WarpCourseListItem> GetOrganizedWarpCourses()
    {
        var ordered = Courses
            .Where(c => c.Warpable)
            .Select(c => new WarpCourseListItem(c, ClassifyWarpGroup(c)))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Course.CourseId)
            .ToList();

        string? lastGroup = null;
        var result = new List<WarpCourseListItem>(ordered.Count);
        foreach (var item in ordered)
        {
            var showHeader = item.Group != lastGroup;
            result.Add(new WarpCourseListItem(item.Course, item.Group, showHeader));
            lastGroup = item.Group;
        }

        return result;
    }

    private static string ClassifyWarpGroup(CourseEntry course)
    {
        var id = course.CourseId;
        if (id <= 9)
            return "Main Story";
        if (id is >= 20 and <= 29)
            return "Delfino Plaza";
        if (id == 52)
            return "Final Area";
        if (id == 30)
            return "Minigames";
        if (course.DisplayName.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            course.DisplayName.Contains('\u2014') && course.DisplayName.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            return "Secret Areas";
        if (course.DisplayName.Contains('\u2014'))
            return "Sub-Areas";
        return "Other Areas";
    }
}

public interface IWarpListEntry
{
    string DisplayName { get; }
}

public sealed class WarpCourseListItem : IWarpListEntry
{
    public WarpCourseListItem(CourseEntry course, string group, bool showGroupHeader = false)
    {
        Course = course;
        Group = group;
        ShowGroupHeader = showGroupHeader;
        DisplayName = course.DisplayName;
        SortOrder = group switch
        {
            "Main Story" => 0,
            "Delfino Plaza" => 1,
            "Sub-Areas" => 2,
            "Secret Areas" => 3,
            "Minigames" => 4,
            "Final Area" => 5,
            _ => 6,
        };
    }

    public CourseEntry Course { get; }
    public string Group { get; }
    public bool ShowGroupHeader { get; }
    public string DisplayName { get; }
    public int SortOrder { get; }
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

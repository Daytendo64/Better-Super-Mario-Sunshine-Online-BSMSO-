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

    /// <summary>
    /// Maps an in-game scenario index (<c>mEpisodeID</c>) to a catalog episode id for roster/display.
    /// Plaza and hotel interiors use dedicated scenario↔catalog tables; other courses usually match 1:1.
    /// When <paramref name="catalog"/> is provided, unknown ids on single-episode courses (secrets,
    /// plaza minigames) coerce to that course's sole catalog episode — the game often inherits the
    /// parent episode id when entering those areas.
    /// </summary>
    public static byte NormalizeEpisodeFromGame(byte courseId, byte gameScenarioId,
        LevelCatalog? catalog = null)
    {
        byte catalogId;
        if (courseId == DelfinoPlazaMapping.AreaId &&
            DelfinoPlazaMapping.TryScenarioToCatalog(gameScenarioId, out catalogId))
        {
            // mapped
        }
        else if (courseId == SirenaHotelInteriorMapping.AreaId &&
                 SirenaHotelInteriorMapping.TryScenarioToCatalog(gameScenarioId, out catalogId))
        {
            // mapped
        }
        else if (courseId == PinnaParkInteriorMapping.AreaId &&
                 PinnaParkInteriorMapping.TryScenarioToCatalog(gameScenarioId, out catalogId))
        {
            // mapped
        }
        else if (courseId == SirenaCasinoMapping.AreaId &&
                 SirenaCasinoMapping.TryScenarioToCatalog(gameScenarioId, out catalogId))
        {
            // mapped
        }
        else
        {
            catalogId = gameScenarioId;
        }

        return catalog?.CoerceToKnownEpisode(courseId, catalogId) ?? catalogId;
    }

    /// <summary>
    /// If <paramref name="catalogEpisodeId"/> is not listed for the course but the course has exactly
    /// one catalog episode, return that episode (typical for secret/plaza sub-areas).
    /// </summary>
    public byte CoerceToKnownEpisode(byte courseId, byte catalogEpisodeId)
    {
        var course = FindCourse(courseId);
        if (course == null || course.Episodes.Count == 0)
            return catalogEpisodeId;

        if (course.Episodes.Any(e => e.EpisodeId == catalogEpisodeId))
            return catalogEpisodeId;

        if (course.Episodes.Count == 1)
            return course.Episodes[0].EpisodeId;

        return catalogEpisodeId;
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

        if (courseId == PinnaParkInteriorMapping.AreaId &&
            PinnaParkInteriorMapping.TryCatalogToScenario(catalogEpisodeId, out scenarioId))
        {
            return scenarioId;
        }

        if (courseId == SirenaCasinoMapping.AreaId &&
            SirenaCasinoMapping.TryCatalogToMission(catalogEpisodeId, out scenarioId))
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
        if (course == null)
            return $"Episode {catalogEpisodeId + 1}";

        var match = course.Episodes.FirstOrDefault(e => e.EpisodeId == catalogEpisodeId);
        if (match != null)
            return match.DisplayName;

        // Accept raw in-game scenario ids when the roster/UI path skipped Normalize.
        // Only remap when the scenario is not already a listed catalog episode id (plaza
        // catalog 0..7 overlaps some scenario numbers — never reinterpret a direct miss
        // that Normalize would change into a different listed id via ambiguous overlap).
        if (courseId == DelfinoPlazaMapping.AreaId &&
            DelfinoPlazaMapping.TryScenarioToCatalog(catalogEpisodeId, out var plazaMapped) &&
            plazaMapped != catalogEpisodeId)
        {
            match = course.Episodes.FirstOrDefault(e => e.EpisodeId == plazaMapped);
            if (match != null)
                return match.DisplayName;
        }

        // Hotel / Pinna park: accept raw mission scenarios if not already normalized.
        if (courseId == SirenaHotelInteriorMapping.AreaId &&
            SirenaHotelInteriorMapping.TryScenarioToCatalog(catalogEpisodeId, out var mapped) &&
            mapped != catalogEpisodeId)
        {
            match = course.Episodes.FirstOrDefault(e => e.EpisodeId == mapped);
            if (match != null)
                return match.DisplayName;
        }

        if (courseId == PinnaParkInteriorMapping.AreaId &&
            PinnaParkInteriorMapping.TryScenarioToCatalog(catalogEpisodeId, out mapped) &&
            mapped != catalogEpisodeId)
        {
            match = course.Episodes.FirstOrDefault(e => e.EpisodeId == mapped);
            if (match != null)
                return match.DisplayName;
        }

        if (courseId == SirenaCasinoMapping.AreaId &&
            SirenaCasinoMapping.TryScenarioToCatalog(catalogEpisodeId, out mapped) &&
            mapped != catalogEpisodeId)
        {
            match = course.Episodes.FirstOrDefault(e => e.EpisodeId == mapped);
            if (match != null)
                return match.DisplayName;
        }

        // Secrets / plaza interiors: game may still report a parent scenario id.
        if (course.Episodes.Count == 1)
            return course.Episodes[0].DisplayName;

        return $"Episode {catalogEpisodeId + 1}";
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
  /// <summary>8-char hex model id, or empty for retail Mario.</summary>
  public string MarioModelId { get; set; } = "";
}

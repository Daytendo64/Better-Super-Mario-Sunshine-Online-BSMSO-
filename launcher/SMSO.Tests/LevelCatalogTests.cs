using SMSO.Net;

namespace SMSO.Tests;

public class LevelCatalogTests
{
    [Fact]
    public void NormalizeEpisodeFromGame_DelfinoPlaza_Flooded()
    {
        var normalized = LevelCatalog.NormalizeEpisodeFromGame(DelfinoPlazaMapping.AreaId, 9);
        Assert.Equal(6, normalized);
    }

    [Fact]
    public void NormalizeEpisodeFromGame_DelfinoPlaza_MainHub()
    {
        var normalized = LevelCatalog.NormalizeEpisodeFromGame(DelfinoPlazaMapping.AreaId, 8);
        Assert.Equal(0, normalized);
        Assert.Contains("Main Hub",
            LevelCatalog.Load(FindLevelsPath()).GetEpisodeDisplayName(DelfinoPlazaMapping.AreaId, normalized),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(47, 2)] // Hillside Cave often inherits Bianco parent scenario
    [InlineData(21, 8)] // Plaza Super Slide may inherit plaza scenario
    [InlineData(56, 4)] // King Boo Down Below
    [InlineData(30, 1)] // Blooper surfing minigame
    public void NormalizeEpisodeFromGame_SingleEpisodeCourses_CoerceToSoleEpisode(
        byte courseId, byte gameScenarioId)
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var course = catalog.FindCourse(courseId);
        Assert.NotNull(course);
        Assert.Single(course!.Episodes);

        var normalized = LevelCatalog.NormalizeEpisodeFromGame(courseId, gameScenarioId, catalog);
        Assert.Equal(course.Episodes[0].EpisodeId, normalized);
        Assert.Equal(course.Episodes[0].DisplayName,
            catalog.GetEpisodeDisplayName(courseId, gameScenarioId));
    }

    [Theory]
    [InlineData(7, 4, 7, "Red Coins in the Hotel")] // mission scenario 4 → catalog 7
    [InlineData(7, 6, 6, "Shadow Mario Checks In")]
    [InlineData(7, 0, 0, "Hotel Lobby")]
    [InlineData(7, 2, 2, "Mysterious Hotel Delfino")]
    public void NormalizeEpisodeFromGame_SirenaHotel_MapsAndDisplays(
        byte courseId, byte gameScenarioId, byte catalogEpisodeId, string titleFragment)
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var normalized = LevelCatalog.NormalizeEpisodeFromGame(courseId, gameScenarioId, catalog);
        Assert.Equal(catalogEpisodeId, normalized);
        Assert.Contains(titleFragment,
            catalog.GetEpisodeDisplayName(courseId, normalized),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetOrganizedWarpCourses_InsertsGroupHeaderFlags()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var courses = catalog.GetOrganizedWarpCourses();
        Assert.NotEmpty(courses);
        Assert.Contains(courses, c => c.ShowGroupHeader && c.Group == "Main Story");
        Assert.Contains(courses, c => !c.ShowGroupHeader && c.Group == "Main Story");
    }

    [Fact]
    public void GetEpisodeDisplayName_UsesCatalogEpisodeId()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var label = catalog.GetEpisodeDisplayName(DelfinoPlazaMapping.AreaId, 7);
        Assert.Contains("Post-Flood", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EpisodeReference_MatchesLevelsCatalog()
    {
        var levelsPath = FindLevelsPath();
        var referencePath = FindEpisodeNamesPath();
        if (!File.Exists(levelsPath) || !File.Exists(referencePath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var reference = EpisodeNameReference.Load(referencePath);

        foreach (var entry in reference.Entries)
        {
            Assert.True(catalog.IsValidWarp(entry.CourseId, entry.EpisodeId),
                $"Invalid warp entry course={entry.CourseId} episode={entry.EpisodeId}");

            var expectedScenario = reference.ResolveExpectedScenario(entry.CourseId, entry.EpisodeId);
            Assert.Equal(expectedScenario,
                LevelCatalog.ResolveEpisodeForWarp(entry.CourseId, entry.EpisodeId));

            var expectedDisplay = EpisodeNameReference.FormatDisplayName(entry);
            var actualDisplay = catalog.GetEpisodeDisplayName(entry.CourseId, entry.EpisodeId);
            Assert.Equal(expectedDisplay, actualDisplay);
        }
    }

    [Theory]
    [InlineData(2, 3, "Red Coins of Windmill Village")]
    [InlineData(3, 0, "Gooper Blooper Breaks Out")]
    [InlineData(3, 1, "Blooper Surfing Safari")]
    [InlineData(5, 0, "Mecha-Bowser Appears!")]
    [InlineData(5, 7, "Roller Coaster Balloons")]
    [InlineData(7, 0, "Hotel Lobby")]
    [InlineData(7, 2, "Mysterious Hotel Delfino")]
    [InlineData(7, 6, "Shadow Mario Checks In")]
    [InlineData(7, 7, "Red Coins in the Hotel")]
    [InlineData(56, 0, "King Boo Down Below")]
    [InlineData(8, 2, "The Goopy Inferno")]
    [InlineData(9, 7, "The Red Coin Fish")]
    public void EpisodeReference_SpotCheckTitles(byte courseId, byte episodeId, string titleFragment)
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var label = catalog.GetEpisodeDisplayName(courseId, episodeId);
        Assert.Contains(titleFragment, label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EpisodeReference_DelfinoPlazaWarpScenarios()
    {
        var referencePath = FindEpisodeNamesPath();
        if (!File.Exists(referencePath))
            return;

        var reference = EpisodeNameReference.Load(referencePath);
        var plaza = reference.Entries.Where(e => e.CourseId == DelfinoPlazaMapping.AreaId).ToList();
        Assert.Equal(8, plaza.Count);

        foreach (var entry in plaza)
        {
            Assert.NotNull(entry.ExpectedScenario);
            Assert.Equal(entry.ExpectedScenario.Value,
                LevelCatalog.ResolveEpisodeForWarp(entry.CourseId, entry.EpisodeId));
        }
    }

    private static string FindLevelsPath()
    {
        foreach (var path in AssetCandidates("levels.ntsc-u.json"))
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }

        return AssetCandidates("levels.ntsc-u.json")[0];
    }

    private static string FindEpisodeNamesPath()
    {
        foreach (var path in AssetCandidates("episode-names.ntsc-u.json"))
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }

        return AssetCandidates("episode-names.ntsc-u.json")[0];
    }

    private static string[] AssetCandidates(string fileName)
    {
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "assets", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", fileName),
        };
    }
}

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
    [InlineData(33, 3)] // Sand Bird often inherits Gelato parent scenario
    [InlineData(44, 2)] // Bottle inherits Noki parent scenario
    [InlineData(55, 1)] // Petey boss arena
    [InlineData(57, 3)] // Eely-Mouth boss arena
    [InlineData(58, 0)] // Pinna roller coaster / Mecha-Bowser
    [InlineData(59, 0)] // Gooper Blooper boss arena
    [InlineData(60, 0)] // Corona Bowser arena
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

    [Fact]
    public void MareUndersea_OnlyLoadEpisodeZeroIsWarpable()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        Assert.True(catalog.IsValidWarp(16, 0));
        Assert.False(catalog.IsValidWarp(16, 3));
        Assert.False(catalog.IsValidWarp(16, 7));
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
    [InlineData(16, 0, "Undersea")]
    [InlineData(33, 0, "The Sand Bird is Born")]
    [InlineData(44, 0, "Red Coins in a Bottle")]
    [InlineData(55, 0, "Down with Petey Piranha!")]
    [InlineData(57, 0, "Eely-Mouth's Dentist")]
    [InlineData(58, 0, "Mecha-Bowser Appears!")]
    [InlineData(59, 0, "Gooper Blooper Breaks Out")]
    [InlineData(60, 0, "Father and Son Shine!")]
    public void EpisodeReference_SpotCheckTitles(byte courseId, byte episodeId, string titleFragment)
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var label = catalog.GetEpisodeDisplayName(courseId, episodeId);
        Assert.Contains(titleFragment, label, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Secrets / plaza interiors / bosses that formerly fell through to "Course {id}"
    [InlineData(16, "Undersea")]
    [InlineData(31, "Shell's Secret")]
    [InlineData(32, "Sand Castle")]
    [InlineData(33, "Sand Bird")]
    [InlineData(40, "Casino Delfino Secret")]
    [InlineData(41, "Yoshi-Go-Round")]
    [InlineData(42, "Village Underside")]
    [InlineData(44, "Bottle")]
    [InlineData(46, "Dirty Lake")]
    [InlineData(47, "Hillside Cave")]
    [InlineData(48, "Ricco Tower")]
    [InlineData(50, "Beach Cannon")]
    [InlineData(51, "Hotel Lobby Secret")]
    [InlineData(55, "Petey")]
    [InlineData(56, "King Boo")]
    [InlineData(57, "Eely-Mouth")]
    [InlineData(58, "Roller Coaster")]
    [InlineData(59, "Gooper Blooper")]
    [InlineData(60, "Father and Son")]
    public void GetCourseName_SecretAndBossAreas_UseHumanReadableNames(
        byte courseId, string nameFragment)
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var name = catalog.GetCourseName(courseId);
        Assert.DoesNotContain($"Course {courseId}", name, StringComparison.Ordinal);
        Assert.Contains(nameFragment, name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCourseName_CoversAllRaScriptPlayableAreas()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        // timenoe/RAScripts NTSC-U courseIDs (playable; excludes title/test).
        byte[] required =
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 13, 14, 16,
            20, 21, 22, 23, 24, 29, 30, 31, 32, 33,
            40, 41, 42, 44, 46, 47, 48, 50, 51, 52,
            55, 56, 57, 58, 59, 60,
        ];

        var catalog = LevelCatalog.Load(levelsPath);
        foreach (var courseId in required)
        {
            var name = catalog.GetCourseName(courseId);
            Assert.False(string.IsNullOrWhiteSpace(name), $"empty name for course {courseId}");
            Assert.DoesNotContain($"Course {courseId}", name, StringComparison.Ordinal);
        }
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

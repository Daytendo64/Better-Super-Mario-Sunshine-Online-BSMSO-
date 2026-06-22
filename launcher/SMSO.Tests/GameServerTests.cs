using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

public class GameServerTests
{
    [Fact]
    public void LevelCatalog_NormalizesDelfinoHubEpisode()
    {
        Assert.Equal(0, LevelCatalog.NormalizeEpisodeFromGame(1, 8));  // dolpic8 open hub
        Assert.Equal(1, LevelCatalog.NormalizeEpisodeFromGame(1, 0));  // dolpic0 arrival
        Assert.Equal(6, LevelCatalog.NormalizeEpisodeFromGame(1, 9));  // dolpic9 flooded
        Assert.Equal(7, LevelCatalog.NormalizeEpisodeFromGame(1, 2));  // dolpic10 post-flood
        Assert.Equal(2, LevelCatalog.NormalizeEpisodeFromGame(2, 2));
    }

    [Fact]
    public void LevelCatalog_ResolvesDelfinoWarpEpisodes()
    {
        Assert.Equal(8, LevelCatalog.ResolveEpisodeForWarp(1, 0));  // open hub
        Assert.Equal(0, LevelCatalog.ResolveEpisodeForWarp(1, 1)); // arrival
        Assert.Equal(9, LevelCatalog.ResolveEpisodeForWarp(1, 6)); // flooded
        Assert.Equal(2, LevelCatalog.ResolveEpisodeForWarp(1, 7)); // post-flood dolpic10
        Assert.Equal(3, LevelCatalog.ResolveEpisodeForWarp(2, 3));
    }

    [Fact]
    public void LevelCatalog_IncludesAllDelfinoScenes()
    {
        var path = FindLevels();
        if (!File.Exists(path)) return;
        var cat = LevelCatalog.Load(path);
        var plaza = cat.FindCourse(1);
        Assert.NotNull(plaza);
        Assert.Equal(8, plaza!.Episodes.Count);
        Assert.True(cat.IsValidWarp(21, 0)); // Super Slide
        Assert.True(cat.IsValidWarp(23, 0)); // Red Coin Field
        Assert.True(cat.IsValidWarp(1, 6));  // Flooded plaza
        Assert.True(cat.IsValidWarp(1, 7));  // Post-flood plaza
    }

    [Fact]
    public void LevelCatalog_ValidatesWarp()
    {
        var path = FindLevels();
        if (!File.Exists(path)) return;
        var cat = LevelCatalog.Load(path);
        Assert.True(cat.IsValidWarp(2, 0)); // Bianco Hills ep 1
        Assert.False(cat.IsValidWarp(255, 0));
    }

    [Fact]
    public void Server_StartStop()
    {
        var server = new GameServer(new LevelCatalog());
        server.Start(27115);
        Assert.True(server.IsRunning);
        server.Stop();
        Assert.False(server.IsRunning);
    }

    private static string FindLevels()
    {
        var p = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "assets", "levels.ntsc-u.json");
        return Path.GetFullPath(p);
    }
}

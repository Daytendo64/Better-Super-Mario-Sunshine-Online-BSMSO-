using SMSO.Net;
using SMSO.Server;

var port = 27015;
if (args.Length > 0 && int.TryParse(args[0], out var p))
    port = p;

var maxPlayers = ProtocolConstants.StableMaxPlayers;
if (args.Length > 1 && int.TryParse(args[1], out var m))
    maxPlayers = Math.Clamp(m, 2, ProtocolConstants.StableMaxPlayers);

// Game profile: --profile=vanilla|eclipse (or BSMSO_GAME_PROFILE env). Default vanilla.
var profile = GameProfileId.VanillaSms;
var profileArg = args.FirstOrDefault(a => a.StartsWith("--profile", StringComparison.OrdinalIgnoreCase));
var profileText = profileArg is null
    ? Environment.GetEnvironmentVariable("BSMSO_GAME_PROFILE")
    : profileArg.Contains('=')
        ? profileArg.Split('=', 2)[1]
        : null;
if (!string.IsNullOrWhiteSpace(profileText))
{
    if (!GameProfileIds.TryParse(profileText, out profile))
    {
        Console.Error.WriteLine(
            $"Unknown game profile '{profileText}' — use --profile=vanilla or --profile=eclipse.");
        return 2;
    }
}

var levelsPath = Path.Combine(AppContext.BaseDirectory, "assets", "levels.ntsc-u.json");
if (!File.Exists(levelsPath))
{
    levelsPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "assets", "levels.ntsc-u.json");
}

var levels = File.Exists(levelsPath) ? LevelCatalog.Load(levelsPath) : new LevelCatalog();
// No launcher self-join ever arrives here, so skip the host-slot reservation: the first
// joiner leads immediately instead of waiting out the window with warps/SyncSettings refused.
var server = new GameServer(levels)
{
    MaxPlayers = maxPlayers,
    IsDedicatedServer = true,
    ExpectedGameProfileId = (ushort)profile,
};
server.Log += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

Console.WriteLine(
    $"BSMSO Dedicated Server — build {ProtocolConstants.ModBuildId}, " +
    $"comm v{ProtocolConstants.CommVersion}, port {port}, max players {maxPlayers}, " +
    $"profile: {GameProfileIds.DisplayName(profile)}");
Console.WriteLine("Press Ctrl+C to stop.");

try
{
    server.Start(port);
}
catch (System.Net.Sockets.SocketException ex)
{
    Console.Error.WriteLine(
        $"Failed to bind port {port}: {ex.SocketErrorCode}. " +
        "Another BSMSO.ServerHost / launcher host may still be running.");
    return 1;
}

var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Set();
};
quit.Wait();

try
{
    server.NotifyShutdown();
    Thread.Sleep(150);
}
catch
{
    // best-effort
}

server.Stop();
return 0;

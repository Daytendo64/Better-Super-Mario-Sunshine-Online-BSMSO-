using SMSO.Net;
using SMSO.Server;

var port = 27015;
if (args.Length > 0 && int.TryParse(args[0], out var p))
    port = p;

var maxPlayers = ProtocolConstants.StableMaxPlayers;
if (args.Length > 1 && int.TryParse(args[1], out var m))
    maxPlayers = Math.Clamp(m, 2, ProtocolConstants.StableMaxPlayers);

var levelsPath = Path.Combine(AppContext.BaseDirectory, "assets", "levels.ntsc-u.json");
if (!File.Exists(levelsPath))
{
    levelsPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "assets", "levels.ntsc-u.json");
}

var levels = File.Exists(levelsPath) ? LevelCatalog.Load(levelsPath) : new LevelCatalog();
var server = new GameServer(levels) { MaxPlayers = maxPlayers };
server.Log += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

Console.WriteLine($"SMSO Dedicated Server — port {port}, max players {maxPlayers}");
Console.WriteLine("Press Ctrl+C to stop.");
server.Start(port);

var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
quit.Wait();
server.Stop();

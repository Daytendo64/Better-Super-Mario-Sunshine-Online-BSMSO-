using SMSO.Net.MarioPack;

string Arg(string name, string fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return fallback;
}

var gameRoot = Arg("--game", @"C:\Users\young\OneDrive\Desktop\sms online files");
var installGame = !args.Any(a => string.Equals(a, "--no-game", StringComparison.OrdinalIgnoreCase));

byte[] retailBytes = Array.Empty<byte>();
string? retailSource = null;
foreach (var candidate in new[]
         {
             Path.Combine(gameRoot, @"files\mario\mario.szs.bsmso-retail"),
             Path.Combine(gameRoot, @"files\mario\mario.szs"),
             Path.Combine(gameRoot, @"files\data\mario.szs"),
             Path.Combine(gameRoot, @"files\data\mario.arc"),
         })
{
    if (!File.Exists(candidate))
        continue;
    retailBytes = File.ReadAllBytes(candidate);
    retailSource = candidate;
    break;
}

if (retailBytes.Length == 0 || retailSource == null)
    throw new FileNotFoundException("Could not find retail mario archive under game folder.", gameRoot);

Console.WriteLine($"Retail: {retailSource} ({retailBytes.Length:N0} bytes)");

var imports = new List<(string Path, string? Name)>();
for (var i = 0; i < args.Length; i++)
{
    if (!string.Equals(args[i], "--szs", StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Length)
        continue;
    var path = args[i + 1];
    string? name = null;
    if (i + 2 < args.Length && string.Equals(args[i + 2], "--name", StringComparison.OrdinalIgnoreCase) &&
        i + 3 < args.Length)
    {
        name = args[i + 3];
        i += 2;
    }

    imports.Add((path, name));
}

if (imports.Count == 0)
    throw new InvalidOperationException("Usage: ImportSzs --szs <path> [--name Display] [--game <root>] [--no-game]");

var modelsDir = Path.Combine(gameRoot, @"files\data\bsmso_models");
if (installGame)
    Directory.CreateDirectory(modelsDir);

foreach (var (path, explicitName) in imports)
{
    if (!File.Exists(path))
        throw new FileNotFoundException("Custom SZS not found.", path);

    var name = string.IsNullOrWhiteSpace(explicitName)
        ? CharacterPack.DisplayNameFromFileName(path)
        : explicitName.Trim();
    var entry = ModelLibrary.ImportSzs(path, retailBytes, name);
    Console.WriteLine($"Imported {entry.DisplayName} -> {entry.Id} ({entry.PackFileName})");

    if (installGame)
    {
        var packSrc = ModelLibrary.GetPackPath(entry.Id);
        var packDst = Path.Combine(modelsDir, entry.Id + ".arc");
        File.Copy(packSrc, packDst, overwrite: true);
        Console.WriteLine($"Installed game pack: {packDst}");
    }
}

Console.WriteLine($"Library: {ModelLibrary.LibraryDirectory}");

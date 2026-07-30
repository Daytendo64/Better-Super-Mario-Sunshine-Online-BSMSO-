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

var sourceDir = Arg("--source", @"C:\Users\young\OneDrive\Desktop\needle");
var gameRoot = Arg("--game", @"C:\Users\young\OneDrive\Desktop\sms online files");
var displayName = Arg("--name", "Needle");
var outSzs = Arg("--out", Path.Combine(sourceDir, "needle.szs"));

if (!Directory.Exists(sourceDir))
    throw new DirectoryNotFoundException(sourceDir);

var bmds = Directory.GetFiles(sourceDir, "*.bmd", SearchOption.TopDirectoryOnly)
    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (bmds.Length == 0)
    throw new InvalidDataException($"No .bmd files in {sourceDir}");

Console.WriteLine($"Packing {bmds.Length} BMD(s) from {sourceDir}");

var root = new RarcDirectory { Name = "mario" };
var bmdDir = new RarcDirectory { Name = "bmd" };
foreach (var path in bmds)
{
    var name = Path.GetFileName(path);
    bmdDir.Files.Add(new RarcFileEntry { Name = name, Data = File.ReadAllBytes(path) });
    Console.WriteLine($"  + bmd/{name} ({new FileInfo(path).Length:N0} bytes)");
}
root.Directories.Add(bmdDir);

var rarc = new RarcArchive { RootName = "mario", Root = root }.Save();
var szs = Yaz0.Compress(rarc);
Directory.CreateDirectory(Path.GetDirectoryName(outSzs)!);
File.WriteAllBytes(outSzs, szs);
Console.WriteLine($"Wrote {outSzs} ({szs.Length:N0} bytes Yaz0 / {rarc.Length:N0} RARC)");

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

var entry = ModelLibrary.ImportSzs(outSzs, retailBytes, displayName);
Console.WriteLine($"Imported {entry.DisplayName} -> {entry.Id} ({entry.PackFileName})");
Console.WriteLine($"Library: {ModelLibrary.LibraryDirectory}");

var modelsDir = Path.Combine(gameRoot, @"files\data\bsmso_models");
Directory.CreateDirectory(modelsDir);
var packSrc = ModelLibrary.GetPackPath(entry.Id);
var packDst = Path.Combine(modelsDir, entry.Id + ".arc");
File.Copy(packSrc, packDst, overwrite: true);
Console.WriteLine($"Installed game pack: {packDst}");

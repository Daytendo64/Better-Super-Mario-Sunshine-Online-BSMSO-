using SMSO.Net.MarioPack;

var gameRoot = args.Length > 0 ? args[0] : @"C:\Users\young\OneDrive\Desktop\sms online files";
var bundled = @"C:\Users\young\OneDrive\Desktop\SMSOBB\dist\launcher\CustomModels";

byte[] retailBytes = Array.Empty<byte>();
string? retailSource = null;
foreach (var candidate in new[]
         {
             Path.Combine(gameRoot, @"files\mario\mario.szs.bsmso-retail"),
             Path.Combine(gameRoot, @"files\mario\mario.szs"),
         })
{
    if (!File.Exists(candidate)) continue;
    retailBytes = File.ReadAllBytes(candidate);
    retailSource = candidate;
    break;
}
if (retailBytes.Length == 0)
    throw new FileNotFoundException("Retail mario archive not found.", gameRoot);
Console.WriteLine($"Retail: {retailSource}");

var imports = new (string Path, string Name, string Id)[]
{
    (@"C:\Users\young\Downloads\Sonic (2).arc", "Sonic", "841192a3"),
    (@"C:\Users\young\Downloads\Shadow (6).arc", "Shadow", "23704068"),
    (@"C:\Users\young\Downloads\Luigi (3).arc", "Luigi", "cadf67c6"),
};

ModelLibrary.EnsureLibraryDirectory();
Directory.CreateDirectory(bundled);
var modelsDir = Path.Combine(gameRoot, @"files\data\bsmso_models");
Directory.CreateDirectory(modelsDir);

foreach (var (path, name, id) in imports)
{
    if (!File.Exists(path))
        throw new FileNotFoundException(path);

    ModelLibrary.SetDisplayName(id, name);
    var szsPath = ModelLibrary.GetSzsPath(id);
    File.Copy(path, szsPath, overwrite: true);
    Console.WriteLine($"Source -> {szsPath} ({new FileInfo(szsPath).Length:N0} bytes)");

    var entry = ModelLibrary.ReimportStoredSzsPreserveId(id, retailBytes)
        ?? throw new InvalidOperationException("Reimport failed for " + id);
    Console.WriteLine($"Merged {entry.DisplayName} id={entry.Id} pack={entry.PackFileName}");

    var packSrc = ModelLibrary.GetPackPath(entry.Id);
    var packDst = Path.Combine(modelsDir, entry.Id + ".arc");
    File.Copy(packSrc, packDst, overwrite: true);
    Console.WriteLine($"Game -> {packDst} ({new FileInfo(packDst).Length:N0} bytes)");

    File.Copy(packSrc, Path.Combine(bundled, name + ".arc"), overwrite: true);
    File.Copy(szsPath, Path.Combine(bundled, name + ".szs"), overwrite: true);
}

var libJson = Path.Combine(ModelLibrary.LibraryDirectory, "library.json");
if (File.Exists(libJson))
    File.Copy(libJson, Path.Combine(bundled, "library.json"), overwrite: true);

Console.WriteLine("Done.");

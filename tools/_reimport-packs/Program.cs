using SMSO.Net.MarioPack;

var gameRoot = @"C:\Users\young\OneDrive\Desktop\sms online files";
var retailPath = Path.Combine(gameRoot, @"files\mario\mario.szs.bsmso-retail");
if (!File.Exists(retailPath))
    throw new FileNotFoundException("Retail mario archive not found.", retailPath);

var retailBytes = File.ReadAllBytes(retailPath);
var modelsDir = Path.Combine(gameRoot, @"files\data\bsmso_models");
Directory.CreateDirectory(modelsDir);

foreach (var id in new[] { "1b683fc7", "f130b25e" })
{
    var entry = ModelLibrary.ReimportStoredSzsPreserveId(id, retailBytes);
    if (entry == null)
        throw new InvalidOperationException($"Could not reimport stored SZS for {id}.");

    var merge = CharacterPack.BuildMergedPack(retailBytes,
        File.ReadAllBytes(Path.Combine(ModelLibrary.LibraryDirectory, id + ".szs")));
    Console.WriteLine(
        $"Reimported {entry.DisplayName} ({id}): replaced={merge.ReplacedCount} skipped=[{string.Join(", ", merge.SkippedReplacements)}] hideCaps={merge.InjectedHideCapsMarker}");

    var dst = Path.Combine(modelsDir, id + ".arc");
    File.Copy(ModelLibrary.GetPackPath(id), dst, overwrite: true);
    Console.WriteLine($"  Installed {dst}");
}

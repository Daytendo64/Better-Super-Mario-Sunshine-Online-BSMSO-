using SMSO.Net.MarioPack;

static void Dump(string label, byte[] bytes)
{
    var arc = CharacterPack.OpenArchive(bytes);
    Console.WriteLine($"=== {label} ({bytes.Length} bytes) ===");
    foreach (var f in arc.EnumerateFiles().OrderBy(x => x.FullPath))
    {
        if (f.Name.EndsWith(".bmd", StringComparison.OrdinalIgnoreCase) ||
            f.Name.EndsWith(".btk", StringComparison.OrdinalIgnoreCase) ||
            f.Name.EndsWith(".prm", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  {f.FullPath} ({f.Data.Length})");
    }
}

var retail = File.ReadAllBytes(
    @"C:\Users\young\OneDrive\Desktop\sms online files\files\mario\mario.szs.bsmso-retail");

foreach (var (szs, name) in new (string, string)[]
         {
             (@"C:\Users\young\Downloads\birdo.szs", "birdo"),
             (@"C:\Users\young\Downloads\yoshi.szs", "yoshi"),
             (@"C:\Users\young\AppData\Roaming\SMSO\CustomModels\cadf67c6.szs", "luigi"),
             (@"C:\Users\young\AppData\Roaming\SMSO\CustomModels\841192a3.szs", "sonic"),
         })
{
    var custom = File.ReadAllBytes(szs);
    var merge = CharacterPack.BuildMergedPack(retail, custom);
    Dump($"{name} custom", custom);
    Dump($"{name} merged id={merge.ModelId} replaced={merge.ReplacedCount} injected={merge.InjectedBtkCount} customBtk={merge.InjectedCustomBtkCount}",
        merge.PackArc);
    Console.WriteLine($"  Replaced: {string.Join(", ", merge.ReplacedNames)}");
    if (merge.InjectedBtkNames.Count > 0)
        Console.WriteLine($"  Injected BTK: {string.Join(", ", merge.InjectedBtkNames)}");
    Console.WriteLine();
}

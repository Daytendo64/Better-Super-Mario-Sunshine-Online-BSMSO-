using SMSO.Net.MarioPack;
using System.Text;

void Dump(string label, byte[] bytes) {
    var arc = CharacterPack.OpenArchive(bytes);
    Console.WriteLine($"=== {label} ({bytes.Length} bytes) ===");
    foreach (var f in arc.EnumerateFiles().OrderBy(x => x.FullPath)) {
        if (f.Name.EndsWith(".bmd", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".btk", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".prm", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  {f.FullPath} ({f.Data.Length})");
    }
}

var retail = File.ReadAllBytes(@"C:\Users\young\OneDrive\Desktop\sms online files\files\mario\mario.szs.bsmso-retail");
foreach (var (szs, name) in new[] {
    (@"C:\Users\young\Downloads\birdo.szs", "birdo"),
    (@"C:\Users\young\Downloads\yoshi.szs", "yoshi"),
    (@"C:\Users\young\AppData\Roaming\SMSO\CustomModels\cadf67c6.szs", "luigi"),
}) {
    var custom = File.ReadAllBytes(szs);
    var merge = CharacterPack.BuildMergedPack(retail, custom);
    Dump($"{name} custom", custom);
    Dump($"{name} merged id={merge.ModelId}", merge.PackArc);
    Console.WriteLine();
}

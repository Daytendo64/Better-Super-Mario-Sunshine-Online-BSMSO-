using SMSO.Net.MarioPack;
using System.Security.Cryptography;

string Hash(byte[] d) => Convert.ToHexString(SHA1.HashData(d))[..12];

var retail = CharacterPack.OpenArchive(File.ReadAllBytes(
    @"C:\Users\young\OneDrive\Desktop\sms online files\files\mario\mario.szs.bsmso-retail"));
var retailBy = retail.EnumerateFiles()
    .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.First().Data, StringComparer.OrdinalIgnoreCase);

void Dump(string label, string path)
{
    if (!File.Exists(path)) { Console.WriteLine(label + ": MISSING"); return; }
    var arc = CharacterPack.OpenArchive(File.ReadAllBytes(path));
    Console.WriteLine("=== " + label + " (" + new FileInfo(path).Length + ") ===");
    foreach (var name in new[] { "ma_jump.bck", "ma_2jmp1.bck", "ma_2jmp2.bck", "ma_broad_jump.bck", "ma_wait.bck", "ma_run1.bck", "ma_run2.bck" })
    {
        var files = arc.EnumerateFiles().Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { Console.WriteLine("  " + name + ": absent"); continue; }
        foreach (var f in files)
        {
            retailBy.TryGetValue(f.Name, out var r);
            var same = r != null && f.Data.AsSpan().SequenceEqual(r);
            CharacterPack.TryReadBckJointCount(f.Data, out var j);
            int rj = -1; if (r != null) CharacterPack.TryReadBckJointCount(r, out rj);
            Console.WriteLine($"  {f.FullPath}: len={f.Data.Length} joints={j} retailJoints={rj} vsRetail={(same ? "SAME" : "DIFF")} hash={Hash(f.Data)}");
        }
    }
    var prm = arc.EnumerateFiles().FirstOrDefault(f => f.Name.Equals("BodyAngleFree.prm", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(prm == null ? "  BodyAngleFree: NO" : "  BodyAngleFree: " + prm.Data.Length);
}

var cm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMSO", "CustomModels");
Dump("AppData Wario.arc", Path.Combine(cm, "Wario.arc"));
Dump("source Wario (1).arc", @"c:\Users\young\Downloads\Wario (1).arc");
Dump("game 3c297fff", @"C:\Users\young\OneDrive\Desktop\sms online files\files\data\bsmso_models\3c297fff.arc");
Dump("old 23062f17 if present", @"C:\Users\young\OneDrive\Desktop\sms online files\files\data\bsmso_models\23062f17.arc");

// Compare ma_jump between source custom and merged
var src = CharacterPack.OpenArchive(File.ReadAllBytes(@"c:\Users\young\Downloads\Wario (1).arc"));
var srcJump = src.EnumerateFiles().FirstOrDefault(f => f.Name.Equals("ma_jump.bck", StringComparison.OrdinalIgnoreCase));
var retailJump = retailBy["ma_jump.bck"];
if (srcJump != null)
{
    Console.WriteLine("source ma_jump == retail? " + srcJump.Data.AsSpan().SequenceEqual(retailJump));
    CharacterPack.TryReadBckJointCount(srcJump.Data, out var sj);
    CharacterPack.TryReadBckJointCount(retailJump, out var rj);
    Console.WriteLine($"source joints={sj} retail joints={rj} srcLen={srcJump.Data.Length} retailLen={retailJump.Length}");
}

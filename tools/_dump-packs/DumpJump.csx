using SMSO.Net.MarioPack;
using System.Security.Cryptography;
string Hash(byte[] d) => Convert.ToHexString(SHA1.HashData(d))[..12];
string cm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMSO", "CustomModels");
string retailPath = @"C:\Users\young\OneDrive\Desktop\sms online files\files\mario\mario.szs.bsmso-retail";
if (!File.Exists(retailPath)) {
  foreach (var c in new[]{@"C:\Users\young\OneDrive\Desktop\sms online files\files\data\mario.arc"})
    if (File.Exists(c)) { retailPath = c; break; }
}
if (!File.Exists(retailPath)) { Console.WriteLine("NO RETAIL"); return; }
var retail = CharacterPack.OpenArchive(File.ReadAllBytes(retailPath));
var retailBy = retail.EnumerateFiles().GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Data, StringComparer.OrdinalIgnoreCase);
void Dump(string label) {
  var path = Path.Combine(cm, label + ".arc");
  if (!File.Exists(path)) { Console.WriteLine(label + " MISSING"); return; }
  var arc = CharacterPack.OpenArchive(File.ReadAllBytes(path));
  Console.WriteLine("=== " + label + " ===");
  foreach (var name in new[]{"ma_jump.bck","ma_2jmp1.bck","wg_pump.bck","ma_wait.bck"}) {
    var files = arc.EnumerateFiles().Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    foreach (var f in files) {
      retailBy.TryGetValue(f.Name, out var r);
      var same = r != null && f.Data.AsSpan().SequenceEqual(r);
      CharacterPack.TryReadBckJointCount(f.Data, out var j);
      Console.WriteLine($"  {f.FullPath.Replace('\\','/')}: len={f.Data.Length} j={j} vsRetail={(same?"SAME":"DIFF")} hash={Hash(f.Data)}");
    }
  }
  Console.WriteLine($"  AllowsBck={CharacterPack.AllowsBckReplacement(label)} BodyAngle={CharacterPack.AllowsBodyAngleFreeReplacement(label)}");
}
Dump("Sonic"); Dump("Shadow"); Dump("Luigi"); Dump("Shadow Mario");

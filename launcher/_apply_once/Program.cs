using System;
using System.IO;
using System.Linq;
using SMSO.Net.MarioPack;

// Stamp BodyAngleFree.prm onto the existing Sonic pack (stable id 841192a3).
const string sonicId = "841192a3";
var lib = ModelLibrary.LibraryDirectory;
var gameModels = @"C:\Users\young\OneDrive\Desktop\sms online files\files\data\bsmso_models";
var distCm = @"C:\Users\young\OneDrive\Desktop\SMSOBB\dist\launcher\CustomModels";
var retailBytes = File.ReadAllBytes(
    @"C:\Users\young\OneDrive\Desktop\sms online files\files\mario\mario.szs.bsmso-retail");

Console.WriteLine("--- Sonic BodyAngleFree.prm ---");
Console.WriteLine($"AllowsBodyAngleFreeReplacement(Sonic)={CharacterPack.AllowsBodyAngleFreeReplacement("Sonic")}");

var entry = ModelLibrary.ReimportStoredSzsPreserveId(sonicId, retailBytes)
            ?? throw new InvalidOperationException("Sonic reimport failed");
ModelLibrary.SetDisplayName(entry.Id, "Sonic");

var packPath = ModelLibrary.GetPackPath(entry.Id);
// false = already stamped with matching bytes (still success).
CharacterPack.EnsureBodyAngleFreePrmInPackFile(packPath);

var namedArc = Path.Combine(lib, "Sonic.arc");
if (!string.Equals(Path.GetFullPath(namedArc), Path.GetFullPath(packPath),
        StringComparison.OrdinalIgnoreCase))
{
    File.Copy(packPath, namedArc, overwrite: true);
}

var gameDst = Path.Combine(gameModels, entry.Id + ".arc");
File.Copy(packPath, gameDst, overwrite: true);

if (Directory.Exists(distCm))
{
    File.Copy(namedArc, Path.Combine(distCm, "Sonic.arc"), overwrite: true);
    var namedSzs = Path.Combine(lib, "Sonic.szs");
    if (File.Exists(namedSzs))
        File.Copy(namedSzs, Path.Combine(distCm, "Sonic.szs"), overwrite: true);
}

var arc = CharacterPack.OpenArchive(File.ReadAllBytes(packPath));
var hasPrm = arc.EnumerateFiles()
    .Any(f => f.Name.Equals("BodyAngleFree.prm", StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"OK Sonic id={entry.Id} BodyAngleFree.prm={hasPrm} bytes={new FileInfo(packPath).Length}");

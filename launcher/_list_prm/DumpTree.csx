using System;
using System.Linq;
using SMSO.Net.MarioPack;

var path = args[0];
var arc = CharacterPack.OpenArchive(System.IO.File.ReadAllBytes(path));
Console.WriteLine(RootName={arc.RootName});
foreach (var f in arc.EnumerateFiles().Where(x => x.Name.Contains(prm, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(Body, StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(.prm, StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine(  {f.FullPath}  len={f.Data.Length});
// also show first-level children
void Dump(RarcDirectory d, string indent, int depth) {
  if (depth > 2) return;
  foreach (var e in d.Entries) {
    if (e is RarcFile rf) Console.WriteLine(${indent}FILE {rf.Name} ({rf.Data.Length}));
    else if (e is RarcDirectory rd) {
      Console.WriteLine(${indent}DIR  {rd.Name}/);
      Dump(rd, indent +   , depth+1);
    }
  }
}
Dump(arc.Root, `, 0);

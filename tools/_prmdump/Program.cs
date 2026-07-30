using System;
using System.IO;
using System.Linq;
using SMSO.Net.MarioPack;

var arc = CharacterPack.OpenArchive(File.ReadAllBytes(Path.Combine(ModelLibrary.LibraryDirectory, "bb698f9c.arc")));
File.WriteAllBytes(@"C:\Users\young\AppData\Local\Temp\ma_mdl1.bmd",
    arc.EnumerateFiles().First(f => f.FullPath.Replace('\\', '/').Equals("bmd/ma_mdl1.bmd", StringComparison.OrdinalIgnoreCase)).Data);
File.WriteAllBytes(@"C:\Users\young\AppData\Local\Temp\ma_cap1.bmd",
    arc.EnumerateFiles().First(f => f.FullPath.Replace('\\', '/').Equals("bmd/ma_cap1.bmd", StringComparison.OrdinalIgnoreCase)).Data);
Console.WriteLine("dumped");

using System;
using System.IO;
using System.Linq;
using SMSO.Net.MarioPack;

// Quick pack inspector: list .prm / odd files, or verify BodyAngleFree.prm.
var paths = args.Length > 0
    ? args
    : new[]
    {
        Path.Combine(ModelLibrary.LibraryDirectory, "Luigi.arc"),
        Path.Combine(ModelLibrary.LibraryDirectory, "Waluigi.arc"),
        Path.Combine(ModelLibrary.LibraryDirectory, "Wario.arc"),
        Path.Combine(ModelLibrary.LibraryDirectory, "Nightendo.arc"),
        Path.Combine(ModelLibrary.LibraryDirectory, "Shadow.arc"),
    };

var expected = CharacterPack.GetBodyAngleFree2PrmBytes();
foreach (var path in paths)
{
    Console.WriteLine("==== " + path);
    if (!File.Exists(path))
    {
        Console.WriteLine("MISSING");
        continue;
    }

    var arc = CharacterPack.OpenArchive(File.ReadAllBytes(path));
    var prm = arc.EnumerateFiles()
        .FirstOrDefault(f => f.Name.Equals(CharacterPack.BodyAngleFreePrmName,
            StringComparison.OrdinalIgnoreCase));
    if (prm == null)
        Console.WriteLine("BodyAngleFree.prm: absent");
    else
        Console.WriteLine(
            $"BodyAngleFree.prm: {prm.Data.Length} bytes match2={prm.Data.AsSpan().SequenceEqual(expected)}");

    foreach (var f in arc.EnumerateFiles()
                 .Where(x => x.Name.EndsWith(".prm", StringComparison.OrdinalIgnoreCase) ||
                             x.Name.Equals(CharacterPack.HideCapsMarkerName,
                                 StringComparison.OrdinalIgnoreCase))
                 .OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {f.FullPath}\t{f.Data.Length}");
    }
}

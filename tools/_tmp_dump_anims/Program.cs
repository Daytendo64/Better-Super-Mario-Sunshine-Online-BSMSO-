using System.Text;

var dol = File.ReadAllBytes(@"C:\Users\young\OneDrive\Desktop\sms online files\sys\main.dol");
uint Be32(int off) =>
    (uint)((dol[off] << 24) | (dol[off + 1] << 16) | (dol[off + 2] << 8) | dol[off + 3]);
ushort Be16(int off) => (ushort)((dol[off] << 8) | dol[off + 1]);

var sections = new List<(uint fileOff, uint virt, uint size)>();
for (var i = 0; i < 7; i++)
{
    var fileOff = Be32(0x00 + i * 4);
    var size = Be32(0x90 + i * 4);
    var virt = Be32(0x48 + i * 4);
    if (size != 0) sections.Add((fileOff, virt, size));
}
for (var i = 0; i < 11; i++)
{
    var fileOff = Be32(0x1C + i * 4);
    var size = Be32(0xAC + i * 4);
    var virt = Be32(0x64 + i * 4);
    if (size != 0) sections.Add((fileOff, virt, size));
}

int? VirtToFile(uint virt)
{
    foreach (var s in sections)
        if (virt >= s.virt && virt < s.virt + s.size)
            return (int)(s.fileOff + (virt - s.virt));
    return null;
}

string ReadCString(uint virt)
{
    var fo = VirtToFile(virt);
    if (fo == null) return $"?{virt:X}";
    var end = fo.Value;
    while (end < dol.Length && dol[end] != 0 && end - fo.Value < 64) end++;
    return Encoding.ASCII.GetString(dol, fo.Value, end - fo.Value);
}

foreach (var needle in new[] { "rspmp", "ma_rspmp", "r_spmp", "spmp", "pump" })
{
    var nb = Encoding.ASCII.GetBytes(needle);
    for (var i = 0; i < dol.Length - nb.Length; i++)
    {
        var ok = true;
        for (var j = 0; j < nb.Length; j++)
            if (dol[i + j] != nb[j]) { ok = false; break; }
        if (ok)
            Console.WriteLine($"DOL '{needle}' @ 0x{i:X}");
    }
}

const uint kMarioAnimeFiles = 0x803DBF88u;
var baseFo = VirtToFile(kMarioAnimeFiles)!.Value;
Console.WriteLine("ALL info names:");
for (uint i = 0; i < 199; i++)
{
    var namePtr = Be32(baseFo + (int)(i * 8) + 4);
    Console.WriteLine($"{i,3}: {ReadCString(namePtr)}");
}

const uint kGMarioAnimeData = 0x803DC5C0u;
var dataFo = VirtToFile(kGMarioAnimeData)!.Value;
Console.WriteLine("--- animIds using info pump (68) ---");
for (var i = 0; i < 336; i++)
{
    var off = dataFo + i * 8;
    var animInfo = Be16(off);
    var fludd = Be16(off + 2);
    if (animInfo == 68 || fludd == 68)
        Console.WriteLine($"animId=0x{i:X} ({i}) bodyInfo={animInfo} fluddInfo={fludd}");
}

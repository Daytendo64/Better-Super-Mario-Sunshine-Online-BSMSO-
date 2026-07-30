using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SMSO.Net.MarioPack;

/// <summary>
/// Builds complete Mario character packs: retail archive with matching
/// <c>.bmd</c>/<c>.btk</c> basenames replaced, plus any custom-only
/// <c>.btk</c> files injected into the retail <c>btk/</c> folder (Shadow Mario
/// style UV animations live as <c>custom/ma_mdl1.btk</c> etc. and have no
/// retail counterpart to replace in-place).
/// When the custom SZS ships a <c>custom/</c> folder (Shadow Mario / Shadow
/// Luigi), those BTKs are also written under <c>custom/</c> so deferred
/// MActorAnmData can init from <c>/mario/custom</c>. <c>better_sms.prm</c> is
/// never injected — BSE initMario freezes on <c>mHasMActor</c> during load.
/// Matching <c>.bck</c> replacements are opt-in for packs that ship full custom
/// animation sets (Waluigi, Wario, Luigi, Nightendo, Shadow, Sonic). When BCK
/// replacement is enabled, matching <c>.bas</c> files are merged too so animSound
/// tables stay paired with the clips. Custom packs may ship multiple BCK buffers
/// under the same basename (Mario-body 16-joint + FLUDD 14-joint
/// <c>wg_pump.bck</c>); each retail target picks the joint-compatible candidate.
/// A stem is dropped only when none of its candidates match any retail joint
/// count for that basename (keeps retail when a pack ships only a mismatched
/// clip). Paired <c>.bas</c> files follow the same stem gate. Shadow Mario/Luigi
/// and other packs keep retail animations to avoid BAS/BCK mismatches that crash remotes.
/// Tall characters (Luigi, Nightendo, Waluigi, Wario) also opt into a pack-local
/// <c>BodyAngleFree.prm</c> override (<c>bodyanglefree2.prm</c>) so upper-body
/// lean matches their proportions. This is not <c>better_sms.prm</c>. Note:
/// <c>TMario</c> constructs <c>TBodyAngleParams</c> from the <c>params</c> volume
/// (<c>params.szs</c> → <c>mario/bodyanglefree.prm</c>), not the character pack;
/// the module reloads from this pack-local file after remount via
/// <c>ensureLocalBodyAngleFreeParams</c>.
/// </summary>
public static class CharacterPack
{
    public const int ModelIdLength = 8;
    public const string BetterSmsPrmName = "better_sms.prm";
    /// <summary>
    /// Official BetterSunshineMoveset movement-params file. Loaded by Moveset from
    /// the mounted mario volume (<c>/better_movement.prm</c> /
    /// <c>/mario/better_movement.prm</c>). Retail SMS and the BSMSO release zip omit it
    /// so Moveset keeps 1.0 multipliers. Injecting this file (gravity≈1.04× etc.) is what
    /// made Moveset-ON feel heavier than release — Install must strip, not inject.
    /// </summary>
    public const string BetterMovementPrmName = "better_movement.prm";
    /// <summary>
    /// Retail / BSE path basename for free-look body angle params
    /// (<c>/Mario/BodyAngleFree.prm</c> on the mounted mario volume).
    /// </summary>
    public const string BodyAngleFreePrmName = "BodyAngleFree.prm";
    public const string CustomFolderName = "custom";
    /// <summary>
    /// Injected into packs that kept retail caps because the custom SZS shipped
    /// stub <c>ma_cap*</c> BMDs. Runtime hides those retail meshes so Yoshi/Birdo
    /// look capless while <c>TMarioCap</c> still constructs safely.
    /// </summary>
    public const string HideCapsMarkerName = "bsmso_hide_caps";

    /// <summary>Display names that opt into custom <c>.bck</c> replacement.</summary>
    public static readonly string[] BckReplacementDisplayNames =
    {
        "Waluigi",
        "Wario",
        "Luigi",
        "Nightendo",
        "Shadow",
        "Sonic",
    };

    /// <summary>
    /// Display names that receive the tall-character <c>BodyAngleFree.prm</c>
    /// override (<c>bodyanglefree2.prm</c>). Intentionally narrower than the BCK
    /// allow-list — Shadow keeps retail code defaults.
    /// </summary>
    public static readonly string[] BodyAngleFreeReplacementDisplayNames =
    {
        "Luigi",
        "Nightendo",
        "Sonic",
        "Waluigi",
        "Wario",
    };

    public static bool AllowsBckReplacement(string? displayName)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var allowed in BckReplacementDisplayNames)
        {
            if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool AllowsBodyAngleFreeReplacement(string? displayName)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var allowed in BodyAngleFreeReplacementDisplayNames)
        {
            if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Bytes for the tall-character <c>BodyAngleFree.prm</c> override
    /// (embedded <c>bodyanglefree2.prm</c>).
    /// </summary>
    public static byte[] GetBodyAngleFree2PrmBytes()
    {
        const string resourceName = "SMSO.Net.MarioPack.bodyanglefree2.prm";
        var asm = typeof(CharacterPack).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "Embedded resource missing: " + resourceName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Official Moveset <c>better_movement.prm</c> payload (embedded from assets).
    /// </summary>
    public static byte[] GetBetterMovementPrmBytes()
    {
        const string resourceName = "SMSO.Net.MarioPack.better_movement.prm";
        var asm = typeof(CharacterPack).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "Embedded resource missing: " + resourceName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Inject/replace <c>better_movement.prm</c> at the RARC root of a mario archive
    /// (raw RARC or Yaz0 SZS). Returns null when the archive already matches.
    /// </summary>
    public static byte[]? EnsureBetterMovementPrmInArchiveBytes(byte[] archiveBytes, out bool wasSzs)
    {
        wasSzs = Yaz0.IsYaz0(archiveBytes);
        var expected = GetBetterMovementPrmBytes();
        var rarcBytes = OpenToRarcBytes(archiveBytes);
        var arc = RarcArchive.Open(rarcBytes);
        var existing = arc.EnumerateFiles()
            .FirstOrDefault(f => f.Name.Equals(BetterMovementPrmName, StringComparison.OrdinalIgnoreCase));
        if (existing != null && existing.Data.AsSpan().SequenceEqual(expected))
            return null;

        UpsertRootFile(arc.Root, BetterMovementPrmName, expected);
        var saved = new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
        return wasSzs ? Yaz0.Compress(saved.AsSpan()) : saved;
    }

    /// <summary>
    /// Remove <c>better_movement.prm</c> from a mario archive so retail/default feel
    /// is restored when BetterSunshineMoveset is not installed.
    /// </summary>
    public static byte[]? RemoveBetterMovementPrmFromArchiveBytes(byte[] archiveBytes, out bool wasSzs)
    {
        wasSzs = Yaz0.IsYaz0(archiveBytes);
        var rarcBytes = OpenToRarcBytes(archiveBytes);
        var arc = RarcArchive.Open(rarcBytes);
        var removed = RemoveRootFile(arc.Root, BetterMovementPrmName);
        if (!removed)
            return null;

        var saved = new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
        return wasSzs ? Yaz0.Compress(saved.AsSpan()) : saved;
    }

    /// <summary>
    /// Ensure a pack .arc on disk has official <c>better_movement.prm</c>.
    /// </summary>
    /// <returns><c>true</c> when the file was rewritten.</returns>
    public static bool EnsureBetterMovementPrmInPackFile(string packPath)
    {
        if (string.IsNullOrWhiteSpace(packPath) || !File.Exists(packPath))
            return false;

        try
        {
            var bytes = File.ReadAllBytes(packPath);
            var patched = EnsureBetterMovementPrmInArchiveBytes(bytes, out _);
            if (patched == null)
                return false;
            File.WriteAllBytes(packPath, patched);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Strip <c>better_movement.prm</c> from a pack .arc on disk.
    /// </summary>
    public static bool RemoveBetterMovementPrmFromPackFile(string packPath)
    {
        if (string.IsNullOrWhiteSpace(packPath) || !File.Exists(packPath))
            return false;

        try
        {
            var bytes = File.ReadAllBytes(packPath);
            var patched = RemoveBetterMovementPrmFromArchiveBytes(bytes, out _);
            if (patched == null)
                return false;
            File.WriteAllBytes(packPath, patched);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public sealed class MergeResult
    {
        public required byte[] PackArc { get; init; }
        public required string ModelId { get; init; }
        public required int ReplacedCount { get; init; }
        public required IReadOnlyList<string> ReplacedNames { get; init; }
        public required int InjectedBtkCount { get; init; }
        public required IReadOnlyList<string> InjectedBtkNames { get; init; }
        public bool InjectedBetterSmsPrm { get; init; }
        public bool InjectedBodyAngleFreePrm { get; init; }
        public int InjectedCustomBtkCount { get; init; }
        public IReadOnlyList<string> SkippedReplacements { get; init; } = Array.Empty<string>();
        public bool InjectedHideCapsMarker { get; init; }
    }

    public static byte[] OpenToRarcBytes(byte[] archiveBytes)
    {
        if (archiveBytes == null || archiveBytes.Length == 0)
            throw new InvalidDataException("Archive is empty.");
        return Yaz0.IsYaz0(archiveBytes) ? Yaz0.Decompress(archiveBytes) : archiveBytes;
    }

    public static RarcArchive OpenArchive(byte[] archiveBytes) =>
        RarcArchive.Open(OpenToRarcBytes(archiveBytes));

    public static Dictionary<string, byte[]> CollectBmdBtkByBasename(RarcArchive archive,
        bool includeBck = false, bool includeBas = false)
    {
        // Public helper keeps first-wins for callers that expect a flat map.
        // BuildMergedPack uses CollectReplacementsMulti so duplicate BCK joint
        // variants (Mario body vs FLUDD) can both participate in per-target merge.
        var multi = CollectReplacementsMulti(archive, includeBck, includeBas);
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in multi)
        {
            if (kvp.Value.Count > 0)
                map[kvp.Key] = kvp.Value[0];
        }

        return map;
    }

    /// <summary>
    /// Collect replacement payloads keyed by basename. BMD/BTK/BAS keep
    /// first-wins; BCK keeps every unique buffer so joint-variant duplicates
    /// (e.g. 16-joint <c>bck/wg_pump.bck</c> + 14-joint
    /// <c>watergun2/body/wg_pump.bck</c>) both reach per-target merge.
    /// </summary>
    internal static Dictionary<string, List<byte[]>> CollectReplacementsMulti(
        RarcArchive archive, bool includeBck = false, bool includeBas = false)
    {
        var map = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in archive.EnumerateFiles())
        {
            var ext = Path.GetExtension(file.Name);
            var isBck = includeBck && ext.Equals(".bck", StringComparison.OrdinalIgnoreCase);
            if (!ext.Equals(".bmd", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".btk", StringComparison.OrdinalIgnoreCase) &&
                !isBck &&
                !(includeBas && ext.Equals(".bas", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!map.TryGetValue(file.Name, out var list))
            {
                list = new List<byte[]>();
                map[file.Name] = list;
            }

            if (isBck)
            {
                // Keep unique BCK payloads (by exact bytes) for joint variants.
                var exists = false;
                foreach (var existing in list)
                {
                    if (existing.AsSpan().SequenceEqual(file.Data))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                    list.Add(file.Data);
                continue;
            }

            // BMD / BTK / BAS: first basename wins.
            if (list.Count == 0)
                list.Add(file.Data);
        }

        return map;
    }

    /// <summary>
    /// Read ANK1 joint-animation entry count from a J3D1bck1 clip.
    /// Returns false when the file is not a recognizable BCK.
    /// </summary>
    public static bool TryReadBckJointCount(ReadOnlySpan<byte> bck, out int jointCount)
    {
        jointCount = 0;
        if (bck.Length < 0x40)
            return false;
        if (bck.Length < 8 ||
            bck[0] != (byte)'J' || bck[1] != (byte)'3' || bck[2] != (byte)'D' || bck[3] != (byte)'1')
        {
            return false;
        }

        for (int i = 0x20; i + 0x10 <= bck.Length; i++)
        {
            if (bck[i] != (byte)'A' || bck[i + 1] != (byte)'N' || bck[i + 2] != (byte)'K' ||
                bck[i + 3] != (byte)'1')
            {
                continue;
            }

            jointCount = BinaryPrimitives.ReadUInt16BigEndian(bck.Slice(i + 0x0C, 2));
            return jointCount > 0;
        }

        return false;
    }

    /// <summary>
    /// Collect custom-only <c>.btk</c> files (basename not present in retail).
    /// These are the Shadow Mario / Shadow Luigi UV scroll anims.
    /// </summary>
    public static Dictionary<string, byte[]> CollectUnmatchedBtks(RarcArchive custom, RarcArchive retail)
    {
        var retailNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in retail.EnumerateFiles())
            retailNames.Add(file.Name);

        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in custom.EnumerateFiles())
        {
            if (!Path.GetExtension(file.Name).Equals(".btk", StringComparison.OrdinalIgnoreCase))
                continue;
            if (retailNames.Contains(file.Name))
                continue;
            map[file.Name] = file.Data;
        }

        return map;
    }

    /// <summary>
    /// Insert unmatched BTK files into the retail <c>btk/</c> directory so runtime
    /// can load them as <c>/mario/btk/&lt;name&gt;</c>. Rebuilds via <see cref="RarcArchive.Save"/>
    /// after the in-place basename patch — path-based <c>getGlbResource</c> lookups
    /// (BMD/BCK/BAS/BTK) remain valid.
    /// </summary>
    public static byte[] InjectBtksIntoBtkDirectory(byte[] packRarc, IReadOnlyDictionary<string, byte[]> btks,
        out List<string> injectedNames)
    {
        injectedNames = new List<string>();
        if (btks == null || btks.Count == 0)
            return packRarc;

        var arc = RarcArchive.Open(packRarc);
        UpsertFilesIntoDirectory(arc.Root, "btk", btks, injectedNames);
        if (injectedNames.Count == 0)
            return packRarc;

        return new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
    }

    /// <summary>
    /// True when the custom archive is a Shadow-style BetterSMS pack that needs
    /// <c>better_sms.prm</c> + <c>/mario/custom</c> for MActor / screen texture.
    /// </summary>
    public static bool NeedsBetterSmsPrm(RarcArchive custom)
    {
        foreach (var file in custom.EnumerateFiles())
        {
            if (file.Name.Equals(BetterSmsPrmName, StringComparison.OrdinalIgnoreCase))
                return true;
            var path = file.FullPath.Replace('\\', '/');
            if (path.Contains("/custom/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("custom/", StringComparison.OrdinalIgnoreCase))
            {
                if (Path.GetExtension(file.Name).Equals(".btk", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collect <c>.btk</c> files that live under a <c>custom/</c> directory in the
    /// custom SZS (BetterSMS MActorAnmData looks for <c>/mario/custom</c>).
    /// </summary>
    public static Dictionary<string, byte[]> CollectCustomFolderBtks(RarcArchive custom)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in custom.EnumerateFiles())
        {
            if (!Path.GetExtension(file.Name).Equals(".btk", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = file.FullPath.Replace('\\', '/');
            if (!path.Contains("/custom/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("custom/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            map[file.Name] = file.Data;
        }

        return map;
    }

    /// <summary>
    /// Build a valid SMS <c>TParams</c> binary for BetterSMS player flags.
    /// Layout matches retail/BetterSMS packs: count, then per-entry
    /// keyCode(u16) + nameLen(u16) + name + valueLen(u32) + value.
    /// <para>
    /// Default: screen-texture only. <c>mHasMActor</c> stays off because BSMSO
    /// already binds Shadow UV scrolls via <c>mario_tex_anim</c>; enabling BSE
    /// MActor on top of that freezes during <c>initMario</c> on level load.
    /// </para>
    /// </summary>
    public static byte[] BuildBetterSmsPrm(bool hasMActor = false, float mActorFramerate = 1.0f,
        bool hasScreenTexture = true)
    {
        using var ms = new MemoryStream();
        // Entry count written after we know how many we emit.
        ms.Write(new byte[4]);
        int count = 0;
        WritePrmBool(ms, "mHasMActor", hasMActor);
        count++;
        WritePrmF32(ms, "mMActorFramerate", mActorFramerate);
        count++;
        WritePrmBool(ms, "mHasScreenTexture", hasScreenTexture);
        count++;

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), count);
        return bytes;
    }

    private static void WritePrmBool(Stream stream, string name, bool value) =>
        WritePrmEntry(stream, name, new[] { (byte)(value ? 1 : 0) });

    private static void WritePrmF32(Stream stream, string name, float value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buf, value);
        WritePrmEntry(stream, name, buf.ToArray());
    }

    private static void WritePrmEntry(Stream stream, string name, byte[] value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(header, CalcKeyCode(name));
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)nameBytes.Length);
        stream.Write(header);
        stream.Write(nameBytes);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)value.Length);
        stream.Write(len);
        stream.Write(value);
    }

    /// <summary>JDrama::TNameRef::calcKeyCode — same hash used by SMS TParams.</summary>
    public static ushort CalcKeyCode(string name)
    {
        uint hash = 0;
        foreach (var ch in name)
            hash = (uint)ch + hash * 3;
        return (ushort)(hash & 0xFFFF);
    }

    private static void UpsertFilesIntoDirectory(RarcDirectory root, string dirName,
        IReadOnlyDictionary<string, byte[]> files, List<string> injectedNames)
    {
        var dir = FindChildDirectory(root, dirName);
        if (dir == null)
        {
            dir = new RarcDirectory { Name = dirName };
            root.Directories.Add(dir);
        }

        foreach (var kvp in files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var existing = dir.Files.FirstOrDefault(f =>
                f.Name.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                existing.Data = kvp.Value;
            else
                dir.Files.Add(new RarcFileEntry { Name = kvp.Key, Data = kvp.Value });
            injectedNames.Add(kvp.Key);
        }
    }

    private static void UpsertRootFile(RarcDirectory root, string fileName, byte[] data)
    {
        var existing = root.Files.FirstOrDefault(f =>
            f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.Data = data;
        else
            root.Files.Add(new RarcFileEntry { Name = fileName, Data = data });
    }

    private static bool RemoveRootFile(RarcDirectory root, string fileName) =>
        root.Files.RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) > 0;

    private static RarcDirectory? FindChildDirectory(RarcDirectory dir, string name)
    {
        foreach (var child in dir.Directories)
        {
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;
        }

        return null;
    }

    private static RarcDirectory? FindDirectory(RarcDirectory dir, string name)
    {
        if (dir.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return dir;
        foreach (var child in dir.Directories)
        {
            var found = FindDirectory(child, name);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// NTSC-U retail Mario joint counts. Cap BMDs with the wrong skeleton crash
    /// <c>TMarioCap</c> / <c>TMario::initValues</c> on remote puppets (custom
    /// <c>ma_cap*</c> packs that ship 1 joint vs retail 2/3).
    /// </summary>
    public const int RetailBodyJointCount = 29;
    public const int RetailCap1JointCount = 2;
    public const int RetailCap3JointCount = 3;

    /// <summary>Locate the JNT1 joint count inside a J3D BMD.</summary>
    public static bool TryReadBmdJointCount(ReadOnlySpan<byte> bmd, out int jointCount)
    {
        jointCount = 0;
        if (!TryFindJnt1Section(bmd, out var jntOff, out _))
            return false;
        jointCount = BinaryPrimitives.ReadUInt16BigEndian(bmd.Slice(jntOff + 8, 2));
        return true;
    }

    /// <summary>
    /// Pad a custom <c>ma_cap*</c> BMD whose JNT1 count is below retail so
    /// <c>TMarioCap</c> can index bone 1 (wet-cap mtx effect) without crashing.
    /// Duplicates the existing joint entry into identity-like extras; mesh data
    /// is preserved. Returns <c>false</c> when the BMD cannot be safely padded.
    /// </summary>
    public static bool TryPadBmdJointCount(ReadOnlySpan<byte> bmd, int targetJointCount,
        out byte[] padded)
    {
        padded = Array.Empty<byte>();
        if (targetJointCount < 1 || targetJointCount > 64 || bmd.Length < 0x40)
            return false;
        if (!TryFindJnt1Section(bmd, out var jntOff, out var jntSize))
            return false;
        if (jntOff + jntSize > bmd.Length || jntSize < 0x18)
            return false;

        ushort count = BinaryPrimitives.ReadUInt16BigEndian(bmd.Slice(jntOff + 8, 2));
        if (count == targetJointCount)
        {
            padded = bmd.ToArray();
            return true;
        }

        // Only pad upward from a real (non-empty) skeleton — never invent joints
        // from zero, and never shrink.
        if (count == 0 || count > targetJointCount)
            return false;

        uint joff = BinaryPrimitives.ReadUInt32BigEndian(bmd.Slice(jntOff + 0x0C, 4));
        uint roff = BinaryPrimitives.ReadUInt32BigEndian(bmd.Slice(jntOff + 0x10, 4));
        uint soff = BinaryPrimitives.ReadUInt32BigEndian(bmd.Slice(jntOff + 0x14, 4));
        if (joff < 0x18 || roff <= joff || soff < roff)
            return false;
        if (jntOff + (int)soff + 4 > bmd.Length)
            return false;

        // Remap table: count u16s.
        if (jntOff + (int)roff + count * 2 > bmd.Length)
            return false;
        int maxRemap = 0;
        for (int i = 0; i < count; i++)
        {
            int r = BinaryPrimitives.ReadUInt16BigEndian(bmd.Slice(jntOff + (int)roff + i * 2, 2));
            if (r > maxRemap)
                maxRemap = r;
        }

        int uniqueCount = maxRemap + 1;
        if (uniqueCount < 1 || jntOff + (int)joff + uniqueCount * 0x40 > jntOff + (int)roff)
            return false;

        // Source joint bytes — pad slots copy joint 0 but clear translation and
        // set matrix type 2 (matches retail null_weight_B secondary joints).
        var srcJoint0 = bmd.Slice(jntOff + (int)joff, 0x40).ToArray();

        // Names: keep existing, append pad names for extras.
        int st = jntOff + (int)soff;
        ushort nstr = BinaryPrimitives.ReadUInt16BigEndian(bmd.Slice(st, 2));
        if (nstr == 0 || nstr > 64 || st + 4 + nstr * 4 > bmd.Length)
            return false;
        var names = new List<string>(targetJointCount);
        for (int i = 0; i < nstr; i++)
        {
            ushort nameRel = BinaryPrimitives.ReadUInt16BigEndian(bmd.Slice(st + 6 + i * 4, 2));
            int nameAbs = st + nameRel;
            if (nameAbs < st || nameAbs >= bmd.Length)
                return false;
            int end = nameAbs;
            while (end < bmd.Length && bmd[end] != 0)
                end++;
            if (end >= bmd.Length)
                return false;
            names.Add(Encoding.ASCII.GetString(bmd.Slice(nameAbs, end - nameAbs)));
        }

        while (names.Count < targetJointCount)
        {
            names.Add(names.Count == 1 ? "null_weight_B" :
                names.Count == 2 ? "null_weight_A" :
                "null_weight_pad" + names.Count);
        }

        // Build replacement JNT1 (32-byte aligned, retail-style).
        const int jointEntrySize = 0x40;
        int jointDataOff = 0x18;
        int remapOff = jointDataOff + targetJointCount * jointEntrySize;
        int remapBytes = targetJointCount * 2;
        if ((remapOff + remapBytes) % 4 != 0)
            remapBytes += 4 - ((remapOff + remapBytes) % 4);
        int stringOff = remapOff + remapBytes;

        var nameBytes = new byte[targetJointCount][];
        int strTableHeader = 4 + targetJointCount * 4;
        int strCursor = strTableHeader;
        var nameOffs = new int[targetJointCount];
        for (int i = 0; i < targetJointCount; i++)
        {
            nameBytes[i] = Encoding.ASCII.GetBytes(names[i]);
            nameOffs[i] = strCursor;
            strCursor += nameBytes[i].Length + 1;
        }

        int strTableSize = strCursor;
        if (strTableSize % 4 != 0)
            strTableSize += 4 - (strTableSize % 4);

        int sectionSize = stringOff + strTableSize;
        if (sectionSize % 32 != 0)
            sectionSize += 32 - (sectionSize % 32);

        var newJnt = new byte[sectionSize];
        newJnt[0] = (byte)'J';
        newJnt[1] = (byte)'N';
        newJnt[2] = (byte)'T';
        newJnt[3] = (byte)'1';
        BinaryPrimitives.WriteInt32BigEndian(newJnt.AsSpan(4, 4), sectionSize);
        BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(8, 2), (ushort)targetJointCount);
        BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(10, 2), 0xFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(newJnt.AsSpan(0x0C, 4), (uint)jointDataOff);
        BinaryPrimitives.WriteUInt32BigEndian(newJnt.AsSpan(0x10, 4), (uint)remapOff);
        BinaryPrimitives.WriteUInt32BigEndian(newJnt.AsSpan(0x14, 4), (uint)stringOff);

        for (int i = 0; i < targetJointCount; i++)
        {
            int dst = jointDataOff + i * jointEntrySize;
            srcJoint0.CopyTo(newJnt.AsSpan(dst, jointEntrySize));
            if (i >= count)
            {
                // Secondary joint: matrix type 2, identity translation.
                BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(dst, 2), 2);
                newJnt[dst + 2] = 0x00;
                newJnt[dst + 3] = 0xFF;
                // scale already 1 from joint 0
                BinaryPrimitives.WriteInt32BigEndian(newJnt.AsSpan(dst + 0x18, 4), 0); // tx
                BinaryPrimitives.WriteInt32BigEndian(newJnt.AsSpan(dst + 0x1C, 4), 0); // ty
                BinaryPrimitives.WriteInt32BigEndian(newJnt.AsSpan(dst + 0x20, 4), 0); // tz
            }

            BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(remapOff + i * 2, 2), (ushort)i);
        }

        for (int i = remapOff + targetJointCount * 2; i < stringOff; i++)
            newJnt[i] = 0xFF;

        BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(stringOff, 2), (ushort)targetJointCount);
        BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(stringOff + 2, 2), 0xFFFF);
        for (int i = 0; i < targetJointCount; i++)
        {
            ushort hash = CalcKeyCode(names[i]);
            BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(stringOff + 4 + i * 4, 2), hash);
            BinaryPrimitives.WriteUInt16BigEndian(newJnt.AsSpan(stringOff + 6 + i * 4, 2),
                (ushort)nameOffs[i]);
            nameBytes[i].CopyTo(newJnt.AsSpan(stringOff + nameOffs[i], nameBytes[i].Length));
            newJnt[stringOff + nameOffs[i] + nameBytes[i].Length] = 0;
        }

        // Splice JNT1 into the BMD and fix the file-size field at +0x08.
        int delta = sectionSize - jntSize;
        padded = new byte[bmd.Length + delta];
        bmd.Slice(0, jntOff).CopyTo(padded);
        newJnt.CopyTo(padded.AsSpan(jntOff));
        bmd.Slice(jntOff + jntSize).CopyTo(padded.AsSpan(jntOff + sectionSize));
        BinaryPrimitives.WriteInt32BigEndian(padded.AsSpan(8, 4), padded.Length);
        return true;
    }

    /// <summary>
    /// Prefer padding under-jointed custom cap meshes to retail counts so the
    /// Waluigi-style geometry is kept. Body mismatches and stub caps are never padded.
    /// </summary>
    public static bool TryNormalizeCapBmdJoints(string basename, ReadOnlySpan<byte> custom,
        ReadOnlySpan<byte> retail, out byte[] normalized)
    {
        normalized = Array.Empty<byte>();
        if (!basename.StartsWith("ma_cap", StringComparison.OrdinalIgnoreCase) ||
            !basename.EndsWith(".bmd", StringComparison.OrdinalIgnoreCase))
            return false;
        if (retail.IsEmpty || custom.IsEmpty)
            return false;
        // Stub filter — same thresholds as GetBmdSkipReason.
        if (custom.Length < 6000 || custom.Length < retail.Length / 2)
            return false;
        if (!TryReadBmdJointCount(retail, out var retailJoints) || retailJoints < 1)
            return false;
        if (!TryReadBmdJointCount(custom, out var customJoints))
            return false;
        if (customJoints == retailJoints)
        {
            normalized = custom.ToArray();
            return true;
        }

        return TryPadBmdJointCount(custom, retailJoints, out normalized);
    }

    private static bool TryFindJnt1Section(ReadOnlySpan<byte> bmd, out int offset, out int size)
    {
        offset = 0;
        size = 0;
        // Prefer walking the section table when the BMD header looks sane.
        if (bmd.Length >= 0x20 &&
            bmd[0] == (byte)'J' && bmd[1] == (byte)'3' && bmd[2] == (byte)'D' && bmd[3] == (byte)'2')
        {
            int nsec = BinaryPrimitives.ReadInt32BigEndian(bmd.Slice(0x0C, 4));
            if (nsec > 0 && nsec < 64)
            {
                int sec = 0x20;
                for (int s = 0; s < nsec && sec + 8 <= bmd.Length; s++)
                {
                    int secSize = BinaryPrimitives.ReadInt32BigEndian(bmd.Slice(sec + 4, 4));
                    if (secSize < 8 || sec + secSize > bmd.Length)
                        break;
                    if (bmd[sec] == (byte)'J' && bmd[sec + 1] == (byte)'N' &&
                        bmd[sec + 2] == (byte)'T' && bmd[sec + 3] == (byte)'1')
                    {
                        offset = sec;
                        size = secSize;
                        return true;
                    }

                    sec += secSize;
                }
            }
        }

        // Fallback: scan (legacy / minimal test fixtures).
        for (int i = 0; i + 10 <= bmd.Length; i++)
        {
            if (bmd[i] != (byte)'J' || bmd[i + 1] != (byte)'N' || bmd[i + 2] != (byte)'T' ||
                bmd[i + 3] != (byte)'1')
                continue;
            offset = i;
            size = i + 8 <= bmd.Length
                ? BinaryPrimitives.ReadInt32BigEndian(bmd.Slice(i + 4, 4))
                : 0;
            if (size < 8 || i + size > bmd.Length)
                size = bmd.Length - i;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when a merged pack is safe for <c>TMario::initValues</c>: body and
    /// cap BMDs must expose retail joint counts (or the pack keeps retail caps
    /// via <see cref="HideCapsMarkerName"/>).
    /// </summary>
    public static bool TryValidatePackForInit(byte[] packArcBytes, out string reason)
    {
        reason = "";
        if (packArcBytes == null || packArcBytes.Length == 0)
        {
            reason = "Pack is empty.";
            return false;
        }

        RarcArchive arc;
        try
        {
            arc = OpenArchive(packArcBytes);
        }
        catch (Exception ex)
        {
            reason = "Pack is not a valid RARC/Yaz0 archive: " + ex.Message;
            return false;
        }

        byte[]? FindByBasename(string name) =>
            arc.EnumerateFiles()
                .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.Data;

        var body = FindByBasename("ma_mdl1.bmd");
        if (body == null || !TryReadBmdJointCount(body, out var bodyJoints) ||
            bodyJoints != RetailBodyJointCount)
        {
            reason =
                $"ma_mdl1.bmd joint count must be {RetailBodyJointCount} (got {(body != null && TryReadBmdJointCount(body, out var j) ? j.ToString() : "missing")}).";
            return false;
        }

        var hideCaps = FindByBasename(HideCapsMarkerName) != null;
        if (!hideCaps)
        {
            var cap1 = FindByBasename("ma_cap1.bmd");
            if (cap1 == null || !TryReadBmdJointCount(cap1, out var c1) ||
                c1 != RetailCap1JointCount)
            {
                reason =
                    $"ma_cap1.bmd joint count must be {RetailCap1JointCount} (or pack must include {HideCapsMarkerName}).";
                return false;
            }

            var cap3 = FindByBasename("ma_cap3.bmd");
            if (cap3 == null || !TryReadBmdJointCount(cap3, out var c3) ||
                c3 != RetailCap3JointCount)
            {
                reason =
                    $"ma_cap3.bmd joint count must be {RetailCap3JointCount} (or pack must include {HideCapsMarkerName}).";
                return false;
            }
        }

        return true;
    }

    internal enum BmdSkipReason
    {
        None = 0,
        /// <summary>Tiny placeholder caps (Yoshi/Birdo) — keep retail + hide-caps marker.</summary>
        StubCap = 1,
        /// <summary>Wrong JNT1 count and unpad-able — keep retail geometry, leave caps visible.</summary>
        JointMismatch = 2,
    }

    /// <summary>
    /// Birdo/Yoshi-style packs ship 4000-byte stub cap BMDs. Replacing retail caps
    /// with those stubs hangs TMario::initValues during remote puppet construction.
    /// Keep retail cap geometry when the custom asset is clearly a placeholder.
    /// Also skip body BMDs (and unpad-able caps) whose JNT1 joint count differs
    /// from retail — wrong skeletons crash remotes on join. Real custom caps with
    /// too few joints are padded in <see cref="FilterUnsafeReplacements"/> instead.
    /// </summary>
    internal static BmdSkipReason GetBmdSkipReason(string basename, ReadOnlySpan<byte> custom,
        ReadOnlySpan<byte> retail)
    {
        if (!basename.EndsWith(".bmd", StringComparison.OrdinalIgnoreCase))
            return BmdSkipReason.None;
        if (retail.IsEmpty)
            return BmdSkipReason.None;

        if (basename.StartsWith("ma_cap", StringComparison.OrdinalIgnoreCase))
        {
            if (custom.Length < 6000 || custom.Length < retail.Length / 2)
                return BmdSkipReason.StubCap;
        }

        // Critical skeleton BMDs: joint count must match retail or initValues faults.
        if (basename.Equals("ma_mdl1.bmd", StringComparison.OrdinalIgnoreCase) ||
            basename.StartsWith("ma_cap", StringComparison.OrdinalIgnoreCase))
        {
            var customHasJoints = TryReadBmdJointCount(custom, out var customJoints);
            var retailHasJoints = TryReadBmdJointCount(retail, out var retailJoints);
            if (retailHasJoints && (!customHasJoints || customJoints != retailJoints))
                return BmdSkipReason.JointMismatch;
        }

        return BmdSkipReason.None;
    }

    /// <summary>
    /// Per retail target: refuse BCK/BMD replacements whose joint count disagrees
    /// with that specific node. Protects duplicate basenames such as
    /// <c>bck/wg_pump.bck</c> (16) vs <c>watergun2/body/wg_pump.bck</c> (14).
    /// </summary>
    internal static bool AcceptJointCompatibleReplacement(string basename,
        byte[] archiveBuffer, int retailOffset, int retailLength, byte[] replacement)
    {
        if (replacement == null || archiveBuffer == null || retailLength <= 0 ||
            retailOffset < 0 || retailOffset + retailLength > archiveBuffer.Length)
            return true;

        var retailData = archiveBuffer.AsSpan(retailOffset, retailLength);

        if (basename.EndsWith(".bck", StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadBckJointCount(replacement, out var customJoints) &&
                TryReadBckJointCount(retailData, out var retailJoints) &&
                customJoints != retailJoints)
            {
                return false;
            }

            return true;
        }

        if (basename.EndsWith(".bmd", StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadBmdJointCount(replacement, out var customJoints) &&
                TryReadBmdJointCount(retailData, out var retailJoints) &&
                customJoints != retailJoints)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool ShouldSkipBmdReplacement(string basename, ReadOnlySpan<byte> custom,
        ReadOnlySpan<byte> retail) =>
        GetBmdSkipReason(basename, custom, retail) != BmdSkipReason.None;

    internal static Dictionary<string, byte[]> FilterUnsafeReplacements(
        IReadOnlyDictionary<string, byte[]> replacements, RarcArchive retail,
        out List<string> skipped) =>
        FilterUnsafeReplacements(replacements, retail, out skipped, out _);

    internal static Dictionary<string, byte[]> FilterUnsafeReplacements(
        IReadOnlyDictionary<string, byte[]> replacements, RarcArchive retail,
        out List<string> skipped, out List<BmdSkipReason> skipReasons)
    {
        var multi = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in replacements)
            multi[kvp.Key] = new List<byte[]> { kvp.Value };

        var filteredMulti = FilterUnsafeReplacements(multi, retail, out skipped, out skipReasons);
        var filtered = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in filteredMulti)
        {
            if (kvp.Value.Count > 0)
                filtered[kvp.Key] = kvp.Value[0];
        }

        return filtered;
    }

    internal static Dictionary<string, List<byte[]>> FilterUnsafeReplacements(
        Dictionary<string, List<byte[]>> replacements, RarcArchive retail,
        out List<string> skipped, out List<BmdSkipReason> skipReasons)
    {
        var retailByName = retail.EnumerateFiles()
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Data, StringComparer.OrdinalIgnoreCase);

        // All retail joint counts per BCK basename (bck/ 16 + watergun2/body 14).
        var retailBckJoints = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in retail.EnumerateFiles())
        {
            if (!file.Name.EndsWith(".bck", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryReadBckJointCount(file.Data, out var joints))
                continue;
            if (!retailBckJoints.TryGetValue(file.Name, out var set))
            {
                set = new HashSet<int>();
                retailBckJoints[file.Name] = set;
            }

            set.Add(joints);
        }

        skipped = new List<string>();
        skipReasons = new List<BmdSkipReason>();

        // Pre-scan BCK stems that have zero joint-compatible candidates so paired
        // BAS can be dropped even when dictionary order visits .bas before .bck.
        var skippedBckStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in replacements)
        {
            if (!kvp.Key.EndsWith(".bck", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!retailBckJoints.TryGetValue(kvp.Key, out var retailJoints) || retailJoints.Count == 0)
                continue;

            var anyCompatible = false;
            foreach (var candidate in kvp.Value)
            {
                if (!TryReadBckJointCount(candidate, out var customJoints) ||
                    retailJoints.Contains(customJoints))
                {
                    anyCompatible = true;
                    break;
                }
            }

            if (!anyCompatible)
                skippedBckStems.Add(Path.GetFileNameWithoutExtension(kvp.Key));
        }

        var filtered = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in replacements)
        {
            retailByName.TryGetValue(kvp.Key, out var retailData);

            // Drop the whole stem when no candidate matches any retail joint count
            // for that basename. Compatible candidates still reach per-target merge
            // (Mario-body 16 vs FLUDD 14 picked independently).
            if (kvp.Key.EndsWith(".bck", StringComparison.OrdinalIgnoreCase) &&
                skippedBckStems.Contains(Path.GetFileNameWithoutExtension(kvp.Key)))
            {
                skipped.Add(kvp.Key);
                skipReasons.Add(BmdSkipReason.JointMismatch);
                continue;
            }

            // Keep BAS with the retail BCK when every custom clip was rejected.
            if (kvp.Key.EndsWith(".bas", StringComparison.OrdinalIgnoreCase) &&
                skippedBckStems.Contains(Path.GetFileNameWithoutExtension(kvp.Key)))
            {
                skipped.Add(kvp.Key);
                skipReasons.Add(BmdSkipReason.JointMismatch);
                continue;
            }

            if (kvp.Key.EndsWith(".bck", StringComparison.OrdinalIgnoreCase))
            {
                retailBckJoints.TryGetValue(kvp.Key, out var retailJoints);
                var kept = new List<byte[]>();
                foreach (var candidate in kvp.Value)
                {
                    if (retailJoints == null || retailJoints.Count == 0 ||
                        !TryReadBckJointCount(candidate, out var customJoints) ||
                        retailJoints.Contains(customJoints))
                    {
                        kept.Add(candidate);
                    }
                }

                if (kept.Count == 0)
                {
                    skipped.Add(kvp.Key);
                    skipReasons.Add(BmdSkipReason.JointMismatch);
                    continue;
                }

                filtered[kvp.Key] = kept;
                continue;
            }

            var data = kvp.Value.Count > 0 ? kvp.Value[0] : Array.Empty<byte>();

            // Waluigi-style caps: real mesh but 1 joint vs retail 2/3. Pad JNT1 so
            // TMarioCap bone-1 access survives and the custom hat geometry is kept.
            if (retailData != null &&
                TryNormalizeCapBmdJoints(kvp.Key, data, retailData, out var normalized))
            {
                data = normalized;
            }

            var reason = GetBmdSkipReason(kvp.Key, data, retailData);
            if (reason != BmdSkipReason.None)
            {
                skipped.Add(kvp.Key);
                skipReasons.Add(reason);
                continue;
            }

            filtered[kvp.Key] = new List<byte[]> { data };
        }

        return filtered;
    }

    /// <summary>
    /// Ensure a pack .arc on disk has the tall-character <c>BodyAngleFree.prm</c>
    /// override. Used by zip seed so AppData packs always receive the current
    /// embedded PRM even when an older bundled .arc omitted it.
    /// </summary>
    /// <returns><c>true</c> when the file was rewritten.</returns>
    public static bool EnsureBodyAngleFreePrmInPackFile(string packPath)
    {
        if (string.IsNullOrWhiteSpace(packPath) || !File.Exists(packPath))
            return false;

        try
        {
            var expected = GetBodyAngleFree2PrmBytes();
            var bytes = File.ReadAllBytes(packPath);
            var arc = RarcArchive.Open(OpenToRarcBytes(bytes));
            var existing = arc.EnumerateFiles()
                .FirstOrDefault(f => f.Name.Equals(BodyAngleFreePrmName, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.Data.AsSpan().SequenceEqual(expected))
                return false;

            UpsertRootFile(arc.Root, BodyAngleFreePrmName, expected);
            var saved = new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
            File.WriteAllBytes(packPath, saved);
            return true;
        }
        catch
        {
            // Corrupt / non-RARC packs are left alone; seed still copied the file.
            return false;
        }
    }

    public static MergeResult BuildMergedPack(byte[] retailArchiveBytes, byte[] customSzsBytes,
        bool replaceMatchingBcks = false, bool injectBodyAngleFreePrm = false,
        bool injectBetterMovementPrm = false)
    {
        var retailRarc = OpenToRarcBytes(retailArchiveBytes);
        var retail = RarcArchive.Open(retailRarc);
        var custom = OpenArchive(customSzsBytes);
        // When BCK replacement is on, also pull matching BAS so animSound tables
        // stay paired with the clips (even when BAS bytes happen to match retail).
        // BCK duplicates keep every unique buffer (joint variants); other types
        // stay first-wins.
        var replacements = CollectReplacementsMulti(custom, includeBck: replaceMatchingBcks,
            includeBas: replaceMatchingBcks);
        if (replacements.Count == 0)
        {
            throw new InvalidDataException(
                "Custom SZS has no usable .bmd or .btk files. Only BMD/BTK replacements are applied.");
        }

        replacements = FilterUnsafeReplacements(replacements, retail, out var skipped,
            out var skipReasons);

        if (replacements.Count == 0)
        {
            throw new InvalidDataException(
                "Custom SZS has no usable .bmd or .btk files after filtering unsafe replacements.");
        }

        // Patch matching BMD/BTK (and opt-in BCK/BAS) into the retail RARC buffer in
        // place first. Unmatched custom BTKs are injected afterward.
        // Missing custom BCKs keep retail clips so packs missing e.g. ma_tree_stand.bck
        // still resolve during initValues.
        // Per-target joint gate: basename maps are 1→N (bck/wg_pump + watergun2/body/
        // wg_pump). Each retail node picks a joint-compatible candidate so a
        // 16-joint Mario spray clip cannot overwrite the 14-joint FLUDD pump, while
        // a pack that ships both variants can patch both targets.
        var perTargetSkipped = new List<string>();
        var multiMap = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in replacements)
            multiMap[kvp.Key] = kvp.Value;
        var packArc = RarcArchive.ReplaceFilesByBasename(retailRarc, multiMap, out var replaced,
            AcceptJointCompatibleReplacement, perTargetSkipped);
        foreach (var name in perTargetSkipped)
        {
            skipped.Add(name);
            skipReasons.Add(BmdSkipReason.JointMismatch);
        }

        if (replaced.Count == 0)
        {
            throw new InvalidDataException(
                "Custom SZS BMD/BTK filenames do not match any retail Mario assets (case-insensitive basename).");
        }

        var unmatchedBtks = CollectUnmatchedBtks(custom, retail);
        packArc = InjectBtksIntoBtkDirectory(packArc, unmatchedBtks, out var injected);

        // Inject custom/ BTKs for deferred MActorAnmData ("mario/custom") used by
        // post-init Shadow setup. Do NOT inject better_sms.prm — BSE initMario's
        // mHasMActor / mHasScreenTexture paths freeze during level load.
        // Save() preserves original file IDs (see RarcFileEntry.FileId) so this
        // rebuild does not renumber retail BAS/BCK nodes.
        var customFolderBtks = CollectCustomFolderBtks(custom);
        // Also mirror unmatched BTKs into custom/ so packs that only have btk/
        // inject still expose the BetterSMS folder layout after remount.
        if (customFolderBtks.Count == 0 && unmatchedBtks.Count > 0)
            customFolderBtks = new Dictionary<string, byte[]>(unmatchedBtks, StringComparer.OrdinalIgnoreCase);

        var injectedCustom = 0;
        if (customFolderBtks.Count > 0)
        {
            var arc = RarcArchive.Open(packArc);
            var customNames = new List<string>();
            UpsertFilesIntoDirectory(arc.Root, CustomFolderName, customFolderBtks, customNames);
            packArc = new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
            injectedCustom = customNames.Count;
        }

        // Capless skins (Yoshi / Birdo): stub caps skipped → keep retail TMarioCap
        // but stamp hide-caps so the hat/wet meshes are not drawn.
        // Unpad-able joint-mismatch skips keep retail caps visible — do NOT hide them.
        // (Waluigi-style 1-joint real caps are padded earlier and replaced normally.)
        var hideCaps = false;
        for (int i = 0; i < skipped.Count; i++)
        {
            if (skipReasons[i] == BmdSkipReason.StubCap &&
                skipped[i].StartsWith("ma_cap", StringComparison.OrdinalIgnoreCase))
            {
                hideCaps = true;
                break;
            }
        }

        if (hideCaps)
        {
            var arc = RarcArchive.Open(packArc);
            UpsertRootFile(arc.Root, HideCapsMarkerName, new byte[] { 1 });
            packArc = new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
        }

        // Tall-character BodyAngleFree.prm (bodyanglefree2). Safe TBodyAngleParams
        // payload — not better_sms.prm. Injected at mario RARC root for the
        // module to reload after TMario construction (params.szs is authoritative
        // at construct; pack file is the override source).
        var injectedBodyAngle = false;
        if (injectBodyAngleFreePrm)
        {
            var arc = RarcArchive.Open(packArc);
            UpsertRootFile(arc.Root, BodyAngleFreePrmName, GetBodyAngleFree2PrmBytes());
            packArc = new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
            injectedBodyAngle = true;
        }

        // Official Moveset retune — only when the caller wants it. Always injecting
        // left custom packs heavier after Moveset.kxe was removed (PRM still mounted).
        if (injectBetterMovementPrm)
        {
            var arc = RarcArchive.Open(packArc);
            UpsertRootFile(arc.Root, BetterMovementPrmName, GetBetterMovementPrmBytes());
            packArc = new RarcArchive { RootName = arc.RootName, Root = arc.Root }.Save();
        }

        // Model id hashes visual/anim replacements (BMD/BTK/BCK) so re-importing
        // the same SZS keeps a stable 8-char id. BAS is paired for animSound but
        // excluded from the id — identical retail BAS would otherwise churn ids
        // every time BCK allow-list packs are reimported. PRM overrides are also
        // excluded so BodyAngleFree injection does not churn multiplayer ids.
        // Walk replaced names in RARC order; emit each basename once. Single-candidate
        // stems match the legacy hash. Multi-candidate BCK stems include every
        // unique buffer so Mario-body + FLUDD variants get distinct ids.
        var hashMaterial = new MemoryStream();
        var hashedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in replaced)
        {
            if (name.EndsWith(".bas", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!hashedNames.Add(name))
                continue;
            if (!replacements.TryGetValue(name, out var candidates) || candidates.Count == 0)
                continue;
            var nameBytes = Encoding.ASCII.GetBytes(name.ToLowerInvariant());
            foreach (var replacement in candidates)
            {
            hashMaterial.Write(nameBytes, 0, nameBytes.Length);
            hashMaterial.WriteByte(0);
            hashMaterial.Write(replacement, 0, replacement.Length);
            }
        }

        var modelId = ComputeModelId(hashMaterial.ToArray());
        return new MergeResult
        {
            PackArc = packArc,
            ModelId = modelId,
            ReplacedCount = replaced.Count,
            ReplacedNames = replaced,
            InjectedBtkCount = injected.Count,
            InjectedBtkNames = injected,
            InjectedBetterSmsPrm = false,
            InjectedBodyAngleFreePrm = injectedBodyAngle,
            InjectedCustomBtkCount = injectedCustom,
            SkippedReplacements = skipped,
            InjectedHideCapsMarker = hideCaps,
        };
    }

    public static string ComputeModelId(byte[] content)
    {
        var hash = SHA256.HashData(content);
        var sb = new StringBuilder(ModelIdLength);
        for (int i = 0; i < ModelIdLength / 2; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    public static string DisplayNameFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return "Custom Model";

        name = name.Replace('_', ' ').Replace('-', ' ');
        // Strip Windows duplicate suffixes: "luigi (1)", "birdo (1) (1)"
        while (true)
        {
            var trimmed = System.Text.RegularExpressions.Regex.Replace(
                name, @"\s*\(\d+\)\s*$", string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Equals(name, StringComparison.Ordinal))
                break;
            name = trimmed;
        }

        while (name.Contains("  ", StringComparison.Ordinal))
            name = name.Replace("  ", " ", StringComparison.Ordinal);
        name = name.Trim();
        if (name.Length == 0)
            return "Custom Model";

        var chars = name.ToCharArray();
        bool newWord = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == ' ')
            {
                newWord = true;
                continue;
            }

            if (newWord)
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                newWord = false;
            }
            else
            {
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
        }

        return new string(chars);
    }

    public static bool IsRetailModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId);

    public static string NormalizeModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return string.Empty;

        var trimmed = modelId.Trim().ToLowerInvariant();
        if (trimmed.Length > ModelIdLength)
            trimmed = trimmed[..ModelIdLength];
        foreach (var ch in trimmed)
        {
            if (ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
                continue;
            return string.Empty;
        }

        return trimmed;
    }

    public static byte[] EncodeModelId(string? modelId)
    {
        var bytes = new byte[ModelIdLength];
        var normalized = NormalizeModelId(modelId);
        if (normalized.Length == 0)
            return bytes;
        Encoding.ASCII.GetBytes(normalized).AsSpan(0, Math.Min(ModelIdLength, normalized.Length))
            .CopyTo(bytes);
        return bytes;
    }

    public static string DecodeModelId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;
        int len = 0;
        while (len < bytes.Length && len < ModelIdLength && bytes[len] != 0)
            len++;
        if (len == 0)
            return string.Empty;
        return NormalizeModelId(Encoding.ASCII.GetString(bytes[..len]));
    }
}

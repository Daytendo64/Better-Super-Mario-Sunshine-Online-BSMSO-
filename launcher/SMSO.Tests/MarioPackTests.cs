using SMSO.Net.MarioPack;

namespace SMSO.Tests;

public class MarioPackTests
{
    [Fact]
    public void Yaz0_RoundTrip_PreservesBytes()
    {
        var original = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(
            Enumerable.Repeat((byte)0xAB, 64)).ToArray();
        var compressed = Yaz0.Compress(original);
        Assert.True(Yaz0.IsYaz0(compressed));
        var roundTrip = Yaz0.Decompress(compressed);
        Assert.Equal(original, roundTrip);
    }

    [Fact]
    public void Merge_ReplacesOnlyMatchingBmdBtk()
    {
        var retail = BuildArchive(new[]
        {
            ("ma_mdl1.bmd", new byte[] { 1, 1, 1 }),
            ("ma_tex.btk", new byte[] { 2, 2, 2 }),
            ("ma_wait.bck", new byte[] { 3, 3, 3 }),
            ("extra.bin", new byte[] { 4, 4, 4 }),
        });
        var custom = BuildArchive(new[]
        {
            ("ma_mdl1.bmd", new byte[] { 9, 9, 9 }),
            ("ma_tex.btk", new byte[] { 8, 8, 8 }),
            ("ma_wait.bck", new byte[] { 7, 7, 7 }), // must be ignored
            ("noise.bas", new byte[] { 6, 6, 6 }),   // must be ignored
        });

        var merge = CharacterPack.BuildMergedPack(retail, custom);
        Assert.Equal(2, merge.ReplacedCount);
        Assert.Equal(0, merge.InjectedBtkCount);
        Assert.Equal(8, merge.ModelId.Length);

        var patched = CharacterPack.OpenArchive(merge.PackArc);
        var files = patched.EnumerateFiles().ToDictionary(f => f.Name, f => f.Data,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 9, 9, 9 }, files["ma_mdl1.bmd"]);
        Assert.Equal(new byte[] { 8, 8, 8 }, files["ma_tex.btk"]);
        Assert.Equal(new byte[] { 3, 3, 3 }, files["ma_wait.bck"]);
        Assert.Equal(new byte[] { 4, 4, 4 }, files["extra.bin"]);
    }

    [Fact]
    public void Merge_InjectsCustomOnlyBtksIntoBtkFolder()
    {
        // Retail layout: btk/ holds watergun_water; body has no ma_mdl1.btk.
        var retailRoot = new RarcDirectory { Name = "mario" };
        var bmdDir = new RarcDirectory { Name = "bmd" };
        var btkDir = new RarcDirectory { Name = "btk" };
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = new byte[] { 1, 1, 1 } });
        btkDir.Files.Add(new RarcFileEntry { Name = "watergun_water.btk", Data = new byte[] { 2, 2 } });
        retailRoot.Directories.Add(bmdDir);
        retailRoot.Directories.Add(btkDir);
        var retail = new RarcArchive { RootName = "mario", Root = retailRoot }.Save();

        var customRoot = new RarcDirectory { Name = "mario" };
        var customBmd = new RarcDirectory { Name = "bmd" };
        var customExtra = new RarcDirectory { Name = "custom" };
        customBmd.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = new byte[] { 9, 9, 9 } });
        customExtra.Files.Add(new RarcFileEntry { Name = "ma_mdl1.btk", Data = new byte[] { 5, 5, 5, 5 } });
        customExtra.Files.Add(new RarcFileEntry { Name = "ma_cap1.btk", Data = new byte[] { 6, 6 } });
        customRoot.Directories.Add(customBmd);
        customRoot.Directories.Add(customExtra);
        var custom = Yaz0.Compress(new RarcArchive { RootName = "mario", Root = customRoot }.Save());

        var merge = CharacterPack.BuildMergedPack(retail, custom);
        Assert.Equal(1, merge.ReplacedCount);
        Assert.Equal(2, merge.InjectedBtkCount);
        // custom/ is injected for deferred MActor; better_sms.prm stays out
        // (BSE initMario freezes on mHasMActor/mHasScreenTexture).
        Assert.False(merge.InjectedBetterSmsPrm);
        Assert.Equal(2, merge.InjectedCustomBtkCount);
        Assert.Contains("ma_mdl1.btk", merge.InjectedBtkNames);
        Assert.Contains("ma_cap1.btk", merge.InjectedBtkNames);

        var files = CharacterPack.OpenArchive(merge.PackArc).EnumerateFiles()
            .ToDictionary(f => f.FullPath.Replace('\\', '/'), f => f.Data,
                StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 9, 9, 9 }, files["bmd/ma_mdl1.bmd"]);
        Assert.Equal(new byte[] { 5, 5, 5, 5 }, files["btk/ma_mdl1.btk"]);
        Assert.Equal(new byte[] { 6, 6 }, files["btk/ma_cap1.btk"]);
        Assert.Equal(new byte[] { 2, 2 }, files["btk/watergun_water.btk"]);
        Assert.Equal(new byte[] { 5, 5, 5, 5 }, files["custom/ma_mdl1.btk"]);
        Assert.Equal(new byte[] { 6, 6 }, files["custom/ma_cap1.btk"]);
        Assert.False(files.ContainsKey("better_sms.prm"));
    }

    [Fact]
    public void Merge_WithoutCustomOrPrm_DoesNotInjectBetterSms()
    {
        var retail = BuildArchive(new[]
        {
            ("ma_mdl1.bmd", new byte[] { 1, 1, 1 }),
            ("ma_tex.btk", new byte[] { 2, 2 }),
        });
        var custom = BuildArchive(new[]
        {
            ("ma_mdl1.bmd", new byte[] { 9, 9, 9 }),
            ("ma_tex.btk", new byte[] { 8, 8 }),
        });

        var merge = CharacterPack.BuildMergedPack(retail, custom);
        Assert.False(merge.InjectedBetterSmsPrm);
        Assert.Equal(0, merge.InjectedCustomBtkCount);
        var files = CharacterPack.OpenArchive(merge.PackArc).EnumerateFiles()
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("better_sms.prm", files);
    }

    [Fact]
    public void BuildBetterSmsPrm_UsesSmsKeyCodeLayout()
    {
        Assert.Equal(0xE401, CharacterPack.CalcKeyCode("mHasMActor"));
        Assert.Equal(0x3C94, CharacterPack.CalcKeyCode("mMActorFramerate"));
        Assert.Equal(0xCACA, CharacterPack.CalcKeyCode("mHasScreenTexture"));

        var prm = CharacterPack.BuildBetterSmsPrm();
        Assert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(prm.AsSpan(0, 4)));
        // First entry: mHasMActor = false (TexAnim owns UV scrolls)
        Assert.Equal(0xE401, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(prm.AsSpan(4, 2)));
        Assert.Equal(10, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(prm.AsSpan(6, 2)));
        Assert.Equal("mHasMActor", System.Text.Encoding.ASCII.GetString(prm, 8, 10));
        Assert.Equal(0, prm[8 + 10 + 4]); // bool false
        // Third entry ends with mHasScreenTexture = true
        Assert.Equal(1, prm[^1]);
    }

    [Fact]
    public void Merge_IgnoresNonBmdBtkOnlyCustom_Rejects()
    {
        var retail = BuildArchive(new[] { ("ma_mdl1.bmd", new byte[] { 1 }) });
        var custom = BuildArchive(new[] { ("readme.txt", new byte[] { 2 }) });
        Assert.Throws<InvalidDataException>(() => CharacterPack.BuildMergedPack(retail, custom));
    }

    [Fact]
    public void Merge_NoMatchingBasenames_Rejects()
    {
        var retail = BuildArchive(new[] { ("ma_mdl1.bmd", new byte[] { 1 }) });
        var custom = BuildArchive(new[] { ("other.bmd", new byte[] { 2 }) });
        Assert.Throws<InvalidDataException>(() => CharacterPack.BuildMergedPack(retail, custom));
    }

    [Fact]
    public void DisplayName_FromFileName_CleansUnderscores()
    {
        Assert.Equal("Cool Skin", CharacterPack.DisplayNameFromFileName(@"C:\mods\cool_skin.szs"));
        Assert.Equal("Luigi Classic", CharacterPack.DisplayNameFromFileName("luigi-classic.szs"));
        Assert.Equal("Luigi", CharacterPack.DisplayNameFromFileName("luigi (1).szs"));
        Assert.Equal("Shadowluigi", CharacterPack.DisplayNameFromFileName("shadowluigi (1).szs"));
        Assert.Equal("Birdo", CharacterPack.DisplayNameFromFileName("birdo (1) (1).szs"));
    }

    [Fact]
    public void EmptyModelId_IsRetail()
    {
        Assert.True(CharacterPack.IsRetailModelId(null));
        Assert.True(CharacterPack.IsRetailModelId(""));
        Assert.True(CharacterPack.IsRetailModelId("   "));
        Assert.Equal("", CharacterPack.NormalizeModelId(null));
        Assert.Equal(new byte[8], CharacterPack.EncodeModelId(""));
        Assert.Equal("", CharacterPack.DecodeModelId(new byte[8]));
    }

    [Fact]
    public void ModelId_EncodeDecode_RoundTrip()
    {
        const string id = "a1b2c3d4";
        var bytes = CharacterPack.EncodeModelId(id);
        Assert.Equal(8, bytes.Length);
        Assert.Equal(id, CharacterPack.DecodeModelId(bytes));
    }

    [Fact]
    public void BasenameMatch_IsCaseInsensitive()
    {
        var retail = BuildArchive(new[] { ("Ma_Mdl1.BMD", new byte[] { 1 }) });
        var custom = BuildArchive(new[] { ("ma_mdl1.bmd", new byte[] { 5, 5 }) });
        var merge = CharacterPack.BuildMergedPack(retail, custom);
        Assert.Equal(1, merge.ReplacedCount);
        var patched = CharacterPack.OpenArchive(merge.PackArc);
        var file = patched.EnumerateFiles().First(f =>
            f.Name.Equals("Ma_Mdl1.BMD", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new byte[] { 5, 5 }, file.Data);
    }

    [Fact]
    public void Merge_PreservesRetailRootFourCCAndBasBytes()
    {
        // Nested layout matching mario.arc: root/bas + root/bmd.
        var retailRoot = new RarcDirectory { Name = "mario" };
        var basDir = new RarcDirectory { Name = "bas" };
        var bmdDir = new RarcDirectory { Name = "bmd" };
        basDir.Files.Add(new RarcFileEntry { Name = "ma_jump.bas", Data = new byte[] { 1, 2, 3, 4 } });
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = new byte[] { 9, 9, 9 } });
        retailRoot.Directories.Add(basDir);
        retailRoot.Directories.Add(bmdDir);
        var retailArc = new RarcArchive { RootName = "mario", Root = retailRoot }.Save();

        // Dir table starts at dataHeader(0x20) + relDir(0x20) = 0x40.
        // Root fourcc must be ROOT (retail SMS convention).
        Assert.Equal((byte)'R', retailArc[0x40]);
        Assert.Equal((byte)'O', retailArc[0x41]);
        Assert.Equal((byte)'O', retailArc[0x42]);
        Assert.Equal((byte)'T', retailArc[0x43]);

        var custom = BuildArchive(new[] { ("ma_mdl1.bmd", new byte[] { 7, 7, 7, 7 }) });
        var merge = CharacterPack.BuildMergedPack(retailArc, custom);
        Assert.Equal(1, merge.ReplacedCount);

        // In-place patch keeps ROOT and does not rewrite the directory table.
        Assert.Equal((byte)'R', merge.PackArc[0x40]);
        Assert.Equal((byte)'O', merge.PackArc[0x41]);
        Assert.Equal((byte)'O', merge.PackArc[0x42]);
        Assert.Equal((byte)'T', merge.PackArc[0x43]);

        var patched = CharacterPack.OpenArchive(merge.PackArc);
        var files = patched.EnumerateFiles()
            .ToDictionary(f => f.FullPath.Replace('\\', '/'), f => f.Data,
                StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, files["bas/ma_jump.bas"]);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, files["bmd/ma_mdl1.bmd"]);
    }

    [Fact]
    public void ReplaceFilesByBasename_SameSize_KeepsArchiveLength()
    {
        var root = new RarcDirectory { Name = "mario" };
        root.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = new byte[] { 1, 1, 1 } });
        root.Files.Add(new RarcFileEntry { Name = "keep.bas", Data = new byte[] { 4, 5, 6 } });
        var retail = new RarcArchive { RootName = "mario", Root = root }.Save();
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ma_mdl1.bmd"] = new byte[] { 9, 9, 9 },
        };
        var patched = RarcArchive.ReplaceFilesByBasename(retail, replacements, out var names);
        Assert.Equal(new[] { "ma_mdl1.bmd" }, names);
        Assert.Equal(retail.Length, patched.Length);
        Assert.Equal(retail.AsSpan(0, 0x20).ToArray(), patched.AsSpan(0, 0x20).ToArray());
    }

    [Fact]
    public void PadBmd_ExpandsOneJointCapToRetailCount()
    {
        var oneJoint = BuildPadableBmd(joints: 1, minSize: 7000);
        Assert.True(CharacterPack.TryPadBmdJointCount(oneJoint, 2, out var padded2));
        Assert.True(CharacterPack.TryReadBmdJointCount(padded2, out var c2));
        Assert.Equal(2, c2);
        Assert.Equal(padded2.Length,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(padded2.AsSpan(8, 4)));

        Assert.True(CharacterPack.TryPadBmdJointCount(oneJoint, 3, out var padded3));
        Assert.True(CharacterPack.TryReadBmdJointCount(padded3, out var c3));
        Assert.Equal(3, c3);
    }

    [Fact]
    public void Merge_PadsCapBmdsWithWrongJointCount()
    {
        var retailRoot = new RarcDirectory { Name = "mario" };
        var bmdDir = new RarcDirectory { Name = "bmd" };
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = BuildPadableBmd(29, 117248) });
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap1.bmd", Data = BuildPadableBmd(2, 9888) });
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap3.bmd", Data = BuildPadableBmd(3, 9152) });
        retailRoot.Directories.Add(bmdDir);
        var retail = new RarcArchive { RootName = "mario", Root = retailRoot }.Save();

        // Real-size custom caps with 1 joint (Waluigi-style) — must be padded in,
        // not skipped, and must not get hide-caps.
        var customCap1 = BuildPadableBmd(1, 11328);
        var customCap3 = BuildPadableBmd(1, 7424);
        // Mark payloads so we can prove custom geometry survived (not retail).
        customCap1[0x1F] = 0xA1;
        customCap3[0x1F] = 0xA3;

        var custom = BuildArchive(new[]
        {
            ("ma_mdl1.bmd", BuildPadableBmd(29, 136960)),
            ("ma_cap1.bmd", customCap1),
            ("ma_cap3.bmd", customCap3),
        });

        var merge = CharacterPack.BuildMergedPack(retail, custom);
        Assert.Equal(3, merge.ReplacedCount);
        Assert.Contains("ma_mdl1.bmd", merge.ReplacedNames);
        Assert.Contains("ma_cap1.bmd", merge.ReplacedNames);
        Assert.Contains("ma_cap3.bmd", merge.ReplacedNames);
        Assert.DoesNotContain("ma_cap1.bmd", merge.SkippedReplacements);
        Assert.DoesNotContain("ma_cap3.bmd", merge.SkippedReplacements);
        Assert.False(merge.InjectedHideCapsMarker);
        Assert.True(CharacterPack.TryValidatePackForInit(merge.PackArc, out _));

        var files = CharacterPack.OpenArchive(merge.PackArc).EnumerateFiles()
            .ToDictionary(f => f.Name, f => f.Data, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, CharacterPack.TryReadBmdJointCount(files["ma_cap1.bmd"], out var c1) ? c1 : -1);
        Assert.Equal(3, CharacterPack.TryReadBmdJointCount(files["ma_cap3.bmd"], out var c3) ? c3 : -1);
        Assert.Equal(0xA1, files["ma_cap1.bmd"][0x1F]);
        Assert.Equal(0xA3, files["ma_cap3.bmd"][0x1F]);
        Assert.False(files.ContainsKey(CharacterPack.HideCapsMarkerName));
    }

    [Fact]
    public void ValidatePack_RejectsOneJointCaps()
    {
        var root = new RarcDirectory { Name = "mario" };
        var bmdDir = new RarcDirectory { Name = "bmd" };
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = BuildPadableBmd(29, 1000) });
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap1.bmd", Data = BuildPadableBmd(1, 1000) });
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap3.bmd", Data = BuildPadableBmd(1, 1000) });
        root.Directories.Add(bmdDir);
        var pack = new RarcArchive { RootName = "mario", Root = root }.Save();

        Assert.False(CharacterPack.TryValidatePackForInit(pack, out var reason));
        Assert.Contains("ma_cap1", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_SkipsStubCapBmds()
    {
        var retailRoot = new RarcDirectory { Name = "mario" };
        var bmdDir = new RarcDirectory { Name = "bmd" };
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = BuildPadableBmd(29, 117248) });
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap1.bmd", Data = BuildPadableBmd(2, 9888) });
        bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap3.bmd", Data = BuildPadableBmd(3, 9152) });
        retailRoot.Directories.Add(bmdDir);
        var retail = new RarcArchive { RootName = "mario", Root = retailRoot }.Save();

        var custom = BuildArchive(new[]
        {
            ("ma_mdl1.bmd", BuildPadableBmd(29, 138272)),
            ("ma_cap1.bmd", new byte[4000]),
            ("ma_cap3.bmd", new byte[4000]),
        });

        var merge = CharacterPack.BuildMergedPack(retail, custom);
        Assert.Equal(1, merge.ReplacedCount);
        Assert.Contains("ma_mdl1.bmd", merge.ReplacedNames);
        Assert.Contains("ma_cap1.bmd", merge.SkippedReplacements);
        Assert.Contains("ma_cap3.bmd", merge.SkippedReplacements);
        Assert.True(merge.InjectedHideCapsMarker);

        var files = CharacterPack.OpenArchive(merge.PackArc).EnumerateFiles()
            .ToDictionary(f => f.Name, f => f.Data, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(9888, files["ma_cap1.bmd"].Length);
        Assert.Equal(9152, files["ma_cap3.bmd"].Length);
        Assert.True(files.ContainsKey(CharacterPack.HideCapsMarkerName));
        Assert.True(CharacterPack.TryValidatePackForInit(merge.PackArc, out _));
    }

    /// <summary>
    /// Minimal J3D2bmd3 with a real JNT1 (joint entries + remap + names) so
    /// <see cref="CharacterPack.TryPadBmdJointCount"/> can expand it.
    /// </summary>
    private static byte[] BuildPadableBmd(int joints, int minSize)
    {
        const int jointEntry = 0x40;
        int jointDataOff = 0x18;
        int remapOff = jointDataOff + joints * jointEntry;
        int remapBytes = joints * 2;
        if ((remapOff + remapBytes) % 4 != 0)
            remapBytes += 4 - ((remapOff + remapBytes) % 4);
        int stringOff = remapOff + remapBytes;
        var names = Enumerable.Range(0, joints).Select(i => i == 0 ? "root" : "j" + i).ToArray();
        var nameBytes = names.Select(n => System.Text.Encoding.ASCII.GetBytes(n)).ToArray();
        int strHeader = 4 + joints * 4;
        int strCursor = strHeader;
        var nameOffs = new int[joints];
        for (int i = 0; i < joints; i++)
        {
            nameOffs[i] = strCursor;
            strCursor += nameBytes[i].Length + 1;
        }

        int strSize = strCursor;
        if (strSize % 4 != 0)
            strSize += 4 - (strSize % 4);
        int jntSize = stringOff + strSize;
        if (jntSize % 32 != 0)
            jntSize += 32 - (jntSize % 32);

        var jnt = new byte[jntSize];
        jnt[0] = (byte)'J';
        jnt[1] = (byte)'N';
        jnt[2] = (byte)'T';
        jnt[3] = (byte)'1';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(jnt.AsSpan(4, 4), jntSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(8, 2), (ushort)joints);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(10, 2), 0xFFFF);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(jnt.AsSpan(0x0C, 4), (uint)jointDataOff);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(jnt.AsSpan(0x10, 4), (uint)remapOff);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(jnt.AsSpan(0x14, 4), (uint)stringOff);
        for (int i = 0; i < joints; i++)
        {
            int o = jointDataOff + i * jointEntry;
            // scale 1,1,1
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(jnt.AsSpan(o + 4, 4),
                BitConverter.SingleToInt32Bits(1f));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(jnt.AsSpan(o + 8, 4),
                BitConverter.SingleToInt32Bits(1f));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(jnt.AsSpan(o + 12, 4),
                BitConverter.SingleToInt32Bits(1f));
            jnt[o + 2] = 0x00;
            jnt[o + 3] = 0xFF;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(o + 0x16, 2), 0xFFFF);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(remapOff + i * 2, 2),
                (ushort)i);
        }

        for (int i = remapOff + joints * 2; i < stringOff; i++)
            jnt[i] = 0xFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(stringOff, 2), (ushort)joints);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(stringOff + 2, 2), 0xFFFF);
        for (int i = 0; i < joints; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(stringOff + 4 + i * 4, 2),
                CharacterPack.CalcKeyCode(names[i]));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(jnt.AsSpan(stringOff + 6 + i * 4, 2),
                (ushort)nameOffs[i]);
            nameBytes[i].CopyTo(jnt.AsSpan(stringOff + nameOffs[i]));
        }

        int fileSize = Math.Max(minSize, 0x20 + jntSize);
        var data = new byte[fileSize];
        data[0] = (byte)'J';
        data[1] = (byte)'3';
        data[2] = (byte)'D';
        data[3] = (byte)'2';
        data[4] = (byte)'b';
        data[5] = (byte)'m';
        data[6] = (byte)'d';
        data[7] = (byte)'3';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8, 4), fileSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0x0C, 4), 1); // 1 section
        jnt.CopyTo(data.AsSpan(0x20));
        return data;
    }

    [Fact]
    public void Rarc_FileDataOffset_IsRelativeToDataHeader()
    {
        // Header 0x0C must be relative to the data header (0x20), matching retail
        // SMS / JKRArchive. Writing an absolute offset here shifts every file
        // 0x20 bytes early and corrupts same-size BMD replaces.
        var root = new RarcDirectory { Name = "mario" };
        var payload = new byte[] { (byte)'J', (byte)'3', (byte)'D', (byte)'2', 5, 6, 7, 8 };
        root.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = payload });
        var saved = new RarcArchive { RootName = "mario", Root = root }.Save();

        uint dataHeaderOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(saved.AsSpan(0x08, 4));
        uint fileDataRel = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(saved.AsSpan(0x0C, 4));
        Assert.Equal(0x20u, dataHeaderOff);
        int absFileData = RarcArchive.GetFileDataAbsoluteOffset(saved);
        Assert.Equal((int)dataHeaderOff + (int)fileDataRel, absFileData);
        // Must NOT store an absolute offset in 0x0C (the old bug wrote 0x20 + rel).
        Assert.NotEqual(absFileData, (int)fileDataRel);

        var opened = RarcArchive.Open(saved);
        var file = opened.EnumerateFiles().Single(f => f.Name == "ma_mdl1.bmd");
        Assert.Equal(payload, file.Data);

        // Same-size in-place replace must overwrite the real file bytes, not the
        // 0x20-byte gap before them.
        var replaced = new byte[] { (byte)'J', (byte)'3', (byte)'D', (byte)'2', 9, 9, 9, 9 };
        var patched = RarcArchive.ReplaceFilesByBasename(saved,
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ma_mdl1.bmd"] = replaced,
            }, out _);
        Assert.Equal(replaced, RarcArchive.Open(patched).EnumerateFiles()
            .Single(f => f.Name == "ma_mdl1.bmd").Data);
        Assert.Equal(replaced, patched.AsSpan(absFileData, replaced.Length).ToArray());
    }

    [Fact]
    public void SanitizeFileStem_RemovesInvalidChars()
    {
        Assert.Equal("Shadow Luigi", ModelLibrary.SanitizeFileStem("Shadow Luigi"));
        Assert.Equal("Cool Skin", ModelLibrary.SanitizeFileStem("Cool<>Skin"));
        Assert.Equal("", ModelLibrary.SanitizeFileStem("   "));
    }

    [Fact]
    public void ImportSzs_WritesDisplayNameFiles()
    {
        static byte[] FakeBmd(int joints, int size)
        {
            var data = new byte[Math.Max(size, 32)];
            data[0] = (byte)'J';
            data[1] = (byte)'3';
            data[2] = (byte)'D';
            data[3] = (byte)'2';
            const int o = 16;
            data[o] = (byte)'J';
            data[o + 1] = (byte)'N';
            data[o + 2] = (byte)'T';
            data[o + 3] = (byte)'1';
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(o + 8, 2),
                (ushort)joints);
            return data;
        }

        var root = Path.Combine(Path.GetTempPath(), "bsmso-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = ModelLibrary.LibraryDirectoryOverride;
        try
        {
            ModelLibrary.LibraryDirectoryOverride = root;

            var retailRoot = new RarcDirectory { Name = "mario" };
            var bmdDir = new RarcDirectory { Name = "bmd" };
            bmdDir.Files.Add(new RarcFileEntry { Name = "ma_mdl1.bmd", Data = FakeBmd(29, 64) });
            bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap1.bmd", Data = FakeBmd(2, 64) });
            bmdDir.Files.Add(new RarcFileEntry { Name = "ma_cap3.bmd", Data = FakeBmd(3, 64) });
            retailRoot.Directories.Add(bmdDir);
            var retail = new RarcArchive { RootName = "mario", Root = retailRoot }.Save();

            var customPath = Path.Combine(root, "source_yoshi.szs");
            File.WriteAllBytes(customPath, BuildArchive(new[]
            {
                ("ma_mdl1.bmd", FakeBmd(29, 80)),
                ("ma_cap1.bmd", FakeBmd(2, 80)),
                ("ma_cap3.bmd", FakeBmd(3, 80)),
            }));

            var entry = ModelLibrary.ImportSzs(customPath, retail, "Yoshi");
            Assert.Equal("Yoshi", entry.DisplayName);
            Assert.Equal("Yoshi.arc", entry.PackFileName);
            Assert.True(File.Exists(Path.Combine(root, "Yoshi.arc")));
            Assert.True(File.Exists(Path.Combine(root, "Yoshi.szs")));
            Assert.False(File.Exists(Path.Combine(root, entry.Id + ".arc")));

            // Legacy hex packs are renamed on list.
            File.WriteAllBytes(Path.Combine(root, entry.Id + ".arc"), File.ReadAllBytes(Path.Combine(root, "Yoshi.arc")));
            File.Delete(Path.Combine(root, "Yoshi.arc"));
            var listed = ModelLibrary.ListEntries(includeRetail: false);
            Assert.Contains(listed, e => e.Id == entry.Id && e.PackFileName == "Yoshi.arc");
            Assert.True(File.Exists(Path.Combine(root, "Yoshi.arc")));
            Assert.False(File.Exists(Path.Combine(root, entry.Id + ".arc")));

            // Collision-suffixed names collapse back to the clean stem once free.
            var collisionArc = Path.Combine(root, $"Yoshi-{entry.Id}.arc");
            var collisionSzs = Path.Combine(root, $"Yoshi-{entry.Id}.szs");
            File.Move(Path.Combine(root, "Yoshi.arc"), collisionArc);
            File.Move(Path.Combine(root, "Yoshi.szs"), collisionSzs);
            var listed2 = ModelLibrary.ListEntries(includeRetail: false);
            Assert.Contains(listed2, e => e.Id == entry.Id && e.PackFileName == "Yoshi.arc");
            Assert.True(File.Exists(Path.Combine(root, "Yoshi.arc")));
            Assert.True(File.Exists(Path.Combine(root, "Yoshi.szs")));
            Assert.False(File.Exists(collisionArc));
            Assert.False(File.Exists(collisionSzs));
        }
        finally
        {
            ModelLibrary.LibraryDirectoryOverride = previous;
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static byte[] BuildArchive(IEnumerable<(string Name, byte[] Data)> files)
    {
        var root = new RarcDirectory { Name = "mario" };
        foreach (var (name, data) in files)
            root.Files.Add(new RarcFileEntry { Name = name, Data = data });
        var archive = new RarcArchive { RootName = "mario", Root = root };
        var rarc = archive.Save();
        return Yaz0.Compress(rarc);
    }
}

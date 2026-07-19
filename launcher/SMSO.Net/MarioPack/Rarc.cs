using System.Buffers.Binary;
using System.Text;

namespace SMSO.Net.MarioPack;

/// <summary>GameCube RARC archive with nested directories.</summary>
public sealed class RarcArchive
{
    public string RootName { get; set; } = ".";
    public RarcDirectory Root { get; set; } = new() { Name = "." };

    public IEnumerable<RarcFileEntry> EnumerateFiles() => EnumerateFiles(Root, "");

    private static IEnumerable<RarcFileEntry> EnumerateFiles(RarcDirectory dir, string prefix)
    {
        foreach (var file in dir.Files)
        {
            file.FullPath = string.IsNullOrEmpty(prefix) ? file.Name : prefix + "/" + file.Name;
            yield return file;
        }

        foreach (var child in dir.Directories)
        {
            var childPrefix = string.IsNullOrEmpty(prefix) ? child.Name : prefix + "/" + child.Name;
            foreach (var file in EnumerateFiles(child, childPrefix))
                yield return file;
        }
    }

    /// <summary>
    /// Absolute start of the file-data blob. RARC header 0x0C stores the offset
    /// relative to the data header (typically at 0x20), not an absolute file
    /// offset — i.e. <c>absFileData = dataHeaderOff + header[0x0C]</c>.
    /// Treating 0x0C as absolute shifts every file 0x20 bytes early and breaks
    /// same-size in-place BMD replaces (Needle freeze on stage load).
    /// </summary>
    public static int GetFileDataAbsoluteOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x10)
            throw new InvalidDataException("RARC too small.");
        uint dataHeaderOff = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x08, 4));
        if (dataHeaderOff == 0 || dataHeaderOff >= (uint)data.Length)
            dataHeaderOff = 0x20;
        uint fileDataRel = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x0C, 4));
        return (int)dataHeaderOff + (int)fileDataRel;
    }

    public static RarcArchive Open(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40)
            throw new InvalidDataException("RARC too small.");
        if (data[0] != (byte)'R' || data[1] != (byte)'A' || data[2] != (byte)'R' || data[3] != (byte)'C')
            throw new InvalidDataException("Not an RARC archive.");

        uint dataHeaderOff = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x08, 4));
        if (dataHeaderOff == 0 || dataHeaderOff >= (uint)data.Length)
            dataHeaderOff = 0x20;

        int hb = (int)dataHeaderOff;
        uint dirCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(hb + 0x00, 4));
        uint dirRel = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(hb + 0x04, 4));
        uint nodeCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(hb + 0x08, 4));
        uint nodeRel = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(hb + 0x0C, 4));
        uint stringRel = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(hb + 0x14, 4));

        int absDir = hb + (int)dirRel;
        int absNode = hb + (int)nodeRel;
        int absString = hb + (int)stringRel;
        int absFileData = GetFileDataAbsoluteOffset(data);

        var dirs = new DirRec[dirCount];
        for (int i = 0; i < dirCount; i++)
        {
            int o = absDir + i * 0x10;
            uint nameOff = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(o + 0x04, 4));
            dirs[i] = new DirRec
            {
                Name = ReadCString(data, absString + (int)nameOff),
                EntryCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(o + 0x0A, 2)),
                FirstNode = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(o + 0x0C, 4)),
            };
        }

        var nodes = new NodeRec[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            int o = absNode + i * 0x14;
            ushort nameOff = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(o + 0x06, 2));
            nodes[i] = new NodeRec
            {
                Id = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(o + 0x00, 2)),
                Type = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(o + 0x04, 2)),
                Name = ReadCString(data, absString + nameOff),
                DataOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(o + 0x08, 4)),
                DataSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(o + 0x0C, 4)),
            };
        }

        var archive = new RarcArchive();
        if (dirCount == 0)
            return archive;

        archive.RootName = dirs[0].Name;
        archive.Root = BuildDirectory(0, dirs, nodes, data, absFileData);
        return archive;
    }

    /// <summary>
    /// Optional per-node gate for <see cref="ReplaceFilesByBasename"/>. Return
    /// false to leave the retail node unchanged.
    /// </summary>
    /// <param name="basename">File basename (no directory).</param>
    /// <param name="retailData">Bytes currently stored for this retail node.</param>
    /// <param name="retailOffset">Absolute offset of <paramref name="retailData"/> in the RARC buffer.</param>
    /// <param name="retailLength">Length of the retail file payload.</param>
    /// <param name="replacement">Candidate replacement bytes.</param>
    public delegate bool ReplacementAcceptFilter(
        string basename, byte[] archiveBuffer, int retailOffset, int retailLength, byte[] replacement);

    /// <summary>
    /// Patch files into an existing RARC buffer by basename while preserving the
    /// original directory/node/string layout (ROOT fourcc, global file IDs, entry
    /// order). Same-size replacements overwrite in place; different sizes append
    /// at the end and retarget the file entry. Critical for SMS mario.arc remounts
    /// — a full Save() rebuild can mute animSound/BAS lookups.
    /// <para>
    /// Optional <paramref name="acceptReplacement"/> is evaluated per retail target
    /// node (not once per basename). SMS ships duplicate basenames such as
    /// <c>bck/wg_pump.bck</c> (16 joints) and <c>watergun2/body/wg_pump.bck</c>
    /// (14 joints); rejecting a mismatched target keeps that node retail while
    /// still allowing the matching sibling to be patched. The multi-candidate
    /// overload tries each custom buffer until one is accepted so a pack can
    /// ship both joint variants under the same basename.
    /// </para>
    /// </summary>
    /// <param name="acceptReplacement">
    /// Per-node filter; null accepts every basename match.
    /// </param>
    /// <param name="rejectedNames">
    /// Optional sink for basenames skipped by <paramref name="acceptReplacement"/>.
    /// </param>
    public static byte[] ReplaceFilesByBasename(ReadOnlySpan<byte> retailRarc,
        IReadOnlyDictionary<string, byte[]> replacementsByBasename,
        out List<string> replacedNames,
        ReplacementAcceptFilter? acceptReplacement = null,
        List<string>? rejectedNames = null)
    {
        var multi = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
        if (replacementsByBasename != null)
        {
            foreach (var kvp in replacementsByBasename)
            {
                if (kvp.Value != null)
                    multi[kvp.Key] = new[] { kvp.Value };
            }
        }

        return ReplaceFilesByBasename(retailRarc, multi, out replacedNames, acceptReplacement,
            rejectedNames);
    }

    /// <summary>
    /// Same as the single-buffer overload, but each basename may supply multiple
    /// candidate payloads. Per retail node the first candidate accepted by
    /// <paramref name="acceptReplacement"/> is used (or the first candidate when
    /// the filter is null). Enables joint-matched Mario-body vs FLUDD
    /// <c>wg_pump.bck</c> replacements from one custom pack.
    /// </summary>
    public static byte[] ReplaceFilesByBasename(ReadOnlySpan<byte> retailRarc,
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> replacementsByBasename,
        out List<string> replacedNames,
        ReplacementAcceptFilter? acceptReplacement = null,
        List<string>? rejectedNames = null)
    {
        replacedNames = new List<string>();
        if (replacementsByBasename == null || replacementsByBasename.Count == 0)
            throw new InvalidDataException("No replacement files provided.");
        if (retailRarc.Length < 0x40)
            throw new InvalidDataException("RARC too small.");
        if (retailRarc[0] != (byte)'R' || retailRarc[1] != (byte)'A' ||
            retailRarc[2] != (byte)'R' || retailRarc[3] != (byte)'C')
            throw new InvalidDataException("Not an RARC archive.");

        // Materialize once so the accept filter can slice retail payloads without
        // ReadOnlySpan-in-Func (ref structs are banned as generic type args).
        var archiveBuffer = retailRarc.ToArray();

        var map = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in replacementsByBasename)
        {
            if (kvp.Value != null && kvp.Value.Count > 0)
                map[kvp.Key] = kvp.Value;
        }

        uint dataHeaderOff = BinaryPrimitives.ReadUInt32BigEndian(archiveBuffer.AsSpan(0x08, 4));
        if (dataHeaderOff == 0 || dataHeaderOff >= (uint)archiveBuffer.Length)
            dataHeaderOff = 0x20;

        int hb = (int)dataHeaderOff;
        uint nodeCount = BinaryPrimitives.ReadUInt32BigEndian(archiveBuffer.AsSpan(hb + 0x08, 4));
        uint nodeRel = BinaryPrimitives.ReadUInt32BigEndian(archiveBuffer.AsSpan(hb + 0x0C, 4));
        uint stringRel = BinaryPrimitives.ReadUInt32BigEndian(archiveBuffer.AsSpan(hb + 0x14, 4));
        int absNode = hb + (int)nodeRel;
        int absString = hb + (int)stringRel;
        int absFileData = GetFileDataAbsoluteOffset(archiveBuffer);

        // Collect patches first so we know how much to append.
        var patches = new List<(int NodeOffset, int OldDataAbs, uint OldSize, byte[] NewData)>();
        int appendBytes = 0;
        for (int i = 0; i < (int)nodeCount; i++)
        {
            int no = absNode + i * 0x14;
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(archiveBuffer.AsSpan(no + 0x00, 2));
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(archiveBuffer.AsSpan(no + 0x04, 2));
            ushort nameOff = BinaryPrimitives.ReadUInt16BigEndian(archiveBuffer.AsSpan(no + 0x06, 2));
            string name = ReadCString(archiveBuffer, absString + nameOff);
            if (name is "." or "..")
                continue;

            bool isDir = id == 0xFFFF || (type & 0xFF00) == 0x0200 || (type & 0x00FF) == 0x02;
            if ((type & 0xFF00) == 0x1100 || (type & 0x00FF) == 0x11)
                isDir = false;
            if (isDir)
                continue;

            if (!map.TryGetValue(name, out var candidates) || candidates == null || candidates.Count == 0)
                continue;

            uint oldSize = BinaryPrimitives.ReadUInt32BigEndian(archiveBuffer.AsSpan(no + 0x0C, 4));
            uint dataOff = BinaryPrimitives.ReadUInt32BigEndian(archiveBuffer.AsSpan(no + 0x08, 4));
            int oldDataAbs = absFileData + (int)dataOff;
            if (oldDataAbs < 0 || oldSize > int.MaxValue ||
                oldDataAbs + (int)oldSize > archiveBuffer.Length)
            {
                continue;
            }

            byte[]? replacement = null;
            foreach (var candidate in candidates)
            {
                if (candidate == null)
                    continue;
                if (acceptReplacement != null &&
                    !acceptReplacement(name, archiveBuffer, oldDataAbs, (int)oldSize, candidate))
                {
                    continue;
                }

                replacement = candidate;
                break;
            }

            if (replacement == null)
            {
                rejectedNames?.Add(name);
                continue;
            }

            patches.Add((no, oldDataAbs, oldSize, replacement));
            replacedNames.Add(name);
            if (replacement.Length != (int)oldSize)
            {
                int aligned = (replacement.Length + 0x1F) & ~0x1F;
                appendBytes += aligned;
            }
        }

        if (patches.Count == 0)
            throw new InvalidDataException("No matching basenames found in retail RARC.");

        // Extra padding budget: up to 0x1F before each appended file + final align.
        int padBudget = patches.Count * 0x20 + 0x20;
        var output = new byte[retailRarc.Length + appendBytes + padBudget];
        retailRarc.CopyTo(output);
        int appendAt = retailRarc.Length;

        foreach (var (nodeOffset, oldDataAbs, oldSize, newData) in patches)
        {
            if (newData.Length == (int)oldSize)
            {
                newData.CopyTo(output.AsSpan(oldDataAbs, newData.Length));
                continue;
            }

            while (((appendAt - absFileData) & 0x1F) != 0)
                output[appendAt++] = 0;

            int newRel = appendAt - absFileData;
            newData.CopyTo(output.AsSpan(appendAt, newData.Length));
            appendAt += newData.Length;

            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(nodeOffset + 0x08, 4), (uint)newRel);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(nodeOffset + 0x0C, 4),
                (uint)newData.Length);
        }

        while (((appendAt - absFileData) & 0x1F) != 0)
            output[appendAt++] = 0;

        // Update archive total size + file-data length fields.
        int fileDataLen = appendAt - absFileData;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x04, 4), (uint)appendAt);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x10, 4), (uint)fileDataLen);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x14, 4), (uint)fileDataLen);

        if (appendAt != output.Length)
            Array.Resize(ref output, appendAt);

        return output;
    }

    public byte[] Save()
    {
        var dirList = new List<RarcDirectory>();
        var flatFiles = new List<RarcFileEntry>();
        CollectTree(Root, dirList, flatFiles);

        var stringTable = new MemoryStream();
        var stringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        int Intern(string s)
        {
            if (stringOffsets.TryGetValue(s, out var existing))
                return existing;
            int off = (int)stringTable.Position;
            var bytes = Encoding.ASCII.GetBytes(s);
            stringTable.Write(bytes, 0, bytes.Length);
            stringTable.WriteByte(0);
            stringOffsets[s] = off;
            return off;
        }

        Intern(".");
        Intern("..");
        foreach (var d in dirList)
            Intern(d.Name);
        foreach (var f in flatFiles)
            Intern(f.Name);

        while (stringTable.Length % 0x20 != 0)
            stringTable.WriteByte(0);

        var fileBlob = new MemoryStream();
        var fileDataOffsets = new Dictionary<RarcFileEntry, int>();
        foreach (var f in flatFiles)
        {
            Align(fileBlob, 0x20);
            fileDataOffsets[f] = (int)fileBlob.Length;
            fileBlob.Write(f.Data, 0, f.Data.Length);
        }

        Align(fileBlob, 0x20);

        // Match retail SMS RARC layout: children first, then "." / ".." at the end.
        // Root directory fourcc is "ROOT" (not the folder name). File IDs are global.
        // Prefer original FileIds from Open() so inject-via-Save keeps retail BAS/BCK
        // IDs stable; only newly added files (BTK inject) get fresh IDs.
        var nodeList = new List<NodeWrite>();
        var dirFirstNode = new int[dirList.Count];
        var dirNodeCount = new int[dirList.Count];
        var usedIds = new HashSet<ushort>();
        ushort maxPreservedId = 0;
        foreach (var existing in flatFiles)
        {
            if (!existing.FileId.HasValue)
                continue;
            usedIds.Add(existing.FileId.Value);
            if (existing.FileId.Value > maxPreservedId)
                maxPreservedId = existing.FileId.Value;
        }

        ushort nextFileId = usedIds.Count > 0 ? (ushort)(maxPreservedId + 1) : (ushort)0;
        ushort AllocFileId(RarcFileEntry file)
        {
            if (file.FileId.HasValue && usedIds.Contains(file.FileId.Value))
            {
                // Already reserved during the scan; reclaim for this node.
                return file.FileId.Value;
            }

            if (file.FileId.HasValue && !usedIds.Contains(file.FileId.Value))
            {
                usedIds.Add(file.FileId.Value);
                return file.FileId.Value;
            }

            while (usedIds.Contains(nextFileId) && nextFileId < 0xFFFE)
                nextFileId++;
            var id = nextFileId;
            if (nextFileId < 0xFFFE)
                nextFileId++;
            usedIds.Add(id);
            file.FileId = id;
            return id;
        }

        for (int di = 0; di < dirList.Count; di++)
        {
            dirFirstNode[di] = nodeList.Count;
            var dir = dirList[di];
            int parentIndex = di == 0 ? -1 : FindParentIndex(dirList, dir);

            foreach (var child in dir.Directories)
            {
                int childIndex = dirList.IndexOf(child);
                nodeList.Add(new NodeWrite
                {
                    Id = 0xFFFF,
                    Type = 0x02,
                    Name = child.Name,
                    DirIndex = (uint)childIndex,
                });
            }

            foreach (var file in dir.Files)
            {
                nodeList.Add(new NodeWrite
                {
                    Id = AllocFileId(file),
                    Type = 0x11,
                    Name = file.Name,
                    File = file,
                });
            }

            nodeList.Add(new NodeWrite { Id = 0xFFFF, Type = 0x02, Name = ".", DirIndex = (uint)di });
            nodeList.Add(new NodeWrite
            {
                Id = 0xFFFF,
                Type = 0x02,
                Name = "..",
                DirIndex = parentIndex < 0 ? 0xFFFFFFFFu : (uint)parentIndex,
            });

            dirNodeCount[di] = nodeList.Count - dirFirstNode[di];
        }

        int dirCount = dirList.Count;
        int nodeCount = nodeList.Count;
        int stringSize = (int)stringTable.Length;
        int fileDataSize = (int)fileBlob.Length;

        const int dataHeaderSize = 0x20;
        int relDir = dataHeaderSize;
        int relNode = relDir + dirCount * 0x10;
        int relString = relNode + nodeCount * 0x14;
        while (relString % 0x20 != 0)
            relString++;
        int relFileData = relString + stringSize;
        while (relFileData % 0x20 != 0)
            relFileData++;

        int total = 0x20 + relFileData + fileDataSize;
        var output = new byte[total];

        output[0] = (byte)'R';
        output[1] = (byte)'A';
        output[2] = (byte)'R';
        output[3] = (byte)'C';
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x04, 4), (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x08, 4), 0x20);
        // 0x0C is relative to the data header (0x20), not an absolute offset.
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x0C, 4), (uint)relFileData);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x10, 4), (uint)fileDataSize);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x14, 4), (uint)fileDataSize);

        int hb = 0x20;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(hb + 0x00, 4), (uint)dirCount);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(hb + 0x04, 4), (uint)relDir);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(hb + 0x08, 4), (uint)nodeCount);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(hb + 0x0C, 4), (uint)relNode);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(hb + 0x10, 4), (uint)stringSize);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(hb + 0x14, 4), (uint)relString);
        // nextFreeFileID — retail mario.arc stores total node count here.
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(hb + 0x18, 2), (ushort)nodeCount);

        for (int di = 0; di < dirCount; di++)
        {
            int o = hb + relDir + di * 0x10;
            if (di == 0)
            {
                output[o] = (byte)'R';
                output[o + 1] = (byte)'O';
                output[o + 2] = (byte)'O';
                output[o + 3] = (byte)'T';
            }
            else
            {
                WriteFourCC(output.AsSpan(o, 4), dirList[di].Name);
            }

            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 0x04, 4),
                (uint)stringOffsets[dirList[di].Name]);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o + 0x08, 2),
                HashName(dirList[di].Name));
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o + 0x0A, 2),
                (ushort)dirNodeCount[di]);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 0x0C, 4),
                (uint)dirFirstNode[di]);
        }

        for (int ni = 0; ni < nodeCount; ni++)
        {
            var n = nodeList[ni];
            int o = hb + relNode + ni * 0x14;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o + 0x00, 2), n.Id);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o + 0x02, 2), HashName(n.Name));
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o + 0x04, 2), (ushort)(n.Type << 8));
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o + 0x06, 2),
                (ushort)stringOffsets[n.Name]);
            if (n.File != null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 0x08, 4),
                    (uint)fileDataOffsets[n.File]);
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 0x0C, 4),
                    (uint)n.File.Data.Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 0x08, 4), n.DirIndex);
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 0x0C, 4), 0x10);
            }
        }

        stringTable.ToArray().CopyTo(output, hb + relString);
        fileBlob.ToArray().CopyTo(output, hb + relFileData);
        return output;
    }

    private static void CollectTree(RarcDirectory dir, List<RarcDirectory> dirs, List<RarcFileEntry> files)
    {
        dirs.Add(dir);
        foreach (var f in dir.Files)
            files.Add(f);
        foreach (var child in dir.Directories)
            CollectTree(child, dirs, files);
    }

    private static int FindParentIndex(List<RarcDirectory> dirs, RarcDirectory child)
    {
        for (int i = 0; i < dirs.Count; i++)
        {
            if (dirs[i].Directories.Contains(child))
                return i;
        }

        return 0;
    }

    private static RarcDirectory BuildDirectory(int dirIndex, DirRec[] dirs, NodeRec[] nodes,
        ReadOnlySpan<byte> data, int absFileData)
    {
        var dir = dirs[dirIndex];
        var result = new RarcDirectory { Name = dir.Name };
        for (uint i = 0; i < dir.EntryCount; i++)
        {
            var entry = nodes[dir.FirstNode + i];
            if (entry.Name is "." or "..")
                continue;

            bool isDir = entry.Id == 0xFFFF || (entry.Type & 0x0200) != 0 || (entry.Type & 0x02) != 0;
            // Type field is often stored as 0x02xx for dirs and 0x11xx for files.
            if (entry.Id == 0xFFFF)
                isDir = true;
            else if ((entry.Type & 0xFF00) == 0x1100 || (entry.Type & 0x00FF) == 0x11)
                isDir = false;
            else if ((entry.Type & 0xFF00) == 0x0200 || (entry.Type & 0x00FF) == 0x02)
                isDir = true;

            if (isDir)
            {
                int child = (int)entry.DataOffset;
                if (child >= 0 && child < dirs.Length)
                    result.Directories.Add(BuildDirectory(child, dirs, nodes, data, absFileData));
                continue;
            }

            int dataOff = absFileData + (int)entry.DataOffset;
            if (dataOff < 0 || dataOff + (int)entry.DataSize > data.Length)
                throw new InvalidDataException($"RARC file '{entry.Name}' is out of range.");

            result.Files.Add(new RarcFileEntry
            {
                Name = entry.Name,
                FullPath = entry.Name,
                Data = data.Slice(dataOff, (int)entry.DataSize).ToArray(),
                // Keep retail IDs so a later Save() (BTK/custom inject) does not
                // renumber BAS/BCK nodes — SMS animSound resolves by file ID.
                FileId = entry.Id == 0xFFFF ? null : entry.Id,
            });
        }

        return result;
    }

    private static void Align(Stream stream, int align)
    {
        while (stream.Length % align != 0)
            stream.WriteByte(0);
    }

    private static string ReadCString(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
            return string.Empty;
        int end = offset;
        while (end < data.Length && data[end] != 0)
            end++;
        return Encoding.ASCII.GetString(data.Slice(offset, end - offset));
    }

    private static void WriteFourCC(Span<byte> dest, string name)
    {
        dest.Clear();
        var upper = (name ?? ".").ToUpperInvariant();
        var bytes = Encoding.ASCII.GetBytes(upper);
        bytes.AsSpan(0, Math.Min(4, bytes.Length)).CopyTo(dest);
    }

    private static ushort HashName(string name)
    {
        ushort hash = 0;
        foreach (var ch in name)
            hash = (ushort)(hash * 3 + (byte)ch);
        return hash;
    }

    private struct DirRec
    {
        public string Name;
        public ushort EntryCount;
        public uint FirstNode;
    }

    private struct NodeRec
    {
        public ushort Id;
        public ushort Type;
        public string Name;
        public uint DataOffset;
        public uint DataSize;
    }

    private sealed class NodeWrite
    {
        public ushort Id;
        public ushort Type;
        public string Name = "";
        public uint DirIndex;
        public RarcFileEntry? File;
    }
}

public sealed class RarcDirectory
{
    public string Name { get; set; } = ".";
    public List<RarcFileEntry> Files { get; } = new();
    public List<RarcDirectory> Directories { get; } = new();
}

public sealed class RarcFileEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    /// <summary>
    /// Original RARC file ID when loaded from disk. Preserved across
    /// <see cref="RarcArchive.Save"/> so BTK/custom inject does not renumber
    /// retail BAS/BCK entries (ID churn mutes animSound lookups and crashes
    /// Shadow packs on stage entry).
    /// </summary>
    public ushort? FileId { get; set; }
}

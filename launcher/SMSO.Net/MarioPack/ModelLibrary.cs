using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SMSO.Net.MarioPack;

public sealed class ModelLibraryEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PackFileName { get; set; } = "";

    // ComboBox SelectionBoxItem falls back to ToString() with our custom template.
    public override string ToString() =>
        string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

/// <summary>
/// AppData library of imported custom Mario packs (<c>%AppData%\SMSO\CustomModels</c>).
/// Pack files are stored as <c>&lt;DisplayName&gt;.arc</c> (with collision suffix) while the
/// multiplayer / disc id remains the 8-char content hash.
/// </summary>
public static class ModelLibrary
{
    public const string FolderName = "CustomModels";
    public const string LibraryFileName = "library.json";
    public const string PackExtension = ".arc";
    public const string SzsExtension = ".szs";

    // Pack files are read repeatedly by launch-time sync, remote-roster ensure,
    // validation, and disc patching. Keep a small immutable byte cache so those
    // paths share the same OS read instead of allocating another ~2 MiB buffer
    // per caller. File identity is checked on every lookup, so imports/seed
    // updates become visible without an explicit cache flush.
    private const long PackByteCacheLimit = 32L * 1024 * 1024;
    private static readonly object PackByteCacheLock = new();
    private static readonly Dictionary<string, PackByteCacheEntry> PackByteCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static long s_packByteCacheSize;
    private static long s_packByteCacheSequence;

    private sealed class PackByteCacheEntry
    {
        public required long Length { get; init; }
        public required long LastWriteUtcTicks { get; init; }
        public required byte[] Bytes { get; init; }
        public long LastUseSequence { get; set; }
    }

    /// <summary>Optional redirect for tests / tools. Null uses %AppData%\SMSO\CustomModels.</summary>
    private static string? s_libraryDirectoryOverride;
    public static string? LibraryDirectoryOverride
    {
        get => s_libraryDirectoryOverride;
        set
        {
            if (string.Equals(s_libraryDirectoryOverride, value, StringComparison.OrdinalIgnoreCase))
                return;
            s_libraryDirectoryOverride = value;
            ClearPackByteCache();
        }
    }

    public static string LibraryDirectory =>
        LibraryDirectoryOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMSO",
            FolderName);

    public static string LibraryJsonPath => Path.Combine(LibraryDirectory, LibraryFileName);

    public static ModelLibraryEntry RetailEntry { get; } = new()
    {
        Id = "",
        DisplayName = "Mario",
        PackFileName = "",
    };

    public static void EnsureLibraryDirectory() => Directory.CreateDirectory(LibraryDirectory);

    // Preferred dropdown order (case-insensitive display names). Retail is always
    // first when included. Models not listed here sort alphabetically after these
    // — keep new imports out of this list so they land at the bottom unless a
    // specific position is requested.
    private static readonly string[] PreferredDisplayOrder =
    {
        "Daytendo",
        "Nightendo",
        "Luigi",
        "Nokissia",
        "Piantissimo",
        "Shadow Mario",
        "Shadow Luigi",
        "Sonic",
        "Shadow",
        "Birdo",
        "Yoshi",
    };

    private static int DisplaySortKey(string displayName)
    {
        for (int i = 0; i < PreferredDisplayOrder.Length; i++)
        {
            if (string.Equals(PreferredDisplayOrder[i], displayName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return PreferredDisplayOrder.Length;
    }

    public static IReadOnlyList<ModelLibraryEntry> ListEntries(bool includeRetail = true)
    {
        var list = new List<ModelLibraryEntry>();
        if (includeRetail)
            list.Add(RetailEntry);

        EnsureLibraryDirectory();
        var map = LoadMap();
        var dirty = false;
        DiscoverOrphanPacks(map, ref dirty);
        var customs = new List<ModelLibraryEntry>();
        foreach (var kvp in map.ToList())
        {
            var id = CharacterPack.NormalizeModelId(kvp.Key);
            if (id.Length == 0)
            {
                map.Remove(kvp.Key);
                dirty = true;
                continue;
            }

            var display = ResolveEntryDisplayName(id, kvp.Value);
            if (!string.Equals(map[kvp.Key], display, StringComparison.Ordinal))
            {
                map[kvp.Key] = display;
                dirty = true;
            }

            if (TryMigratePackFilesToDisplayName(id, display))
                dirty = true;

            var packPath = GetPackPath(id, map);
            if (!File.Exists(packPath))
            {
                map.Remove(kvp.Key);
                dirty = true;
                continue;
            }

            customs.Add(new ModelLibraryEntry
            {
                Id = id,
                DisplayName = display,
                PackFileName = Path.GetFileName(packPath),
            });
        }

        if (dirty)
            SaveMap(map);

        customs.Sort((a, b) =>
        {
            var ka = DisplaySortKey(a.DisplayName);
            var kb = DisplaySortKey(b.DisplayName);
            if (ka != kb)
                return ka.CompareTo(kb);
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        list.AddRange(customs);

        return list;
    }

    private static string ResolveEntryDisplayName(string modelId, string? mappedName)
    {
        // Prefer the library.json label as-is (already curated). Only fall back to
        // filename cleanup when the map entry is missing.
        if (!string.IsNullOrWhiteSpace(mappedName))
            return mappedName.Trim();

        var szsCopy = GetSzsPath(modelId);
        if (File.Exists(szsCopy))
            return CharacterPack.DisplayNameFromFileName(szsCopy);

        var packPath = GetPackPath(modelId);
        if (File.Exists(packPath))
        {
            var stem = Path.GetFileNameWithoutExtension(packPath);
            if (!IsLegacyHexFileStem(stem, modelId))
                return CharacterPack.DisplayNameFromFileName(packPath);
        }

        return modelId;
    }

    public static void SetDisplayName(string modelId, string displayName)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0 || string.IsNullOrWhiteSpace(displayName))
            return;
        var map = LoadMap();
        var name = displayName.Trim();
        map[id] = name;
        TryMigratePackFilesToDisplayName(id, name, map);
        SaveMap(map);
    }

    public static string GetPackPath(string modelId) => GetPackPath(modelId, LoadMap());

    public static string GetSzsPath(string modelId) => GetSzsPath(modelId, LoadMap());

    private static string GetPackPath(string modelId, Dictionary<string, string> map)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        map.TryGetValue(id, out var display);
        return ResolveLibraryFilePath(id, display, PackExtension, map);
    }

    private static string GetSzsPath(string modelId, Dictionary<string, string> map)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        map.TryGetValue(id, out var display);
        return ResolveLibraryFilePath(id, display, SzsExtension, map);
    }

    /// <summary>
    /// Turns a display name into a Windows-safe file stem ("Shadow Luigi" → "Shadow Luigi").
    /// </summary>
    public static string SanitizeFileStem(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(displayName.Length);
        foreach (var ch in displayName.Trim())
        {
            if (Array.IndexOf(invalid, ch) >= 0 || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                sb.Append(' ');
            else
                sb.Append(ch);
        }

        var cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        // Avoid reserved device names and empty stems.
        if (cleaned.Length == 0 ||
            cleaned.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return cleaned;
    }

    /// <summary>
    /// Preferred AppData file stem for a pack. Uses the display name; on collision with
    /// another id, appends <c>-{id}</c>.
    /// </summary>
    public static string ChooseFileStem(string modelId, string? displayName,
        Dictionary<string, string>? map = null)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        var stem = SanitizeFileStem(displayName);
        if (stem.Length == 0)
            return id;

        map ??= LoadMap();
        var candidatePath = Path.Combine(LibraryDirectory, stem + PackExtension);
        if (!File.Exists(candidatePath))
            return stem;

        var owner = FindModelIdForLibraryFile(candidatePath, map);
        if (owner is null || string.Equals(owner, id, StringComparison.OrdinalIgnoreCase))
            return stem;

        return $"{stem}-{id}";
    }

    private static string ResolveLibraryFilePath(
        string id,
        string? displayName,
        string extension,
        Dictionary<string, string> map)
    {
        EnsureLibraryDirectory();
        var preferredStem = ChooseFileStem(id, displayName, map);
        var preferred = Path.Combine(LibraryDirectory, preferredStem + extension);
        if (File.Exists(preferred))
            return preferred;

        var legacy = Path.Combine(LibraryDirectory, id + extension);
        if (File.Exists(legacy))
            return legacy;

        // Named collision form from an earlier migrate.
        var suffixed = Path.Combine(LibraryDirectory, $"{SanitizeFileStem(displayName)}-{id}{extension}");
        if (!string.IsNullOrEmpty(SanitizeFileStem(displayName)) && File.Exists(suffixed))
            return suffixed;

        return preferred;
    }

    private static bool TryMigratePackFilesToDisplayName(string modelId, string displayName,
        Dictionary<string, string>? map = null)
    {
        map ??= LoadMap();
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0)
            return false;

        var stem = ChooseFileStem(id, displayName, map);
        if (IsLegacyHexFileStem(stem, id))
            return false;

        var moved = false;
        moved |= TryMoveLibraryFile(id + PackExtension, stem + PackExtension);
        moved |= TryMoveLibraryFile(id + SzsExtension, stem + SzsExtension);

        // Collapse collision-suffixed names ("Name-<id>") back to the clean
        // display stem once the conflicting pack is gone.
        var sanitized = SanitizeFileStem(displayName);
        if (!string.IsNullOrEmpty(sanitized) &&
            !string.Equals(stem, $"{sanitized}-{id}", StringComparison.OrdinalIgnoreCase))
        {
            moved |= TryMoveLibraryFile($"{sanitized}-{id}{PackExtension}", stem + PackExtension);
            moved |= TryMoveLibraryFile($"{sanitized}-{id}{SzsExtension}", stem + SzsExtension);
        }

        return moved;
    }

    private static bool TryMoveLibraryFile(string fromName, string toName)
    {
        if (string.Equals(fromName, toName, StringComparison.OrdinalIgnoreCase))
            return false;

        var from = Path.Combine(LibraryDirectory, fromName);
        var to = Path.Combine(LibraryDirectory, toName);
        if (!File.Exists(from) || File.Exists(to))
            return false;

        File.Move(from, to);
        return true;
    }

    private static bool IsLegacyHexFileStem(string stem, string modelId) =>
        string.Equals(stem, modelId, StringComparison.OrdinalIgnoreCase) &&
        CharacterPack.NormalizeModelId(stem).Length == CharacterPack.ModelIdLength;

    private static string? FindModelIdForLibraryFile(string path, Dictionary<string, string> map)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var asId = CharacterPack.NormalizeModelId(stem);
        if (asId.Length == CharacterPack.ModelIdLength)
            return asId;

        // Collision form: "Shadow Luigi-cc27492b"
        var dash = stem.LastIndexOf('-');
        if (dash > 0 && dash + 1 + CharacterPack.ModelIdLength == stem.Length)
        {
            var suffix = CharacterPack.NormalizeModelId(stem[(dash + 1)..]);
            if (suffix.Length == CharacterPack.ModelIdLength)
                return suffix;
        }

        foreach (var kvp in map)
        {
            var id = CharacterPack.NormalizeModelId(kvp.Key);
            if (id.Length == 0)
                continue;
            if (string.Equals(SanitizeFileStem(kvp.Value), stem, StringComparison.OrdinalIgnoreCase))
                return id;
            if (string.Equals($"{SanitizeFileStem(kvp.Value)}-{id}", stem, StringComparison.OrdinalIgnoreCase))
                return id;
        }

        return null;
    }

    private static void DiscoverOrphanPacks(Dictionary<string, string> map, ref bool dirty)
    {
        foreach (var pack in Directory.EnumerateFiles(LibraryDirectory, "*" + PackExtension))
        {
            var id = FindModelIdForLibraryFile(pack, map);
            if (id is null || id.Length == 0)
                continue;
            if (map.ContainsKey(id) && !string.IsNullOrWhiteSpace(map[id]))
                continue;

            var stem = Path.GetFileNameWithoutExtension(pack);
            var label = IsLegacyHexFileStem(stem, id)
                ? id
                : CharacterPack.DisplayNameFromFileName(pack);
            // Strip trailing -id from collision stems for the display label.
            var dash = label.LastIndexOf('-');
            if (dash > 0 &&
                CharacterPack.NormalizeModelId(label[(dash + 1)..]).Length == CharacterPack.ModelIdLength)
            {
                label = label[..dash].Trim();
            }

            map[id] = string.IsNullOrWhiteSpace(label) ? id : label;
            dirty = true;
        }
    }

    public static ModelLibraryEntry ImportSzs(string sourceSzsPath, byte[] retailArchiveBytes,
        string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(sourceSzsPath) || !File.Exists(sourceSzsPath))
            throw new FileNotFoundException("Custom SZS not found.", sourceSzsPath);

        var name = string.IsNullOrWhiteSpace(displayName)
            ? CharacterPack.DisplayNameFromFileName(sourceSzsPath)
            : displayName.Trim();

        var customBytes = File.ReadAllBytes(sourceSzsPath);
        var merge = CharacterPack.BuildMergedPack(retailArchiveBytes, customBytes,
            replaceMatchingBcks: CharacterPack.AllowsBckReplacement(name),
            injectBodyAngleFreePrm: CharacterPack.AllowsBodyAngleFreeReplacement(name));
        if (!CharacterPack.TryValidatePackForInit(merge.PackArc, out var unsafeReason))
        {
            throw new InvalidDataException(
                "Imported pack is unsafe for multiplayer (would crash remotes): " + unsafeReason);
        }

        EnsureLibraryDirectory();

        var map = LoadMap();
        map[merge.ModelId] = name;
        SaveMap(map);

        var packPath = GetPackPath(merge.ModelId, map);
        // Drop legacy hex-named copies so we don't keep duplicates after rename.
        var legacyPack = Path.Combine(LibraryDirectory, merge.ModelId + PackExtension);
        if (!string.Equals(Path.GetFullPath(packPath), Path.GetFullPath(legacyPack),
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(legacyPack))
        {
            File.Delete(legacyPack);
        }

        WriteAllBytesAtomically(packPath, merge.PackArc);

        var srcCopy = GetSzsPath(merge.ModelId, map);
        var legacySzs = Path.Combine(LibraryDirectory, merge.ModelId + SzsExtension);
        if (!string.Equals(Path.GetFullPath(srcCopy), Path.GetFullPath(legacySzs),
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(legacySzs))
        {
            File.Delete(legacySzs);
        }

        var srcFull = Path.GetFullPath(sourceSzsPath);
        var destFull = Path.GetFullPath(srcCopy);
        if (!string.Equals(srcFull, destFull, StringComparison.OrdinalIgnoreCase))
            CopyFileAtomically(sourceSzsPath, srcCopy);

        return new ModelLibraryEntry
        {
            Id = merge.ModelId,
            DisplayName = name,
            PackFileName = Path.GetFileName(packPath),
        };
    }

    /// <summary>
    /// Re-merge a library entry from its stored SZS copy, keeping the
    /// existing display name. Used when pack-merge rules change (e.g. better_sms.prm).
    /// </summary>
    public static ModelLibraryEntry? ReimportStoredSzs(string modelId, byte[] retailArchiveBytes)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0)
            return null;

        var srcCopy = GetSzsPath(id);
        if (!File.Exists(srcCopy))
            return null;

        var map = LoadMap();
        map.TryGetValue(id, out var existingName);
        return ImportSzs(srcCopy, retailArchiveBytes, existingName);
    }

    /// <summary>
    /// Re-merge using stored SZS but overwrite the pack at <paramref name="modelId"/>
    /// even when merge rules change the content hash. Keeps multiplayer ids stable.
    /// </summary>
    public static ModelLibraryEntry? ReimportStoredSzsPreserveId(string modelId,
        byte[] retailArchiveBytes)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0)
            return null;

        var srcCopy = GetSzsPath(id);
        if (!File.Exists(srcCopy))
            return null;

        EnsureLibraryDirectory();

        var map = LoadMap();
        map.TryGetValue(id, out var existingName);
        var name = string.IsNullOrWhiteSpace(existingName)
            ? CharacterPack.DisplayNameFromFileName(srcCopy)
            : existingName.Trim();

        var merge = CharacterPack.BuildMergedPack(retailArchiveBytes, File.ReadAllBytes(srcCopy),
            replaceMatchingBcks: CharacterPack.AllowsBckReplacement(name),
            injectBodyAngleFreePrm: CharacterPack.AllowsBodyAngleFreeReplacement(name));
        if (!CharacterPack.TryValidatePackForInit(merge.PackArc, out var unsafeReason))
        {
            throw new InvalidDataException(
                "Reimported pack is unsafe for multiplayer (would crash remotes): " + unsafeReason);
        }

        map[id] = name;
        SaveMap(map);

        var packPath = GetPackPath(id, map);
        WriteAllBytesAtomically(packPath, merge.PackArc);

        return new ModelLibraryEntry
        {
            Id = id,
            DisplayName = name,
            PackFileName = Path.GetFileName(packPath),
        };
    }

    public static bool TryGetPackBytes(string modelId, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0)
            return false;
        var path = GetPackPath(id);
        if (!File.Exists(path))
            return false;

        // A seed/import may atomically replace the file between the first stat
        // and read. Retry once when its identity changes so cache entries always
        // describe a complete version, never a mixed/partial handoff.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            FileInfo before;
            try
            {
                before = new FileInfo(path);
                if (!before.Exists)
                    return false;
            }
            catch
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            lock (PackByteCacheLock)
            {
                if (PackByteCache.TryGetValue(fullPath, out var cached) &&
                    cached.Length == before.Length &&
                    cached.LastWriteUtcTicks == before.LastWriteTimeUtc.Ticks)
                {
                    cached.LastUseSequence = ++s_packByteCacheSequence;
                    bytes = cached.Bytes;
                    return true;
                }
            }

            byte[] loaded;
            try
            {
                // Readers share with launcher seed/import atomic replacements.
                using var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                loaded = new byte[stream.Length];
                stream.ReadExactly(loaded);
            }
            catch
            {
                return false;
            }

            var after = new FileInfo(fullPath);
            if (!after.Exists)
                return false;
            if (before.Length != after.Length ||
                before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks)
            {
                continue;
            }

            lock (PackByteCacheLock)
            {
                // Another caller may have won the same read while ours was in
                // flight. Reuse its buffer so validation caching can deduplicate too.
                if (PackByteCache.TryGetValue(fullPath, out var winner) &&
                    winner.Length == after.Length &&
                    winner.LastWriteUtcTicks == after.LastWriteTimeUtc.Ticks)
                {
                    winner.LastUseSequence = ++s_packByteCacheSequence;
                    bytes = winner.Bytes;
                    return true;
                }

                if (PackByteCache.Remove(fullPath, out var stale))
                    s_packByteCacheSize -= stale.Bytes.LongLength;
                PackByteCache[fullPath] = new PackByteCacheEntry
                {
                    Length = after.Length,
                    LastWriteUtcTicks = after.LastWriteTimeUtc.Ticks,
                    Bytes = loaded,
                    LastUseSequence = ++s_packByteCacheSequence,
                };
                s_packByteCacheSize += loaded.LongLength;
                TrimPackByteCache_NoLock(fullPath);
                bytes = loaded;
                return true;
            }
        }

        return false;
    }

    private static void TrimPackByteCache_NoLock(string keepPath)
    {
        while (s_packByteCacheSize > PackByteCacheLimit && PackByteCache.Count > 1)
        {
            string? oldestPath = null;
            long oldestSequence = long.MaxValue;
            foreach (var kvp in PackByteCache)
            {
                if (string.Equals(kvp.Key, keepPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kvp.Value.LastUseSequence >= oldestSequence)
                    continue;
                oldestSequence = kvp.Value.LastUseSequence;
                oldestPath = kvp.Key;
            }

            if (oldestPath == null || !PackByteCache.Remove(oldestPath, out var removed))
                break;
            s_packByteCacheSize -= removed.Bytes.LongLength;
        }
    }

    private static void ClearPackByteCache()
    {
        lock (PackByteCacheLock)
        {
            PackByteCache.Clear();
            s_packByteCacheSize = 0;
            s_packByteCacheSequence = 0;
        }
    }

    private static void InvalidatePackByteCache(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        lock (PackByteCacheLock)
        {
            if (!PackByteCache.Remove(fullPath, out var removed))
                return;
            s_packByteCacheSize -= removed.Bytes.LongLength;
        }
    }

    public static string ResolveDisplayName(string? modelId)
    {
        if (CharacterPack.IsRetailModelId(modelId))
            return RetailEntry.DisplayName;
        var id = CharacterPack.NormalizeModelId(modelId);
        var map = LoadMap();
        if (map.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
            return name.Trim();
        return id;
    }

    /// <summary>
    /// Candidate folders that may ship bundled packs next to the launcher
    /// (release zip / publish output). First existing folder wins.
    /// </summary>
    public static IEnumerable<string> EnumerateBundledModelDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty,
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var dir = Path.GetFullPath(Path.Combine(root, FolderName));
            if (!seen.Add(dir) || !Directory.Exists(dir))
                continue;
            yield return dir;
        }
    }

    /// <summary>
    /// Copy release-bundled <c>CustomModels/</c> packs into the AppData library.
    /// Zip updates always overwrite matching pack / SZS files and library labels
    /// so testers get the same content as the release. User-imported packs whose
    /// ids are not in the bundle are left alone; stale entries that reuse a
    /// bundled display name under an older id are removed.
    /// </summary>
    /// <returns>Number of pack/SZS files written or updated in AppData.</returns>
    public static int SeedBundledModels(Action<string>? log = null) =>
        SeedBundledModelsFrom(EnumerateBundledModelDirectories().FirstOrDefault(), log);

    /// <summary>Testable seed entry point with an explicit bundled directory.</summary>
    public static int SeedBundledModelsFrom(string? bundledDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(bundledDir) || !Directory.Exists(bundledDir))
            return 0;

        EnsureLibraryDirectory();
        var map = LoadMap();
        var dirty = false;
        var updated = 0;

        Dictionary<string, string>? bundledMap = null;
        var bundledMapPath = Path.Combine(bundledDir, LibraryFileName);
        if (File.Exists(bundledMapPath))
        {
            try
            {
                bundledMap = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(bundledMapPath));
            }
            catch
            {
                // Ignore corrupt bundled library.json; packs below still seed.
            }
        }

        if (bundledMap != null)
        {
            foreach (var kvp in bundledMap)
            {
                var id = CharacterPack.NormalizeModelId(kvp.Key);
                var name = kvp.Value?.Trim() ?? string.Empty;
                if (id.Length == 0 || name.Length == 0)
                    continue;

                // Drop other ids that claim the same display name (stale reimports
                // after a pack content-hash / id change in a newer zip).
                foreach (var otherKey in map.Keys.ToList())
                {
                    var otherId = CharacterPack.NormalizeModelId(otherKey);
                    if (otherId.Length == 0 ||
                        string.Equals(otherId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(map[otherKey]?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DeleteLibraryPackFiles(otherId, map);
                    map.Remove(otherKey);
                    dirty = true;
                }

                if (!map.TryGetValue(id, out var existing) ||
                    !string.Equals(existing, name, StringComparison.Ordinal))
                {
                    map[id] = name;
                    dirty = true;
                }
            }
        }

        foreach (var pack in Directory.EnumerateFiles(bundledDir, "*" + PackExtension))
        {
            var stem = Path.GetFileNameWithoutExtension(pack);
            var id = CharacterPack.NormalizeModelId(stem);
            if (id.Length == 0)
            {
                // Bundled already uses a display-name file — resolve via map / bundle.
                id = FindModelIdForLibraryFile(pack, map) ?? string.Empty;
                if (id.Length == 0 && bundledMap != null)
                {
                    foreach (var kvp in bundledMap)
                    {
                        var label = kvp.Value?.Trim() ?? string.Empty;
                        if (string.Equals(SanitizeFileStem(label), stem, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(label, stem, StringComparison.OrdinalIgnoreCase))
                        {
                            id = CharacterPack.NormalizeModelId(kvp.Key);
                            break;
                        }
                    }
                }

                if (id.Length == 0)
                    continue;
            }

            var display = map.TryGetValue(id, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                ? mapped.Trim()
                : (id.Length == CharacterPack.ModelIdLength &&
                   string.Equals(stem, id, StringComparison.OrdinalIgnoreCase)
                    ? id
                    : CharacterPack.DisplayNameFromFileName(pack));
            if (!map.TryGetValue(id, out var currentLabel) ||
                string.IsNullOrWhiteSpace(currentLabel) ||
                !string.Equals(currentLabel, display, StringComparison.Ordinal))
            {
                map[id] = display;
                dirty = true;
            }

            var preferredStem = ChooseFileStem(id, map[id], map);
            var destPack = Path.Combine(LibraryDirectory, preferredStem + PackExtension);
            if (CopyLibraryFileOverwriting(pack, destPack))
            {
                updated++;
                dirty = true;
            }

            DeleteAlternateLibraryPaths(id, map[id], PackExtension, destPack);

            var destSzs = Path.Combine(LibraryDirectory, preferredStem + SzsExtension);
            var srcSzsNamed = Path.Combine(bundledDir, stem + SzsExtension);
            var srcSzsHex = Path.Combine(bundledDir, id + SzsExtension);
            foreach (var srcSzs in new[] { srcSzsNamed, srcSzsHex })
            {
                if (!File.Exists(srcSzs))
                    continue;
                if (CopyLibraryFileOverwriting(srcSzs, destSzs))
                {
                    updated++;
                    dirty = true;
                }

                DeleteAlternateLibraryPaths(id, map[id], SzsExtension, destSzs);
                break;
            }

            if (TryMigratePackFilesToDisplayName(id, map[id], map))
                dirty = true;

            // Zip seed copies prebuilt .arcs; stamp BodyAngleFree.prm for tall
            // packs so an older/missing PRM in the bundle cannot stick around.
            var stampPath = GetPackPath(id, map);
            if (CharacterPack.AllowsBodyAngleFreeReplacement(map[id]) &&
                CharacterPack.EnsureBodyAngleFreePrmInPackFile(stampPath))
            {
                updated++;
                dirty = true;
            }
        }

        DiscoverOrphanPacks(map, ref dirty);

        if (dirty)
            SaveMap(map);

        if (updated > 0)
            log?.Invoke($"Updated {updated} bundled custom model file(s) into {LibraryDirectory}.");
        return updated;
    }

    private static bool CopyLibraryFileOverwriting(string sourcePath, string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        if (File.Exists(destPath))
        {
            try
            {
                var sourceInfo = new FileInfo(sourcePath);
                var destInfo = new FileInfo(destPath);
                if (sourceInfo.Length == destInfo.Length &&
                    sourceInfo.LastWriteTimeUtc == destInfo.LastWriteTimeUtc)
                    return false;

                if (sourceInfo.Length == destInfo.Length &&
                    FilesEqual(sourcePath, destPath))
                {
                    // Future seeds become an O(1) metadata check.
                    File.SetLastWriteTimeUtc(destPath, sourceInfo.LastWriteTimeUtc);
                    return false;
                }
            }
            catch
            {
                // Fall through and rewrite.
            }
        }

        CopyFileAtomically(sourcePath, destPath);
        return true;
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        const int bufferSize = 128 * 1024;
        using var left = new FileStream(
            leftPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        using var right = new FileStream(
            rightPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        if (left.Length != right.Length)
            return false;

        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        while (true)
        {
            var leftRead = left.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = right.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
                return false;
            if (leftRead == 0)
                return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                return false;
        }
    }

    private static void CopyFileAtomically(string sourcePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("Destination has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
            InvalidatePackByteCache(destinationPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static void WriteAllBytesAtomically(string destinationPath, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("Destination has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 128 * 1024, FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            InvalidatePackByteCache(destinationPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static void DeleteLibraryPackFiles(string modelId, Dictionary<string, string> map)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0)
            return;

        map.TryGetValue(id, out var display);
        foreach (var ext in new[] { PackExtension, SzsExtension })
        {
            foreach (var path in EnumerateLibraryPathsForId(id, display, ext))
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // Best-effort cleanup of stale packs.
                }
            }
        }
    }

    private static void DeleteAlternateLibraryPaths(
        string modelId, string? displayName, string extension, string keepPath)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0)
            return;

        foreach (var path in EnumerateLibraryPathsForId(id, displayName, extension))
        {
            if (string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private static IEnumerable<string> EnumerateLibraryPathsForId(
        string id, string? displayName, string extension)
    {
        yield return Path.Combine(LibraryDirectory, id + extension);
        var sanitized = SanitizeFileStem(displayName);
        if (sanitized.Length > 0)
        {
            yield return Path.Combine(LibraryDirectory, sanitized + extension);
            yield return Path.Combine(LibraryDirectory, $"{sanitized}-{id}{extension}");
        }
    }

    private static Dictionary<string, string> LoadMap()
    {
        try
        {
            if (!File.Exists(LibraryJsonPath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(LibraryJsonPath);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveMap(Dictionary<string, string> map)
    {
        EnsureLibraryDirectory();
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        WriteAllBytesAtomically(LibraryJsonPath, Encoding.UTF8.GetBytes(json));
    }
}

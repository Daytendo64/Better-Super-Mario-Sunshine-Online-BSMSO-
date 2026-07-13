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

    /// <summary>Optional redirect for tests / tools. Null uses %AppData%\SMSO\CustomModels.</summary>
    public static string? LibraryDirectoryOverride { get; set; }

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

        var customBytes = File.ReadAllBytes(sourceSzsPath);
        var merge = CharacterPack.BuildMergedPack(retailArchiveBytes, customBytes);
        if (!CharacterPack.TryValidatePackForInit(merge.PackArc, out var unsafeReason))
        {
            throw new InvalidDataException(
                "Imported pack is unsafe for multiplayer (would crash remotes): " + unsafeReason);
        }

        EnsureLibraryDirectory();

        var name = string.IsNullOrWhiteSpace(displayName)
            ? CharacterPack.DisplayNameFromFileName(sourceSzsPath)
            : displayName.Trim();

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

        File.WriteAllBytes(packPath, merge.PackArc);

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
            File.Copy(sourceSzsPath, srcCopy, overwrite: true);

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

        var merge = CharacterPack.BuildMergedPack(retailArchiveBytes, File.ReadAllBytes(srcCopy));
        if (!CharacterPack.TryValidatePackForInit(merge.PackArc, out var unsafeReason))
        {
            throw new InvalidDataException(
                "Reimported pack is unsafe for multiplayer (would crash remotes): " + unsafeReason);
        }

        EnsureLibraryDirectory();

        var map = LoadMap();
        map.TryGetValue(id, out var existingName);
        var name = string.IsNullOrWhiteSpace(existingName)
            ? CharacterPack.DisplayNameFromFileName(srcCopy)
            : existingName.Trim();
        map[id] = name;
        SaveMap(map);

        var packPath = GetPackPath(id, map);
        File.WriteAllBytes(packPath, merge.PackArc);

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
        bytes = File.ReadAllBytes(path);
        return true;
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
    /// Copy release-bundled <c>CustomModels/</c> packs into the AppData library
    /// so first-run users get the same model list as the packager. Existing
    /// AppData packs are kept; missing ids / library labels are filled in.
    /// </summary>
    /// <returns>Number of pack files newly copied into AppData.</returns>
    public static int SeedBundledModels(Action<string>? log = null)
    {
        var bundledDir = EnumerateBundledModelDirectories().FirstOrDefault();
        if (bundledDir == null)
            return 0;

        EnsureLibraryDirectory();
        var map = LoadMap();
        var dirty = false;
        var copied = 0;

        var bundledMapPath = Path.Combine(bundledDir, LibraryFileName);
        if (File.Exists(bundledMapPath))
        {
            try
            {
                var bundledMap = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(bundledMapPath));
                if (bundledMap != null)
                {
                    foreach (var kvp in bundledMap)
                    {
                        var id = CharacterPack.NormalizeModelId(kvp.Key);
                        if (id.Length == 0 || string.IsNullOrWhiteSpace(kvp.Value))
                            continue;
                        if (map.TryGetValue(id, out var existing) &&
                            string.Equals(existing, kvp.Value.Trim(), StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // Prefer an existing user-edited label; only fill blanks.
                        if (!map.ContainsKey(id) || string.IsNullOrWhiteSpace(map[id]))
                        {
                            map[id] = kvp.Value.Trim();
                            dirty = true;
                        }
                    }
                }
            }
            catch
            {
                // Ignore corrupt bundled library.json; packs below still seed.
            }
        }

        foreach (var pack in Directory.EnumerateFiles(bundledDir, "*" + PackExtension))
        {
            var stem = Path.GetFileNameWithoutExtension(pack);
            var id = CharacterPack.NormalizeModelId(stem);
            if (id.Length == 0)
            {
                // Bundled already uses a display-name file — resolve via bundled map.
                id = FindModelIdForLibraryFile(pack, map) ?? string.Empty;
                if (id.Length == 0)
                    continue;
            }

            if (!map.ContainsKey(id) || string.IsNullOrWhiteSpace(map[id]))
            {
                map[id] = id.Length == CharacterPack.ModelIdLength &&
                          string.Equals(stem, id, StringComparison.OrdinalIgnoreCase)
                    ? id
                    : CharacterPack.DisplayNameFromFileName(pack);
                dirty = true;
            }

            var destPack = GetPackPath(id, map);
            if (!File.Exists(destPack))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPack)!);
                File.Copy(pack, destPack, overwrite: false);
                copied++;
                dirty = true;
            }

            var srcSzsHex = Path.Combine(bundledDir, id + SzsExtension);
            var srcSzsNamed = Path.Combine(bundledDir, Path.GetFileNameWithoutExtension(destPack) + SzsExtension);
            var destSzs = GetSzsPath(id, map);
            foreach (var srcSzs in new[] { srcSzsNamed, srcSzsHex })
            {
                if (File.Exists(srcSzs) && !File.Exists(destSzs))
                {
                    File.Copy(srcSzs, destSzs, overwrite: false);
                    break;
                }
            }

            if (TryMigratePackFilesToDisplayName(id, map[id], map))
                dirty = true;
        }

        DiscoverOrphanPacks(map, ref dirty);

        if (dirty)
            SaveMap(map);

        if (copied > 0)
            log?.Invoke($"Seeded {copied} bundled custom model pack(s) into {LibraryDirectory}.");
        return copied;
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
        File.WriteAllText(LibraryJsonPath, json);
    }
}

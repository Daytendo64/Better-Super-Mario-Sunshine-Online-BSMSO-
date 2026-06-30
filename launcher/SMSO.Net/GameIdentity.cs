using System.Text;

namespace SMSO.Net;

public static class GameIdentity
{
    public const string BsmsGameId = "GMSE90";
    public const string VanillaNtscUGameId = "GMSE01";

    public static bool TryResolveBootBinPath(string gamePath, out string bootBinPath)
    {
        bootBinPath = string.Empty;
        if (string.IsNullOrWhiteSpace(gamePath))
            return false;

        var trimmed = gamePath.Trim().Trim('"');
        if (File.Exists(trimmed))
        {
            var fileName = Path.GetFileName(trimmed);
            if (string.Equals(fileName, "main.dol", StringComparison.OrdinalIgnoreCase))
            {
                bootBinPath = Path.Combine(Path.GetDirectoryName(trimmed) ?? string.Empty, "boot.bin");
                return File.Exists(bootBinPath);
            }

            if (IsDiscImagePath(trimmed))
            {
                bootBinPath = trimmed;
                return true;
            }

            var sysBoot = Path.Combine(trimmed, "sys", "boot.bin");
            if (File.Exists(sysBoot))
            {
                bootBinPath = sysBoot;
                return true;
            }

            return false;
        }

        if (!Directory.Exists(trimmed))
            return false;

        bootBinPath = Path.Combine(trimmed, "sys", "boot.bin");
        return File.Exists(bootBinPath);
    }

    public static bool TryReadGameId(string path, out string gameId)
    {
        gameId = string.Empty;
        if (!File.Exists(path))
            return false;

        if (IsDiscImagePath(path))
            return TryReadDiscImageGameId(path, out gameId);

        return TryReadBootBinGameId(path, out gameId);
    }

    public static bool TryPatchGameId(string path, string targetGameId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(targetGameId) || targetGameId.Length != 6)
        {
            error = "Game ID must be exactly 6 characters.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"File not found: {path}";
            return false;
        }

        try
        {
            if (IsDiscImagePath(path))
                return TryPatchDiscImageGameId(path, targetGameId, out error);

            return TryPatchBootBinGameId(path, targetGameId, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsDiscImagePath(string path) =>
        path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gcm", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gcz", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadBootBinGameId(string bootBinPath, out string gameId)
    {
        gameId = string.Empty;
        using var stream = File.OpenRead(bootBinPath);
        if (stream.Length < 6)
            return false;

        var buffer = new byte[6];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return false;

        gameId = Encoding.ASCII.GetString(buffer);
        return true;
    }

    private static bool TryReadDiscImageGameId(string discPath, out string gameId)
    {
        gameId = string.Empty;
        using var stream = File.OpenRead(discPath);
        if (stream.Length < 6)
            return false;

        var buffer = new byte[6];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return false;

        gameId = Encoding.ASCII.GetString(buffer);
        return true;
    }

    private static bool TryPatchBootBinGameId(string bootBinPath, string targetGameId, out string? error)
    {
        error = null;
        var bytes = File.ReadAllBytes(bootBinPath);
        if (bytes.Length < 6)
        {
            error = "boot.bin is too small to contain a GameCube game ID.";
            return false;
        }

        if (TryReadAsciiGameId(bytes, 0, out var currentId) &&
            string.Equals(currentId, targetGameId, StringComparison.Ordinal))
        {
            return false;
        }

        Encoding.ASCII.GetBytes(targetGameId, 0, 6, bytes, 0);
        File.WriteAllBytes(bootBinPath, bytes);
        return true;
    }

    private static bool TryPatchDiscImageGameId(string discPath, string targetGameId, out string? error)
    {
        error = null;
        using var stream = new FileStream(
            discPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);

        if (stream.Length < 6)
        {
            error = "Disc image is too small to contain a GameCube game ID.";
            return false;
        }

        var current = new byte[6];
        if (stream.Read(current, 0, current.Length) != current.Length)
        {
            error = "Failed to read disc header game ID.";
            return false;
        }

        var currentId = Encoding.ASCII.GetString(current);
        if (string.Equals(currentId, targetGameId, StringComparison.Ordinal))
            return false;

        var next = Encoding.ASCII.GetBytes(targetGameId);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(next, 0, next.Length);
        return true;
    }

    private static bool TryReadAsciiGameId(byte[] buffer, int offset, out string gameId)
    {
        gameId = string.Empty;
        if (buffer.Length < offset + 6)
            return false;

        gameId = Encoding.ASCII.GetString(buffer, offset, 6);
        return true;
    }
}

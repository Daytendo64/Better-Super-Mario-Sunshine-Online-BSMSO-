using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SMSO.Launcher;

/// <summary>
/// An exclusive hold on one launcher instance index. Held for the lifetime of the
/// process; the OS releases the underlying lock file if the launcher crashes.
/// </summary>
internal sealed class InstanceClaim : IDisposable
{
    private FileStream? _lockFile;

    internal InstanceClaim(int index, FileStream? lockFile)
    {
        Index = index;
        _lockFile = lockFile;
    }

    public int Index { get; }

    /// <summary>False when no lock could be taken and the index is a degraded fallback.</summary>
    public bool IsExclusive => _lockFile != null;

    public void Dispose()
    {
        var file = _lockFile;
        _lockFile = null;
        if (file == null)
            return;

        try { file.Dispose(); }
        catch { /* releasing on exit — nothing left to recover */ }
    }
}

internal static class InstanceAllocator
{
    /// <summary>Plenty for local multi-instance testing; beyond this we stop searching.</summary>
    internal const int MaxInstances = 16;

    /// <summary>
    /// Claim the lowest free instance index by taking an exclusive lock file. Process
    /// enumeration used to decide this, and its failure path returned 0 — two launchers
    /// could then both be "instance 0" and share config.json, username, and log file.
    /// </summary>
    public static InstanceClaim Claim(string instancesDirectory)
    {
        try
        {
            Directory.CreateDirectory(instancesDirectory);
        }
        catch
        {
            return new InstanceClaim(0, null);
        }

        for (var index = 0; index < MaxInstances; index++)
        {
            var path = Path.Combine(instancesDirectory, $"lock{index}");
            try
            {
                var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 64, FileOptions.DeleteOnClose);
                WriteOwnerStamp(stream);
                return new InstanceClaim(index, stream);
            }
            catch (IOException)
            {
                // Held by another launcher — try the next index.
            }
            catch (UnauthorizedAccessException)
            {
                // Locked or not writable — try the next index.
            }
        }

        // Every index is taken (or the directory is unusable): fall back to shared
        // instance 0 behaviour rather than refusing to start.
        return new InstanceClaim(0, null);
    }

    /// <summary>Diagnostics only — the lock itself is what reserves the index.</summary>
    private static void WriteOwnerStamp(FileStream stream)
    {
        try
        {
            var stamp = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"pid={Environment.ProcessId} started={DateTime.UtcNow:O}"));
            stream.Write(stamp, 0, stamp.Length);
            stream.Flush();
        }
        catch
        {
            // The claim is valid without the stamp.
        }
    }
}

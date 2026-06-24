using System;
using System.Diagnostics;
using System.Linq;

namespace SMSO.Launcher;

internal static class InstanceAllocator
{
    public static int GetInstanceIndex()
    {
        var current = Process.GetCurrentProcess();
        Process[] launchers;
        try
        {
            launchers = Process.GetProcessesByName("BSMSO.Launcher");
        }
        catch
        {
            return 0;
        }

        try
        {
            var ordered = launchers
                .Where(p =>
                {
                    try
                    {
                        return !p.HasExited;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderBy(p => p.StartTime)
                .ThenBy(p => p.Id)
                .ToList();

            var index = ordered.FindIndex(p => p.Id == current.Id);
            return index < 0 ? 0 : index;
        }
        finally
        {
            foreach (var proc in launchers)
                proc.Dispose();
        }
    }
}

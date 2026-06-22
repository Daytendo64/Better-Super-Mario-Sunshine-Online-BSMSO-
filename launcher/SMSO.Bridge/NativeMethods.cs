using System.Runtime.InteropServices;

namespace SMSO.Bridge;

internal static class NativeMethods
{
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesWritten);

    public static bool ReadProcessMemory(IntPtr hProcess, UIntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead)
        => ReadProcessMemory(hProcess, (IntPtr)(long)lpBaseAddress.ToUInt64(), lpBuffer, dwSize, out lpNumberOfBytesRead);

    public static bool WriteProcessMemory(IntPtr hProcess, UIntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead)
        => WriteProcessMemory(hProcess, (IntPtr)(long)lpBaseAddress.ToUInt64(), lpBuffer, dwSize, out lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GetProcessId(IntPtr process);
}

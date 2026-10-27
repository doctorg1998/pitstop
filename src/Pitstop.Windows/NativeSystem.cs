using System.Runtime.InteropServices;

namespace Pitstop.Windows;

/// <summary>Kernel counters shared by the Health Check probe and the live metrics sampler.</summary>
internal static class NativeSystem
{
    /// <summary>Percent of physical memory in use, and total memory in bytes.</summary>
    public static (uint LoadPercent, ulong TotalBytes)? Memory()
    {
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref mem) ? (mem.dwMemoryLoad, mem.ullTotalPhys) : null;
    }

    /// <summary>Cumulative idle/kernel/user CPU time; diff two readings to get load over an interval.</summary>
    public static (long Idle, long Kernel, long User)? CpuTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel, user) : null;

    /// <summary>CPU busy percent between two <see cref="CpuTimes"/> readings (kernel time includes idle).</summary>
    public static double? CpuPercent((long Idle, long Kernel, long User) a, (long Idle, long Kernel, long User) b)
    {
        var idle = b.Idle - a.Idle;
        var total = (b.Kernel - a.Kernel) + (b.User - a.User);
        return total > 0 ? Math.Round(100.0 * (total - idle) / total, 1) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

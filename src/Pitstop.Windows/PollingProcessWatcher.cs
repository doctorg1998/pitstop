using System.Runtime.InteropServices;
using System.Text;
using Pitstop.Core.Profiles;

namespace Pitstop.Windows;

/// <summary>
/// Detects app starts and exits without admin rights (Win32_ProcessStartTrace needs admin).
/// Every poll reads only the list of process IDs (EnumProcesses), and opens a handle just for new ones,
/// so a poll costs well under a millisecond of CPU even with hundreds of processes running.
/// </summary>
public sealed class PollingProcessWatcher : IProcessWatcher
{
    private readonly TimeSpan _interval;
    private readonly ProcessDiffer _differ = new();
    private Timer? _timer;
    private int _polling;

    public PollingProcessWatcher(TimeSpan? interval = null) => _interval = interval ?? TimeSpan.FromSeconds(2);

    public event EventHandler<ProcessEvent>? ProcessStarted;
    public event EventHandler<ProcessEvent>? ProcessExited;

    /// <summary>Apps that were already running when Pitstop started (so their profile can activate at once).</summary>
    public IReadOnlyDictionary<int, string> AlreadyRunning => _differ.Known;

    public void Start()
    {
        Poll(null); // primes the differ synchronously
        _timer = new Timer(Poll, null, _interval, _interval);
    }

    private void Poll(object? _)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // never overlap polls
        try
        {
            var now = DateTimeOffset.Now;
            var (started, exited) = _differ.Update(CurrentPids(), ImageName);
            foreach (var (pid, exe) in exited) ProcessExited?.Invoke(this, new ProcessEvent(exe, pid, now));
            foreach (var (pid, exe) in started) ProcessStarted?.Invoke(this, new ProcessEvent(exe, pid, now));
        }
        catch (Exception) { /* a failed poll is retried on the next tick */ }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private static int[] CurrentPids()
    {
        var buffer = new int[1024];
        while (true)
        {
            if (!EnumProcesses(buffer, buffer.Length * sizeof(int), out var bytes)) throw new InvalidOperationException("EnumProcesses failed");
            if (bytes < buffer.Length * sizeof(int)) return buffer[..(bytes / sizeof(int))];
            buffer = new int[buffer.Length * 2];
        }
    }

    private static string? ImageName(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null; // already exited, or a protected system process
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : null;
        }
        finally { CloseHandle(handle); }
    }

    public void Dispose() => _timer?.Dispose();

    private const uint ProcessQueryLimitedInformation = 0x1000;
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EnumProcesses([Out] int[] pids, int size, out int bytesReturned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

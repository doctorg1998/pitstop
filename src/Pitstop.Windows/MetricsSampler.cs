using System.Net.NetworkInformation;

namespace Pitstop.Windows;

public sealed record MetricsSample(
    DateTimeOffset At,
    double? CpuPercent,
    double? MemoryPercent,
    double? NetDownKBps,
    double? NetUpKBps,
    int ProcessCount);

public sealed record DriveUsage(string Name, string Label, long TotalBytes, long FreeBytes);

/// <summary>
/// Live system readings for the dashboard graphs. Each call returns the change since the previous call,
/// so the dashboard calls it once a second while its window is open and never otherwise.
/// </summary>
public sealed class MetricsSampler
{
    private (long Idle, long Kernel, long User)? _lastCpu;
    private (long Down, long Up, DateTimeOffset At)? _lastNet;

    public MetricsSample Sample()
    {
        var now = DateTimeOffset.Now;

        double? cpu = null;
        if (NativeSystem.CpuTimes() is { } cpuNow)
        {
            if (_lastCpu is { } prev) cpu = NativeSystem.CpuPercent(prev, cpuNow);
            _lastCpu = cpuNow;
        }

        double? down = null, up = null;
        var (bytesDown, bytesUp) = NetworkBytes();
        if (_lastNet is { } last && now > last.At)
        {
            var seconds = (now - last.At).TotalSeconds;
            down = Math.Round(Math.Max(0, bytesDown - last.Down) / 1024.0 / seconds, 1);
            up = Math.Round(Math.Max(0, bytesUp - last.Up) / 1024.0 / seconds, 1);
        }
        _lastNet = (bytesDown, bytesUp, now);

        return new MetricsSample(now, cpu, NativeSystem.Memory()?.LoadPercent, down, up, PollingProcessWatcher.ProcessCount());
    }

    public static double? TotalMemoryGb() =>
        NativeSystem.Memory() is { } m ? Math.Round(m.TotalBytes / 1073741824.0) : null;

    public static IReadOnlyList<DriveUsage> Drives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveUsage(d.Name.TrimEnd('\\'), string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel,
                d.TotalSize, d.AvailableFreeSpace))
            .ToList();

    /// <summary>Total bytes received/sent on active physical and Wi-Fi adapters (loopback and tunnels excluded).</summary>
    private static (long Down, long Up) NetworkBytes()
    {
        long down = 0, up = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = nic.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
            catch (NetworkInformationException) { }
        }
        return (down, up);
    }
}

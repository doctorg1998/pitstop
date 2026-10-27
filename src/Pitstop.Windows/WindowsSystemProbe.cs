using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Pitstop.Core.Health;

namespace Pitstop.Windows;

/// <summary>
/// Reads the real system. Read-only and admin-free; each check runs in parallel and records its own
/// failure in the snapshot instead of throwing.
/// </summary>
public sealed class WindowsSystemProbe : ISystemProbe
{
    /// <summary>Hosts used for the DNS timing test. These lookups and the pings are the only network traffic.</summary>
    public static readonly string[] DnsTestHosts = { "www.microsoft.com", "www.cloudflare.com", "www.wikipedia.org" };
    /// <summary>
    /// Public resolvers throttle rapid pings (4 of 20 dropped at 100 ms spacing, 0 at 250 ms; BUG-004),
    /// so the internet test splits 20 pings across two providers at a gentle 400 ms spacing.
    /// </summary>
    private static readonly string[] PingInternetTargets = { "1.1.1.1", "8.8.8.8" };
    private const int InternetPingsPerTarget = 10, InternetPingSpacingMs = 400;
    private const int GatewayPings = 20, GatewayPingSpacingMs = 200;

    public async Task<SystemSnapshot> CollectAsync(CancellationToken ct)
    {
        var s = new SystemSnapshot
        {
            TakenAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            IsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };

        Task Run(string checkId, Action<SystemSnapshot> probe) => Task.Run(() =>
        {
            try { probe(s); }
            catch (Exception ex) when (ex is not OperationCanceledException) { s.AddProbeError(checkId, Describe(ex)); }
        }, ct);

        Task RunAsync(string checkId, Func<SystemSnapshot, Task> probe) => Task.Run(async () =>
        {
            try { await probe(s).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { s.AddProbeError(checkId, Describe(ex)); }
        }, ct);

        await Task.WhenAll(
            Run(CheckIds.Updates, ReadOsAndUpdates),
            Run(CheckIds.Crashes, ReadCrashes),
            Run(CheckIds.DiskSpace, ReadDrives),
            Run(CheckIds.DiskHealth, ReadPhysicalDisks),
            RunAsync(CheckIds.MemoryCpu, x => ReadMemoryAndCpuAsync(x, ct)),
            Run(CheckIds.Startup, ReadStartupItems),
            Run(CheckIds.Drivers, ReadDrivers),
            RunAsync(CheckIds.Network, x => ReadNetworkAsync(x, ct)),
            Run(CheckIds.Battery, ReadBattery),
            Run(CheckIds.Temperature, ReadTemperature)).ConfigureAwait(false);

        return s;
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException or ManagementException { ErrorCode: ManagementStatus.AccessDenied } => "access denied",
        _ => ex.Message.Trim(),
    };

    // H1
    private static void ReadOsAndUpdates(SystemSnapshot s)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var b) ? b : 0;
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string;
            s.OsName = Parsers.OsDisplayName(product, build) + (display is null ? "" : $" {display}");
            s.OsBuild = $"{build}.{key?.GetValue("UBR")}";
        }

        using var searcher = new ManagementObjectSearcher("SELECT InstalledOn FROM Win32_QuickFixEngineering");
        DateTime? latest = null;
        foreach (ManagementObject mo in searcher.Get())
            using (mo)
                if (Parsers.QfeDate(mo["InstalledOn"] as string) is { } d && (latest is null || d > latest))
                    latest = d;
        s.LastUpdateInstalled = latest;
    }

    // H3
    private static void ReadCrashes(SystemSnapshot s)
    {
        const string query = "*[System[(" +
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
            "(Provider[@Name='EventLog'] and EventID=6008) or " +
            "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)" +
            ") and TimeCreated[timediff(@SystemTime) <= 2592000000]]]"; // 30 days in ms

        var events = new List<CrashEvent>();
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
        for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            using (e)
            {
                var kind = e.Id switch { 1001 => Parsers.BlueScreen, 41 => Parsers.PowerLoss, _ => Parsers.UnexpectedShutdown };
                if (e.TimeCreated is { } t) events.Add(new CrashEvent(t, kind));
            }
        s.Crashes = Parsers.MergeCrashEvents(events);
    }

    // H4
    private static void ReadDrives(SystemSnapshot s) =>
        s.Drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveSample(d.Name.TrimEnd('\\'), d.TotalSize, d.AvailableFreeSpace))
            .ToList();

    // H5
    private static void ReadPhysicalDisks(SystemSnapshot s)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT FriendlyName, MediaType, HealthStatus FROM MSFT_PhysicalDisk"));
        var disks = new List<PhysicalDiskHealth>();
        foreach (ManagementObject mo in searcher.Get())
            using (mo)
            {
                var media = Convert.ToInt32(mo["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "drive" };
                var health = Convert.ToInt32(mo["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                disks.Add(new PhysicalDiskHealth(((mo["FriendlyName"] as string) ?? "Disk").Trim(), media, health));
            }
        s.PhysicalDisks = disks;
    }

    // H6
    private static async Task ReadMemoryAndCpuAsync(SystemSnapshot s, CancellationToken ct)
    {
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            s.MemoryUsedPercent = mem.dwMemoryLoad;
            s.TotalMemoryGb = Math.Round(mem.ullTotalPhys / 1073741824.0);
        }

        // CPU load over a 3-second window from kernel counters: cheaper and steadier than a single sample.
        if (!GetSystemTimes(out var idle1, out var kernel1, out var user1)) return;
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        if (!GetSystemTimes(out var idle2, out var kernel2, out var user2)) return;
        var idle = idle2 - idle1;
        var total = (kernel2 - kernel1) + (user2 - user1); // kernel time includes idle time
        if (total > 0) s.CpuPercent = Math.Round(100.0 * (total - idle) / total, 1);
    }

    // H7
    private static void ReadStartupItems(SystemSnapshot s)
    {
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        var items = new List<StartupItem>();

        bool Disabled(string approvedSubKey, string name) =>
            new[] { Registry.CurrentUser, Registry.LocalMachine }.Any(hive =>
            {
                using var k = hive.OpenSubKey(approved + approvedSubKey);
                return Parsers.StartupApprovedIsDisabled(k?.GetValue(name) as byte[]);
            });

        void FromRegistry(RegistryKey hive, string path, string approvedSubKey, string label)
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
                if (!Disabled(approvedSubKey, name)) items.Add(new StartupItem(Parsers.CleanStartupName(name), label));
        }

        void FromFolder(Environment.SpecialFolder folder, string label)
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                if (!Disabled("StartupFolder", Path.GetFileName(file)))
                    items.Add(new StartupItem(Parsers.CleanStartupName(Path.GetFileNameWithoutExtension(file)), label));
        }

        FromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (this user)");
        FromRegistry(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (all users)");
        FromRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Registry (all users, 32-bit)");
        FromFolder(Environment.SpecialFolder.Startup, "Startup folder (this user)");
        FromFolder(Environment.SpecialFolder.CommonStartup, "Startup folder (all users)");
        s.StartupItems = items;
    }

    // H8
    private static void ReadDrivers(SystemSnapshot s)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, DeviceName, DeviceClass, DriverProviderName, DriverDate FROM Win32_PnPSignedDriver " +
            "WHERE DeviceClass = 'DISPLAY' OR DeviceClass = 'MEDIA' OR DeviceClass = 'NET'");
        var drivers = new Dictionary<string, DriverInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagementObject mo in searcher.Get())
            using (mo)
            {
                var device = (mo["DeviceName"] as string)?.Trim();
                var provider = (mo["DriverProviderName"] as string)?.Trim() ?? "";
                // Only physical hardware with vendor drivers is graded: Microsoft drivers are serviced by
                // Windows Update, and virtual devices' driver dates say nothing about the hardware.
                if (string.IsNullOrEmpty(device)
                    || provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                    || Parsers.IsVirtualDevice(mo["DeviceID"] as string)) continue;
                var category = (mo["DeviceClass"] as string) switch { "DISPLAY" => "Graphics", "MEDIA" => "Audio", _ => "Network" };
                drivers.TryAdd(device, new DriverInfo(device, category, provider, Parsers.CimDate(mo["DriverDate"] as string)));
            }
        s.Drivers = drivers.Values.ToList();
    }

    // H9
    private static async Task ReadNetworkAsync(SystemSnapshot s, CancellationToken ct)
    {
        var gateway = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

        var gatewayTask = gateway is null
            ? Task.FromResult<IReadOnlyList<long?>>(Array.Empty<long?>())
            : PingAsync(gateway.ToString(), GatewayPings, GatewayPingSpacingMs, ct);
        var internetTasks = PingInternetTargets.Select(t => PingAsync(t, InternetPingsPerTarget, InternetPingSpacingMs, ct)).ToList();
        var dnsTask = DnsMedianMsAsync(ct);
        await Task.WhenAll(internetTasks.Append<Task>(gatewayTask).Append(dnsTask)).ConfigureAwait(false);

        var gw = gatewayTask.Result.Count > 0 ? Parsers.PingStats(gatewayTask.Result) : ((double?)null, (double?)null);
        var net = Parsers.PingStats(internetTasks.SelectMany(t => t.Result).ToList());
        var internetReachable = net.AvgMs is not null;
        s.Network = new NetworkSample(
            dnsTask.Result,
            gw.Item2, gw.Item1,
            net.AvgMs, internetReachable || gw.Item1 is not null ? net.LossPercent : null);
    }

    private static async Task<IReadOnlyList<long?>> PingAsync(string host, int count, int spacingMs, CancellationToken ct)
    {
        using var ping = new Ping();
        var results = new List<long?>(count);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(host, 1000).ConfigureAwait(false);
                results.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (PingException) { results.Add(null); }
            if (i < count - 1) await Task.Delay(spacingMs, ct).ConfigureAwait(false);
        }
        return results;
    }

    private static async Task<double?> DnsMedianMsAsync(CancellationToken ct)
    {
        var times = new List<double>();
        foreach (var host in DnsTestHosts)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (SocketException) { }
        }
        if (times.Count == 0) return null;
        times.Sort();
        return Math.Round(times[times.Count / 2], 1);
    }

    // H10
    private static void ReadBattery(SystemSnapshot s)
    {
        using (var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery"))
        using (var batteries = searcher.Get())
            if (batteries.Count == 0)
            {
                s.BatteryProbed = true;
                return;
            }

        var xmlPath = Path.Combine(Path.GetTempPath(), $"pitstop-battery-{Guid.NewGuid():N}.xml");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", $"/batteryreport /xml /output \"{xmlPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;
            if (!p.WaitForExit(15_000)) { p.Kill(); throw new TimeoutException("powercfg didn't finish in 15 seconds"); }
            s.Battery = Parsers.BatteryReport(File.ReadAllText(xmlPath));
            s.BatteryProbed = true;
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    // H11
    private static void ReadTemperature(SystemSnapshot s)
    {
        if (!s.IsElevated) return; // The rule reports this as skipped with the reason.
        var scope = new ManagementScope(@"\\.\root\wmi");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
        double? max = null;
        foreach (ManagementObject mo in searcher.Get())
            using (mo)
            {
                var c = Convert.ToDouble(mo["CurrentTemperature"]) / 10.0 - 273.15; // tenths of a kelvin
                if (c is > 0 and < 150 && (max is null || c > max)) max = Math.Round(c, 1);
            }
        s.TemperatureC = max;
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

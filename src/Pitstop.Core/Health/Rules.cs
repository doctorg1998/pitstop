using System.Globalization;

namespace Pitstop.Core.Health;

/// <summary>Shared plumbing: a probe error or missing data becomes a Skipped finding, never an exception.</summary>
public abstract class HealthRule : IHealthRule
{
    public abstract string CheckId { get; }
    public abstract string Title { get; }

    public Finding Evaluate(SystemSnapshot s)
    {
        if (s.ProbeErrors.TryGetValue(CheckId, out var error))
            return Skipped($"Couldn't read this check: {error}");
        return EvaluateCore(s) ?? Skipped("No data was collected for this check.");
    }

    /// <summary>Returns null when the snapshot has no data for this check.</summary>
    protected abstract Finding? EvaluateCore(SystemSnapshot s);

    protected Finding Make(Severity severity, string summary, string meaning, string todo) =>
        new(CheckId, Title, severity, summary, meaning, todo);

    protected Finding Skipped(string why, string todo = "Nothing to do.") =>
        Make(Severity.Skipped, why, "Pitstop couldn't check this on this PC.", todo);

    protected Finding NotApplicable(string why, string meaning) =>
        Make(Severity.NotApplicable, why, meaning, "Nothing to do.");

    /// <summary>Capitalizes the first letter of a summary built from parts.</summary>
    protected static string Sentence(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    protected static Severity Grade(double value, double watch, double action, bool higherIsWorse = true) =>
        higherIsWorse
            ? value >= action ? Severity.Action : value >= watch ? Severity.Watch : Severity.Ok
            : value <= action ? Severity.Action : value <= watch ? Severity.Watch : Severity.Ok;

    protected static string N(double v, string format = "0") => v.ToString(format, CultureInfo.InvariantCulture);
}

public sealed class UpdatesRule : HealthRule
{
    public override string CheckId => CheckIds.Updates;
    public override string Title => "Windows updates";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.LastUpdateInstalled is not { } last) return null;
        var days = (int)(s.TakenAt.Date - last.Date).TotalDays;
        var sev = Grade(days, Thresholds.DaysSinceUpdate.Watch, Thresholds.DaysSinceUpdate.Action);
        var os = s.OsName is null ? "" : $"{s.OsName} (build {s.OsBuild}). ";
        return Make(sev, $"{os}Last update installed {days} day{(days == 1 ? "" : "s")} ago.",
            "Updates patch security holes and fix bugs. Windows normally installs one every month.",
            sev == Severity.Ok ? "Nothing to do." : "Open Settings > Windows Update and click Check for updates.");
    }
}

public sealed class UptimeRule : HealthRule
{
    public override string CheckId => CheckIds.Uptime;
    public override string Title => "Time since restart";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.Uptime is not { } up) return null;
        var sev = Grade(up.TotalDays, Thresholds.UptimeDays.Watch, Thresholds.UptimeDays.Action);
        return Make(sev, $"Running for {N(up.TotalDays, "0.#")} days without a restart.",
            "Long uptimes let memory leaks build up and keep updates from finishing. Shutting down with Fast Startup on doesn't count as a restart.",
            sev == Severity.Ok ? "Nothing to do." : "Restart the PC (Start > Power > Restart).");
    }
}

public sealed class CrashesRule : HealthRule
{
    public override string CheckId => CheckIds.Crashes;
    public override string Title => "Crashes and unexpected shutdowns";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.Crashes is not { } crashes) return null;
        var recent = crashes.Where(c => c.At >= s.TakenAt.AddDays(-30)).ToList();
        var sev = Grade(recent.Count, Thresholds.CrashesIn30Days.Watch, Thresholds.CrashesIn30Days.Action);
        var summary = recent.Count == 0
            ? "No crashes or unexpected shutdowns in the last 30 days."
            : $"{recent.Count} in the last 30 days, most recent {recent.Max(c => c.At):yyyy-MM-dd} ({string.Join(", ", recent.Select(c => c.Kind).Distinct())}).";
        var onlyPowerLoss = recent.Count > 0 && recent.All(c => c.Kind != Parsers.BlueScreen);
        return Make(sev, summary,
            "Blue screens and sudden power-offs usually point to a driver, overheating, or a power problem.",
            sev == Severity.Ok ? "Nothing to do."
                : onlyPowerLoss
                    ? "If the power went out or you held the power button, you can ignore this. Otherwise check that vents are clear, the power cable is secure, and GPU and chipset drivers are current."
                    : "Update GPU and chipset drivers, check that vents are clear, and make sure the power cable is secure. If it keeps happening, note what you were doing each time.");
    }
}

public sealed class DiskSpaceRule : HealthRule
{
    public override string CheckId => CheckIds.DiskSpace;
    public override string Title => "Disk space";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.Drives is not { Count: > 0 } drives) return null;
        var worst = drives.Where(d => d.TotalBytes > 0)
            .Select(d => (Drive: d, Pct: 100.0 * d.FreeBytes / d.TotalBytes))
            .OrderBy(x => x.Pct).First();
        var sev = Grade(worst.Pct, Thresholds.FreeDiskPercent.Watch, Thresholds.FreeDiskPercent.Action, higherIsWorse: false);
        var list = string.Join(", ", drives.Where(d => d.TotalBytes > 0).Select(d =>
            $"{d.Name} {N(d.FreeBytes / 1e9)} GB free of {N(d.TotalBytes / 1e9)} GB ({N(100.0 * d.FreeBytes / d.TotalBytes)}%)"));
        return Make(sev, list + ".",
            "Windows needs free space for updates, virtual memory and temp files. Under about 10% free, things slow down and updates fail.",
            sev == Severity.Ok ? "Nothing to do."
                : $"Free up space on drive {worst.Drive.Name} by running Disk Cleanup, emptying the Recycle Bin, and uninstalling games or apps you no longer use.");
    }
}

public sealed class DiskHealthRule : HealthRule
{
    public override string CheckId => CheckIds.DiskHealth;
    public override string Title => "Drive health";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.PhysicalDisks is not { Count: > 0 } disks) return null;
        var sev = disks.Any(d => d.HealthStatus == "Unhealthy") ? Severity.Action
            : disks.Any(d => d.HealthStatus == "Warning") ? Severity.Watch : Severity.Ok;
        return Make(sev, string.Join(", ", disks.Select(d => $"{d.Name} ({d.MediaType}): {d.HealthStatus}")) + ".",
            "This is Windows' own verdict on each drive, based on its built-in failure warnings.",
            sev == Severity.Ok ? "Nothing to do."
                : "Back up your important files now, then plan to replace the flagged drive.");
    }
}

public sealed class MemoryCpuRule : HealthRule
{
    public override string CheckId => CheckIds.MemoryCpu;
    public override string Title => "Memory and processor load";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.MemoryUsedPercent is null && s.CpuPercent is null) return null;
        var parts = new List<string>();
        var sev = Severity.Ok;
        if (s.MemoryUsedPercent is { } mem)
        {
            parts.Add($"Memory {N(mem)}% used" + (s.TotalMemoryGb is { } gb ? $" of {N(gb)} GB" : ""));
            if (mem >= Thresholds.MemoryWatchPercent) sev = Severity.Watch;
        }
        if (s.CpuPercent is { } cpu)
        {
            parts.Add($"processor {N(cpu)}% busy");
            if (cpu >= Thresholds.CpuWatchPercent) sev = Severity.Watch;
        }
        return Make(sev, string.Join(", ", parts) + " during the check.",
            "This is a snapshot of how busy the PC was during the check. Constantly high numbers make everything feel slow.",
            sev == Severity.Ok ? "Nothing to do."
                : "Open Task Manager (Ctrl+Shift+Esc), sort by Memory or CPU, and close what you don't need.");
    }
}

public sealed class StartupRule : HealthRule
{
    public override string CheckId => CheckIds.Startup;
    public override string Title => "Startup apps";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.StartupItems is not { } items) return null;
        var sev = Grade(items.Count, Thresholds.StartupItems.Watch, Thresholds.StartupItems.Action);
        var names = string.Join(", ", items.Take(8).Select(i => i.Name)) + (items.Count > 8 ? ", …" : "");
        return Make(sev, $"{items.Count} app{(items.Count == 1 ? "" : "s")} start with Windows" + (items.Count > 0 ? $": {names}." : "."),
            "Every startup app slows boot time and uses memory in the background.",
            sev == Severity.Ok ? "Nothing to do."
                : "Open Task Manager > Startup apps and disable anything you don't need right after login.");
    }
}

public sealed class DriversRule : HealthRule
{
    public override string CheckId => CheckIds.Drivers;
    public override string Title => "Driver age (graphics, audio, network)";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.Drivers is not { } drivers) return null;
        if (drivers.Count == 0)
            return Make(Severity.Ok, "Only built-in Windows drivers were found for graphics, audio and network.",
                "Windows keeps its own drivers up to date.", "Nothing to do.");
        var cutoff = s.TakenAt.DateTime.AddYears(-Thresholds.DriverWatchYears);
        var old = drivers.Where(d => d.DriverDate is { } date && date < cutoff).ToList();
        var sev = old.Count > 0 ? Severity.Watch : Severity.Ok;
        var summary = old.Count == 0
            ? $"{drivers.Count} vendor driver{(drivers.Count == 1 ? " is" : "s are")} less than {Thresholds.DriverWatchYears} years old."
            : "Older than " + Thresholds.DriverWatchYears + " years: " + string.Join("; ", old.Select(d => $"{d.Device} ({d.DriverDate:yyyy-MM-dd})")) + ".";
        return Make(sev, summary,
            "Old drivers are a common cause of crashes, audio dropouts and poor game performance.",
            sev == Severity.Ok ? "Nothing to do."
                : "Download the latest driver from the device maker's site (NVIDIA, AMD, Intel, Realtek, or your PC maker).");
    }
}

public sealed class NetworkRule : HealthRule
{
    public override string CheckId => CheckIds.Network;
    public override string Title => "Network connection";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.Network is not { } n) return null;
        if (n.InternetLossPercent is null && n.GatewayLossPercent is null)
            return Make(Severity.Action, "No network connection was found.",
                "Pitstop couldn't reach your router or the internet.",
                "Check Wi-Fi or the network cable, then restart your router.");

        var sev = Severity.Ok;
        void Raise(Severity x) { if (x > sev) sev = x; }
        var parts = new List<string>();
        if (n.GatewayLatencyMs is { } gw) parts.Add($"router {N(gw)} ms");
        if (n.GatewayLossPercent is { } gl) { parts.Add($"{N(gl)}% loss to router"); Raise(Grade(gl, Thresholds.PacketLossPercent.Watch, Thresholds.PacketLossPercent.Action)); }
        if (n.InternetLatencyMs is { } il) { parts.Add($"internet {N(il)} ms"); if (il >= Thresholds.LatencyWatchMs) Raise(Severity.Watch); }
        if (n.InternetLossPercent is { } ip) { parts.Add($"{N(ip)}% loss to internet"); Raise(Grade(ip, Thresholds.PacketLossPercent.Watch, Thresholds.PacketLossPercent.Action)); }
        if (n.DnsMs is { } dns) { parts.Add($"DNS lookup {N(dns)} ms"); if (dns >= Thresholds.DnsWatchMs) Raise(Severity.Watch); }

        var lossAtRouter = n.GatewayLossPercent >= Thresholds.PacketLossPercent.Watch;
        return Make(sev, Sentence(string.Join(", ", parts)) + ".",
            "Latency is delay, and packet loss is data that never arrives. Loss to your own router points to Wi-Fi or cabling; loss only beyond it points to your internet provider.",
            sev == Severity.Ok ? "Nothing to do."
                : lossAtRouter ? "Move closer to the router or use a network cable, and restart the router."
                : "Restart your router. If it keeps happening, contact your internet provider with these numbers.");
    }
}

public sealed class BatteryRule : HealthRule
{
    public override string CheckId => CheckIds.Battery;
    public override string Title => "Battery wear";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (!s.BatteryProbed) return null;
        if (s.Battery is not { DesignCapacityMWh: > 0 } b)
            return NotApplicable("No battery (desktop PC).", "This check only applies to laptops.");
        var pct = 100.0 * b.FullChargeCapacityMWh / b.DesignCapacityMWh;
        var sev = Grade(pct, Thresholds.BatteryHealthPercent.Watch, Thresholds.BatteryHealthPercent.Action, higherIsWorse: false);
        return Make(sev, $"Battery holds {N(pct)}% of its original charge ({N(b.FullChargeCapacityMWh / 1000.0, "0.#")} of {N(b.DesignCapacityMWh / 1000.0, "0.#")} Wh).",
            "All batteries wear out. Below about 80%, you'll notice shorter unplugged time.",
            sev == Severity.Ok ? "Nothing to do."
                : sev == Severity.Watch ? "Normal wear. Avoid leaving it at 100% and hot for long periods."
                : "Consider a battery replacement from the laptop's manufacturer or a repair shop.");
    }
}

public sealed class TemperatureRule : HealthRule
{
    public override string CheckId => CheckIds.Temperature;
    public override string Title => "Temperature";

    protected override Finding? EvaluateCore(SystemSnapshot s)
    {
        if (s.TemperatureC is not { } t)
            return s.IsElevated
                ? Skipped("This PC's firmware doesn't report a temperature to Windows.")
                : Skipped("Reading temperatures needs administrator rights, so this check was skipped.",
                    "Optional: to include it, right-click Pitstop and choose Run as administrator.");
        var sev = Grade(t, Thresholds.TemperatureC.Watch, Thresholds.TemperatureC.Action);
        return Make(sev, $"System thermal zone at {N(t)} °C.",
            "Sustained heat makes the PC slow itself down, and over time it shortens hardware life.",
            sev == Severity.Ok ? "Nothing to do." : "Clean dust from vents and fans, and make sure air can flow around the PC.");
    }
}

public static class DefaultRules
{
    public static IReadOnlyList<IHealthRule> All { get; } = new IHealthRule[]
    {
        new UpdatesRule(), new UptimeRule(), new CrashesRule(), new DiskSpaceRule(), new DiskHealthRule(),
        new MemoryCpuRule(), new StartupRule(), new DriversRule(), new NetworkRule(), new BatteryRule(), new TemperatureRule(),
    };
}

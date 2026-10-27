using System.Globalization;
using System.Xml.Linq;

namespace Pitstop.Core.Health;

/// <summary>Pure parsers for raw Windows data, kept here so they're testable with recorded fixtures.</summary>
public static class Parsers
{
    /// <summary>Parses a CIM/WMI datetime such as "20230512000000.******+***".</summary>
    public static DateTime? CimDate(string? value)
    {
        if (value is null || value.Length < 8) return null;
        return DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    /// <summary>Win32_QuickFixEngineering.InstalledOn is "M/d/yyyy" or, on some builds, a hex FILETIME.</summary>
    public static DateTime? QfeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value.Trim(), new[] { "M/d/yyyy", "MM/dd/yyyy" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d;
        if (long.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft) && ft > 0)
        {
            try { return DateTime.FromFileTimeUtc(ft).Date; } catch (ArgumentOutOfRangeException) { }
        }
        return null;
    }

    /// <summary>
    /// Task Manager stores a startup app's enabled state in Explorer\StartupApproved as binary data.
    /// An odd first byte (0x01, 0x03, 0x07…) means the user disabled it.
    /// </summary>
    public static bool StartupApprovedIsDisabled(byte[]? data) => data is { Length: > 0 } && (data[0] & 1) == 1;

    /// <summary>
    /// Software-only devices (virtual audio, VPN tunnels, virtual monitors) are enumerated under ROOT\ or SWD\.
    /// Physical hardware is under a bus such as PCI\, HDAUDIO\ or USB\.
    /// </summary>
    public static bool IsVirtualDevice(string? deviceId) =>
        deviceId is null
        || deviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
        || deviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims startup entry names and drops the per-install hash suffix some apps add (e.g. Chrome's).</summary>
    public static string CleanStartupName(string name)
    {
        var trimmed = name.Trim();
        var underscore = trimmed.LastIndexOf('_');
        if (underscore > 0 && trimmed.Length - underscore - 1 == 32 && trimmed[(underscore + 1)..].All(Uri.IsHexDigit))
            trimmed = trimmed[..underscore];
        return trimmed;
    }

    /// <summary>Reads the first battery from <c>powercfg /batteryreport /xml</c> output. Null if there's no battery.</summary>
    public static BatterySample? BatteryReport(string xml)
    {
        var doc = XDocument.Parse(xml);
        var battery = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Battery");
        if (battery is null) return null;

        long Read(string name) =>
            long.TryParse(battery.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        var design = Read("DesignCapacity");
        return design > 0 ? new BatterySample(design, Read("FullChargeCapacity")) : null;
    }

    /// <summary>Windows 11 still reports "Windows 10" in ProductName; build 22000+ is Windows 11.</summary>
    public static string OsDisplayName(string productName, int build) =>
        build >= 22000 ? productName.Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : productName;

    public const string BlueScreen = "Blue screen", PowerLoss = "Unexpected power loss", UnexpectedShutdown = "Unexpected shutdown";

    /// <summary>
    /// One crash usually logs several events at the next boot (BugCheck 1001, Kernel-Power 41, EventLog 6008).
    /// Events within 10 minutes of each other are one incident, labelled by the most specific cause.
    /// </summary>
    public static List<CrashEvent> MergeCrashEvents(IEnumerable<CrashEvent> events)
    {
        static int Rank(string kind) => kind switch { BlueScreen => 0, PowerLoss => 1, _ => 2 };

        var merged = new List<CrashEvent>();
        foreach (var e in events.OrderBy(e => e.At))
        {
            if (merged.Count > 0 && e.At - merged[^1].At <= TimeSpan.FromMinutes(10))
            {
                var last = merged[^1];
                merged[^1] = last with { Kind = Rank(e.Kind) < Rank(last.Kind) ? e.Kind : last.Kind };
            }
            else merged.Add(e);
        }
        return merged;
    }

    /// <summary>Packet loss percent and average round trip of the successful replies (null if none succeeded).</summary>
    public static (double LossPercent, double? AvgMs) PingStats(IReadOnlyList<long?> roundTripsMs)
    {
        if (roundTripsMs.Count == 0) return (100, null);
        var ok = roundTripsMs.Where(r => r.HasValue).Select(r => (double)r!.Value).ToList();
        var loss = 100.0 * (roundTripsMs.Count - ok.Count) / roundTripsMs.Count;
        return (loss, ok.Count > 0 ? ok.Average() : null);
    }
}

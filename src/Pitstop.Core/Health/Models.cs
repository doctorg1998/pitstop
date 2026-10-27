namespace Pitstop.Core.Health;

public enum Severity { Ok, Watch, Action, Skipped, NotApplicable }

/// <summary>One evaluated health check, written for a non-technical reader.</summary>
public sealed record Finding(
    string CheckId,
    string Title,
    Severity Severity,
    string Summary,
    string WhatItMeans,
    string WhatToDo);

public sealed record CrashEvent(DateTimeOffset At, string Kind);
public sealed record DriveSample(string Name, long TotalBytes, long FreeBytes);
public sealed record PhysicalDiskHealth(string Name, string MediaType, string HealthStatus);
public sealed record StartupItem(string Name, string Location);
public sealed record DriverInfo(string Device, string Category, string Provider, DateTime? DriverDate);
public sealed record NetworkSample(
    double? DnsMs,
    double? GatewayLatencyMs, double? GatewayLossPercent,
    double? InternetLatencyMs, double? InternetLossPercent);
public sealed record BatterySample(long DesignCapacityMWh, long FullChargeCapacityMWh);

/// <summary>Raw data gathered by an <see cref="ISystemProbe"/>. A null field means "not collected".</summary>
public sealed class SystemSnapshot
{
    public DateTimeOffset TakenAt { get; set; }
    public string MachineName { get; set; } = "";
    public string UserName { get; set; } = "";
    public bool IsElevated { get; set; }

    public string? OsName { get; set; }
    public string? OsBuild { get; set; }
    public DateTime? LastUpdateInstalled { get; set; }
    public TimeSpan? Uptime { get; set; }
    public List<CrashEvent>? Crashes { get; set; }
    public List<DriveSample>? Drives { get; set; }
    public List<PhysicalDiskHealth>? PhysicalDisks { get; set; }
    public double? MemoryUsedPercent { get; set; }
    public double? TotalMemoryGb { get; set; }
    public double? CpuPercent { get; set; }
    public List<StartupItem>? StartupItems { get; set; }
    public List<DriverInfo>? Drivers { get; set; }
    public NetworkSample? Network { get; set; }

    /// <summary>True once the battery probe ran; <see cref="Battery"/> is null on desktops.</summary>
    public bool BatteryProbed { get; set; }
    public BatterySample? Battery { get; set; }
    public double? TemperatureC { get; set; }

    private readonly Dictionary<string, string> _probeErrors = new();

    /// <summary>Per-check probe errors (check id → message). A failed probe never throws.</summary>
    public IReadOnlyDictionary<string, string> ProbeErrors
    {
        get { lock (_probeErrors) return new Dictionary<string, string>(_probeErrors); }
    }

    public void AddProbeError(string checkId, string message)
    {
        lock (_probeErrors) _probeErrors[checkId] = message;
    }
}

public interface ISystemProbe
{
    Task<SystemSnapshot> CollectAsync(CancellationToken ct);
}

public interface IHealthRule
{
    string CheckId { get; }
    string Title { get; }
    Finding Evaluate(SystemSnapshot snapshot);
}

public static class CheckIds
{
    public const string Updates = "H1", Uptime = "H2", Crashes = "H3", DiskSpace = "H4", DiskHealth = "H5",
        MemoryCpu = "H6", Startup = "H7", Drivers = "H8", Network = "H9", Battery = "H10", Temperature = "H11";
}

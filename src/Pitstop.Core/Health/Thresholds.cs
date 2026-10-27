namespace Pitstop.Core.Health;

/// <summary>Every threshold the rules use, in one place. Values are (Watch, Action).</summary>
public static class Thresholds
{
    public static readonly (int Watch, int Action) DaysSinceUpdate = (30, 60);
    public static readonly (int Watch, int Action) UptimeDays = (7, 21);
    public static readonly (int Watch, int Action) CrashesIn30Days = (1, 2);
    public static readonly (double Watch, double Action) FreeDiskPercent = (15, 8);
    public const double MemoryWatchPercent = 85;
    public const double CpuWatchPercent = 90;
    public static readonly (int Watch, int Action) StartupItems = (10, 20);
    public const int DriverWatchYears = 2;
    public const double LatencyWatchMs = 80;
    /// <summary>With 20 pings, one lost reply (5%) is Watch; it takes 3 or more (15%) for Action (BUG-003).</summary>
    public static readonly (double Watch, double Action) PacketLossPercent = (5, 15);
    public const double DnsWatchMs = 150;
    public static readonly (double Watch, double Action) BatteryHealthPercent = (80, 60);
    public static readonly (double Watch, double Action) TemperatureC = (80, 90);
}

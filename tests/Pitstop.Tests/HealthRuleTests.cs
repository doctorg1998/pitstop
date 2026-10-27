using Pitstop.Core.Health;

namespace Pitstop.Tests;

public class HealthRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static SystemSnapshot Snap(Action<SystemSnapshot> setup)
    {
        var s = new SystemSnapshot { TakenAt = Now };
        setup(s);
        return s;
    }

    [Theory]
    [InlineData(5, Severity.Ok)]
    [InlineData(30, Severity.Watch)]
    [InlineData(60, Severity.Action)]
    public void Updates_graded_by_age(int daysAgo, Severity expected) =>
        Assert.Equal(expected, new UpdatesRule().Evaluate(Snap(s => s.LastUpdateInstalled = Now.DateTime.AddDays(-daysAgo))).Severity);

    [Theory]
    [InlineData(1.5, Severity.Ok)]
    [InlineData(8, Severity.Watch)]
    [InlineData(30, Severity.Action)]
    public void Uptime_graded_by_days(double days, Severity expected) =>
        Assert.Equal(expected, new UptimeRule().Evaluate(Snap(s => s.Uptime = TimeSpan.FromDays(days))).Severity);

    [Fact]
    public void Crashes_older_than_30_days_are_ignored()
    {
        var f = new CrashesRule().Evaluate(Snap(s => s.Crashes = new()
        {
            new(Now.AddDays(-45), "Blue screen"),
            new(Now.AddDays(-3), "Unexpected shutdown"),
        }));

        Assert.Equal(Severity.Watch, f.Severity);
        Assert.Contains("1 in the last 30 days", f.Summary);
    }

    [Fact]
    public void Two_recent_crashes_need_action() =>
        Assert.Equal(Severity.Action, new CrashesRule().Evaluate(Snap(s => s.Crashes = new()
        {
            new(Now.AddDays(-1), "Blue screen"), new(Now.AddDays(-2), "Blue screen"),
        })).Severity);

    [Fact]
    public void Disk_space_uses_the_fullest_drive()
    {
        var f = new DiskSpaceRule().Evaluate(Snap(s => s.Drives = new()
        {
            new("C:", 500_000_000_000, 200_000_000_000), // 40% free
            new("D:", 1_000_000_000_000, 50_000_000_000), // 5% free
        }));

        Assert.Equal(Severity.Action, f.Severity);
        Assert.Contains("D:", f.WhatToDo);
    }

    [Theory]
    [InlineData("Healthy", Severity.Ok)]
    [InlineData("Warning", Severity.Watch)]
    [InlineData("Unhealthy", Severity.Action)]
    public void Disk_health_takes_worst_disk(string status, Severity expected) =>
        Assert.Equal(expected, new DiskHealthRule().Evaluate(Snap(s => s.PhysicalDisks = new()
        {
            new("Samsung SSD", "SSD", "Healthy"), new("Seagate", "HDD", status),
        })).Severity);

    [Fact]
    public void Memory_or_cpu_over_threshold_is_watch()
    {
        Assert.Equal(Severity.Ok, new MemoryCpuRule().Evaluate(Snap(s => { s.MemoryUsedPercent = 50; s.CpuPercent = 10; })).Severity);
        Assert.Equal(Severity.Watch, new MemoryCpuRule().Evaluate(Snap(s => { s.MemoryUsedPercent = 91; s.CpuPercent = 10; })).Severity);
        Assert.Equal(Severity.Watch, new MemoryCpuRule().Evaluate(Snap(s => s.CpuPercent = 95)).Severity);
    }

    [Theory]
    [InlineData(4, Severity.Ok)]
    [InlineData(12, Severity.Watch)]
    [InlineData(25, Severity.Action)]
    public void Startup_graded_by_count(int count, Severity expected) =>
        Assert.Equal(expected, new StartupRule().Evaluate(Snap(s =>
            s.StartupItems = Enumerable.Range(0, count).Select(i => new StartupItem($"App{i}", "HKCU Run")).ToList())).Severity);

    [Fact]
    public void Old_vendor_driver_is_flagged_by_name()
    {
        var f = new DriversRule().Evaluate(Snap(s => s.Drivers = new()
        {
            new("NVIDIA GeForce RTX 3070", "Display", "NVIDIA", Now.DateTime.AddMonths(-3)),
            new("Realtek Audio", "Audio", "Realtek", new DateTime(2021, 3, 1)),
        }));

        Assert.Equal(Severity.Watch, f.Severity);
        Assert.Contains("Realtek Audio", f.Summary);
        Assert.DoesNotContain("NVIDIA", f.Summary);
    }

    [Fact]
    public void Network_loss_at_router_suggests_wifi_fix()
    {
        var f = new NetworkRule().Evaluate(Snap(s => s.Network = new(12, 3, 20, 40, 20)));

        Assert.Equal(Severity.Action, f.Severity);
        Assert.Contains("closer to the router", f.WhatToDo);
    }

    [Fact]
    public void Network_loss_only_upstream_points_to_provider()
    {
        var f = new NetworkRule().Evaluate(Snap(s => s.Network = new(12, 2, 0, 35, 5)));

        Assert.Equal(Severity.Watch, f.Severity);
        Assert.Contains("internet provider", f.WhatToDo);
    }

    // BUG-003: one dropped ping out of 20 is normal noise, not a red flag.
    [Theory]
    [InlineData(1, Severity.Watch)]
    [InlineData(2, Severity.Watch)]
    [InlineData(3, Severity.Action)]
    public void Single_dropped_ping_is_not_action(int lostOf20, Severity expected)
    {
        var (loss, avg) = Parsers.PingStats(Enumerable.Range(0, 20).Select(i => i < lostOf20 ? (long?)null : 9).ToList());
        var f = new NetworkRule().Evaluate(Snap(s => s.Network = new(10, 3, 0, avg, loss)));
        Assert.Equal(expected, f.Severity);
    }

    [Fact]
    public void Network_with_no_replies_is_offline() =>
        Assert.Equal(Severity.Action, new NetworkRule().Evaluate(Snap(s => s.Network = new(null, null, null, null, null))).Severity);

    [Fact]
    public void Healthy_network_is_ok() =>
        Assert.Equal(Severity.Ok, new NetworkRule().Evaluate(Snap(s => s.Network = new(15, 2, 0, 22, 0))).Severity);

    [Theory]
    [InlineData(50000, 47000, Severity.Ok)]
    [InlineData(50000, 36000, Severity.Watch)]
    [InlineData(50000, 25000, Severity.Action)]
    public void Battery_graded_by_wear(long design, long full, Severity expected) =>
        Assert.Equal(expected, new BatteryRule().Evaluate(Snap(s => { s.BatteryProbed = true; s.Battery = new(design, full); })).Severity);

    // Report review: a desktop's battery check must not inflate the "Healthy" count.
    [Fact]
    public void Desktop_without_battery_is_not_applicable()
    {
        var f = new BatteryRule().Evaluate(Snap(s => s.BatteryProbed = true));
        Assert.Equal(Severity.NotApplicable, f.Severity);
        Assert.Contains("desktop", f.Summary);
    }

    [Fact]
    public void Temperature_without_admin_is_skipped_and_says_how_to_enable()
    {
        var f = new TemperatureRule().Evaluate(Snap(s => s.IsElevated = false));
        Assert.Equal(Severity.Skipped, f.Severity);
        Assert.Contains("administrator", f.Summary);
        Assert.Contains("Run as administrator", f.WhatToDo);
    }

    [Theory]
    [InlineData(55, Severity.Ok)]
    [InlineData(82, Severity.Watch)]
    [InlineData(95, Severity.Action)]
    public void Temperature_graded_when_elevated(double celsius, Severity expected) =>
        Assert.Equal(expected, new TemperatureRule().Evaluate(Snap(s => { s.IsElevated = true; s.TemperatureC = celsius; })).Severity);

    [Fact]
    public void Temperature_elevated_but_not_exposed_explains_firmware()
    {
        var f = new TemperatureRule().Evaluate(Snap(s => s.IsElevated = true));
        Assert.Equal(Severity.Skipped, f.Severity);
        Assert.Contains("firmware", f.Summary);
    }

    [Fact]
    public void Disk_space_shows_percent_and_has_no_double_colon()
    {
        var f = new DiskSpaceRule().Evaluate(Snap(s => s.Drives = new() { new("C:", 1_023_000_000_000, 134_000_000_000) }));
        Assert.Contains("(13%)", f.Summary);
        Assert.DoesNotContain("::", f.WhatToDo);
    }

    [Fact]
    public void Network_summary_starts_with_capital() =>
        Assert.StartsWith("Router", new NetworkRule().Evaluate(Snap(s => s.Network = new(15, 2, 0, 22, 0))).Summary);

    [Fact]
    public void Power_loss_only_mentions_power_button_case()
    {
        var f = new CrashesRule().Evaluate(Snap(s => s.Crashes = new() { new(Now.AddDays(-3), Parsers.PowerLoss) }));
        Assert.Contains("power button", f.WhatToDo);

        var bsod = new CrashesRule().Evaluate(Snap(s => s.Crashes = new() { new(Now.AddDays(-3), Parsers.BlueScreen) }));
        Assert.DoesNotContain("power button", bsod.WhatToDo);
    }

    [Fact]
    public void Probe_error_becomes_skipped_finding()
    {
        var snap = Snap(s => s.Uptime = TimeSpan.FromDays(1));
        snap.AddProbeError(CheckIds.Uptime, "Access denied");

        var f = new UptimeRule().Evaluate(snap);

        Assert.Equal(Severity.Skipped, f.Severity);
        Assert.Contains("Access denied", f.Summary);
    }

    [Fact]
    public void Every_rule_skips_cleanly_on_an_empty_snapshot()
    {
        var empty = new SystemSnapshot { TakenAt = Now, IsElevated = true };
        foreach (var rule in DefaultRules.All)
            Assert.Equal(Severity.Skipped, rule.Evaluate(empty).Severity);
    }

    [Fact]
    public void Rule_ids_are_unique_and_cover_H1_to_H11() =>
        Assert.Equal(Enumerable.Range(1, 11).Select(i => $"H{i}"), DefaultRules.All.Select(r => r.CheckId).OrderBy(id => int.Parse(id[1..])));
}

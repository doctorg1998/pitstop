using Pitstop.Core.Health;

namespace Pitstop.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("20230512000000.******+***", 2023, 5, 12)]
    [InlineData("20210301", 2021, 3, 1)]
    public void CimDate_parses_wmi_dates(string raw, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), Parsers.CimDate(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage!")]
    public void CimDate_rejects_bad_input(string? raw) => Assert.Null(Parsers.CimDate(raw));

    [Theory]
    [InlineData("10/14/2026", 2026, 10, 14)]
    [InlineData("3/5/2025", 2025, 3, 5)]
    public void QfeDate_parses_us_format(string raw, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), Parsers.QfeDate(raw));

    [Fact]
    public void QfeDate_parses_hex_filetime()
    {
        var expected = new DateTime(2026, 1, 15);
        var hex = expected.ToFileTimeUtc().ToString("x");
        Assert.Equal(expected, Parsers.QfeDate(hex));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0 }, true)]
    public void StartupApproved_odd_first_byte_is_disabled(byte[] data, bool disabled) =>
        Assert.Equal(disabled, Parsers.StartupApprovedIsDisabled(data));

    [Fact]
    public void StartupApproved_missing_entry_counts_as_enabled() =>
        Assert.False(Parsers.StartupApprovedIsDisabled(null));

    [Fact]
    public void BatteryReport_reads_recorded_powercfg_xml()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "battery-report.xml"));
        var b = Parsers.BatteryReport(xml);
        Assert.Equal(new BatterySample(57000, 44460), b);
    }

    [Fact]
    public void BatteryReport_desktop_has_no_battery() =>
        Assert.Null(Parsers.BatteryReport("""<BatteryReport xmlns="http://schemas.microsoft.com/battery/2012"><Batteries /></BatteryReport>"""));

    [Theory]
    [InlineData("Windows 10 Home", 26200, "Windows 11 Home")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    public void OsDisplayName_corrects_windows_11(string product, int build, string expected) =>
        Assert.Equal(expected, Parsers.OsDisplayName(product, build));

    [Fact]
    public void PingStats_computes_loss_and_average_of_replies()
    {
        var (loss, avg) = Parsers.PingStats(new long?[] { 10, 20, null, 30 });
        Assert.Equal(25, loss);
        Assert.Equal(20, avg);
    }

    [Fact]
    public void One_blue_screen_logging_three_events_counts_once()
    {
        var boot = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var merged = Parsers.MergeCrashEvents(new[]
        {
            new CrashEvent(boot, Parsers.PowerLoss),
            new CrashEvent(boot.AddSeconds(5), Parsers.UnexpectedShutdown),
            new CrashEvent(boot.AddMinutes(1), Parsers.BlueScreen),
        });

        var crash = Assert.Single(merged);
        Assert.Equal(Parsers.BlueScreen, crash.Kind);
    }

    [Fact]
    public void Crashes_on_different_days_stay_separate()
    {
        var day = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var merged = Parsers.MergeCrashEvents(new[]
        {
            new CrashEvent(day.AddDays(2), Parsers.PowerLoss),
            new CrashEvent(day, Parsers.UnexpectedShutdown),
        });

        Assert.Equal(2, merged.Count);
        Assert.True(merged[0].At < merged[1].At);
    }

    // Device IDs shaped like those recorded on the test PC (BUG-002); vendor/device numbers anonymized.
    [Theory]
    [InlineData(@"ROOT\MEDIA\0003", true)]                  // virtual audio device
    [InlineData(@"ROOT\VIRTUALSPEAKERS\0000", true)]         // game-streaming virtual speakers
    [InlineData(@"SWD\WINTUN\{00000000-0000-0000}", true)]   // VPN tunnel adapter
    [InlineData(@"PCI\VEN_1234&DEV_5678&SUBSYS_0", false)]   // discrete graphics card
    [InlineData(@"HDAUDIO\FUNC_01&VEN_1234&DEV_0", false)]   // onboard audio
    [InlineData(@"USB\VID_1234&PID_0001", false)]            // USB headset
    public void IsVirtualDevice_separates_software_devices_from_hardware(string id, bool expected) =>
        Assert.Equal(expected, Parsers.IsVirtualDevice(id));

    // Names shaped like those found on the test PC (BUG-001): a trailing space and a per-install hash.
    [Theory]
    [InlineData("GPU Companion ", "GPU Companion")]
    [InlineData("BrowserAutoLaunch_0F1E2D3C4B5A69788796A5B4C3D2E1F0", "BrowserAutoLaunch")]
    [InlineData("My_App", "My_App")]
    [InlineData("VPN Client", "VPN Client")]
    public void CleanStartupName_trims_and_drops_hash_suffix(string raw, string expected) =>
        Assert.Equal(expected, Parsers.CleanStartupName(raw));

    [Fact]
    public void PingStats_all_lost() =>
        Assert.Equal((100.0, (double?)null), Parsers.PingStats(new long?[] { null, null }));
}

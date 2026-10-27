using Pitstop.Core.Health;

namespace Pitstop.Tests;

public class HealthCheckRunnerTests
{
    private sealed class FakeProbe(SystemSnapshot snapshot) : ISystemProbe
    {
        public Task<SystemSnapshot> CollectAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class ThrowingRule : IHealthRule
    {
        public string CheckId => "H99";
        public string Title => "Broken";
        public Finding Evaluate(SystemSnapshot s) => throw new InvalidOperationException("boom");
    }

    private static SystemSnapshot Snapshot() => new()
    {
        TakenAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
        MachineName = "GIO-RIG",
        UserName = "Gio",
        StartupItems = new() { new("Updater", @"C:\Users\Gio\AppData\Local\Updater.exe") },
        Drives = new() { new("C:", 100, 2) },
    };

    [Fact]
    public async Task A_throwing_rule_is_reported_as_skipped_not_crashed()
    {
        var report = await new HealthCheckRunner(new FakeProbe(Snapshot()), new IHealthRule[] { new ThrowingRule() })
            .RunAsync(includeMachineName: true);

        var f = Assert.Single(report.Findings);
        Assert.Equal(Severity.Skipped, f.Severity);
        Assert.Contains("InvalidOperationException", f.Summary);
    }

    [Fact]
    public async Task Overall_is_worst_finding()
    {
        var report = await new HealthCheckRunner(new FakeProbe(Snapshot())).RunAsync(includeMachineName: true);

        Assert.Equal(Severity.Action, report.Overall); // C: is 2% free
        Assert.Equal(11, report.Findings.Count);
    }

    [Fact]
    public async Task Report_is_redacted_by_default()
    {
        var report = await new HealthCheckRunner(new FakeProbe(Snapshot())).RunAsync(includeMachineName: false);
        var json = ReportWriter.ToJson(report);

        Assert.DoesNotContain("GIO-RIG", json);
        Assert.DoesNotContain(@"Users\Gio", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Redactor.PcToken, report.Machine);
    }

    [Fact]
    public async Task Opt_in_keeps_machine_name()
    {
        var report = await new HealthCheckRunner(new FakeProbe(Snapshot())).RunAsync(includeMachineName: true);
        Assert.Equal("GIO-RIG", report.Machine);
    }

    [Fact]
    public void Html_escapes_untrusted_text_and_makes_no_external_requests()
    {
        var report = new HealthReport(DateTimeOffset.Now, "<pc>", TimeSpan.FromSeconds(4), new[]
        {
            new Finding("H7", "Startup apps", Severity.Watch, "<script>alert(1)</script>", "x", "y"),
        });

        var html = ReportWriter.ToHtml(report);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Html_footer_discloses_dns_and_pings_and_has_print_theme()
    {
        var html = ReportWriter.ToHtml(new HealthReport(DateTimeOffset.Now, "<pc>", TimeSpan.Zero, Array.Empty<Finding>()));
        Assert.Contains("DNS lookups and pings", html);
        Assert.Contains("@media print", html);
    }

    [Fact]
    public void Not_applicable_is_labelled_and_not_counted_healthy()
    {
        var report = new HealthReport(DateTimeOffset.Now, "<pc>", TimeSpan.Zero, new[]
        {
            new Finding("H10", "Battery wear", Severity.NotApplicable, "No battery (desktop PC).", "b", "c"),
        });

        Assert.Equal(0, report.Count(Severity.Ok));
        Assert.Equal(Severity.Ok, report.Overall);
        Assert.Contains(">N/A<", ReportWriter.ToHtml(report));
    }

    [Fact]
    public void Html_lists_action_items_first()
    {
        var report = new HealthReport(DateTimeOffset.Now, "<pc>", TimeSpan.Zero, new[]
        {
            new Finding("H1", "Updates", Severity.Ok, "a", "b", "c"),
            new Finding("H4", "Disk space", Severity.Action, "a", "b", "c"),
        });

        var html = ReportWriter.ToHtml(report);

        Assert.True(html.IndexOf("Disk space", StringComparison.Ordinal) < html.IndexOf(">Updates<", StringComparison.Ordinal));
    }
}

using Pitstop.Core.Digest;
using Pitstop.Core.Health;
using Pitstop.Core.Profiles;

namespace Pitstop.Tests;

public class DigestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);

    private static SessionRecord S(string name, double startDaysAgo, double hours) =>
        new(name.ToLowerInvariant(), name, Now.AddDays(-startDaysAgo), Now.AddDays(-startDaysAgo).AddHours(hours), "test");

    [Fact]
    public void Sums_time_per_profile_and_orders_by_most_time()
    {
        var d = DigestBuilder.Build(new[] { S("Work", 3, 2), S("Play", 2, 3), S("Work", 1, 0.5) }, Array.Empty<HealthSummary>(), Now);

        Assert.Equal(new[] { "Play", "Work" }, d.TimePerProfile.Select(p => p.ProfileName));
        Assert.Equal(TimeSpan.FromHours(2.5), d.TimePerProfile[1].Time);
        Assert.Equal(2, d.TimePerProfile[1].Sessions);
        Assert.Equal(TimeSpan.FromHours(5.5), d.TotalProfileTime);
    }

    [Fact]
    public void Session_crossing_the_window_start_is_clipped()
    {
        // Started 7 days + 1 hour ago and lasted 3 hours: only 2 hours fall inside the 7-day window.
        var d = DigestBuilder.Build(new[] { S("Play", 7 + 1.0 / 24, 3) }, Array.Empty<HealthSummary>(), Now);
        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(d.TimePerProfile).Time);
    }

    [Fact]
    public void Sessions_outside_the_window_are_ignored()
    {
        var d = DigestBuilder.Build(new[] { S("Play", 10, 2) }, Array.Empty<HealthSummary>(), Now);
        Assert.Empty(d.TimePerProfile);
    }

    [Fact]
    public void Health_trend_keeps_latest_four_oldest_first()
    {
        var reports = Enumerable.Range(1, 6).Select(i => new HealthSummary(Now.AddDays(-i), Severity.Ok, 0, 0, Array.Empty<string>()));
        var d = DigestBuilder.Build(Array.Empty<SessionRecord>(), reports, Now);

        Assert.Equal(4, d.HealthTrend.Count);
        Assert.Equal(Now.AddDays(-4), d.HealthTrend[0].TakenAt);
        Assert.Equal(Now.AddDays(-1), d.HealthTrend[^1].TakenAt);
    }

    [Fact]
    public void Reads_back_a_report_written_by_ReportWriter()
    {
        var report = new HealthReport(Now, "<pc>", TimeSpan.FromSeconds(5), new[]
        {
            new Finding("H4", "Disk space", Severity.Watch, "a", "b", "c"),
            new Finding("H3", "Crashes", Severity.Action, "a", "b", "c"),
            new Finding("H1", "Windows updates", Severity.Ok, "a", "b", "c"),
        });

        var summary = DigestBuilder.ReadReport(ReportWriter.ToJson(report));

        Assert.NotNull(summary);
        Assert.Equal(Severity.Action, summary!.Overall);
        Assert.Equal(1, summary.Action);
        Assert.Equal(1, summary.Watch);
        Assert.Equal(new[] { "Disk space", "Crashes" }, summary.OpenItems);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "takenAt": "2026-10-27T00:00:00Z", "overall": "Sideways", "findings": [] }""")]
    public void Unreadable_report_is_skipped(string json) => Assert.Null(DigestBuilder.ReadReport(json));

    [Fact]
    public void Html_handles_empty_week_and_escapes_profile_names()
    {
        var empty = DigestBuilder.ToHtml(DigestBuilder.Build(Array.Empty<SessionRecord>(), Array.Empty<HealthSummary>(), Now));
        Assert.Contains("No profile sessions this week", empty);
        Assert.Contains("No Health Checks yet", empty);

        var html = DigestBuilder.ToHtml(DigestBuilder.Build(new[] { S("<b>Play</b>", 1, 1) }, Array.Empty<HealthSummary>(), Now));
        Assert.DoesNotContain("<b>Play</b>", html);
        Assert.DoesNotContain("https://", html);
    }
}

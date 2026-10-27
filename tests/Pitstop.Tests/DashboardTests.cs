using Pitstop.Core.Dashboard;
using Pitstop.Core.Health;

namespace Pitstop.Tests;

public class HealthScoreTests
{
    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(0, 3, 82)]   // the dev PC's real result: 3 Watch
    [InlineData(1, 0, 80)]
    [InlineData(2, 3, 42)]
    [InlineData(6, 4, 0)]    // never below 0
    public void Score_from_counts(int action, int watch, int expected) =>
        Assert.Equal(expected, HealthScore.FromCounts(action, watch));

    [Fact]
    public void One_action_outweighs_three_watch_items() =>
        Assert.True(HealthScore.FromCounts(1, 0) < HealthScore.FromCounts(0, 3));

    [Fact]
    public void Skipped_and_not_applicable_do_not_cost_points()
    {
        var findings = new[]
        {
            new Finding("H10", "Battery", Severity.NotApplicable, "", "", ""),
            new Finding("H11", "Temperature", Severity.Skipped, "", "", ""),
            new Finding("H1", "Updates", Severity.Ok, "", "", ""),
        };
        Assert.Equal(100, HealthScore.Compute(findings));
    }

    [Theory]
    [InlineData(100, "Excellent")]
    [InlineData(82, "Good")]
    [InlineData(60, "Fair")]
    [InlineData(10, "Needs attention")]
    public void Grade_labels(int score, string grade) => Assert.Equal(grade, HealthScore.Grade(score));
}

public class DashboardCommandParserTests
{
    [Theory]
    [InlineData("""{"type":"runCheck"}""", "runCheck")]
    [InlineData("""{"type":"ready"}""", "ready")]
    [InlineData("""{"type":"openConfig","extra":1}""", "openConfig")]
    public void Accepts_simple_commands(string json, string type) =>
        Assert.Equal(type, DashboardCommandParser.Parse(json, out _)?.Type);

    [Fact]
    public void Accepts_toggle_with_valid_profile_id()
    {
        var cmd = DashboardCommandParser.Parse("""{"type":"toggleProfile","id":"play"}""", out _);
        Assert.Equal("play", cmd?.ProfileId);
    }

    [Fact]
    public void Accepts_boolean_setting()
    {
        var cmd = DashboardCommandParser.Parse("""{"type":"setAutostart","on":true}""", out _);
        Assert.True(cmd?.On);
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("not json", "invalid JSON")]
    [InlineData("[1,2]", "not an object")]
    [InlineData("""{"kind":"runCheck"}""", "missing type")]
    [InlineData("""{"type":"deleteEverything"}""", "unknown type")]
    [InlineData("""{"type":"toggleProfile","id":"../../etc"}""", "invalid profile id")]
    [InlineData("""{"type":"toggleProfile","id":42}""", "invalid profile id")]
    [InlineData("""{"type":"setAutostart","on":"yes"}""", "missing on/off")]
    [InlineData("""{"type":"openUrl","url":"https://evil.example/pitstop"}""", "not allowed")]
    [InlineData("""{"type":"openUrl","url":"http://github.com/doctorg1998/pitstop"}""", "not allowed")]
    [InlineData("""{"type":"openUrl","url":"https://github.com/doctorg1998/pitstop-evil"}""", "not allowed")]
    [InlineData("""{"type":"openUrl","url":"file:///C:/Windows/System32/calc.exe"}""", "not allowed")]
    public void Rejects_bad_messages_with_a_reason(string? json, string reason)
    {
        Assert.Null(DashboardCommandParser.Parse(json, out var why));
        Assert.Contains(reason, why);
    }

    [Fact]
    public void Rejects_oversized_message()
    {
        var json = "{\"type\":\"runCheck\",\"pad\":\"" + new string('x', DashboardCommandParser.MaxMessageLength) + "\"}";
        Assert.Null(DashboardCommandParser.Parse(json, out var why));
        Assert.Contains("too large", why);
    }

    [Theory]
    [InlineData("https://github.com/doctorg1998/pitstop")]
    [InlineData("https://github.com/doctorg1998/pitstop/blob/main/docs/BUGS.md")]
    [InlineData("https://doctorg1998.github.io")]
    public void Allows_project_links(string url) => Assert.True(DashboardCommandParser.IsAllowedUrl(url));
}

public class ReportRoundTripTests
{
    [Fact]
    public void Findings_survive_json_round_trip()
    {
        var report = new HealthReport(new DateTimeOffset(2026, 10, 27, 10, 6, 0, TimeSpan.Zero), "<pc>", TimeSpan.FromSeconds(5), new[]
        {
            new Finding("H4", "Disk space", Severity.Watch, "C: 13% free", "why", "todo"),
            new Finding("H10", "Battery wear", Severity.NotApplicable, "No battery", "why", "todo"),
        });

        var back = ReportWriter.FromJson(ReportWriter.ToJson(report));

        Assert.NotNull(back);
        Assert.Equal(report.TakenAt, back!.Value.TakenAt);
        Assert.Equal(report.Findings, back.Value.Findings);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("{}")]
    public void Unreadable_report_returns_null(string json) => Assert.Null(ReportWriter.FromJson(json));
}

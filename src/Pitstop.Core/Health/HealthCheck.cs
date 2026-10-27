using System.Diagnostics;

namespace Pitstop.Core.Health;

public sealed record HealthReport(
    DateTimeOffset TakenAt,
    string Machine,
    TimeSpan Duration,
    IReadOnlyList<Finding> Findings)
{
    public Severity Overall =>
        Findings.Any(f => f.Severity == Severity.Action) ? Severity.Action
        : Findings.Any(f => f.Severity == Severity.Watch) ? Severity.Watch : Severity.Ok;

    public int Count(Severity s) => Findings.Count(f => f.Severity == s);
}

public sealed class HealthCheckRunner
{
    private readonly ISystemProbe _probe;
    private readonly IReadOnlyList<IHealthRule> _rules;

    public HealthCheckRunner(ISystemProbe probe, IReadOnlyList<IHealthRule>? rules = null)
    {
        _probe = probe;
        _rules = rules ?? DefaultRules.All;
    }

    public async Task<HealthReport> RunAsync(bool includeMachineName, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var snapshot = await _probe.CollectAsync(ct).ConfigureAwait(false);
        var findings = _rules.Select(rule => EvaluateSafely(rule, snapshot)).ToList();
        sw.Stop();

        var report = new HealthReport(snapshot.TakenAt, snapshot.MachineName, sw.Elapsed, findings);
        return includeMachineName ? report : Redactor.Redact(report, snapshot.MachineName, snapshot.UserName);
    }

    private static Finding EvaluateSafely(IHealthRule rule, SystemSnapshot snapshot)
    {
        try { return rule.Evaluate(snapshot); }
        catch (Exception ex)
        {
            return new Finding(rule.CheckId, rule.Title, Severity.Skipped,
                $"This check hit an internal error ({ex.GetType().Name}).",
                "Pitstop couldn't check this on this PC.", "Nothing to do.");
        }
    }
}

/// <summary>Replaces the PC name and Windows username in every report string.</summary>
public static class Redactor
{
    public const string PcToken = "<pc>", UserToken = "<user>";

    public static HealthReport Redact(HealthReport report, string machineName, string userName)
    {
        string Clean(string text)
        {
            if (!string.IsNullOrEmpty(machineName))
                text = text.Replace(machineName, PcToken, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 2)
                text = text.Replace(userName, UserToken, StringComparison.OrdinalIgnoreCase);
            return text;
        }

        return report with
        {
            Machine = PcToken,
            Findings = report.Findings
                .Select(f => f with { Summary = Clean(f.Summary), WhatItMeans = Clean(f.WhatItMeans), WhatToDo = Clean(f.WhatToDo) })
                .ToList(),
        };
    }
}

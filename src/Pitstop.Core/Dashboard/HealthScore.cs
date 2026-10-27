using Pitstop.Core.Health;

namespace Pitstop.Core.Dashboard;

/// <summary>
/// A single 0–100 number for the dashboard gauge. Each Action costs 20 points and each Watch costs 6,
/// so one real problem always outweighs a few minor ones. Skipped and N/A checks don't count.
/// </summary>
public static class HealthScore
{
    public const int ActionPenalty = 20, WatchPenalty = 6;

    public static int FromCounts(int action, int watch) =>
        Math.Clamp(100 - ActionPenalty * action - WatchPenalty * watch, 0, 100);

    public static int Compute(IEnumerable<Finding> findings)
    {
        var list = findings.ToList();
        return FromCounts(list.Count(f => f.Severity == Severity.Action), list.Count(f => f.Severity == Severity.Watch));
    }

    public static string Grade(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 50 => "Fair",
        _ => "Needs attention",
    };
}

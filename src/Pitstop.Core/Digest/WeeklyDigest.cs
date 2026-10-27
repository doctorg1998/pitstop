using System.Net;
using System.Text;
using System.Text.Json;
using Pitstop.Core.Health;
using Pitstop.Core.Profiles;

namespace Pitstop.Core.Digest;

public sealed record ProfileTime(string ProfileName, TimeSpan Time, int Sessions);

/// <summary>Overall verdict and counts of one saved Health Check, read back from its JSON file.</summary>
public sealed record HealthSummary(DateTimeOffset TakenAt, Severity Overall, int Action, int Watch, IReadOnlyList<string> OpenItems);

public sealed record WeeklyDigest(
    DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<ProfileTime> TimePerProfile,
    IReadOnlyList<HealthSummary> HealthTrend)
{
    public TimeSpan TotalProfileTime => TimePerProfile.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Time);
}

public static class DigestBuilder
{
    /// <summary>
    /// Builds the digest for the 7 days ending at <paramref name="now"/>. Sessions that started before the
    /// window are clipped to it, so a session spanning midnight on day 7 counts only its in-window part.
    /// </summary>
    public static WeeklyDigest Build(IEnumerable<SessionRecord> sessions, IEnumerable<HealthSummary> reports, DateTimeOffset now)
    {
        var from = now.AddDays(-7);
        var perProfile = sessions
            .Where(s => s.End > from && s.Start < now)
            .GroupBy(s => s.ProfileName)
            .Select(g => new ProfileTime(g.Key,
                g.Aggregate(TimeSpan.Zero, (sum, s) => sum + (Min(s.End, now) - Max(s.Start, from))),
                g.Count()))
            .OrderByDescending(p => p.Time)
            .ToList();

        var trend = reports.OrderByDescending(r => r.TakenAt).Take(4).Reverse().ToList();
        return new WeeklyDigest(from, now, perProfile, trend);
    }

    /// <summary>Reads a report JSON written by <see cref="ReportWriter"/>. Returns null for files it can't read.</summary>
    public static HealthSummary? ReadReport(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var findings = root.GetProperty("findings").EnumerateArray().ToList();
            Severity Sev(JsonElement f) => Enum.Parse<Severity>(f.GetProperty("severity").GetString()!);
            return new HealthSummary(
                root.GetProperty("takenAt").GetDateTimeOffset(),
                Enum.Parse<Severity>(root.GetProperty("overall").GetString()!),
                findings.Count(f => Sev(f) == Severity.Action),
                findings.Count(f => Sev(f) == Severity.Watch),
                findings.Where(f => Sev(f) is Severity.Action or Severity.Watch)
                    .Select(f => f.GetProperty("title").GetString()!).ToList());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    public static string ToHtml(WeeklyDigest d)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)t.TotalMinutes} min";
        var max = d.TimePerProfile.Count > 0 ? d.TimePerProfile.Max(p => p.Time.TotalMinutes) : 1;

        var sb = new StringBuilder($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Pitstop Weekly Digest</title>
            <style>
            :root{--bg:#0b0f14;--panel:#121923;--line:#223042;--text:#d9e2ec;--muted:#8796a8;--ok:#3ddc97;--watch:#ffb547;--action:#ff5d5d;--accent:#4cc9f0}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.55 "IBM Plex Sans","Segoe UI",system-ui,sans-serif}
            main{max-width:880px;margin:0 auto;padding:32px 16px 48px}
            h1{font-family:"Chakra Petch","Segoe UI",sans-serif;letter-spacing:.04em;margin:0 0 4px;font-size:28px}
            h2{font-size:17px;margin:28px 0 12px}.meta{color:var(--muted);font-family:"JetBrains Mono",Consolas,monospace;font-size:13px}
            .panel{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
            .row{display:grid;grid-template-columns:minmax(90px,160px) 1fr auto;gap:12px;align-items:center;margin:8px 0}
            .bar{height:10px;border-radius:99px;background:var(--accent)}.num{font-family:"JetBrains Mono",Consolas,monospace;font-size:13px;color:var(--muted)}
            table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:8px 6px;border-bottom:1px solid var(--line);font-size:14px}
            th{color:var(--muted);font-weight:500}.ok{color:var(--ok)}.watch{color:var(--watch)}.action{color:var(--action)}
            .empty{color:var(--muted)}footer{margin-top:28px;color:var(--muted);font-size:12px}
            @media print{:root{--bg:#fff;--panel:#fff;--line:#c9d2dc;--text:#111;--muted:#4a5562;--accent:#0b5c80} }
            </style></head><body><main>
            <h1>PITSTOP // WEEKLY DIGEST</h1>
            <div class="meta">{{d.From:yyyy-MM-dd}} – {{d.To:yyyy-MM-dd}}</div>
            <h2>Time per profile <span class="num">· {{Dur(d.TotalProfileTime)}} total</span></h2>
            <div class="panel">
            """);

        if (d.TimePerProfile.Count == 0) sb.Append("<p class=\"empty\">No profile sessions this week.</p>");
        foreach (var p in d.TimePerProfile)
        {
            var pct = Math.Max(2, (int)Math.Round(100 * p.Time.TotalMinutes / max));
            sb.Append($"""<div class="row"><span>{E(p.ProfileName)}</span><div class="bar" style="width:{pct}%"></div><span class="num">{Dur(p.Time)} · {p.Sessions} session{(p.Sessions == 1 ? "" : "s")}</span></div>""");
        }
        sb.Append("</div><h2>Health trend (last 4 checks)</h2><div class=\"panel\">");

        if (d.HealthTrend.Count == 0) sb.Append("<p class=\"empty\">No Health Checks yet. Run one from the tray menu.</p>");
        else
        {
            sb.Append("<table><tr><th>Date</th><th>Overall</th><th>Action</th><th>Watch</th></tr>");
            foreach (var r in d.HealthTrend)
                sb.Append($"<tr><td>{r.TakenAt:yyyy-MM-dd HH:mm}</td><td class=\"{r.Overall.ToString().ToLowerInvariant()}\">{r.Overall.ToString().ToUpperInvariant()}</td><td>{r.Action}</td><td>{r.Watch}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</div><h2>Open items (from the latest check)</h2><div class=\"panel\">");

        var latest = d.HealthTrend.LastOrDefault();
        if (latest is null || latest.OpenItems.Count == 0) sb.Append("<p class=\"empty\">Nothing needs attention.</p>");
        else sb.Append("<ul>" + string.Concat(latest.OpenItems.Select(i => $"<li>{E(i)}</li>")) + "</ul>");

        sb.Append("</div><footer>Generated locally by Pitstop from your own session log and saved Health Check reports.</footer></main></body></html>");
        return sb.ToString();
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

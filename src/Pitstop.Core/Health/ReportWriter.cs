using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pitstop.Core.Storage;

namespace Pitstop.Core.Health;

/// <summary>Writes a report as a self-contained HTML page (no external requests) plus machine-readable JSON.</summary>
public sealed class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IFileStore _files;

    public ReportWriter(IFileStore files) => _files = files;

    /// <summary>Writes both files into <paramref name="folder"/> and returns the HTML path.</summary>
    public string Write(HealthReport report, string folder)
    {
        var stem = Path.Combine(folder, $"pitstop-health-{report.TakenAt:yyyyMMdd-HHmmss}");
        _files.WriteAllTextAtomic(stem + ".json", ToJson(report));
        _files.WriteAllTextAtomic(stem + ".html", ToHtml(report));
        return stem + ".html";
    }

    public static string ToJson(HealthReport report) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        report.TakenAt,
        report.Machine,
        durationSeconds = Math.Round(report.Duration.TotalSeconds, 1),
        overall = report.Overall,
        report.Findings,
    }, Json);

    public static string ToHtml(HealthReport report)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Badge(Severity s) => s switch
        {
            Severity.Ok => "ok", Severity.Watch => "watch", Severity.Action => "action", _ => "skip",
        };
        static string Label(Severity s) => s switch
        {
            Severity.Skipped => "SKIPPED", Severity.NotApplicable => "N/A", _ => s.ToString().ToUpperInvariant(),
        };

        var ordered = report.Findings
            .OrderBy(f => f.Severity switch
            {
                Severity.Action => 0, Severity.Watch => 1, Severity.Ok => 2, Severity.Skipped => 3, _ => 4,
            })
            .ThenBy(f => int.Parse(f.CheckId[1..]));

        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Pitstop Health Check</title>
            <style>
            :root{--bg:#0b0f14;--panel:#121923;--line:#223042;--text:#d9e2ec;--muted:#8796a8;--ok:#3ddc97;--watch:#ffb547;--action:#ff5d5d;--skip:#6b7a8c;--accent:#4cc9f0}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.55 "IBM Plex Sans","Segoe UI",system-ui,sans-serif}
            main{max-width:880px;margin:0 auto;padding:32px 16px 48px}
            h1{font-family:"Chakra Petch","Segoe UI",sans-serif;letter-spacing:.04em;margin:0 0 4px;font-size:28px}
            .meta{color:var(--muted);font-family:"JetBrains Mono",Consolas,monospace;font-size:13px}
            .summary{display:flex;gap:12px;flex-wrap:wrap;margin:20px 0 28px}
            .tile{flex:1 1 120px;background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
            .tile b{display:block;font-family:"Chakra Petch","Segoe UI",sans-serif;font-size:26px}
            .tile span{color:var(--muted);font-size:13px}
            .card{background:var(--panel);border:1px solid var(--line);border-left:4px solid var(--skip);border-radius:10px;padding:14px 16px;margin:0 0 12px}
            .card.ok{border-left-color:var(--ok)}.card.watch{border-left-color:var(--watch)}.card.action{border-left-color:var(--action)}
            .head{display:flex;justify-content:space-between;gap:12px;align-items:baseline}
            .head h2{font-size:16px;margin:0}.pill{font:600 11px "JetBrains Mono",Consolas,monospace;letter-spacing:.08em;padding:2px 8px;border-radius:99px;border:1px solid currentColor}
            .pill.ok{color:var(--ok)}.pill.watch{color:var(--watch)}.pill.action{color:var(--action)}.pill.skip{color:var(--skip)}
            .card p{margin:6px 0 0}.why{color:var(--muted);font-size:14px}.todo b{color:var(--accent);font-weight:600}
            footer{margin-top:28px;color:var(--muted);font-size:12px}
            @media print{
            :root{--bg:#fff;--panel:#fff;--line:#c9d2dc;--text:#111;--muted:#4a5562;--ok:#1a7f4b;--watch:#a35f00;--action:#b42318;--skip:#5b6673;--accent:#0b5c80}
            main{padding:0}.card,.tile{break-inside:avoid}
            }
            </style></head><body><main>
            <h1>PITSTOP // HEALTH CHECK</h1>
            <div class="meta">{{E(report.Machine)}} · {{report.TakenAt:yyyy-MM-dd HH:mm}} · checked in {{report.Duration.TotalSeconds:0.0}} s</div>
            <div class="summary">
            <div class="tile"><b style="color:var(--{{Badge(report.Overall)}})">{{Label(report.Overall)}}</b><span>Overall</span></div>
            <div class="tile"><b>{{report.Count(Severity.Action)}}</b><span>Need action</span></div>
            <div class="tile"><b>{{report.Count(Severity.Watch)}}</b><span>Worth watching</span></div>
            <div class="tile"><b>{{report.Count(Severity.Ok)}}</b><span>Healthy</span></div>
            </div>

            """);

        foreach (var f in ordered)
        {
            var b = Badge(f.Severity);
            sb.Append($"""
                <section class="card {b}"><div class="head"><h2>{E(f.Title)}</h2><span class="pill {b}">{Label(f.Severity)}</span></div>
                <p>{E(f.Summary)}</p><p class="why">{E(f.WhatItMeans)}</p><p class="todo"><b>What to do:</b> {E(f.WhatToDo)}</p></section>

                """);
        }

        sb.Append("""
            <footer>Generated locally by Pitstop. Read-only: nothing on this PC was changed. The only network traffic was the connection test (DNS lookups and pings); no data about this PC was sent anywhere.</footer>
            </main></body></html>
            """);
        return sb.ToString();
    }
}

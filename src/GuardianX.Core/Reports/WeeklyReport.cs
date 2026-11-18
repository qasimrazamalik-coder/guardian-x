using System.Globalization;
using System.Net;
using System.Text;
using GuardianX.Core.Assistant;
using GuardianX.Core.Data;
using GuardianX.Core.Insights;

namespace GuardianX.Core.Reports;

/// <summary>A one-page weekly battery report as a self-contained HTML file (no scripts, no external files).</summary>
public static class WeeklyReport
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    public static string Folder => Path.Combine(Store.DefaultFolder, "reports");

    public static string Write(Store store, AssistantContext c, DateTime? now = null)
    {
        var day = (now ?? DateTime.Now).Date;
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"guardian-x-week-{day:yyyy-MM-dd}.html");
        File.WriteAllText(path, Build(store, c, day));
        return path;
    }

    public static string Build(Store store, AssistantContext c, DateTime today)
    {
        // The last seven days including today, so a report made mid-week shows what's happened so far.
        var from = today.AddDays(-6);
        var days = new List<(DateTime Day, StressReport Stress)>();
        for (int i = 6; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            var off = new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d));
            var samples = store.Samples(off, off.AddDays(1));
            if (samples.Count > 0) days.Add((d, BatteryInsights.Stress(samples)));
        }
        var daily = c.Daily;
        var weekAgo = daily.LastOrDefault(d => d.Day <= DateOnly.FromDateTime(from));
        int cyclesThisWeek = c.Battery is { } b && weekAgo.Cycles > 0 ? Math.Max(0, b.Cycles - weekAgo.Cycles) : 0;
        var sleeps = BatteryInsights.Sleeps(store.Events(new DateTimeOffset(from))).Where(s => s.Start >= from).ToList();
        var tips = c.Wear is null ? new List<Tip>() :
            BatteryInsights.Coach(c.Wear, c.Today ?? new StressReport(0, "Low", 0, 0, 0, false), c.HoldMinutes, c.LastSleep, c.GpuWeek, c.ChargerHoursPerDay);

        var sb = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        sb.Append("""
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Guardian X weekly report</title><style>
:root{--bg:#05080d;--card:#0b1118;--line:#1b2632;--ink:#e8edf2;--muted:#8a97a6;--mint:#3dffb0;--cyan:#3de0ff;--amber:#ffb547;--red:#ff5d6c}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.55 "Segoe UI",system-ui,sans-serif;padding:40px}
.wrap{max-width:960px;margin:0 auto}h1{font-size:28px;margin:0}h2{font-size:13px;letter-spacing:.14em;text-transform:uppercase;color:var(--muted);margin:0 0 14px;font-weight:600}
.sub{color:var(--muted);margin:4px 0 28px}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:18px}.big{font-size:30px;font-weight:600;font-variant-numeric:tabular-nums}
.k{color:var(--muted);font-size:12px;letter-spacing:.08em;text-transform:uppercase}.sec{margin-top:18px}
table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}td,th{padding:8px 6px;border-bottom:1px solid var(--line);text-align:left}th{color:var(--muted);font-weight:500;font-size:12px}
.good{color:var(--mint)}.warn{color:var(--amber)}.bad{color:var(--red)}li{margin:6px 0}footer{color:var(--muted);font-size:12px;margin-top:28px}
@media(max-width:760px){.grid{grid-template-columns:repeat(2,1fr)}body{padding:18px}}
@media print{body{background:#fff;color:#000}.card{background:#fff;border-color:#ccc}}
</style></head><body><div class="wrap">
""");
        sb.Append($"<h1>Battery report</h1><div class=\"sub\">{from.ToString("d MMM", En)} – {today.ToString("d MMM yyyy", En)} · {E(Environment.MachineName)}</div>");

        var w = c.Wear;
        string Health() => w is null ? "—" : $"{w.HealthPercent:0.0}%";
        string Cls(double v, double warn, double bad) => v >= bad ? "bad" : v >= warn ? "warn" : "good";
        sb.Append("<div class=\"grid\">");
        sb.Append($"<div class=\"card\"><div class=\"k\">Health</div><div class=\"big\">{Health()}</div><div class=\"k\">{c.Battery?.Cycles ?? 0} cycles</div></div>");
        sb.Append($"<div class=\"card\"><div class=\"k\">Wear trend</div><div class=\"big {(w?.WearPerMonth is double m ? Cls(m, 1, 2.5) : "")}\">{(w?.WearPerMonth is double m2 ? $"−{m2:0.0}%" : "—")}</div><div class=\"k\">per month</div></div>");
        sb.Append($"<div class=\"card\"><div class=\"k\">80% health</div><div class=\"big\">{(c.Forecast?.HealthAt80On is DateOnly d80 ? d80.ToString("MMM yyyy", En) : "—")}</div><div class=\"k\">at this trend</div></div>");
        sb.Append($"<div class=\"card\"><div class=\"k\">On the charger</div><div class=\"big {(c.ChargerHoursPerDay is double ch ? Cls(ch, 12, 18) : "")}\">{(c.ChargerHoursPerDay is double ch2 ? $"{ch2:0.0} h" : "—")}</div><div class=\"k\">a day, switched on</div></div>");
        sb.Append("</div>");

        // Health over the last 120 days as an inline SVG line.
        var pts = daily.Where(d => d.Day >= DateOnly.FromDateTime(today.AddDays(-120)) && d.DesignMWh > 0).ToList();
        if (pts.Count >= 2)
        {
            double minH = Math.Floor(pts.Min(p => p.FullMWh * 100.0 / p.DesignMWh) - 1), maxH = Math.Min(100, Math.Ceiling(pts.Max(p => p.FullMWh * 100.0 / p.DesignMWh) + 1));
            int x0 = pts[0].Day.DayNumber, span = Math.Max(1, pts[^1].Day.DayNumber - x0);
            string P(int i) => FormattableString.Invariant($"{(pts[i].Day.DayNumber - x0) * 880.0 / span + 40:0.0},{180 - (pts[i].FullMWh * 100.0 / pts[i].DesignMWh - minH) / (maxH - minH) * 160:0.0}");
            var line = string.Join(" ", Enumerable.Range(0, pts.Count).Select(P));
            sb.Append("<div class=\"card sec\"><h2>Health, last 120 days</h2><svg viewBox=\"0 0 940 200\" width=\"100%\" role=\"img\" aria-label=\"Battery health over time\">");
            sb.Append(FormattableString.Invariant($"<text x=\"0\" y=\"24\" fill=\"#8a97a6\" font-size=\"12\">{maxH:0}%</text><text x=\"0\" y=\"184\" fill=\"#8a97a6\" font-size=\"12\">{minH:0}%</text>"));
            sb.Append($"<polyline fill=\"none\" stroke=\"#3dffb0\" stroke-width=\"2.5\" points=\"{line}\"/>");
            sb.Append(FormattableString.Invariant($"<text x=\"40\" y=\"198\" fill=\"#8a97a6\" font-size=\"12\">{pts[0].Day.ToString("d MMM", En)}</text><text x=\"920\" y=\"198\" text-anchor=\"end\" fill=\"#8a97a6\" font-size=\"12\">{pts[^1].Day.ToString("d MMM", En)}</text>"));
            sb.Append("</svg></div>");
        }

        sb.Append("<div class=\"grid sec\" style=\"grid-template-columns:1fr 1fr\">");
        sb.Append("<div class=\"card\"><h2>Day by day</h2><table><tr><th>Day</th><th>Stress</th><th>At 100%</th><th>Under 10%</th></tr>");
        foreach (var (d, st) in days)
            sb.Append($"<tr><td>{d.ToString("ddd d MMM", En)}</td><td class=\"{(st.Score >= 60 ? "bad" : st.Score >= 30 ? "warn" : "good")}\">{st.Score} {st.Level}</td><td>{st.HoursAtFull:0.0} h</td><td>{st.HoursLow:0.0} h</td></tr>");
        if (days.Count == 0) sb.Append("<tr><td colspan=\"4\">No Guardian X readings this week yet.</td></tr>");
        sb.Append($"</table><p class=\"k\" style=\"margin-top:12px\">{cyclesThisWeek} charge cycles this week · {sleeps.Count} sleeps" +
                  (sleeps.Count > 0 ? $", average {sleeps.Average(s => s.PercentPerHour):0.0}%/h" : "") + "</p></div>");

        sb.Append("<div class=\"card\"><h2>Battery use by app (on battery)</h2><table><tr><th>App</th><th>Energy</th></tr>");
        foreach (var e in c.Energy7.Take(8)) sb.Append($"<tr><td>{E(e.Name)}</td><td>{e.MWh / 1000:0.00} Wh</td></tr>");
        if (c.Energy7.Count == 0) sb.Append("<tr><td colspan=\"2\">No time on battery this week.</td></tr>");
        sb.Append("</table>");
        if (c.GpuWeek.Count > 0) sb.Append($"<p class=\"k\" style=\"margin-top:12px\">Woke the NVIDIA GPU: {E(string.Join(", ", c.GpuWeek.Take(4).Select(g => $"{g.App} ({g.Times} min)")))}</p>");
        sb.Append("</div></div>");

        sb.Append("<div class=\"card sec\"><h2>What to do</h2><ul>");
        foreach (var t in tips.Take(5)) sb.Append($"<li><b>{E(t.Title)}</b> {E(t.Body)}</li>");
        sb.Append("</ul></div>");
        sb.Append($"<footer>Made by Guardian X on {DateTime.Now.ToString("d MMM yyyy HH:mm", En)}. All data stays on this PC.</footer></div></body></html>");
        return sb.ToString();
    }
}

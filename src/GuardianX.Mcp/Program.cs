using System.ComponentModel;
using System.Text;
using GuardianX.Core;
using GuardianX.Core.Apps;
using GuardianX.Core.Assistant;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Insights;
using GuardianX.Core.Power;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// Guardian X MCP server: read-only tools over the same local data the app records.
// Claude Desktop / Claude Code start it over stdio; it never changes settings or switches the plug.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

[McpServerToolType]
public static class GuardianTools
{
    // Never open the live database from here: Claude Desktop may run this server inside its app package, where
    // SQLite can't share the WAL index with Guardian X. Each call reads a fresh private copy instead.
    private static Store OpenStore() => Store.OpenCopyOfLive();

    [McpServerTool, Description("Current battery state: charge %, charging or not, power in watts, health %, capacity, cycles, wear speed and temperature if reported.")]
    public static string GetBatteryStatus()
    {
        var s = BatteryReader.Read();
        if (s is null) return "No battery found on this PC.";
        using var store = OpenStore();
        var wear = BatteryInsights.Wear(s.FullMWh, s.DesignMWh, s.Cycles, store.DailyCapacity(400));
        return $"Charge {s.Percent:0}% ({(s.OnAc ? (s.Charging ? "charging" : "on charger") : "on battery")}), power {(s.RateMw is int r ? r / 1000.0 : 0):0.0} W, " +
               $"health {wear.HealthPercent:0.0}% ({s.FullMWh}/{s.DesignMWh} mWh), {s.Cycles} cycles, wear {wear.WearPercent:0.0}%" +
               (wear.WearPer100Cycles is double w ? $" ({w:0.0}% per 100 cycles; typical ~4%)" : "") +
               (s.TemperatureC is double t ? $", {t:0.0} °C" : ", temperature not reported") + ".";
    }

    [McpServerTool, Description("Full Guardian X briefing: live battery data, wear trend, biggest drop, charger hours, stress, sleep drain, mode, smart plug and heaviest apps.")]
    public static string GetBriefing()
    {
        using var store = OpenStore();
        var settings = Settings.Load();
        var plug = new PlugState(settings.Plug.Kind != "none", settings.Plug.Kind, null, "state not read by the MCP server", null);
        var apps = new AppMonitor();
        apps.Sample(null);
        Thread.Sleep(1500);
        return Facts.Build(store, BatteryReader.Read(), apps.Sample(null), plug, settings);
    }

    [McpServerTool, Description("Battery capacity history by day (from Windows' battery report and Guardian X readings), with the monthly wear trend.")]
    public static string GetHealthHistory([Description("How many days back, default 180")] int days = 180)
    {
        using var store = OpenStore();
        var daily = store.DailyCapacity(days);
        if (daily.Count == 0) return "No history yet.";
        var sb = new StringBuilder("date, health %, full mWh, cycles\n");
        foreach (var d in daily) sb.AppendLine($"{d.Day:yyyy-MM-dd}, {(d.DesignMWh > 0 ? d.FullMWh * 100.0 / d.DesignMWh : 0):0.0}, {d.FullMWh}, {d.Cycles}");
        var last = daily[^1];
        var wear = BatteryInsights.Wear(last.FullMWh, last.DesignMWh, last.Cycles, daily);
        if (wear.WearPerMonth is double m) sb.AppendLine($"Trend: {m:0.0}% of capacity lost per month.");
        if (BatteryInsights.BiggestDrop(daily) is { } drop) sb.AppendLine($"Biggest drop: {drop.Lost:0.0}% between {drop.From} and {drop.To}.");
        return sb.ToString();
    }

    [McpServerTool, Description("Apps using the most RAM, CPU and disk right now, with estimated battery power when on battery, and tips.")]
    public static string GetTopApps()
    {
        var s = BatteryReader.Read();
        double? watts = s is { OnAc: false, RateMw: < 0 } ? -s.RateMw!.Value / 1000.0 : null;
        var mon = new AppMonitor();
        mon.Sample(watts);
        Thread.Sleep(1500);
        var apps = mon.Sample(watts).Where(a => a.Kind != "system").Take(12).ToList();
        var sb = new StringBuilder("app, RAM MB, CPU %, disk MB/s, est. W\n");
        foreach (var a in apps) sb.AppendLine($"{a.Name}, {a.MemoryMb:0}, {a.CpuPercent:0.0}, {a.DiskMbPerSec:0.0}, {(a.EstimatedWatts is double w ? w.ToString("0.0") : "-")}");
        foreach (var t in AppMonitor.Tips(apps, watts is not null)) sb.AppendLine("Tip: " + t.Text);
        return sb.ToString();
    }

    [McpServerTool, Description("Estimated battery energy used by each app over the last days (only time on battery counts).")]
    public static string GetAppBatteryUse([Description("Days back, default 7")] int days = 7)
    {
        using var store = OpenStore();
        var e = store.AppEnergy(days);
        return e.Count == 0 ? "No on-battery app usage recorded yet." : string.Join("\n", e.Select(x => $"{x.Name}: ~{x.MWh / 1000:0.00} Wh"));
    }

    [McpServerTool, Description("Disk space per drive, the largest installed apps and the temp folder size.")]
    public static string GetStorage()
    {
        var r = AppMonitor.Storage();
        var sb = new StringBuilder();
        foreach (var d in r.Drives) sb.AppendLine($"{d.Name} {d.Label}: {d.FreeGb:0.0} GB free of {d.TotalGb:0.0} GB");
        sb.AppendLine($"Temp files: {r.TempGb:0.00} GB");
        foreach (var a in r.LargestApps) sb.AppendLine($"{a.Name}: {a.SizeGb:0.0} GB");
        return sb.ToString();
    }

    [McpServerTool, Description("Search Guardian X's knowledge base (battery science, HP OMEN options, Guardian X features, Windows power tips).")]
    public static string SearchKnowledge([Description("What to look up")] string query)
    {
        var hits = Knowledge.Keyword(Knowledge.Load(), query, 4);
        return hits.Count == 0 ? "Nothing found." : string.Join("\n\n", hits.Select(h => $"## {h.Chunk.Title}\n{h.Chunk.Text}"));
    }

    [McpServerTool, Description("Guardian X modes and which one is active, with the charge window each keeps.")]
    public static string GetModes()
    {
        var current = Settings.Load().Mode;
        return string.Join("\n", Modes.All.Select(m => $"{(m.Id == current ? "* " : "  ")}{m.Name}: {m.Description} (keeps {m.ResumeAt}-{m.ChargeLimit}%, power mode {m.Overlay})"));
    }
}

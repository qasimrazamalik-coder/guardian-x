using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace GuardianX.Core.Battery;

/// <summary>One period from Windows' own battery history (weekly for older data, daily for the last weeks).</summary>
public sealed record ReportPeriod(
    DateTime Start, DateTime End, int FullMWh, int DesignMWh, int Cycles,
    TimeSpan ActiveOnCharger, TimeSpan ActiveOnBattery, TimeSpan StandbyOnCharger, TimeSpan StandbyOnBattery)
{
    public double Days => Math.Max((End - Start).TotalDays, 1.0 / 24);
    /// <summary>Hours a day the laptop was on and plugged in during this period.</summary>
    public double ChargerHoursPerDay => (ActiveOnCharger + StandbyOnCharger).TotalHours / Days;
}

/// <summary>
/// Reads the history Windows keeps in "powercfg /batteryreport". It goes back to when the battery was new,
/// so Guardian X can show a wear trend on its first day instead of waiting weeks.
/// </summary>
public static class BatteryReport
{
    public static List<ReportPeriod> Load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gx-battery-{Environment.ProcessId}.xml");
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{path}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return new(); }
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new();
        }
        catch (Exception) { return new(); }
        finally { try { File.Delete(path); } catch { } }
    }

    public static List<ReportPeriod> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var list = new List<ReportPeriod>();
        foreach (var e in doc.Descendants().Where(d => d.Name.LocalName == "HistoryEntry"))
        {
            if ((string?)e.Attribute("BatteryChanged") == "1") list.Clear(); // only the current battery counts
            int full = (int?)e.Attribute("FullChargeCapacity") ?? 0;
            int design = (int?)e.Attribute("DesignCapacity") ?? 0;
            if (full <= 0 || design <= 0) continue;
            list.Add(new ReportPeriod(
                DateTime.Parse((string)e.Attribute("LocalStartDate")!),
                DateTime.Parse((string)e.Attribute("LocalEndDate")!),
                full, design, (int?)e.Attribute("CycleCount") ?? 0,
                Span(e, "ActiveAcTime"), Span(e, "ActiveDcTime"), Span(e, "CsAcTime"), Span(e, "CsDcTime")));
        }
        return list;
    }

    private static TimeSpan Span(XElement e, string name)
    {
        var v = (string?)e.Attribute(name);
        if (string.IsNullOrEmpty(v)) return TimeSpan.Zero;
        try { return XmlConvert.ToTimeSpan(v); } catch (FormatException) { return TimeSpan.Zero; }
    }
}

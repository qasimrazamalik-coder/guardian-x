using System.Globalization;
using GuardianX.Core.Data;

namespace GuardianX.Core.Insights;

/// <summary>One fast (5-second) battery reading kept for power smoothing.</summary>
public readonly record struct PowerPoint(DateTimeOffset Time, int RemainingMWh, int? RateMw, bool OnAc);

/// <summary>The smoothed charge/discharge power: + charging, − discharging. Method is 'energy', 'driver' or 'none'.</summary>
public sealed record SmoothedRate(double? RateMw, string Method);

/// <summary>Precise health numbers worked out from many readings instead of the single live one.</summary>
public sealed record HealthPrecision(double HealthPrecise, double HealthLow, double HealthHigh);

public sealed record HabitFactor(string Name, int Impact, string Detail);

/// <summary>How kind the last 7 days of charging were to the battery (100 = ideal).</summary>
public sealed record HabitReport(int Score, string Grade, int? Change, IReadOnlyList<HabitFactor> Factors);

public sealed record ReadyBy(string Time, double Percent);

/// <summary>The user's usual plug-in routine, learnt from charger connect/disconnect events.</summary>
public sealed record UsagePattern(string? UsualUnplug, string? UsualPlugIn, bool? LastsUntilPlugIn, ReadyBy? ReadyBy, string? Message);

/// <summary>Keeps the last few minutes of fast readings and smooths the power figure.</summary>
public sealed class PowerSampler
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private const int Capacity = 150; // about 12 minutes at one reading every 5 s
    private const double Alpha = 0.3;
    private readonly object _lock = new();
    private readonly List<PowerPoint> _points = new();
    private double? _ewma;
    private bool? _lastAc;

    public void Add(PowerPoint p)
    {
        lock (_lock)
        {
            if (_lastAc is bool was && was != p.OnAc) _ewma = null; // a new charge/discharge starts fresh
            _lastAc = p.OnAc;
            if (p.RateMw is int r) _ewma = _ewma is double e ? e + Alpha * (r - e) : r;
            _points.Add(p);
            if (_points.Count > Capacity) _points.RemoveRange(0, _points.Count - Capacity);
        }
    }

    public SmoothedRate Current(DateTimeOffset now)
    {
        lock (_lock) return PowerSmoothing.Compute(_points, _ewma, now);
    }
}

public static class PowerSmoothing
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Least-squares slope of the remaining energy over the last minutes ('energy'), when there's at least 90 s of
    /// readings with 3 or more different capacity values and the charger wasn't connected or disconnected in that
    /// time. Otherwise the smoothed driver rate ('driver'), or nothing ('none').
    /// </summary>
    public static SmoothedRate Compute(IReadOnlyList<PowerPoint> points, double? driverEwmaMw, DateTimeOffset now)
    {
        var w = points.Where(p => p.Time >= now - Window && p.Time <= now.AddSeconds(5)).ToList();
        if (w.Count >= 3 && (w[^1].Time - w[0].Time) >= MinSpan && w.All(p => p.OnAc == w[0].OnAc) &&
            w.Select(p => p.RemainingMWh).Distinct().Count() >= 3)
        {
            var t0 = w[0].Time;
            double mx = w.Average(p => (p.Time - t0).TotalHours), my = w.Average(p => (double)p.RemainingMWh);
            double sxy = 0, sxx = 0;
            foreach (var p in w)
            {
                double dx = (p.Time - t0).TotalHours - mx;
                sxy += dx * (p.RemainingMWh - my);
                sxx += dx * dx;
            }
            if (sxx > 0) return new SmoothedRate(sxy / sxx, "energy"); // mWh per hour = mW
        }
        return driverEwmaMw is double e ? new SmoothedRate(e, "driver") : new SmoothedRate(null, "none");
    }
}

public static class SmartInsights
{
    public const double LowPercent = 15, FullPercent = 95, DeepPercent = 10;
    /// <summary>Heat score at and above which a reading counts as hot (≈ 48 °C).</summary>
    public const double HotScore = 65;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- health ----------

    /// <summary>
    /// Median full-charge capacity of the last 7 days ÷ design (falls back to the live reading), and the spread of
    /// daily health over 14 days. Single readings jump around by a percent or two; the median doesn't.
    /// </summary>
    public static HealthPrecision PreciseHealth(IReadOnlyList<Sample> samples, int liveFullMWh, int designMWh, DateTimeOffset now)
    {
        double live = designMWh > 0 ? liveFullMWh * 100.0 / designMWh : 0;
        var valid = samples.Where(s => s.FullMWh > 0 && s.DesignMWh > 0 && s.Time <= now).ToList();
        var week = valid.Where(s => s.Time >= now.AddDays(-7)).Select(s => s.FullMWh * 100.0 / s.DesignMWh).ToList();
        double precise = Math.Round(week.Count > 0 ? Median(week) : live, 2);
        var days = valid.Where(s => s.Time >= now.AddDays(-14))
            .GroupBy(s => s.Time.ToLocalTime().Date)
            .Select(g => Median(g.Select(s => s.FullMWh * 100.0 / s.DesignMWh).ToList()))
            .ToList();
        return days.Count == 0
            ? new HealthPrecision(precise, precise, precise)
            : new HealthPrecision(precise, Math.Round(days.Min(), 2), Math.Round(days.Max(), 2));
    }

    /// <summary>Last time the charge went from under 15% to 99% or more, from the readings (chronological). Null if never.</summary>
    public static DateTimeOffset? LastCalibrated(IReadOnlyList<Sample> samples)
    {
        DateTimeOffset? last = null;
        bool sawLow = false;
        foreach (var s in samples)
        {
            if (s.Percent < LowPercent) sawLow = true;
            else if (s.Percent >= 99 && sawLow) { last = s.Time; sawLow = false; }
        }
        return last;
    }

    // ---------- heat ----------

    /// <summary>Heat score 0..100 from a temperature: 40 °C → 35 (cool/warm edge), 48 °C → 65 (warm/hot edge).</summary>
    public static double HeatScoreFromTemp(double tempC) => Math.Clamp(35 + (tempC - 40) * 3.75, 0, 100);

    public static string HeatLevel(double score) => score < 35 ? "cool" : score < HotScore ? "warm" : "hot";

    // ---------- habit ----------

    /// <summary>
    /// 0..100 for the last 7 days: points come off for hours at 95%+ on the charger, hours under 15%, hot hours and
    /// deep discharges (below 10%). Gaps over 5 minutes (sleep, shut down) don't count.
    /// </summary>
    public static HabitReport Habit(IReadOnlyList<Sample> samples, DateTimeOffset now)
    {
        var cur = Score(samples.Where(s => s.Time >= now.AddDays(-7) && s.Time <= now).ToList());
        var prev = Score(samples.Where(s => s.Time >= now.AddDays(-14) && s.Time < now.AddDays(-7)).ToList());
        int? change = cur.HoursSeen >= 6 && prev.HoursSeen >= 6 ? cur.Score - prev.Score : null;
        return new HabitReport(cur.Score, Grade(cur.Score), change, cur.Factors);
    }

    public static string Grade(int score) => score >= 85 ? "A" : score >= 70 ? "B" : score >= 55 ? "C" : score >= 40 ? "D" : "E";

    private static (int Score, double HoursSeen, List<HabitFactor> Factors) Score(List<Sample> s)
    {
        double full = 0, low = 0, hot = 0, seen = 0;
        int deep = 0;
        bool belowDeep = false;
        for (int i = 1; i < s.Count; i++)
        {
            var p = s[i - 1];
            double m = (s[i].Time - p.Time).TotalMinutes;
            if (m <= 0 || m > 5) continue;
            seen += m;
            if (p.OnAc && p.Percent >= FullPercent) full += m;
            if (p.Percent < LowPercent) low += m;
            double? heat = p.Heat ?? (p.TemperatureC is double t ? HeatScoreFromTemp(t) : (double?)null);
            if (heat >= HotScore) hot += m;
        }
        foreach (var x in s)
        {
            if (x.Percent < DeepPercent && !belowDeep) { deep++; belowDeep = true; }
            else if (x.Percent >= 20) belowDeep = false;
        }
        double fh = full / 60, lh = low / 60, hh = hot / 60;
        int fullLoss = (int)Math.Round(Math.Min(40, fh / 7 * 3));
        int lowLoss = (int)Math.Round(Math.Min(20, lh * 2));
        int hotLoss = (int)Math.Round(Math.Min(25, hh * 1.5));
        int deepLoss = Math.Min(15, deep * 5);
        var factors = new List<HabitFactor>
        {
            new("Full on the charger", fullLoss, s.Count < 2 ? "No readings yet." : $"About {(fh / 7).ToString("0.#", Inv)} h a day at 95% or more on the charger."),
            new("Low charge", lowLoss, s.Count < 2 ? "No readings yet." : lh > 0 ? $"{lh.ToString("0.#", Inv)} h below 15% this week." : "Never below 15% this week."),
            new("Heat", hotLoss, s.Count < 2 ? "No readings yet." : hh > 0 ? $"{hh.ToString("0.#", Inv)} h running hot this week." : "No hot spells this week."),
            new("Deep discharges", deepLoss, s.Count < 2 ? "No readings yet." : deep > 0 ? $"{deep} time{(deep == 1 ? "" : "s")} below 10% this week." : "Never below 10% this week."),
        };
        return (Math.Clamp(100 - fullLoss - lowLoss - hotLoss - deepLoss, 0, 100), seen / 60, factors);
    }

    // ---------- usage pattern ----------

    /// <summary>
    /// Median time of the first unplug of each day and of the plug-in that follows it, over the given charger events
    /// (ac_on/ac_off). Weekdays are used when there are at least 3 of them, otherwise every day. Null under 3 days.
    /// </summary>
    public static (TimeSpan? Unplug, TimeSpan? PlugIn) Routine(IReadOnlyList<PowerEvent> events)
    {
        var days = new List<(DateTime Day, TimeSpan Off, TimeSpan? On)>();
        foreach (var g in events.Where(e => e.Kind is "ac_on" or "ac_off").OrderBy(e => e.Time)
                     .GroupBy(e => (e.Time.ToLocalTime().DateTime - TimeSpan.FromHours(4)).Date))
        {
            var list = g.ToList();
            int off = list.FindIndex(e => e.Kind == "ac_off");
            if (off < 0) continue;
            var on = list.Skip(off + 1).FirstOrDefault(e => e.Kind == "ac_on");
            days.Add((g.Key, list[off].Time.ToLocalTime().TimeOfDay, on?.Time.ToLocalTime().TimeOfDay));
        }
        var weekdays = days.Where(d => d.Day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();
        var use = weekdays.Count >= 3 ? weekdays : days;
        var offs = use.Select(d => d.Off).ToList();
        var ons = use.Where(d => d.On is not null).Select(d => d.On!.Value).ToList();
        return (offs.Count >= 3 ? MedianTime(offs) : (TimeSpan?)null, ons.Count >= 3 ? MedianTime(ons) : (TimeSpan?)null);
    }

    /// <summary>Next time of day at or after now (today, else tomorrow).</summary>
    public static DateTimeOffset Next(TimeSpan timeOfDay, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var t = new DateTimeOffset(local.Date + timeOfDay, local.Offset);
        return t >= local ? t : t.AddDays(1);
    }

    /// <summary>
    /// What the routine means right now. On battery: will the charge last to the usual plug-in time. On the charger:
    /// the charge expected at the usual unplug time at the current rate, capped at the mode's limit.
    /// </summary>
    public static UsagePattern Pattern(IReadOnlyList<PowerEvent> events, Battery.BatterySnapshot? s, double? rateMw, int chargeLimit, DateTimeOffset now)
    {
        var (unplug, plugIn) = Routine(events);
        string? Fmt(TimeSpan? t) => t is TimeSpan v ? $"{v.Hours:00}:{v.Minutes:00}" : null;
        bool? lasts = null;
        ReadyBy? ready = null;
        string? message = null;
        if (s is not null)
        {
            if (!s.OnAc && plugIn is TimeSpan pi)
            {
                var at = Next(pi, now);
                double? hours = rateMw is double r && r < 0 ? s.RemainingMWh / -r : null;
                if (hours is double h && (at - now).TotalHours <= 18)
                {
                    lasts = now.AddHours(h) >= at;
                    var empty = now.ToLocalTime().AddHours(h);
                    message = lasts == true
                        ? $"At this rate the charge lasts past your usual plug-in time ({Fmt(pi)})."
                        : $"At this rate it runs out around {empty:HH:mm}, before your usual plug-in time ({Fmt(pi)}). Saver mode would stretch it.";
                }
            }
            else if (s.OnAc && unplug is TimeSpan up && s.FullMWh > 0)
            {
                var at = Next(up, now);
                double hours = (at - now).TotalHours;
                double gain = rateMw is double r && r > 0 ? r * hours / s.FullMWh * 100 : 0;
                double pct = Math.Round(Math.Min(Math.Min(chargeLimit, 100), s.Percent + gain), 0);
                if (s.Percent > pct) pct = Math.Round(s.Percent, 0); // already above the limit: it stays where it is
                ready = new ReadyBy(Fmt(up)!, pct);
                message = $"By your usual unplug time ({Fmt(up)}) it should be at about {pct:0}%.";
            }
        }
        return new UsagePattern(Fmt(unplug), Fmt(plugIn), lasts, ready, message);
    }

    /// <summary>Warn when, on battery, the charge at the smoothed rate runs out 30+ minutes before the usual plug-in time.</summary>
    public static bool ShouldWarnSmartLow(bool onAc, double? hoursToEmpty, DateTimeOffset now, DateTimeOffset? nextPlugIn) =>
        !onAc && hoursToEmpty is double h && nextPlugIn is DateTimeOffset at && (at - now).TotalHours <= 18 &&
        now.AddHours(h) <= at.AddMinutes(-30);

    // ---------- helpers ----------

    internal static double Median(List<double> xs)
    {
        xs.Sort();
        int n = xs.Count;
        return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2;
    }

    /// <summary>Median time of day, counted from 4 am so times either side of midnight stay together.</summary>
    internal static TimeSpan MedianTime(List<TimeSpan> times)
    {
        var shifted = times.Select(t => ((t.TotalMinutes - 240) % 1440 + 1440) % 1440).ToList();
        double m = (Median(shifted) + 240) % 1440;
        return TimeSpan.FromMinutes(Math.Round(m));
    }
}

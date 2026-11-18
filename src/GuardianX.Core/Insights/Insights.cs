using GuardianX.Core.Data;

namespace GuardianX.Core.Insights;

public sealed record WearReport(
    double HealthPercent,
    double WearPercent,
    int Cycles,
    double? WearPer100Cycles,
    double? WearPerMonth,   // from the stored trend; null until there are 14+ days of history
    int TrendDays);

public sealed record StressReport(int Score, string Level, double HoursAtFull, double HoursHot, double HoursLow, bool TemperatureKnown);

public sealed record SleepReport(DateTimeOffset Start, DateTimeOffset End, double PercentLost, double PercentPerHour, bool Unusual);

public enum TipLevel { Good, Info, Warn, Alert }

public sealed record Tip(TipLevel Level, string Title, string Body);

public static class BatteryInsights
{
    /// <summary>A battery rated to keep 80% after 500 cycles loses about this much per 100 cycles.</summary>
    public const double TypicalWearPer100Cycles = 4.0;
    public const double FullThreshold = 95;
    public const double HotThresholdC = 40;
    public const double LowThreshold = 10;

    public static WearReport Wear(int fullMWh, int designMWh, int cycles,
        IReadOnlyList<(DateOnly Day, int FullMWh, int DesignMWh, int Cycles)> daily)
    {
        double health = designMWh > 0 ? Math.Min(100, fullMWh * 100.0 / designMWh) : 0;
        double wear = Math.Max(0, 100 - health);
        double? per100 = cycles >= 10 ? wear / cycles * 100 : null;

        double? perMonth = null;
        int span = 0;
        if (daily.Count >= 2)
        {
            span = daily[^1].Day.DayNumber - daily[0].Day.DayNumber;
            if (span >= 14)
            {
                // Least-squares slope of health % per day.
                var xs = daily.Select(d => (double)(d.Day.DayNumber - daily[0].Day.DayNumber)).ToArray();
                var ys = daily.Select(d => d.DesignMWh > 0 ? d.FullMWh * 100.0 / d.DesignMWh : 0).ToArray();
                double mx = xs.Average(), my = ys.Average();
                double sxy = 0, sxx = 0;
                for (int i = 0; i < xs.Length; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sxx += (xs[i] - mx) * (xs[i] - mx); }
                if (sxx > 0) perMonth = Math.Round(-sxy / sxx * 30.44, 2);
            }
        }
        return new WearReport(Math.Round(health, 1), Math.Round(wear, 1), cycles,
            per100 is null ? null : Math.Round(per100.Value, 1), perMonth, span);
    }

    /// <summary>
    /// How hard the battery had it over the given samples. Time spent full on the charger, hot, or nearly empty
    /// all age a lithium battery faster. Gaps longer than 5 minutes (sleep, shut down) are not counted.
    /// </summary>
    public static StressReport Stress(IReadOnlyList<Sample> samples)
    {
        double full = 0, hot = 0, low = 0;
        bool tempKnown = samples.Any(s => s.TemperatureC is not null);
        for (int i = 1; i < samples.Count; i++)
        {
            var prev = samples[i - 1];
            double minutes = (samples[i].Time - prev.Time).TotalMinutes;
            if (minutes <= 0 || minutes > 5) continue;
            if (prev.OnAc && prev.Percent >= FullThreshold) full += minutes;
            if (prev.TemperatureC >= HotThresholdC) hot += minutes;
            if (prev.Percent <= LowThreshold) low += minutes;
        }
        double fh = full / 60, hh = hot / 60, lh = low / 60;
        int score = (int)Math.Round(Math.Clamp(fh * 6 + hh * 12 + lh * 20, 0, 100));
        string level = score >= 60 ? "High" : score >= 30 ? "Medium" : "Low";
        return new StressReport(score, level, Math.Round(fh, 1), Math.Round(hh, 1), Math.Round(lh, 1), tempKnown);
    }

    /// <summary>Minutes the laptop has sat on the charger at (nearly) 100% without a break, up to the last sample.</summary>
    public static int FullHoldStreakMinutes(IReadOnlyList<Sample> samples)
    {
        double minutes = 0;
        for (int i = samples.Count - 1; i > 0; i--)
        {
            var s = samples[i];
            if (!s.OnAc || s.Percent < 99) break;
            double gap = (s.Time - samples[i - 1].Time).TotalMinutes;
            if (gap > 5) break;
            if (samples[i - 1].OnAc && samples[i - 1].Percent >= 99) minutes += gap; else break;
        }
        return (int)Math.Round(minutes);
    }

    /// <summary>Battery lost during each sleep, and whether it was unusual compared with earlier sleeps.</summary>
    public static List<SleepReport> Sleeps(IReadOnlyList<PowerEvent> events)
    {
        var raw = new List<(DateTimeOffset s, DateTimeOffset e, double lost, double perHour)>();
        for (int i = 0; i < events.Count - 1; i++)
        {
            if (events[i].Kind != "suspend" || events[i + 1].Kind != "resume") continue;
            double hours = (events[i + 1].Time - events[i].Time).TotalHours;
            double lost = events[i].Percent - events[i + 1].Percent;
            if (hours < 0.5 || lost < 0) continue; // short naps and charging sleeps say nothing about drain
            raw.Add((events[i].Time, events[i + 1].Time, lost, lost / hours));
        }
        var result = new List<SleepReport>();
        for (int i = 0; i < raw.Count; i++)
        {
            var earlier = raw.Take(i).Select(r => r.perHour).OrderBy(x => x).ToList();
            double? baseline = earlier.Count >= 3 ? earlier[earlier.Count / 2] : null;
            bool unusual = raw[i].perHour >= 1.0 && raw[i].lost >= 3 &&
                           (baseline is null || raw[i].perHour >= Math.Max(1.0, baseline.Value * 2.5));
            result.Add(new SleepReport(raw[i].s, raw[i].e, Math.Round(raw[i].lost, 1), Math.Round(raw[i].perHour, 2), unusual));
        }
        return result;
    }

    /// <summary>Hours until empty (discharging) or full (charging), from the current rate. Null when idle or unknown.</summary>
    public static double? HoursLeft(int remainingMWh, int fullMWh, int? rateMw)
    {
        if (rateMw is null or 0) return null;
        if (rateMw < 0) return remainingMWh / (double)-rateMw.Value;
        int toFull = fullMWh - remainingMWh;
        return toFull <= 0 ? null : toFull / (double)rateMw.Value;
    }

    /// <summary>Plain advice built from the user's own numbers, most important first.</summary>
    public static List<Tip> Coach(WearReport wear, StressReport today, int fullHoldMinutes,
        SleepReport? lastSleep, IReadOnlyList<(string App, int Times)> gpuWeek)
    {
        var tips = new List<Tip>();
        if (wear.WearPer100Cycles is double w && w > TypicalWearPer100Cycles * 2)
            tips.Add(new Tip(TipLevel.Alert, "Your battery is ageing from time, not use",
                $"It has lost {wear.WearPercent:0.#}% in {wear.Cycles} cycles, about {w:0.#}% per 100 cycles. " +
                $"A typical battery loses about {TypicalWearPer100Cycles:0}% per 100 cycles. That pattern usually means hours " +
                "spent at 100% on the charger and heat from gaming. Unplugging when you don't need a full battery is the biggest fix."));
        if (fullHoldMinutes >= 60)
            tips.Add(new Tip(TipLevel.Warn, $"At 100% on the charger for {FormatMinutes(fullHoldMinutes)}",
                "Sitting full and warm is what ages a lithium battery fastest. Your OMEN has no fixed 80% limit, so " +
                "unplugging for a while when you don't need a full battery is the main thing you can do."));
        if (today.Score >= 60)
            tips.Add(new Tip(TipLevel.Warn, $"High battery stress today ({today.Score}/100)",
                $"{today.HoursAtFull:0.#} h at full on the charger" +
                (today.TemperatureKnown ? $", {today.HoursHot:0.#} h above {HotThresholdC:0}°C" : "") +
                (today.HoursLow > 0 ? $", {today.HoursLow:0.#} h below {LowThreshold:0}%" : "") + "."));
        if (lastSleep is { Unusual: true })
            tips.Add(new Tip(TipLevel.Warn, $"Lost {lastSleep.PercentLost:0.#}% while asleep",
                $"That's {lastSleep.PercentPerHour:0.#}% an hour, more than usual. Something kept the laptop busy in sleep. " +
                "Run 'powercfg /sleepstudy' to see what, or use Hibernate when it goes in your bag."));
        if (gpuWeek.Count > 0)
            tips.Add(new Tip(TipLevel.Info, $"{gpuWeek[0].App} keeps waking the NVIDIA GPU",
                "On battery the NVIDIA GPU should sleep. Close that app when unplugged, or set it to " +
                "'Power saving' in Windows Settings > System > Display > Graphics."));
        if (tips.Count == 0)
            tips.Add(new Tip(TipLevel.Good, "Nothing to fix right now",
                "Your battery isn't under stress today. Guardian X keeps watching and will tell you when something changes."));
        return tips;
    }

    public static string FormatMinutes(int minutes) =>
        minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";
}

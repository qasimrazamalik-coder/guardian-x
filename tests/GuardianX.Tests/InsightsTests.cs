using GuardianX.Core.Data;
using GuardianX.Core.Gpu;
using GuardianX.Core.Insights;
using Xunit;

namespace GuardianX.Tests;

public class InsightsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 8, 0, 0, TimeSpan.FromHours(5));

    private static Sample S(int minute, double pct, bool ac, double? temp = null) =>
        new(T0.AddMinutes(minute), pct, 0, 62986, 70070, 49, null, null, temp, ac, false);

    [Fact]
    public void WearMatchesTheOmenReading()
    {
        // Measured on the user's Omen: 62,986 of 70,070 mWh after 49 cycles.
        var w = BatteryInsights.Wear(62986, 70070, 49, []);
        Assert.Equal(89.9, w.HealthPercent);
        Assert.Equal(10.1, w.WearPercent);
        Assert.Equal(20.6, w.WearPer100Cycles);
        Assert.Null(w.WearPerMonth); // no history yet
    }

    [Fact]
    public void WearPer100CyclesNeedsEnoughCycles()
    {
        Assert.Null(BatteryInsights.Wear(69000, 70070, 3, []).WearPer100Cycles);
    }

    [Fact]
    public void WearTrendFromDailyHistory()
    {
        // Loses 70 mWh a day for 30 days: 0.1% of design a day, about 3% a month.
        var start = new DateOnly(2026, 10, 1);
        var daily = Enumerable.Range(0, 31).Select(d => (start.AddDays(d), 63000 - 70 * d, 70070, 50 + d)).ToList();
        var w = BatteryInsights.Wear(daily[^1].Item2, 70070, 80, daily);
        Assert.Equal(30, w.TrendDays);
        Assert.InRange(w.WearPerMonth!.Value, 3.0, 3.1);
    }

    [Fact]
    public void StressCountsTimeFullOnChargerHotAndLow()
    {
        var samples = new List<Sample>();
        for (int m = 0; m <= 600; m++) samples.Add(S(m, 100, ac: true, temp: m < 120 ? 42 : 35)); // 10 h full, 2 h hot
        var r = BatteryInsights.Stress(samples);
        Assert.Equal(10, r.HoursAtFull);
        Assert.Equal(2, r.HoursHot);
        Assert.Equal(0, r.HoursLow);
        Assert.Equal(84, r.Score); // 10*6 + 2*12
        Assert.Equal("High", r.Level);
        Assert.True(r.TemperatureKnown);
    }

    [Fact]
    public void StressIgnoresGapsLikeSleep()
    {
        var samples = new List<Sample> { S(0, 100, true), S(1, 100, true), S(300, 100, true), S(301, 100, true) };
        Assert.Equal(0, BatteryInsights.Stress(samples).HoursAtFull); // 2 minutes, rounds to 0.0 h
    }

    [Fact]
    public void FullHoldStreakStopsAtUnplug()
    {
        var samples = new List<Sample>();
        for (int m = 0; m < 30; m++) samples.Add(S(m, 80, ac: false));
        for (int m = 30; m <= 150; m++) samples.Add(S(m, 100, ac: true));
        Assert.Equal(120, BatteryInsights.FullHoldStreakMinutes(samples));
        samples.Add(S(151, 99.5, ac: false));
        Assert.Equal(0, BatteryInsights.FullHoldStreakMinutes(samples));
    }

    [Fact]
    public void SleepDrainFlagsAnUnusualNight()
    {
        var ev = new List<PowerEvent>();
        var t = T0;
        for (int n = 0; n < 3; n++) // three normal nights: 2% over 8 h
        {
            ev.Add(new PowerEvent(t, "suspend", 80));
            ev.Add(new PowerEvent(t.AddHours(8), "resume", 78));
            t = t.AddDays(1);
        }
        ev.Add(new PowerEvent(t, "suspend", 90));
        ev.Add(new PowerEvent(t.AddHours(8), "resume", 76)); // 14% overnight
        var sleeps = BatteryInsights.Sleeps(ev);
        Assert.Equal(4, sleeps.Count);
        Assert.False(sleeps[0].Unusual);
        Assert.True(sleeps[^1].Unusual);
        Assert.Equal(14, sleeps[^1].PercentLost);
        Assert.Equal(1.75, sleeps[^1].PercentPerHour);
    }

    [Fact]
    public void ChargingDuringSleepIsNotDrain()
    {
        var ev = new List<PowerEvent> { new(T0, "suspend", 40), new(T0.AddHours(6), "resume", 100) };
        Assert.Empty(BatteryInsights.Sleeps(ev));
    }

    [Fact]
    public void HoursLeft()
    {
        Assert.Equal(2.0, BatteryInsights.HoursLeft(40000, 62986, -20000));
        Assert.Equal(0.5, BatteryInsights.HoursLeft(52986, 62986, 20000));
        Assert.Null(BatteryInsights.HoursLeft(62986, 62986, 0));
        Assert.Null(BatteryInsights.HoursLeft(62986, 62986, null));
    }

    [Fact]
    public void CoachLeadsWithTheBiggestProblem()
    {
        var wear = BatteryInsights.Wear(62986, 70070, 49, []);
        var stress = new StressReport(10, "Low", 1, 0, 0, false);
        var tips = BatteryInsights.Coach(wear, stress, 180, null, []);
        Assert.Equal(TipLevel.Alert, tips[0].Level);
        Assert.Contains("20.6% per 100 cycles", tips[0].Body);
        Assert.Contains(tips, t => t.Title.StartsWith("At 100% on the charger for 3 h"));
    }

    [Fact]
    public void CoachSaysAllIsWellWhenItIs()
    {
        var wear = BatteryInsights.Wear(69000, 70070, 100, []);
        var tips = BatteryInsights.Coach(wear, new StressReport(5, "Low", 0.5, 0, 0, false), 0, null, []);
        Assert.Single(tips);
        Assert.Equal(TipLevel.Good, tips[0].Level);
    }

    private const string ReportXml = """
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <History>
            <HistoryEntry LocalStartDate="2026-07-13T07:04:52" LocalEndDate="2026-07-21T04:34:21" DesignCapacity="70070" FullChargeCapacity="68229" CycleCount="1" ActiveAcTime="PT1H18M32S" ActiveDcTime="PT3H38M0S" CsAcTime="PT43M34S" CsDcTime="PT13M20S" BatteryChanged="0" />
            <HistoryEntry LocalStartDate="2026-11-16T00:00:00" LocalEndDate="2026-11-16T23:59:59" DesignCapacity="70070" FullChargeCapacity="61890" CycleCount="54" ActiveAcTime="PT23H3M25S" ActiveDcTime="PT56M5S" CsAcTime="PT0S" CsDcTime="PT0S" BatteryChanged="0" />
            <HistoryEntry LocalStartDate="2026-11-17T00:00:00" LocalEndDate="2026-11-17T23:59:59" DesignCapacity="70070" FullChargeCapacity="0" CycleCount="54" ActiveAcTime="PT0S" ActiveDcTime="PT0S" CsAcTime="PT0S" CsDcTime="PT0S" BatteryChanged="0" />
          </History>
        </BatteryReport>
        """;

    [Fact]
    public void ParsesWindowsBatteryHistory()
    {
        var periods = GuardianX.Core.Battery.BatteryReport.Parse(ReportXml);
        Assert.Equal(2, periods.Count); // the empty reading is skipped
        Assert.Equal(68229, periods[0].FullMWh);
        Assert.Equal(54, periods[1].Cycles);
        Assert.Equal(new TimeSpan(23, 3, 25), periods[1].ActiveOnCharger);
        Assert.InRange(periods[1].ChargerHoursPerDay, 23.0, 23.1);
    }

    [Fact]
    public void BatteryChangeResetsHistory()
    {
        var xml = ReportXml.Replace("CycleCount=\"54\" ActiveAcTime=\"PT23H3M25S\" ActiveDcTime=\"PT56M5S\" CsAcTime=\"PT0S\" CsDcTime=\"PT0S\" BatteryChanged=\"0\"",
                                    "CycleCount=\"54\" ActiveAcTime=\"PT23H3M25S\" ActiveDcTime=\"PT56M5S\" CsAcTime=\"PT0S\" CsDcTime=\"PT0S\" BatteryChanged=\"1\"");
        var periods = GuardianX.Core.Battery.BatteryReport.Parse(xml);
        Assert.Single(periods);
        Assert.Equal(61890, periods[0].FullMWh);
    }

    [Fact]
    public void ChargerTipUsesWindowsRecords()
    {
        var end = DateTime.Now.Date;
        var periods = Enumerable.Range(0, 7).Select(i => new GuardianX.Core.Battery.ReportPeriod(
            end.AddDays(-i - 1), end.AddDays(-i), 62000, 70070, 54,
            TimeSpan.FromHours(22), TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.Zero)).ToList();
        var hours = BatteryInsights.ChargerHoursPerDay(periods);
        Assert.Equal(23.0, hours!.Value);
        var tips = BatteryInsights.Coach(BatteryInsights.Wear(69000, 70070, 100, []),
            new StressReport(0, "Low", 0, 0, 0, false), 0, null, [], hours);
        Assert.Contains(tips, t => t.Title == "Plugged in about 23 hours a day");
    }

    [Fact]
    public void OldWeeklySummariesDontDiluteThisWeek()
    {
        var today = DateTime.Now.Date;
        var periods = new List<GuardianX.Core.Battery.ReportPeriod>
        {
            new(today.AddDays(-14), today.AddDays(-7), 62000, 70070, 49, TimeSpan.FromHours(10), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
        };
        for (int i = 7; i >= 1; i--)
            periods.Add(new(today.AddDays(-i), today.AddDays(-i + 1), 62000, 70070, 54, TimeSpan.FromHours(20), TimeSpan.Zero, TimeSpan.FromHours(2), TimeSpan.Zero));
        Assert.Equal(22.0, BatteryInsights.ChargerHoursPerDay(periods)!.Value);
    }

    [Fact]
    public void FindsTheBiggestDrop()
    {
        // From the user's Windows records: 68,377 on 5 Oct, 65,731 on 12 Oct, 63,198 on 19 Oct.
        var daily = new List<(DateOnly, int, int, int)>
        {
            (new DateOnly(2026, 9, 28), 68109, 70070, 18),
            (new DateOnly(2026, 10, 5), 68377, 70070, 23),
            (new DateOnly(2026, 10, 12), 65731, 70070, 30),
            (new DateOnly(2026, 10, 19), 63198, 70070, 36),
            (new DateOnly(2026, 10, 26), 63082, 70070, 42),
        };
        var drop = BatteryInsights.BiggestDrop(daily)!.Value;
        Assert.Equal(new DateOnly(2026, 10, 5), drop.From);
        Assert.Equal(new DateOnly(2026, 10, 19), drop.To);
        Assert.Equal(7.4, drop.Lost);
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0001802E_phys_0", 1234)]
    [InlineData("luid_0x00000000_0x0001802E_phys_0", -1)]
    [InlineData("pid_x_luid", -1)]
    public void ParsesGpuCounterInstances(string instance, int pid)
    {
        Assert.Equal(pid, DgpuWatcher.ParsePid(instance));
    }

    [Fact]
    public void StoreRoundTripsSamplesAndEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gx-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new Store(path))
            {
                var now = DateTimeOffset.Now;
                store.Add(new GuardianX.Core.Battery.BatterySnapshot(now, 55.5, 35000, 62986, 70070, 49, -15000, 16000, null,
                    false, false, "LION", "HP", "Primary"));
                store.AddEvent("suspend", 55.5, now);
                var back = store.Samples(now.AddMinutes(-1), now.AddMinutes(1));
                Assert.Single(back);
                Assert.Equal(55.5, back[0].Percent);
                Assert.Equal(-15000, back[0].RateMw);
                Assert.Null(back[0].TemperatureC);
                Assert.Single(store.Events(now.AddMinutes(-1)));
                Assert.Equal("ok", store.IntegrityCheck());
                store.DeleteAll();
                Assert.Equal(0, store.SampleCount());
            }
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}

using System.Text.Json;
using GuardianX.Core;
using GuardianX.Core.Assistant;
using GuardianX.Core.Data;
using GuardianX.Core.Insights;
using GuardianX.Core.Power;
using Xunit;

namespace GuardianX.Tests;

public class PowerSmoothingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(5));

    /// <summary>A steady discharge sampled every 5 s; the driver's capacity is a whole number of mWh, its rate jumps around.</summary>
    private static List<PowerPoint> Discharge(double watts, int seconds, Func<int, bool>? onAc = null)
    {
        var list = new List<PowerPoint>();
        var rnd = new Random(7);
        for (int t = seconds; t >= 0; t -= 5)
        {
            var time = Now.AddSeconds(-t);
            int i = (seconds - t) / 5;
            int remaining = (int)Math.Round(50000 - watts * 1000 * (seconds - t) / 3600.0);
            int noisyRate = (int)(-watts * 1000 + rnd.Next(-6000, 6000));
            list.Add(new PowerPoint(time, remaining, noisyRate, onAc?.Invoke(i) ?? false));
        }
        return list;
    }

    [Fact]
    public void SteadyDischargeIsMeasuredFromTheEnergySlope()
    {
        var r = PowerSmoothing.Compute(Discharge(15, 360), -11000, Now);
        Assert.Equal("energy", r.Method);
        Assert.InRange(r.RateMw!.Value / 1000, -15.3, -14.7);
    }

    [Fact]
    public void SamplerSmoothsTheSameWay()
    {
        var sampler = new PowerSampler();
        foreach (var p in Discharge(15, 360)) sampler.Add(p);
        var r = sampler.Current(Now);
        Assert.Equal("energy", r.Method);
        Assert.InRange(r.RateMw!.Value / 1000, -15.3, -14.7);
    }

    [Fact]
    public void ChargerChangeInTheWindowFallsBackToTheDriverRate()
    {
        var points = Discharge(15, 360, i => i > 40); // plugged in part-way through
        var sampler = new PowerSampler();
        foreach (var p in points) sampler.Add(p);
        var r = sampler.Current(Now);
        Assert.Equal("driver", r.Method);
        Assert.NotNull(r.RateMw);
    }

    [Fact]
    public void TooShortAWindowUsesTheDriverAndNothingMeansNone()
    {
        Assert.Equal("driver", PowerSmoothing.Compute(Discharge(15, 60), -15000, Now).Method);
        Assert.Equal("none", PowerSmoothing.Compute(Array.Empty<PowerPoint>(), null, Now).Method);
    }
}

public class PreciseHealthTests
{
    [Fact]
    public void MedianOfTheWeekAndDailyRange()
    {
        var now = new DateTimeOffset(DateTime.Today.AddHours(23));
        var samples = new List<Sample>();
        for (int d = 13; d >= 0; d--)
        {
            var noon = new DateTimeOffset(DateTime.Today.AddDays(-d).AddHours(12));
            for (int i = 0; i < 12; i++)
                samples.Add(new Sample(noon.AddMinutes(i * 10), 70, 40000, 62000 + d * 100, 70070, 50, -12000, null, null, false, false));
        }
        // One wild reading doesn't move the median.
        samples.Add(new Sample(now.AddHours(-1), 70, 40000, 70000, 70070, 50, -12000, null, null, false, false));
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));

        var h = SmartInsights.PreciseHealth(samples, 61000, 70070, now);
        Assert.Equal(Math.Round(62300 * 100.0 / 70070, 2), h.HealthPrecise);
        Assert.Equal(Math.Round(62000 * 100.0 / 70070, 2), h.HealthLow);
        Assert.Equal(Math.Round(63300 * 100.0 / 70070, 2), h.HealthHigh);
    }

    [Fact]
    public void NoHistoryFallsBackToTheLiveReading()
    {
        var h = SmartInsights.PreciseHealth(Array.Empty<Sample>(), 63000, 70000, DateTimeOffset.Now);
        Assert.Equal(90.0, h.HealthPrecise);
        Assert.Equal(90.0, h.HealthLow);
        Assert.Equal(90.0, h.HealthHigh);
    }

    [Fact]
    public void CalibrationIsALowToFullCharge()
    {
        var t = DateTimeOffset.Now.AddDays(-3);
        Sample S(int min, double pct) => new(t.AddMinutes(min), pct, 0, 60000, 70000, 50, null, null, null, pct > 50, false);
        var samples = new List<Sample> { S(0, 40), S(1, 12), S(2, 60), S(3, 99.5), S(4, 80), S(5, 99.5) };
        Assert.Equal(t.AddMinutes(3), SmartInsights.LastCalibrated(samples));
        Assert.Null(SmartInsights.LastCalibrated(new List<Sample> { S(0, 40), S(1, 100) }));
    }
}

public class HabitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(5));

    private static List<Sample> Days(DateTimeOffset from, DateTimeOffset to, Func<DateTimeOffset, (double Pct, bool Ac, double? Heat)> f)
    {
        var list = new List<Sample>();
        for (var t = from; t < to; t = t.AddMinutes(5))
        {
            var (pct, ac, heat) = f(t);
            list.Add(new Sample(t, pct, 40000, 60000, 70000, 50, null, null, null, ac, false, heat));
        }
        return list;
    }

    [Theory]
    [InlineData(100, "A")] [InlineData(85, "A")] [InlineData(84, "B")] [InlineData(70, "B")] [InlineData(69, "C")]
    [InlineData(55, "C")] [InlineData(54, "D")] [InlineData(40, "D")] [InlineData(39, "E")] [InlineData(0, "E")]
    public void Grades(int score, string grade) => Assert.Equal(grade, SmartInsights.Grade(score));

    [Fact]
    public void KindWeekScoresFull()
    {
        var h = SmartInsights.Habit(Days(Now.AddDays(-7), Now, t => (60, t.Hour % 2 == 0, 20)), Now);
        Assert.Equal(100, h.Score);
        Assert.Equal("A", h.Grade);
        Assert.All(h.Factors, f => Assert.Equal(0, f.Impact));
        Assert.Equal(4, h.Factors.Count);
        Assert.Null(h.Change); // no previous week to compare with
    }

    [Fact]
    public void SittingFullOnTheChargerCostsPointsAndShowsTheChange()
    {
        var samples = Days(Now.AddDays(-14), Now.AddDays(-7), _ => (60, false, 20));
        samples.AddRange(Days(Now.AddDays(-7), Now, _ => (100, true, 20)));
        var h = SmartInsights.Habit(samples, Now);
        Assert.Equal(60, h.Score);
        Assert.Equal("C", h.Grade);
        Assert.Equal(-40, h.Change);
        var full = Assert.Single(h.Factors, f => f.Name == "Full on the charger");
        Assert.Equal(40, full.Impact);
        Assert.Contains("h a day", full.Detail);
    }

    [Fact]
    public void HeatLowChargeAndDeepDischargesAllCount()
    {
        // Each day: one hour hot, one hour at 8% (one deep discharge), the rest fine.
        var h = SmartInsights.Habit(Days(Now.AddDays(-7), Now, t => t.Hour switch
        {
            3 => (8, false, 20),
            4 => (60, false, 80),
            _ => (60, false, 20),
        }), Now);
        Assert.True(h.Factors.Single(f => f.Name == "Heat").Impact > 0);
        Assert.True(h.Factors.Single(f => f.Name == "Low charge").Impact > 0);
        Assert.Equal(15, h.Factors.Single(f => f.Name == "Deep discharges").Impact); // capped
        Assert.True(h.Score < 70);
    }
}

public class PatternTests
{
    private static DateTimeOffset Local(int day, int hour, int minute) => new(new DateTime(2026, 11, day, hour, minute, 0));

    private static List<PowerEvent> Week()
    {
        // Monday 16 to Friday 20 November 2026.
        int[] offMin = { 30, 40, 50, 60, 70 };
        int[] onMin = { 0, 15, 30, 45, 60 };
        var list = new List<PowerEvent>();
        for (int i = 0; i < 5; i++)
        {
            list.Add(new PowerEvent(Local(16 + i, 8, 0).AddMinutes(offMin[i]), "ac_off", 90));
            list.Add(new PowerEvent(Local(16 + i, 17, 0).AddMinutes(onMin[i]), "ac_on", 35));
        }
        return list;
    }

    [Fact]
    public void MedianUnplugAndPlugInTimes()
    {
        var (unplug, plugIn) = SmartInsights.Routine(Week());
        Assert.Equal(new TimeSpan(8, 50, 0), unplug);
        Assert.Equal(new TimeSpan(17, 30, 0), plugIn);
    }

    [Fact]
    public void FewerThanThreeDaysIsNoPattern()
    {
        var (unplug, plugIn) = SmartInsights.Routine(Week().Take(4).ToList());
        Assert.Null(unplug);
        Assert.Null(plugIn);
    }

    [Fact]
    public void ReadyByIsCappedAtTheLimit()
    {
        var now = Local(21, 6, 50);
        var s = new GuardianX.Core.Battery.BatterySnapshot(now, 60, 36000, 60000, 70000, 50, 30000, null, null, true, true, "LION", "HP", "Primary");
        var p = SmartInsights.Pattern(Week(), s, 30000, 80, now);
        Assert.Equal("08:50", p.UsualUnplug);
        Assert.Equal("17:30", p.UsualPlugIn);
        Assert.NotNull(p.ReadyBy);
        Assert.Equal("08:50", p.ReadyBy!.Time);
        Assert.Equal(80.0, p.ReadyBy.Percent);
        Assert.Null(p.LastsUntilPlugIn);
        Assert.Contains("08:50", p.Message);
    }

    [Fact]
    public void OnBatteryItSaysWhetherItLastsToPlugIn()
    {
        var now = Local(21, 12, 0);
        var s = new GuardianX.Core.Battery.BatterySnapshot(now, 30, 18000, 60000, 70000, 50, -9000, null, null, false, false, "LION", "HP", "Primary");
        var p = SmartInsights.Pattern(Week(), s, -9000, 80, now); // 2 h left, plug-in at 17:30
        Assert.False(p.LastsUntilPlugIn);
        Assert.Contains("runs out", p.Message);
    }

    [Fact]
    public void SmartLowWarnsOnlyWhenItRunsOutHalfAnHourEarly()
    {
        var now = Local(20, 10, 0);
        var plugIn = Local(20, 18, 0);
        Assert.True(SmartInsights.ShouldWarnSmartLow(false, 2.0, now, plugIn));
        Assert.True(SmartInsights.ShouldWarnSmartLow(false, 7.5, now, plugIn));   // empty at 17:30, exactly 30 min early
        Assert.False(SmartInsights.ShouldWarnSmartLow(false, 7.6, now, plugIn));  // empty at 17:36
        Assert.False(SmartInsights.ShouldWarnSmartLow(true, 2.0, now, plugIn));   // on the charger
        Assert.False(SmartInsights.ShouldWarnSmartLow(false, null, now, plugIn)); // rate unknown
        Assert.False(SmartInsights.ShouldWarnSmartLow(false, 2.0, now, null));    // no routine yet
    }
}

public class HeatTests
{
    [Fact]
    public void RealTemperatureWinsThenBatteryThenEstimate()
    {
        var zone = HeatSensor.Assess(54, 30, 10, false, null); // a normal idle system zone (measured 53.9 °C on the Omen)
        Assert.Equal("thermal-zone", zone.Source);
        Assert.Equal("cool", zone.Level);
        Assert.True(zone.Score < 35);
        Assert.Equal(54.0, zone.TempC);
        Assert.Equal("warm", HeatSensor.Assess(68, null, 10, false, null).Level);
        var hotZone = HeatSensor.Assess(85, null, 90, true, null);
        Assert.Equal("hot", hotZone.Level);
        Assert.True(hotZone.Score >= SmartInsights.HotScore);

        var battery = HeatSensor.Assess(null, 38, 10, false, null);
        Assert.Equal("battery", battery.Source);
        Assert.Equal("cool", battery.Level);

        var warm = HeatSensor.Assess(null, 44, 10, false, null);
        Assert.Equal("warm", warm.Level);

        var idle = HeatSensor.Assess(null, null, 5, false, null);
        Assert.Equal("estimate", idle.Source);
        Assert.Null(idle.TempC);
        Assert.Equal("cool", idle.Level);

        var gaming = HeatSensor.Assess(null, null, 90, true, 120);
        Assert.Equal("hot", gaming.Level);
        Assert.True(gaming.GpuBusy);
        Assert.Equal(120.0, gaming.ChargingW);
        Assert.Contains("estimated", gaming.Basis);
    }

    [Fact]
    public void HeatIsStoredPerSampleAndOldDatabasesGetTheColumn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gx-heat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "guardian.db");
        try
        {
            // A 2.1 database: samples without the heat column.
            using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE samples(ts INTEGER PRIMARY KEY, pct REAL, remaining INTEGER, full INTEGER, design INTEGER, cycles INTEGER,
                      rate INTEGER, voltage INTEGER, temp REAL, on_ac INTEGER, charging INTEGER);
                    INSERT INTO samples VALUES(1000, 50, 30000, 60000, 70000, 10, -9000, NULL, NULL, 0, 0);
                    """;
                cmd.ExecuteNonQuery();
            }
            var t = DateTimeOffset.Now.AddMinutes(-5);
            using (var store = new Store(path))
                store.Add(new GuardianX.Core.Battery.BatterySnapshot(t, 70, 42000, 60000, 70000, 10, -9000, null, null, false, false, "LION", "HP", "Primary"), 72.4);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var again = new Store(path); // the column already exists: opening again must not fail
            var all = again.Samples(DateTimeOffset.FromUnixTimeSeconds(0), DateTimeOffset.Now.AddMinutes(1));
            Assert.Equal(2, all.Count);
            Assert.Null(all[0].Heat);
            Assert.Equal(72.4, all[1].Heat);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void StoreFindsTheLastCalibration()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gx-cal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new Store(Path.Combine(dir, "guardian.db"));
            Assert.Null(store.LastCalibrated());
            var t = DateTimeOffset.Now.AddDays(-2);
            double[] pct = { 50, 12, 40, 80, 99.5, 100, 70, 13, 60 }; // the last low hasn't reached full yet
            for (int i = 0; i < pct.Length; i++)
                store.Add(new GuardianX.Core.Battery.BatterySnapshot(t.AddMinutes(i), pct[i], 30000, 60000, 70000, 10, null, null, null, false, false, "LION", "HP", "Primary"));
            Assert.Equal(t.AddMinutes(4).ToUnixTimeSeconds(), store.LastCalibrated()!.Value.ToUnixTimeSeconds());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}

public class RomanUrduAndActionsTests
{
    [Theory]
    [InlineData("apps", "battery jaldi kyun khatam hoti hai")]
    [InlineData("plugged", "charger lagaye rakhna theek hai?")]
    [InlineData("heat", "laptop bohat garam ho raha hai")]
    [InlineData("status", "battery kitni der chalegi")]
    [InlineData("apps", "kaun si app zyada battery kha rahi hai")]
    [InlineData("storage", "storage kitni bachi hai")]
    [InlineData("modes", "mode konsa lagaun")]
    [InlineData("habit", "meri charging aadat kaisi hai")]
    [InlineData("habit", "what's my habit score?")]
    public void RoutesRomanUrdu(string topic, string question) =>
        Assert.Equal(topic, new LocalAssistant().Ask(question, AssistantFixture.Context()).Topic);

    [Fact]
    public void AnswersCarryActionButtons()
    {
        var ctx = AssistantFixture.Context();
        var a = new LocalAssistant();
        var tips = a.Ask("How can I make my battery last longer?", ctx);
        var saver = Assert.Single(tips.Actions);
        Assert.Equal("setMode", saver.Method);
        Assert.Equal("saver", saver.Params!["id"]);
        Assert.Equal("charging", Assert.Single(a.Ask("How do I set up the 80% limit with a Tapo plug?", ctx).Actions).Navigate);
        Assert.Equal("apps", Assert.Single(a.Ask("Which apps are draining my battery?", ctx).Actions).Navigate);
        Assert.Equal("apps", Assert.Single(a.Ask("How much storage do I have?", ctx).Actions).Navigate);
        Assert.Empty(a.Ask("hello", ctx).Actions);

        var json = JsonSerializer.Serialize(new AnswerAction("Open Charging", Navigate: "charging"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("""{"label":"Open Charging","navigate":"charging"}""", json);
    }

    [Fact]
    public void HabitAndHeatAnswersUseTheNewData()
    {
        var baseCtx = AssistantFixture.Context();
        var ctx = new AssistantContext
        {
            Battery = baseCtx.Battery, Wear = baseCtx.Wear, Forecast = baseCtx.Forecast, Daily = baseCtx.Daily, Settings = new Settings(),
            Heat = HeatSensor.Assess(78.5, null, 40, true, null),
            Habit = new HabitReport(62, "C", -5, new[]
            {
                new HabitFactor("Full on the charger", 30, "About 10 h a day at 95% or more on the charger."),
                new HabitFactor("Low charge", 0, "Never below 15% this week."),
                new HabitFactor("Heat", 8, "5.3 h running hot this week."),
                new HabitFactor("Deep discharges", 0, "Never below 10% this week."),
            }),
        };
        var a = new LocalAssistant();
        var habit = a.Ask("what's my charging habit score", ctx);
        Assert.Equal("habit", habit.Topic);
        Assert.Contains("62/100", habit.Text);
        Assert.Contains("grade C", habit.Text);
        Assert.Contains("down 5", habit.Text);
        Assert.Equal("charging", habit.Actions.Single().Navigate);
        var heat = a.Ask("How hot does my battery get?", ctx);
        Assert.Contains("78.5 °C", heat.Text);
        Assert.Contains("thermal sensor", heat.Text);
        Assert.True(heat.Actions.Any(x => x.Method == "setMode"), $"topic={heat.Topic} level={ctx.Heat?.Level} mode={ctx.Mode.Id}");
    }
}

public class VendorAndSettingsTests
{
    [Theory]
    [InlineData("HP")]
    [InlineData("Hewlett-Packard")]
    public void HpHasNoSoftwareLimitSoThePlugIsUsed(string maker)
    {
        var v = VendorLimit.Describe(maker);
        Assert.Equal("HP", v.Brand);
        Assert.Null(v.NativeLimit);
        Assert.Null(v.LimitActive);
        Assert.Contains("no software charge limit", v.Note);
        Assert.Contains("smart plug", v.Note);
    }

    [Fact]
    public void AsusAndLenovo()
    {
        var asus = VendorLimit.Describe("ASUSTeK COMPUTER INC.");
        Assert.Equal("asus", asus.NativeLimit);
        Assert.Contains("not tested on real hardware", asus.Note);
        var lenovo = VendorLimit.Describe("LENOVO");
        Assert.Equal("lenovo", lenovo.NativeLimit);
        Assert.Null(lenovo.LimitActive);
        Assert.Contains("Vantage", lenovo.Note);
        Assert.Contains("smart plug", VendorLimit.Describe("Micro-Star International").Note);
    }

    [Fact]
    public void NewSettingsDefaults()
    {
        var s = new Settings();
        Assert.Equal("system", s.Theme);
        Assert.True(s.SmartLowAlert);
        Assert.True(s.ToastsOn);
    }
}

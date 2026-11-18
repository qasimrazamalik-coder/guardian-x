using GuardianX.Core;
using GuardianX.Core.Data;
using GuardianX.Core.Apps;
using GuardianX.Core.Assistant;
using GuardianX.Core.Power;
using Xunit;

namespace GuardianX.Tests;

public class ChargeControllerTests
{
    [Theory]
    // onAc, %, limit, resume, plugOn, chargeToFull, expected
    [InlineData(true, 80, 80, 60, true, false, PlugAction.TurnOff)]      // reached the limit on the charger
    [InlineData(true, 79, 80, 60, true, false, PlugAction.None)]         // still charging up
    [InlineData(false, 70, 80, 60, false, false, PlugAction.None)]       // holding, draining slowly
    [InlineData(false, 60, 80, 60, false, false, PlugAction.TurnOn)]     // dropped to resume level
    [InlineData(true, 85, 80, 60, false, false, PlugAction.ChargerNotOnPlug)] // plug off but power still arrives
    [InlineData(true, 90, 80, 60, null, false, PlugAction.None)]         // plug unreachable: never guess
    [InlineData(false, 70, 80, 60, false, true, PlugAction.TurnOn)]      // "charge to full now"
    [InlineData(true, 95, 100, 95, true, false, PlugAction.None)]        // travel mode keeps charging
    public void Decides(bool onAc, double pct, int limit, int resume, bool? plugOn, bool full, PlugAction expected) =>
        Assert.Equal(expected, ChargeController.Decide(onAc, pct, limit, resume, plugOn, full));

    [Fact]
    public void EveryModeHasAWindowThatMakesSense()
    {
        foreach (var m in Modes.All)
        {
            Assert.InRange(m.ChargeLimit, 50, 100);
            Assert.True(m.ResumeAt < m.ChargeLimit, m.Name);
        }
        Assert.Equal("desk", Modes.Get("nope").Id);
    }
}

public class SoundTests
{
    [Fact]
    public void EveryPackRendersCleanStereoForEveryCue()
    {
        foreach (var pack in Sounds.Packs)
            foreach (Cue cue in Enum.GetValues(typeof(Cue)))
            {
                var x = Sounds.Render(cue, pack);
                Assert.True(x.Length % 2 == 0, "interleaved stereo");
                double seconds = x.Length / 2.0 / Sounds.Rate;
                Assert.InRange(seconds, 0.8, 3.5);
                float peak = x.Max(Math.Abs);
                Assert.InRange(peak, 0.6f, 0.75f);                       // normalised to about -3 dBFS, never clipping
                Assert.True(Math.Abs(x[^2]) < 0.01 && Math.Abs(x[^1]) < 0.01, $"{pack}/{cue} ends with a click");
                double l = 0, r = 0, diff = 0;
                for (int i = 0; i < x.Length; i += 2) { l += x[i] * x[i]; r += x[i + 1] * x[i + 1]; diff += (x[i] - x[i + 1]) * (x[i] - x[i + 1]); }
                Assert.True(diff > (l + r) * 0.01, $"{pack}/{cue} is effectively mono");
            }
    }

    [Fact]
    public void PacksSoundDifferentAndWavIsStereoPcm()
    {
        var a = Sounds.Render(Cue.Charged, "aurora");
        var b = Sounds.Render(Cue.Charged, "pulse");
        Assert.NotEqual(a.Length, b.Length);
        var wav = Sounds.Wav(a);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(2, BitConverter.ToInt16(wav, 22));                 // channels
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));
        Assert.Equal(44 + a.Length * 2, wav.Length);
    }
}

public class TapoTests
{
    [Fact]
    public void KlapEncryptsAndDecryptsWithMatchingSequence()
    {
        var local = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var remote = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
        var auth = KlapSession.AuthHash("user@example.com", "secret");
        var sender = new KlapSession(local, remote, auth, "TP_SESSIONID=x");
        var receiver = new KlapSession(local, remote, auth, "TP_SESSIONID=x");
        var msg = System.Text.Encoding.UTF8.GetBytes("{\"method\":\"get_device_info\"}");
        var (seq, body) = sender.Encrypt(msg);
        Assert.Equal(32 + 32, body.Length);   // 32-byte signature + two AES blocks
        Assert.Equal(msg, receiver.Decrypt(seq, body));
        var (seq2, _) = sender.Encrypt(msg);
        Assert.Equal(seq + 1, seq2);
    }

    [Fact]
    public void AuthHashIsSha256OfSha1s()
    {
        var expected = System.Security.Cryptography.SHA256.HashData(
            System.Security.Cryptography.SHA1.HashData("a"u8.ToArray()).Concat(System.Security.Cryptography.SHA1.HashData("b"u8.ToArray())).ToArray());
        Assert.Equal(expected, KlapSession.AuthHash("a", "b"));
    }
}

public class AssistantTests
{
    [Fact]
    public void KnowledgeLoadsInSections()
    {
        var chunks = Knowledge.Load();
        Assert.True(chunks.Count >= 25, $"only {chunks.Count} sections");
        Assert.Contains(chunks, c => c.Source == "omen-hp" && c.Title.Contains("Adaptive Battery Optimizer"));
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    [Theory]
    [InlineData("why shouldn't I keep my laptop plugged in all the time", "Why keeping it plugged in all the time is not ideal")]
    [InlineData("how do I turn on adaptive battery optimizer F10", "Adaptive Battery Optimizer")]
    [InlineData("can it stop charging at 80% with a tapo plug", "Charge limit with a smart plug")]
    [InlineData("chrome uses too much ram", "Browsers")]
    public void KeywordSearchFindsTheRightSection(string question, string expectedTitle)
    {
        var hits = Knowledge.Keyword(Knowledge.Load(), question, 3);
        Assert.Contains(hits, h => h.Chunk.Title.Contains(expectedTitle));
    }

    [Theory]
    [InlineData("plugged", "Why shouldn't I keep it plugged in all the time?")]
    [InlineData("plugsetup", "How do I set up the 80% limit with a Tapo plug?")]
    [InlineData("apps", "Which apps are draining my battery?")]
    [InlineData("apps", "what is using so much ram")]
    [InlineData("forecast", "When will my battery reach 80% health?")]
    [InlineData("drop", "What caused my biggest battery drop?")]
    [InlineData("storage", "How much storage do I have?")]
    [InlineData("startup", "Which apps start with Windows?")]
    [InlineData("gpu", "Is the NVIDIA GPU awake?")]
    [InlineData("heat", "How hot does my battery get?")]
    [InlineData("modes", "What mode should I use?")]
    [InlineData("tips", "How can I make my battery last longer?")]
    [InlineData("sleep", "why does it drain overnight in sleep")]
    [InlineData("status", "how long will my battery last right now")]
    [InlineData("greeting", "hello")]
    public void RoutesQuestionsToTheRightTopic(string topic, string question)
    {
        Assert.Equal(topic, new LocalAssistant().Ask(question, AssistantFixture.Context()).Topic);
    }

    [Fact]
    public void AnswersUseTheLaptopsOwnNumbers()
    {
        var ctx = AssistantFixture.Context();
        var a = new LocalAssistant();
        var health = a.Ask("How is my battery health?", ctx);
        Assert.Contains("89.8%", health.Text);
        Assert.Contains("54 cycles", health.Text);
        Assert.Contains("Your Guardian X data", health.Sources);
        var apps = a.Ask("Which apps are draining my battery?", ctx);
        Assert.Contains("Microsoft Edge", apps.Text);
        var status = a.Ask("how long will my battery last right now", ctx);
        Assert.Contains("67%", status.Text);
        Assert.Contains("h", status.Text);
        Assert.NotEmpty(status.FollowUps);
    }

    [Fact]
    public void ShortFollowUpStaysOnTopicAndUnknownIsHonest()
    {
        var ctx = AssistantFixture.Context();
        var a = new LocalAssistant();
        a.Ask("Is the NVIDIA GPU awake?", ctx);
        Assert.Equal("gpu", a.Ask("and why?", ctx).Topic);
        var unknown = a.Ask("what's the capital of Peru", ctx);
        Assert.Equal("unknown", unknown.Topic);
        Assert.Contains("don't have", unknown.Text);
    }

    [Fact]
    public void KnowledgeQuestionsWithoutATopicQuoteTheBestSection()
    {
        var a = new LocalAssistant().Ask("adaptive battery optimizer F10 BIOS", AssistantFixture.Context());
        Assert.Equal("knowledge", a.Topic);
        Assert.Contains(a.Sources, s => s.Contains("Adaptive Battery Optimizer"));
    }
}

internal static class AssistantFixture
{
    public static AssistantContext Context()
    {
        var now = DateTimeOffset.Now;
        var battery = new GuardianX.Core.Battery.BatterySnapshot(now, 67, 42000, 62893, 70070, 54, -18500, 15900, null, false, false, "LION", "HP", "Primary");
        var daily = new List<(DateOnly Day, int FullMWh, int DesignMWh, int Cycles)>();
        var start = DateOnly.FromDateTime(DateTime.Today.AddDays(-120));
        for (int i = 0; i <= 120; i += 7) daily.Add((start.AddDays(i), 69500 - i * 55, 70070, 1 + i / 3));
        var wear = GuardianX.Core.Insights.BatteryInsights.Wear(battery.FullMWh, battery.DesignMWh, battery.Cycles, daily);
        return new AssistantContext
        {
            Battery = battery,
            Wear = wear,
            Forecast = GuardianX.Core.Insights.BatteryInsights.Forecast(wear, battery, 80, now),
            Daily = daily,
            ChargerHoursPerDay = 18.2,
            Today = new GuardianX.Core.Insights.StressReport(10, "Low", 0.5, 0, 0, false),
            Apps = new[]
            {
                new GuardianX.Core.Apps.AppUsage("msedge", "Microsoft Edge", 29, 3200, 5.6, 0.3, 2.4, "browser"),
                new GuardianX.Core.Apps.AppUsage("node", "Node.js", 74, 3800, 0.1, 0, 0.1, "app"),
            },
            Settings = new Settings(),
            Storage = () => new GuardianX.Core.Apps.StorageReport(
                new[] { new GuardianX.Core.Apps.DriveUsage("C:\\", "OS", 475, 14), new GuardianX.Core.Apps.DriveUsage("D:\\", "Data", 931, 90) },
                new[] { new GuardianX.Core.Apps.InstalledApp("Visual Studio", 22.5, "Microsoft") }, 3.2),
        };
    }
}

public class ForecastTests
{
    private static GuardianX.Core.Insights.WearReport Wear(double health, double? perMonth) => new(health, 100 - health, 54, 18.6, perMonth, 120);

    [Fact]
    public void HealthReaches80AtTheMeasuredRate()
    {
        var f = GuardianX.Core.Insights.BatteryInsights.Forecast(Wear(89.8, 2.0), null, 80, new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(4.9, f.MonthsTo80);
        Assert.Equal(new DateOnly(2027, 4, 17), f.HealthAt80On);
    }

    [Fact]
    public void NoDateWithoutADownwardTrend()
    {
        Assert.Null(GuardianX.Core.Insights.BatteryInsights.Forecast(Wear(95, null), null, 80, DateTimeOffset.Now).HealthAt80On);
        Assert.Null(GuardianX.Core.Insights.BatteryInsights.Forecast(Wear(95, 0), null, 80, DateTimeOffset.Now).HealthAt80On);
        Assert.Null(GuardianX.Core.Insights.BatteryInsights.Forecast(Wear(78, 2), null, 80, DateTimeOffset.Now).HealthAt80On);
    }

    [Fact]
    public void ChargingSlowsDownAbove80Percent()
    {
        // 50 Wh full, at 40%, charging at 20 W: 20 Wh to 80% takes 1 h; the last 10 Wh at half rate takes 1 h more.
        var s = new GuardianX.Core.Battery.BatterySnapshot(DateTimeOffset.Now, 40, 20000, 50000, 60000, 10, 20000, 16000, null, true, true, "LION", "HP", "x");
        var f = GuardianX.Core.Insights.BatteryInsights.Forecast(Wear(83, 1), s, 80, DateTimeOffset.Now);
        Assert.Equal(1.0, f.HoursToLimit!.Value, 2);
        Assert.Equal(2.0, f.HoursToFull!.Value, 2);
        Assert.Null(f.HoursToEmpty);
    }

    [Fact]
    public void DischargeGivesTimeToEmptyAndChargeAtNight()
    {
        var now = new DateTimeOffset(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        var s = new GuardianX.Core.Battery.BatterySnapshot(now, 60, 30000, 50000, 60000, 10, -10000, 15000, null, false, false, "LION", "HP", "x");
        var f = GuardianX.Core.Insights.BatteryInsights.Forecast(Wear(83, 1), s, 80, now);
        Assert.Equal(3.0, f.HoursToEmpty!.Value, 2);
        Assert.Equal(20, f.PercentAtNight);   // 2 h at 10 W from 30 Wh of 50 Wh
    }
}

public class SettingsTests
{
    [Theory]
    [InlineData(23, 7, 23, true)]
    [InlineData(23, 7, 3, true)]
    [InlineData(23, 7, 7, false)]
    [InlineData(23, 7, 12, false)]
    [InlineData(13, 15, 14, true)]
    [InlineData(13, 15, 16, false)]
    public void QuietHoursWrapPastMidnight(int from, int to, int hour, bool quiet)
    {
        var s = new Settings { QuietHoursOn = true, QuietFrom = from, QuietTo = to };
        Assert.Equal(quiet, s.IsQuiet(new DateTime(2026, 11, 19, hour, 30, 0)));
        Assert.False(new Settings { QuietHoursOn = false }.IsQuiet(new DateTime(2026, 11, 19, 2, 0, 0)));
    }

    [Fact]
    public void DefaultAndEditedSettingsSaveAndLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gx-set-{Guid.NewGuid():N}");
        var before = Environment.GetEnvironmentVariable("GUARDIANX_DATA");
        Environment.SetEnvironmentVariable("GUARDIANX_DATA", dir);
        try
        {
            new Settings().Save();                                   // every default must be writable as JSON
            var s = Settings.Load();
            Assert.Null(s.WidgetLeft);
            s.WidgetLeft = 1200; s.WidgetTop = 40; s.QuietHoursOn = true; s.EfficiencyApps.Add("msedge"); s.SoundPack = "crystal";
            s.Save();
            var back = Settings.Load();
            Assert.Equal(1200, back.WidgetLeft);
            Assert.True(back.QuietHoursOn);
            Assert.Contains("msedge", back.EfficiencyApps);
            Assert.Equal("crystal", back.SoundPack);
        }
        finally { Environment.SetEnvironmentVariable("GUARDIANX_DATA", before); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void WindowsAndGuardianXAreNeverThrottled()
    {
        Assert.False(EfficiencyMode.Allowed("explorer"));
        Assert.False(EfficiencyMode.Allowed("GuardianX"));
        Assert.True(EfficiencyMode.Allowed("msedge"));
    }
}

public class RecoveryTests
{
    private static GuardianX.Core.Battery.BatterySnapshot Sample(DateTimeOffset t, double pct) =>
        new(t, pct, 40000, 62900, 70070, 55, -12000, 15900, null, false, false, "LION", "HP", "Primary");

    [Fact]
    public void DamagedDatabaseIsMovedAsideAndSalvaged()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gx-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "guardian.db");
        try
        {
            var start = DateTimeOffset.Now.AddDays(-2);
            using (var store = new Store(path))
                for (int i = 0; i < 3000; i++) store.Add(Sample(start.AddMinutes(i), 50 + i % 50));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // Wreck a stretch of pages in the middle of the file.
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 40000);
            for (int i = bytes.Length / 2; i < bytes.Length / 2 + 12000; i++) bytes[i] = 0xEE;
            File.WriteAllBytes(path, bytes);

            using var repaired = new Store(path);
            Assert.NotNull(repaired.Recovery);
            Assert.Contains("readings", repaired.Recovery);
            Assert.Single(Directory.GetDirectories(dir, "damaged-*"));
            Assert.True(File.Exists(Path.Combine(Directory.GetDirectories(dir, "damaged-*")[0], "guardian.db")));
            int kept = repaired.SampleCount();
            Assert.InRange(kept, 1, 2999);                 // some rows survive, the wrecked ones don't
            Assert.Equal("ok", repaired.IntegrityCheck());
            repaired.Add(Sample(DateTimeOffset.Now, 77));    // and it works for new readings
            Assert.Equal(kept + 1, repaired.SampleCount());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void HealthyDatabaseIsLeftAlone()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gx-ok-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "guardian.db");
            using (var s = new Store(path)) s.Add(Sample(DateTimeOffset.Now, 50));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var again = new Store(path);
            Assert.Null(again.Recovery);
            Assert.Empty(Directory.GetDirectories(dir, "damaged-*"));
            Assert.Equal(1, again.SampleCount());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ReadersUseTheSnapshotWhileTheAppRuns()
    {
        var live = Path.Combine(Path.GetTempPath(), $"gx-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(live);
        try
        {
            using var app = new Store(Path.Combine(live, "guardian.db"));
            var t = DateTimeOffset.Now.AddHours(-3);
            for (int i = 0; i < 20; i++) app.Add(Sample(t.AddMinutes(i), 60));
            app.WriteSnapshot();
            for (int i = 20; i < 30; i++) app.Add(Sample(t.AddMinutes(i), 60));
            Assert.False(File.Exists(Path.Combine(live, Store.SnapshotName + ".tmp")));

            using (var whileRunning = Store.OpenCopyOfLive(live, appRunning: true))
                Assert.Equal(20, whileRunning.SampleCount());   // the consistent snapshot
            using (var whenClosed = Store.OpenCopyOfLive(live, appRunning: false))
                Assert.Equal(30, whenClosed.SampleCount());     // nothing writing, so the live files as they are
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(live, true); } catch { } }
    }
}

public class ReportTests
{
    [Fact]
    public void WeeklyReportIsSelfContainedHtml()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gx-report-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new Store(path);
            var html = GuardianX.Core.Reports.WeeklyReport.Build(store, AssistantFixture.Context(), DateTime.Today);
            Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
            Assert.Contains("89.8%", html);
            Assert.Contains("<svg", html);
            Assert.DoesNotContain("<script", html);
            Assert.DoesNotContain("http", html.Replace("http-equiv", ""));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}

public class AppTests
{
    [Fact]
    public void BatteryShareLeavesRoomForTheScreen()
    {
        Assert.Equal(5.5, AppMonitor.EstimateShare(20, 0.5), 6);   // 20 W * 55% * 50%
        Assert.Equal(0, AppMonitor.EstimateShare(20, 0));
        Assert.Equal("browser", AppMonitor.KindOf("chrome"));
        Assert.Equal("system", AppMonitor.KindOf("svchost"));
        Assert.Equal("app", AppMonitor.KindOf("Discord"));
    }

    [Fact]
    public void TipsTargetHeavyBrowsersAndBusyApps()
    {
        var apps = new List<AppUsage>
        {
            new("chrome", "Google Chrome", 24, 3200, 6, 0.1, 2.1, "browser"),
            new("Code", "Visual Studio Code", 9, 900, 22, 0.5, 4.0, "app"),
            new("svchost", "Host", 80, 900, 30, 0, null, "system"),
        };
        var tips = AppMonitor.Tips(apps, onBattery: true);
        Assert.Contains(tips, t => t.App == "Google Chrome" && t.Text.Contains("3.1 GB"));
        Assert.Contains(tips, t => t.App == "Visual Studio Code" && t.Text.Contains("4 W"));
        Assert.DoesNotContain(tips, t => t.App == "Host");
    }

    [Fact]
    public void CopyOfLiveHasAllReadingsWhileTheAppKeepsItOpen()
    {
        var live = Path.Combine(Path.GetTempPath(), $"gx-live-{Guid.NewGuid():N}");
        var copy = Path.Combine(Path.GetTempPath(), $"gx-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(live);
        try
        {
            var start = DateTimeOffset.Now.AddHours(-1);
            using var app = new Store(Path.Combine(live, "guardian.db"));   // stays open in WAL mode, like the running app
            for (int i = 0; i < 50; i++)
                app.Add(new GuardianX.Core.Battery.BatterySnapshot(start.AddMinutes(i), 80 - i * 0.5, 50000, 62900, 70070, 54,
                    -12000, 15900, null, false, false, "LION", "HP", "Primary"));
            long liveBytes = new FileInfo(Path.Combine(live, "guardian.db")).Length;

            var path = Store.CopyLiveTo(copy, live);
            using (var reader = new Store(path))
                Assert.Equal(50, reader.Samples(start.AddMinutes(-1), DateTimeOffset.Now).Count);

            // Concurrent readers (the MCP server answering two tools at once) each get their own copy.
            Parallel.For(0, 8, _ =>
            {
                using var r = Store.OpenCopyOfLive(live);
                Assert.Equal(50, r.SampleCount());
            });

            // The live database is untouched and still works.
            Assert.Equal(liveBytes, new FileInfo(Path.Combine(live, "guardian.db")).Length);
            Assert.Equal(50, app.SampleCount());
            Assert.Equal("ok", app.IntegrityCheck());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(live, true); } catch { }
            try { Directory.Delete(copy, true); } catch { }
        }
    }
}

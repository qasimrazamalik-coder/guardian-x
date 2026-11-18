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
    public void SoundsAreValidWavesAndDifferBetweenPacks()
    {
        var a = Sounds.Synth(Cue.Charged, "nebula");
        var b = Sounds.Synth(Cue.Charged, "pulse");
        Assert.NotEqual(a.Length, b.Length);
        Assert.All(a, s => Assert.InRange(s, -1f, 1f));
        Assert.True(a.Max() > 0.3f);
        var wav = Sounds.Wav(a);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
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

    [Fact]
    public void CosineOfIdenticalVectorsIsOne()
    {
        Assert.Equal(1.0, Knowledge.Cosine(new float[] { 1, 2, 3 }, new float[] { 1, 2, 3 }), 6);
        Assert.Equal(0.0, Knowledge.Cosine(new float[] { 1, 0 }, new float[] { 0, 1 }), 6);
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

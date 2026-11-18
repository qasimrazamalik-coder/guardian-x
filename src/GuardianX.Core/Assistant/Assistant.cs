using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuardianX.Core.Apps;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Insights;
using GuardianX.Core.Power;

namespace GuardianX.Core.Assistant;

public sealed record Chunk(string Id, string Source, string Title, string Text);
public sealed record Hit(Chunk Chunk, double Score);
public sealed record ChatTurn(string Role, string Content);
public sealed record AssistantStatus(bool Installed, bool Running, bool ChatModelReady, bool EmbedModelReady, string ChatModel, string EmbedModel, string ModelsFolder);

/// <summary>Talks to a local Ollama server. Nothing leaves the PC.</summary>
public sealed class Ollama
{
    public const string BaseUrl = "http://127.0.0.1:11434";
    private static readonly HttpClient Http = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(30) };

    public static string ExePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
    public static bool Installed => File.Exists(ExePath);

    public async Task<bool> RunningAsync()
    {
        try { using var cts = new CancellationTokenSource(1500); return (await Http.GetAsync("/api/version", cts.Token)).IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<List<string>> ModelsAsync()
    {
        try
        {
            var j = await Http.GetFromJsonAsync<JsonObject>("/api/tags");
            return j?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").ToList() ?? new();
        }
        catch { return new(); }
    }

    public static bool Has(IEnumerable<string> models, string want) =>
        models.Any(m => m == want || m == want + ":latest" || m.StartsWith(want + ":", StringComparison.OrdinalIgnoreCase) && !want.Contains(':'));

    /// <summary>Starts the Ollama server in the background if it's installed but not running.</summary>
    public async Task<bool> EnsureRunningAsync()
    {
        if (await RunningAsync()) return true;
        if (!Installed) return false;
        Process.Start(new ProcessStartInfo(ExePath, "serve") { CreateNoWindow = true, UseShellExecute = false });
        for (int i = 0; i < 30; i++) { await Task.Delay(500); if (await RunningAsync()) return true; }
        return false;
    }

    public async Task PullAsync(string model, Action<string, double> progress, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is string line)
        {
            if (line.Length == 0) continue;
            var j = JsonNode.Parse(line);
            if (j?["error"] is JsonNode err) throw new InvalidOperationException(err.GetValue<string>());
            double total = j?["total"]?.GetValue<double>() ?? 0, done = j?["completed"]?.GetValue<double>() ?? 0;
            progress(j?["status"]?.GetValue<string>() ?? "", total > 0 ? done / total : -1);
        }
    }

    public async Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var res = await Http.PostAsJsonAsync("/api/embed", new { model, input = texts }, ct);
        res.EnsureSuccessStatusCode();
        var j = await res.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        return j!["embeddings"]!.AsArray().Select(e => e!.AsArray().Select(x => x!.GetValue<float>()).ToArray()).ToArray();
    }

    public async Task ChatAsync(string model, IEnumerable<ChatTurn> messages, Action<string> onDelta, CancellationToken ct = default)
    {
        var body = new
        {
            model,
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            options = new { temperature = 0.3, num_ctx = 8192 }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(body) };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is string line)
        {
            if (line.Length == 0) continue;
            var j = JsonNode.Parse(line);
            if (j?["error"] is JsonNode err) throw new InvalidOperationException(err.GetValue<string>());
            var delta = j?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(delta)) onDelta(delta);
            if (j?["done"]?.GetValue<bool>() == true) break;
        }
    }
}

/// <summary>The built-in knowledge (battery science, OMEN options, Guardian X features, Windows tips) split into sections.</summary>
public static class Knowledge
{
    public static List<Chunk> Load()
    {
        var asm = typeof(Knowledge).Assembly;
        var chunks = new List<Chunk>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".md")).OrderBy(n => n))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            var text = new StreamReader(s).ReadToEnd();
            var file = name.Split('.')[^2];
            chunks.AddRange(Split(file, text));
        }
        return chunks;
    }

    internal static IEnumerable<Chunk> Split(string file, string markdown)
    {
        string doc = file, title = "";
        var sb = new StringBuilder();
        int i = 0;
        IEnumerable<Chunk> Flush()
        {
            var body = sb.ToString().Trim();
            sb.Clear();
            if (body.Length > 0) yield return new Chunk($"{file}#{i++}", file, title == "" ? doc : $"{doc}: {title}", body);
        }
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# ")) { doc = line[2..].Trim(); continue; }
            if (line.StartsWith("## ")) { foreach (var c in Flush()) yield return c; title = line[3..].Trim(); continue; }
            sb.AppendLine(line);
        }
        foreach (var c in Flush()) yield return c;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "the","a","an","is","are","to","of","and","or","in","on","it","my","i","for","what","how","why","does","do","can","with","this","that","be","at","by","me","you","your" };

    internal static string[] Words(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), "[a-z0-9%]+").Select(m => m.Value)
            .Where(w => !Stop.Contains(w) && w.Length > 1).ToArray();

    /// <summary>Keyword search (BM25), used when the embedding model isn't available.</summary>
    public static List<Hit> Keyword(IReadOnlyList<Chunk> chunks, string query, int k)
    {
        var q = Words(query).Distinct().ToArray();
        var docs = chunks.Select(c => Words(c.Title + " " + c.Text)).ToArray();
        double avg = docs.Average(d => d.Length);
        var hits = new List<Hit>();
        for (int i = 0; i < chunks.Count; i++)
        {
            double score = 0;
            foreach (var w in q)
            {
                int tf = docs[i].Count(x => x == w || x.StartsWith(w) && w.Length >= 4);
                if (tf == 0) continue;
                int df = docs.Count(d => d.Any(x => x == w || x.StartsWith(w) && w.Length >= 4));
                double idf = Math.Log(1 + (chunks.Count - df + 0.5) / (df + 0.5));
                score += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * docs[i].Length / avg));
            }
            if (score > 0) hits.Add(new Hit(chunks[i], score));
        }
        return hits.OrderByDescending(h => h.Score).Take(k).ToList();
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    public static string Hash(Chunk c) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Title + "\n" + c.Text)))[..16];
}

/// <summary>Turns Guardian X's live data into short text sections the model can quote. Shared with the MCP server.</summary>
public static class Facts
{
    public static string Build(Store store, BatterySnapshot? s, IReadOnlyList<AppUsage>? apps, PlugState? plug, Settings settings)
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;
        if (s is not null)
        {
            var daily = store.DailyCapacity(400);
            var wear = BatteryInsights.Wear(s.FullMWh, s.DesignMWh, s.Cycles, daily);
            sb.AppendLine("[LIVE: battery now]");
            sb.AppendLine($"Charge {s.Percent:0}%, {(s.OnAc ? (s.Charging ? "charging" : "on the charger, not charging") : "on battery")}, power {(s.RateMw is int r ? $"{r / 1000.0:0.0} W" : "unknown")}.");
            sb.AppendLine($"Health {wear.HealthPercent:0.0}% ({s.FullMWh} of {s.DesignMWh} mWh design), {s.Cycles} cycles, {wear.WearPercent:0.0}% worn" +
                          (wear.WearPer100Cycles is double w ? $", {w:0.0}% per 100 cycles (typical about 4%)." : "."));
            sb.AppendLine(s.TemperatureC is double t ? $"Battery temperature {t:0.0} °C." : "This battery does not report its temperature.");
            if (wear.WearPerMonth is double m) sb.AppendLine($"Measured trend: losing {m:0.0}% of capacity a month since {daily[0].Day:d MMM yyyy}.");
            if (BatteryInsights.BiggestDrop(daily) is { } drop) sb.AppendLine($"Biggest drop: {drop.Lost:0.0}% between {drop.From:d MMM} and {drop.To:d MMM yyyy}.");
            if (BatteryInsights.ChargerHoursPerDay(store.ReportPeriods()) is double ch) sb.AppendLine($"Switched on and plugged in about {ch:0.0} hours a day over the last week (Windows records).");
            var today = BatteryInsights.Stress(store.Samples(new DateTimeOffset(now.Date, now.Offset), now.AddMinutes(1)));
            sb.AppendLine($"Stress today {today.Score}/100 ({today.Level}): {today.HoursAtFull:0.0} h at 100% on the charger, {today.HoursLow:0.0} h under 10%.");
            int hold = BatteryInsights.FullHoldStreakMinutes(store.Samples(now.AddHours(-12), now.AddMinutes(1)));
            if (hold > 0) sb.AppendLine($"Sitting at 100% on the charger for {BatteryInsights.FormatMinutes(hold)} right now.");
            var sleeps = BatteryInsights.Sleeps(store.Events(now.AddDays(-60)));
            if (sleeps.Count > 0) sb.AppendLine($"Last sleep lost {sleeps[^1].PercentLost:0.0}% ({sleeps[^1].PercentPerHour:0.0}%/h){(sleeps[^1].Unusual ? ", unusual" : "")}.");
        }
        var mode = Modes.Get(settings.Mode);
        sb.AppendLine("[LIVE: mode and charge limit]");
        sb.AppendLine($"Mode {mode.Name}: {mode.Description} Charge window {mode.ResumeAt}-{mode.ChargeLimit}%. Charge limit {(settings.ChargeLimitOn ? "on" : "off")}.");
        sb.AppendLine(plug is { Configured: true } ? $"Smart plug: {plug.Name}, {plug.Status}." : "No smart plug is set up, so the charge limit can't cut power; Guardian X only alerts.");
        sb.AppendLine($"Alerts at {settings.HighPercent}% (charging) and {settings.LowPercent}% (battery), sounds {(settings.SoundsOn ? "on (" + settings.SoundPack + ")" : "off")}.");
        if (apps is { Count: > 0 })
        {
            sb.AppendLine("[LIVE: heaviest apps right now]");
            foreach (var a in apps.Where(a => a.Kind != "system").OrderByDescending(a => a.CpuPercent * 40 + a.MemoryMb / 100).Take(8))
                sb.AppendLine($"{a.Name}: {a.MemoryMb:0} MB RAM, {a.CpuPercent:0.0}% CPU, {a.Processes} processes" + (a.EstimatedWatts is double w ? $", about {w:0.0} W" : "") + ".");
        }
        var energy = store.AppEnergy(7);
        if (energy.Count > 0)
        {
            sb.AppendLine("[LIVE: estimated battery use by app, last 7 days]");
            foreach (var e in energy.Take(6)) sb.AppendLine($"{e.Name}: about {e.MWh / 1000:0.00} Wh.");
        }
        var gpu = store.GpuHolders(now.AddDays(-7));
        if (gpu.Count > 0) sb.AppendLine("[LIVE: apps that woke the NVIDIA GPU on battery, 7 days] " + string.Join(", ", gpu.Take(5).Select(g => $"{g.App} ({g.Times} min)")));
        return sb.ToString();
    }
}

/// <summary>Retrieval-augmented assistant: retrieves knowledge sections, adds live data, asks the local model.</summary>
public sealed class GuardianAssistant(Store store, Ollama ollama)
{
    private List<Chunk>? _chunks;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    public List<ChatTurn> History { get; } = new();

    public const string SystemPrompt =
        "You are Guardian X, the battery assistant built into the Guardian X app on the user's HP OMEN laptop. " +
        "Answer using the LIVE data and KNOWLEDGE sections below. Quote numbers from LIVE data exactly. " +
        "Cite knowledge you use as [K1], [K2]. If the answer isn't in the context, say you don't know rather than guessing. " +
        "Never claim the laptop or Guardian X can do something the context says it can't (for example, there is no built-in 80% charge limit on OMEN; the smart plug is the way). " +
        "Be practical and brief: lead with the answer, then 2-4 concrete steps if useful. Plain text, short paragraphs or bullets.";

    public List<Chunk> Chunks => _chunks ??= Knowledge.Load();

    /// <summary>Embeds any knowledge sections that changed since last time. Safe to call often.</summary>
    public async Task<bool> IndexAsync(string embedModel, CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var stored = store.Chunks().ToDictionary(c => c.Id);
            var todo = Chunks.Where(c => !stored.TryGetValue(c.Id, out var s) || s.Hash != Knowledge.Hash(c) || s.Vec is null).ToList();
            if (todo.Count == 0) return true;
            var vecs = await ollama.EmbedAsync(embedModel, todo.Select(c => "search_document: " + c.Title + "\n" + c.Text).ToList(), ct);
            for (int i = 0; i < todo.Count; i++) store.SaveChunk(todo[i].Id, todo[i].Source, todo[i].Text, Knowledge.Hash(todo[i]), vecs[i]);
            return true;
        }
        catch { return false; }
        finally { _indexLock.Release(); }
    }

    public async Task<List<Hit>> RetrieveAsync(string query, string embedModel, int k = 4, CancellationToken ct = default)
    {
        try
        {
            if (await IndexAsync(embedModel, ct))
            {
                var q = (await ollama.EmbedAsync(embedModel, new[] { "search_query: " + query }, ct))[0];
                var vecs = store.Chunks().Where(c => c.Vec is not null).ToDictionary(c => c.Id, c => c.Vec!);
                var semantic = Chunks.Where(c => vecs.ContainsKey(c.Id))
                    .Select(c => new Hit(c, Knowledge.Cosine(q, vecs[c.Id]))).OrderByDescending(h => h.Score).Take(k).ToList();
                if (semantic.Count > 0)
                {
                    // Blend in the best keyword match so exact terms (e.g. "F10", "Tapo") are never missed.
                    var kw = Knowledge.Keyword(Chunks, query, 1);
                    if (kw.Count > 0 && semantic.All(h => h.Chunk.Id != kw[0].Chunk.Id)) semantic[^1] = kw[0];
                    return semantic;
                }
            }
        }
        catch { }
        return Knowledge.Keyword(Chunks, query, k);
    }

    public async Task<List<Hit>> AskAsync(string question, string facts, string chatModel, string embedModel, Action<string> onDelta, CancellationToken ct = default)
    {
        var hits = await RetrieveAsync(question, embedModel, 4, ct);
        var context = new StringBuilder();
        context.AppendLine(facts);
        for (int i = 0; i < hits.Count; i++)
            context.AppendLine($"[K{i + 1}] {hits[i].Chunk.Title}\n{hits[i].Chunk.Text}");
        var messages = new List<ChatTurn> { new("system", SystemPrompt + "\n\n" + context) };
        messages.AddRange(History.TakeLast(6));
        messages.Add(new("user", question));
        var answer = new StringBuilder();
        await ollama.ChatAsync(chatModel, messages, d => { answer.Append(d); onDelta(d); }, ct);
        History.Add(new("user", question));
        History.Add(new("assistant", answer.ToString()));
        return hits;
    }
}

/// <summary>Installs Ollama and the models on the drive with the most free space.</summary>
public static class OllamaSetup
{
    public const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";

    public static string ModelsFolder()
    {
        var current = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(current)) return current;
        var best = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).OrderByDescending(d => d.AvailableFreeSpace).First();
        return Path.Combine(best.RootDirectory.FullName, "GuardianX", "ollama-models");
    }

    public static async Task InstallAsync(Action<string, double> progress, CancellationToken ct = default)
    {
        var models = ModelsFolder();
        Directory.CreateDirectory(models);
        Environment.SetEnvironmentVariable("OLLAMA_MODELS", models, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("OLLAMA_MODELS", models, EnvironmentVariableTarget.Process);
        if (Ollama.Installed) return;

        var setup = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var res = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? 0, done = 0;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(setup);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress("Downloading Ollama", total > 0 ? (double)done / total : -1);
            }
        }
        progress("Installing Ollama", -1);
        using var p = Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-") { UseShellExecute = false })!;
        await p.WaitForExitAsync(ct);
        try { File.Delete(setup); } catch { }
        if (!Ollama.Installed) throw new InvalidOperationException("Ollama didn't install. Try again, or install it from ollama.com.");
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GuardianX.Core.Apps;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Insights;
using GuardianX.Core.Power;

namespace GuardianX.Core.Assistant;

public sealed record Chunk(string Id, string Source, string Title, string Text);
public sealed record Hit(Chunk Chunk, double Score);

/// <summary>An answer from the local assistant: text, where it came from, and questions worth asking next.</summary>
public sealed record AssistantAnswer(string Text, IReadOnlyList<string> Sources, IReadOnlyList<string> FollowUps, string Topic);

/// <summary>The built-in knowledge (battery science, OMEN options, Guardian X features, Windows tips) split into sections.</summary>
public static class Knowledge
{
    private static List<Chunk>? _cache;
    public static List<Chunk> Load() => _cache ??= LoadFresh();

    private static List<Chunk> LoadFresh()
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
    {
        "the","a","an","is","are","to","of","and","or","in","on","it","my","i","for","what","how","does","do","can","with",
        "this","that","be","at","by","me","you","your","am","was","were","will","should","would","could","there","its","im",
        "about","from","so","if","then","than","any","some","get","got","have","has","had","just","tell","please","much","many"
    };

    /// <summary>Words that mean the same thing for this app. Each group is searched as one term.</summary>
    private static readonly string[][] Synonyms =
    {
        new[] { "plug", "plugged", "charger", "adapter", "ac", "mains", "power cord", "wall" },
        new[] { "drain", "draining", "drains", "consume", "consuming", "usage", "using", "eat", "eating", "hog" },
        new[] { "ram", "memory" },
        new[] { "health", "capacity", "wear", "worn", "degrade", "degradation", "aging", "ageing", "lifespan" },
        new[] { "hot", "heat", "temperature", "warm", "thermal", "cool", "cooling", "fan" },
        new[] { "gpu", "nvidia", "rtx", "graphics", "dgpu", "discrete" },
        new[] { "storage", "disk", "drive", "space", "ssd" },
        new[] { "sleep", "standby", "lid", "suspend", "overnight" },
        new[] { "limit", "cap", "80", "stop charging" },
        new[] { "smart plug", "tapo", "shelly", "p100", "p110" },
        new[] { "mode", "modes", "profile", "preset" },
        new[] { "forecast", "predict", "prediction", "future", "when", "replace", "replacement" },
        new[] { "alert", "alerts", "notification", "notifications", "notify", "sound", "sounds" },
        new[] { "clean", "cleanup", "temp", "junk", "free up" },
        new[] { "startup", "boot", "autostart" },
    };

    private static readonly Dictionary<string, int> SynGroup = BuildGroups();
    private static Dictionary<string, int> BuildGroups()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int g = 0; g < Synonyms.Length; g++)
            foreach (var w in Synonyms[g]) foreach (var part in w.Split(' ')) d.TryAdd(Stem(part), g);
        return d;
    }

    /// <summary>A light stemmer: enough to match "charging"/"charged"/"charges" and "apps"/"app".</summary>
    internal static string Stem(string w)
    {
        if (w.Length <= 3 || w.Any(char.IsDigit)) return w;
        foreach (var suf in new[] { "ations", "ation", "ings", "ing", "ness", "edly", "ed", "ies", "es", "ly", "s" })
            if (w.EndsWith(suf) && w.Length - suf.Length >= 3)
            {
                if (suf == "ies") return w[..^3] + "y";
                var stem = w[..^suf.Length];
                // "plugged" -> "plugg" -> "plug", "running" -> "runn" -> "run"
                if (suf is "ed" or "ing" && stem.Length >= 4 && stem[^1] == stem[^2] && "lsz".IndexOf(stem[^1]) < 0) stem = stem[..^1];
                return stem;
            }
        return w;
    }

    internal static string[] Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), "[a-z0-9%]+").Select(m => m.Value)
            .Where(w => !Stop.Contains(w) && (w.Length > 1 || char.IsDigit(w[0]))).Select(Stem).ToArray();

    /// <summary>Each word becomes its synonym group if it has one, so different words for one idea match.</summary>
    internal static string[] Terms(string s) => Words(s).Select(w => SynGroup.TryGetValue(w, out var g) ? "§" + g : w).ToArray();

    /// <summary>BM25 search over the knowledge sections, with stemming, synonyms and a bonus for title matches.</summary>
    public static List<Hit> Keyword(IReadOnlyList<Chunk> chunks, string query, int k)
    {
        var q = Terms(query).Distinct().ToArray();
        if (q.Length == 0 || chunks.Count == 0) return new();
        var docs = chunks.Select(c => Terms(c.Title + " " + c.Text)).ToArray();
        var titles = chunks.Select(c => Terms(c.Title).ToHashSet()).ToArray();
        double avg = docs.Average(d => d.Length);
        var hits = new List<Hit>();
        for (int i = 0; i < chunks.Count; i++)
        {
            double score = 0;
            foreach (var w in q)
            {
                int tf = docs[i].Count(x => x == w);
                if (tf == 0) continue;
                int df = docs.Count(d => d.Contains(w));
                double idf = Math.Log(1 + (chunks.Count - df + 0.5) / (df + 0.5));
                score += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * docs[i].Length / avg));
                if (titles[i].Contains(w)) score += idf * 0.8;
            }
            if (score > 0) hits.Add(new Hit(chunks[i], score));
        }
        return hits.OrderByDescending(h => h.Score).Take(k).ToList();
    }

    /// <summary>The first sentences of a section, cleaned of markdown, for quoting in an answer.</summary>
    public static string Gist(Chunk c, int sentences = 3)
    {
        var text = Regex.Replace(c.Text, @"^\s*[-*]\s+", "", RegexOptions.Multiline).Replace("**", "").Replace("`", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var parts = Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z0-9])");
        return string.Join(" ", parts.Take(sentences));
    }
}

/// <summary>Everything the assistant knows about the laptop right now, gathered once per question.</summary>
public sealed class AssistantContext
{
    public BatterySnapshot? Battery { get; init; }
    public WearReport? Wear { get; init; }
    public BatteryForecast? Forecast { get; init; }
    public IReadOnlyList<(DateOnly Day, int FullMWh, int DesignMWh, int Cycles)> Daily { get; init; } = Array.Empty<(DateOnly, int, int, int)>();
    public IReadOnlyList<ReportPeriod> Periods { get; init; } = Array.Empty<ReportPeriod>();
    public double? ChargerHoursPerDay { get; init; }
    public StressReport? Today { get; init; }
    public int HoldMinutes { get; init; }
    public SleepReport? LastSleep { get; init; }
    public IReadOnlyList<AppUsage> Apps { get; init; } = Array.Empty<AppUsage>();
    public IReadOnlyList<(string App, string Name, double MWh)> Energy7 { get; init; } = Array.Empty<(string, string, double)>();
    public IReadOnlyList<(string App, int Times)> GpuWeek { get; init; } = Array.Empty<(string, int)>();
    public IReadOnlyList<string> GpuNow { get; init; } = Array.Empty<string>();
    public GuardianMode Mode { get; init; } = Modes.All[0];
    public PlugState? Plug { get; init; }
    public Settings Settings { get; init; } = new();
    public Func<StorageReport>? Storage { get; init; }

    public static AssistantContext Build(Store store, BatterySnapshot? s, IReadOnlyList<AppUsage>? apps, PlugState? plug,
        Settings settings, IReadOnlyList<string>? gpuNow = null, Func<StorageReport>? storage = null)
    {
        var now = DateTimeOffset.Now;
        var daily = store.DailyCapacity(400);
        var periods = store.ReportPeriods();
        WearReport? wear = s is null ? null : BatteryInsights.Wear(s.FullMWh, s.DesignMWh, s.Cycles, daily);
        var mode = Modes.Get(settings.Mode);
        var sleeps = BatteryInsights.Sleeps(store.Events(now.AddDays(-60)));
        return new AssistantContext
        {
            Battery = s,
            Wear = wear,
            Forecast = wear is null ? null : BatteryInsights.Forecast(wear, s, settings.ChargeLimitOn ? mode.ChargeLimit : 100, now),
            Daily = daily,
            Periods = periods,
            ChargerHoursPerDay = BatteryInsights.ChargerHoursPerDay(periods),
            Today = BatteryInsights.Stress(store.Samples(new DateTimeOffset(now.Date, now.Offset), now.AddMinutes(1))),
            HoldMinutes = BatteryInsights.FullHoldStreakMinutes(store.Samples(now.AddHours(-12), now.AddMinutes(1))),
            LastSleep = sleeps.Count > 0 ? sleeps[^1] : null,
            Apps = apps ?? Array.Empty<AppUsage>(),
            Energy7 = store.AppEnergy(7),
            GpuWeek = store.GpuHolders(now.AddDays(-7)),
            GpuNow = gpuNow ?? Array.Empty<string>(),
            Mode = mode,
            Plug = plug,
            Settings = settings,
            Storage = storage ?? AppMonitor.Storage,
        };
    }
}

/// <summary>
/// Guardian X's assistant. It runs entirely inside the app with no AI model: it works out what the question is
/// about, answers from the laptop's live data and history, and backs the answer with the matching sections of the
/// built-in knowledge base (retrieval-augmented). Questions it can't place get the best knowledge match, or an
/// honest "I don't know" with what it can help with.
/// </summary>
public sealed class LocalAssistant
{
    private readonly List<Chunk> _chunks;
    private string? _lastTopic;

    public LocalAssistant(IEnumerable<Chunk>? knowledge = null) => _chunks = (knowledge ?? Knowledge.Load()).ToList();

    public IReadOnlyList<Chunk> Chunks => _chunks;
    public void Reset() => _lastTopic = null;

    private sealed record Topic(string Id, string[] Triggers, string[] KnowledgeQuery, Func<AssistantContext, string, Composed> Answer, string[] FollowUps);
    private sealed record Composed(string Text, bool UsedLiveData = true);

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    private Topic[]? _topics;
    private Topic[] Topics => _topics ??= new[]
    {
        new Topic("greeting", new[] { "hi", "hello", "hey", "salam", "assalam", "help", "what can you", "who are you" },
            Array.Empty<string>(), Greeting,
            new[] { "How is my battery doing?", "Why shouldn't I keep it plugged in?", "Which apps are draining my battery?" }),
        new Topic("status", new[] { "now", "current", "percent", "left", "remain", "runtime", "how long", "last", "status", "level", "charge level", "time to full", "full by" },
            new[] { "battery status charge" }, Status,
            new[] { "When will my battery reach 80% health?", "Which apps are draining my battery?", "What mode should I use?" }),
        new Topic("plugged", new[] { "plug", "plugged", "charger", "100%", "charge overnight", "charging overnight", "keep", "always", "all the time", "unplug", "charging habit", "charge to", "bad to charge" },
            new[] { "keeping laptop plugged in 100% charge ageing", "charge limit 80" }, Plugged,
            new[] { "How do I set up the 80% limit with a Tapo plug?", "What caused my biggest battery drop?", "When will my battery reach 80% health?" }),
        new Topic("plugsetup", new[] { "tapo", "shelly", "smart plug", "p100", "p110", "set up", "setup", "limit", "80% limit", "stop charging", "cut" },
            new[] { "smart plug charge limit tapo shelly setup" }, PlugSetup,
            new[] { "Why shouldn't I keep it plugged in?", "What mode should I use?", "How is my battery doing?" }),
        new Topic("health", new[] { "health", "wear", "worn", "capacity", "degrade", "cycle", "cycles", "trend", "doing", "condition", "old", "good", "bad" },
            new[] { "battery health wear cycles capacity" }, Health,
            new[] { "What caused my biggest battery drop?", "When will my battery reach 80% health?", "How can I make my battery last longer?" }),
        new Topic("drop", new[] { "drop", "dropped", "caused", "cause", "sudden", "fell", "why did", "lost" },
            new[] { "what ages a lithium battery heat high charge" }, Drop,
            new[] { "Why shouldn't I keep it plugged in?", "When will my battery reach 80% health?", "How hot does my battery get?" }),
        new Topic("forecast", new[] { "when", "forecast", "predict", "future", "replace", "replacement", "80% health", "how long will", "years", "months", "reach" },
            new[] { "battery health wear replacement" }, Forecast,
            new[] { "How can I make my battery last longer?", "Why shouldn't I keep it plugged in?", "What caused my biggest battery drop?" }),
        new Topic("apps", new[] { "app", "apps", "drain", "draining", "using", "ram", "memory", "cpu", "heavy", "slow", "process", "browser", "chrome", "edge", "which", "eating" },
            new[] { "apps battery ram cpu browser efficiency mode" }, Apps,
            new[] { "How can I make my battery last longer?", "Is the NVIDIA GPU awake?", "How much storage do I have?" }),
        new Topic("storage", new[] { "storage", "disk", "space", "drive", "full", "clean", "cleanup", "temp", "big files", "large files", "free up", "ssd" },
            new[] { "storage temp files cleanup disk" }, Storage,
            new[] { "Which apps start with Windows?", "Which apps are draining my battery?", "How is my battery doing?" }),
        new Topic("startup", new[] { "startup", "start with windows", "boot", "autostart", "background apps" },
            new[] { "startup apps windows battery" }, Startup,
            new[] { "How much storage do I have?", "Which apps are draining my battery?" }),
        new Topic("sleep", new[] { "sleep", "standby", "lid", "suspend", "overnight", "closed", "hibernate" },
            new[] { "sleep drain modern standby hibernate" }, Sleep,
            new[] { "Is the NVIDIA GPU awake?", "How can I make my battery last longer?" }),
        new Topic("gpu", new[] { "gpu", "nvidia", "rtx", "graphics", "dgpu", "discrete", "5060" },
            new[] { "nvidia gpu battery optimus" }, Gpu,
            new[] { "Which apps are draining my battery?", "What mode should I use?" }),
        new Topic("heat", new[] { "hot", "heat", "temperature", "warm", "thermal", "fan", "cool", "cooling" },
            new[] { "heat temperature battery ageing cooling" }, Heat,
            new[] { "What mode should I use?", "Why shouldn't I keep it plugged in?" }),
        new Topic("modes", new[] { "mode", "modes", "gaming", "travel", "saver", "desk", "which mode", "profile", "performance", "efficiency" },
            new[] { "modes desk gaming travel saver" }, ModesAnswer,
            new[] { "How do I set up the 80% limit with a Tapo plug?", "How can I make my battery last longer?" }),
        new Topic("alerts", new[] { "alert", "alerts", "notification", "notify", "sound", "sounds", "20%", "80%", "beep", "quiet" },
            new[] { "alerts sounds notifications" }, Alerts,
            new[] { "What mode should I use?", "How is my battery doing?" }),
        new Topic("tips", new[] { "last longer", "longer", "save", "saving", "improve", "extend", "tips", "better battery", "preserve", "protect", "optimi" },
            new[] { "battery life tips windows power efficiency" }, Tips,
            new[] { "Which apps are draining my battery?", "Why shouldn't I keep it plugged in?", "What mode should I use?" }),
    };

    /// <summary>Scores each topic by the trigger words and phrases in the question.</summary>
    internal string Route(string question)
    {
        var q = " " + Regex.Replace(question.ToLowerInvariant(), "[^a-z0-9% ]", " ") + " ";
        var stems = Knowledge.Words(question).ToHashSet();
        string best = "";
        double bestScore = 0;
        foreach (var t in Topics)
        {
            double score = 0;
            var counted = new HashSet<string>(); // "app" and "apps" are one match, not two
            foreach (var trig in t.Triggers)
            {
                if (trig.Contains(' ') || trig.Contains('%'))
                { if ((q.Contains(" " + trig + " ") || q.Contains(" " + trig)) && counted.Add(trig)) score += 2.5; }
                else { var st = Knowledge.Stem(trig); if (stems.Contains(st) && counted.Add(st)) score += 1.0; }
            }
            if (score > bestScore) { bestScore = score; best = t.Id; }
        }
        // A short follow-up ("and why?", "how?") stays on the last topic.
        if (bestScore == 0 && _lastTopic is not null && Knowledge.Words(question).Length <= 3 &&
            Regex.IsMatch(q, @" (why|how|more|explain|details?|that|it|this|so|really|what about|and) ")) return _lastTopic;
        return bestScore > 0 ? best : "";
    }

    public AssistantAnswer Ask(string question, AssistantContext ctx)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0)
            return new AssistantAnswer("Ask me anything about your battery, charging, apps or storage.", Array.Empty<string>(), Topics[0].FollowUps, "empty");

        var topicId = Route(question);
        var topic = Topics.FirstOrDefault(t => t.Id == topicId);
        var hits = Knowledge.Keyword(_chunks, question + " " + string.Join(" ", topic?.KnowledgeQuery ?? Array.Empty<string>()), 3);

        if (topic is null)
        {
            _lastTopic = null;
            if (hits.Count == 0 || hits[0].Score < 1.5)
                return new AssistantAnswer(
                    "I don't have anything on that in my knowledge base. I answer from this laptop's battery data and from " +
                    "built-in guides on battery care, charging, HP OMEN settings, apps, storage and Windows power options.",
                    Array.Empty<string>(), Topics[0].FollowUps, "unknown");
            var best = hits[0].Chunk;
            var text = new StringBuilder();
            text.AppendLine($"From my knowledge base ({best.Title}):");
            text.AppendLine(Knowledge.Gist(best, 4));
            if (hits.Count > 1 && hits[1].Score > hits[0].Score * 0.6)
            {
                text.AppendLine();
                text.AppendLine($"Related ({hits[1].Chunk.Title}): {Knowledge.Gist(hits[1].Chunk, 2)}");
            }
            return new AssistantAnswer(text.ToString().Trim(), hits.Take(2).Select(h => h.Chunk.Title).ToList(), Topics[1].FollowUps, "knowledge");
        }

        _lastTopic = topic.Id;
        var composed = topic.Answer(ctx, question);
        var sb = new StringBuilder(composed.Text.Trim());
        // Back the answer with the strongest knowledge section, unless the answer already covers it.
        var support = hits.FirstOrDefault(h => h.Score >= 1.5);
        var sources = new List<string>();
        if (composed.UsedLiveData) sources.Add("Your Guardian X data");
        if (support is not null && topic.Id is not ("greeting" or "status"))
        {
            sb.AppendLine().AppendLine();
            sb.Append("Why: ").Append(Knowledge.Gist(support.Chunk, 2));
            sources.Add(support.Chunk.Title);
        }
        return new AssistantAnswer(sb.ToString(), sources, topic.FollowUps.Where(f => !Same(f, question)).Take(3).ToList(), topic.Id);
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim('?', ' '), b.Trim('?', ' '), StringComparison.OrdinalIgnoreCase);

    // ---------- answers ----------

    private static string H(double hours) => BatteryInsights.FormatHours(hours);
    private static string Pct(double v) => v.ToString("0.#", En) + "%";

    private static Composed Greeting(AssistantContext c, string q)
    {
        var s = c.Battery;
        var lead = s is null ? "Hi. I can't read the battery right now, but I can still help." :
            $"Hi. You're at {s.Percent:0}% {(s.OnAc ? "on the charger" : "on battery")}, with battery health at {Pct(c.Wear!.HealthPercent)}.";
        return new(lead + "\n\nI run entirely on this laptop, with no AI model and nothing sent online. Ask me about battery health and forecasts, " +
                   "charging habits and the 80% limit, apps using battery or RAM, storage and cleanup, sleep drain, the NVIDIA GPU, heat, modes and alerts.");
    }

    private static Composed Status(AssistantContext c, string q)
    {
        var s = c.Battery;
        if (s is null) return new("I can't read the battery right now. If this keeps happening, Settings > Repair checks the battery connection.", false);
        var sb = new StringBuilder();
        string state = s.OnAc ? (s.Charging ? "charging" : "on the charger and not charging") : "on battery";
        sb.Append($"You're at {s.Percent:0}%, {state}");
        if (s.RateMw is int r && r != 0) sb.Append($", {(r < 0 ? "drawing" : "taking in")} {Math.Abs(r) / 1000.0:0.0} W");
        sb.AppendLine(".");
        var f = c.Forecast;
        if (f?.HoursToEmpty is double e) sb.AppendLine($"At this rate that's about {H(e)} left" + (f.PercentAtNight is double n ? $", and about {n:0}% by 11 pm." : "."));
        if (f?.HoursToLimit is double l) sb.AppendLine($"About {H(l)} until your {c.Mode.ChargeLimit}% limit.");
        if (f?.HoursToFull is double full) sb.AppendLine($"About {H(full)} to full (the last 20% charges more slowly).");
        sb.AppendLine($"Battery health is {Pct(c.Wear!.HealthPercent)} after {s.Cycles} cycles. Mode: {c.Mode.Name} ({c.Mode.ResumeAt}–{c.Mode.ChargeLimit}%).");
        if (c.HoldMinutes >= 30) sb.AppendLine($"It has sat at 100% on the charger for {BatteryInsights.FormatMinutes(c.HoldMinutes)}. Unplugging for a while would help.");
        return new(sb.ToString());
    }

    private static Composed Plugged(AssistantContext c, string q)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Keeping it plugged in isn't dangerous, but it ages the battery faster. A lithium battery wears quickest when it sits " +
                      "at a high charge and is warm, and a gaming laptop on the charger is usually at 100% with the CPU and GPU heating it.");
        if (c.ChargerHoursPerDay is double ch)
            sb.AppendLine($"\nYour laptop has been switched on and plugged in about {ch:0.0} hours a day over the last week (Windows' records).");
        if (BatteryInsights.BiggestDrop(c.Daily) is { } d)
            sb.AppendLine($"Your biggest capacity drop was {Pct(d.Lost)} between {d.From.ToString("d MMM", En)} and {d.To.ToString("d MMM", En)}.");
        sb.AppendLine("\nWhat helps most:");
        sb.AppendLine(c.Plug is { Configured: true }
            ? $"1. Keep the charge limit on. Your plug ({c.Plug.Name}) cuts power at {c.Mode.ChargeLimit}% and resumes at {c.Mode.ResumeAt}%."
            : "1. Hold it between 40% and 80%. HP doesn't offer a charge limit on OMEN laptops, so the reliable way is a TP-Link Tapo or Shelly smart plug that Guardian X switches for you (Charging page). Without one, unplug when the 80% alert sounds.");
        sb.AppendLine("2. Keep it cool: hard surface, clear vents, and Balanced or Efficiency power mode when you aren't gaming.");
        sb.AppendLine("3. Charge to 100% only before you need the full runtime (Travel mode, or Charge to full now).");
        return new(sb.ToString());
    }

    private static Composed PlugSetup(AssistantContext c, string q)
    {
        var sb = new StringBuilder();
        if (c.Plug is { Configured: true })
            sb.AppendLine($"Your smart plug is set up: {c.Plug.Name}, {c.Plug.Status}. Guardian X turns it off at {c.Mode.ChargeLimit}% and back on at {c.Mode.ResumeAt}% while the charge limit is on.");
        else
            sb.AppendLine("You don't have a smart plug set up yet, so Guardian X can only alert you at the limit.");
        sb.AppendLine("\nTo set up the 80% limit:");
        sb.AppendLine("1. Plug the laptop charger into a TP-Link Tapo P100/P110 or a Shelly Plug, and set the plug up on your Wi-Fi with its own app.");
        sb.AppendLine("2. Find its IP address in the plug's app or your router (it's best to reserve it, so it doesn't change).");
        sb.AppendLine("3. In Guardian X, open Charging > Smart plug, pick the type, enter the IP (and your Tapo account email and password for Tapo), then Save & test.");
        sb.AppendLine("4. Leave Charge limit on. Pick Desk mode (60–80%) for daily use, or Travel mode before a trip.");
        sb.AppendLine("\nTo try it without a plug, choose \"Try without\". That uses a simulated plug so you can watch the limiter work.");
        return new(sb.ToString());
    }

    private static Composed Health(AssistantContext c, string q)
    {
        if (c.Wear is not { } w || c.Battery is not { } s) return new("I can't read the battery right now.", false);
        var sb = new StringBuilder();
        string verdict = w.HealthPercent >= 95 ? "excellent" : w.HealthPercent >= 88 ? "good" : w.HealthPercent >= 80 ? "fair" : "worn";
        sb.AppendLine($"Battery health is {Pct(w.HealthPercent)} ({verdict}): it holds {s.FullMWh / 1000.0:0.0} Wh of its {s.DesignMWh / 1000.0:0.0} Wh design capacity after {s.Cycles} cycles.");
        if (w.WearPer100Cycles is double per100)
            sb.AppendLine(per100 > BatteryInsights.TypicalWearPer100Cycles * 1.5
                ? $"That's {Pct(per100)} wear per 100 cycles, against about 4% for a typical battery, so it's ageing faster than its use explains. Time at 100% and heat cause that, not cycles."
                : $"That's {Pct(per100)} wear per 100 cycles, close to a typical battery (about 4%).");
        if (w.WearPerMonth is double m) sb.AppendLine($"Over the last {w.TrendDays} days it has lost about {Pct(m)} of capacity a month.");
        if (BatteryInsights.BiggestDrop(c.Daily) is { } d) sb.AppendLine($"The biggest drop was {Pct(d.Lost)} between {d.From.ToString("d MMM", En)} and {d.To.ToString("d MMM", En)}.");
        if (c.Forecast?.HealthAt80On is DateOnly at80) sb.AppendLine($"If that trend continues, it reaches 80% health around {at80.ToString("MMMM yyyy", En)}.");
        return new(sb.ToString());
    }

    private static Composed Drop(AssistantContext c, string q)
    {
        if (BatteryInsights.BiggestDrop(c.Daily) is not { } d)
            return new("I don't see a drop of 1% or more in any two-week stretch of your history, which is good news.");
        var sb = new StringBuilder();
        sb.AppendLine($"Your biggest drop was {Pct(d.Lost)} of design capacity between {d.From.ToString("d MMM", En)} and {d.To.ToString("d MMM yyyy", En)}.");
        var from = d.From.ToDateTime(TimeOnly.MinValue);
        var to = d.To.ToDateTime(TimeOnly.MaxValue);
        var inside = c.Periods.Where(p => p.End > from && p.Start < to).ToList();
        if (inside.Count > 0)
        {
            double days = inside.Sum(p => p.Days);
            double charger = inside.Sum(p => (p.ActiveOnCharger + p.StandbyOnCharger).TotalHours) / days;
            double battery = inside.Sum(p => (p.ActiveOnBattery + p.StandbyOnBattery).TotalHours) / days;
            int cycles = inside.Max(p => p.Cycles) - inside.Min(p => p.Cycles);
            sb.AppendLine($"In Windows' records for that time the laptop averaged {charger:0.0} h a day plugged in and {battery:0.0} h on battery, and used about {Math.Max(cycles, 0)} cycles.");
            sb.AppendLine(c.ChargerHoursPerDay is double now && now > charger + 2
                ? $"That's less charger time than your current {now:0.0} h a day, so being plugged in wasn't the main cause. Heat (gaming or heavy work) and deep discharges are the usual suspects."
                : "Long hours plugged in at a high charge, especially while warm, are the most likely cause.");
        }
        sb.AppendLine("Windows' capacity readings also jump around by a percent or two after a calibration, so a single step can look bigger than the real loss.");
        return new(sb.ToString());
    }

    private static Composed Forecast(AssistantContext c, string q)
    {
        if (c.Forecast is not { } f) return new("I need a battery reading to forecast anything.", false);
        var sb = new StringBuilder();
        if (f.HealthAt80On is DateOnly at80)
        {
            sb.AppendLine($"At your measured rate (about {Pct(f.LossPerMonth!.Value)} a month) the battery reaches 80% health around {at80.ToString("MMMM yyyy", En)}, about {f.MonthsTo80:0.#} months from now.");
            sb.AppendLine("80% is the usual point where people consider a replacement. It still works below that, just with less runtime.");
            sb.AppendLine("Slow that by keeping it under 80% charge and cool. Even halving the wear rate would roughly double that time.");
        }
        else if (f.HealthNow <= 80)
            sb.AppendLine($"Health is already {Pct(f.HealthNow)}, at or below the usual 80% replacement point. It still works, with less runtime.");
        else
            sb.AppendLine($"Health is {Pct(f.HealthNow)}. {f.Basis}");
        if (f.HoursToEmpty is double e) sb.AppendLine($"\nRight now: about {H(e)} of battery left at the current draw" + (f.PercentAtNight is double n ? $", about {n:0}% by 11 pm." : "."));
        if (f.HoursToLimit is double l) sb.AppendLine($"\nRight now: about {H(l)} to your charge limit.");
        else if (f.HoursToFull is double fl) sb.AppendLine($"\nRight now: about {H(fl)} to full.");
        return new(sb.ToString());
    }

    private static Composed Apps(AssistantContext c, string q)
    {
        var apps = c.Apps.Where(a => a.Kind != "system").ToList();
        if (apps.Count == 0) return new("I don't have a reading of running apps yet. Open the Apps page and ask again in a few seconds.", false);
        bool onBattery = apps.Any(a => a.EstimatedWatts is not null);
        var sb = new StringBuilder();
        var ql = q.ToLowerInvariant();
        IEnumerable<AppUsage> top;
        if (ql.Contains("ram") || ql.Contains("memory"))
        {
            top = apps.OrderByDescending(a => a.MemoryMb).Take(5);
            sb.AppendLine("Using the most memory right now:");
            foreach (var a in top) sb.AppendLine($"• {a.Name}: {a.MemoryMb / 1024:0.0} GB across {a.Processes} process{(a.Processes == 1 ? "" : "es")}");
        }
        else
        {
            top = apps.OrderByDescending(a => a.EstimatedWatts ?? a.CpuPercent / 10).Take(5);
            sb.AppendLine(onBattery ? "Using the most battery right now (estimated from each app's share of CPU):" : "Busiest right now (you're on the charger, so these are by CPU):");
            foreach (var a in top)
                sb.AppendLine($"• {a.Name}: {(a.EstimatedWatts is double w ? $"about {w:0.0} W, " : "")}{a.CpuPercent:0.0}% CPU, {a.MemoryMb / 1024:0.0} GB RAM");
        }
        if (c.Energy7.Count > 0)
            sb.AppendLine("\nOver the last 7 days on battery: " + string.Join(", ", c.Energy7.Take(3).Select(e => $"{e.Name} about {e.MWh / 1000:0.0} Wh")) + ".");
        var tips = AppMonitor.Tips(c.Apps, onBattery);
        if (tips.Count > 0) { sb.AppendLine("\nWhat to do:"); foreach (var t in tips.Take(3)) sb.AppendLine("• " + t.Text); }
        sb.AppendLine("\nTip: Apps & storage > Efficiency mode lets Windows run a background app on low-power cores while you're on battery.");
        return new(sb.ToString());
    }

    private static Composed Storage(AssistantContext c, string q)
    {
        StorageReport r;
        try { r = c.Storage!(); } catch { return new("I couldn't read the drives just now.", false); }
        var sb = new StringBuilder();
        foreach (var d in r.Drives)
            sb.AppendLine($"• {d.Name} {(d.Label == "" ? "" : "(" + d.Label + ") ")}: {d.FreeGb:0} GB free of {d.TotalGb:0} GB" + (d.FreeGb / Math.Max(d.TotalGb, 1) < 0.1 ? " (nearly full)" : ""));
        sb.AppendLine($"\nTemporary files: about {r.TempGb:0.0} GB.");
        if (r.LargestApps.Count > 0) sb.AppendLine("Largest apps: " + string.Join(", ", r.LargestApps.Take(4).Select(a => $"{a.Name} ({a.SizeGb:0.0} GB)")) + ".");
        sb.AppendLine("\nTo free space: Apps & storage > Cleanup clears temporary files (asks first) and finds your biggest files. Uninstall large apps you don't use from Windows Settings > Apps.");
        return new(sb.ToString());
    }

    private static Composed Startup(AssistantContext c, string q) =>
        new("Apps that start with Windows run in the background all day and use battery and memory. Open Apps & storage > Cleanup > " +
            "Start-up apps to see them and switch off the ones you don't need at start-up. Turning one off doesn't uninstall it; " +
            "it just won't start on its own. Apps installed for all users can only be changed from Task Manager as administrator.", false);

    private static Composed Sleep(AssistantContext c, string q)
    {
        var sb = new StringBuilder();
        if (c.LastSleep is { } s)
            sb.AppendLine($"Last sleep ({s.Start.ToString("d MMM HH:mm", En)}–{s.End.ToString("HH:mm", En)}) lost {Pct(s.PercentLost)}, {Pct(s.PercentPerHour)} an hour{(s.Unusual ? ", which is unusual for this laptop" : "")}.");
        else
            sb.AppendLine("I haven't recorded a sleep of 30 minutes or more yet.");
        sb.AppendLine("\nModern laptops use Modern Standby, which keeps Wi-Fi and some apps awake, so 1–2% an hour is normal. More than that usually means an app or the NVIDIA GPU keeps it busy.");
        sb.AppendLine("• For long breaks, use Hibernate (no drain at all).");
        sb.AppendLine("• Close games and GPU apps before closing the lid.");
        sb.AppendLine("• Guardian X alerts you when a sleep drains unusually fast (Settings > Alerts).");
        return new(sb.ToString());
    }

    private static Composed Gpu(AssistantContext c, string q)
    {
        var sb = new StringBuilder();
        sb.AppendLine(c.GpuNow.Count > 0
            ? $"The NVIDIA GPU is awake right now, held by {string.Join(", ", c.GpuNow)}."
            : "The NVIDIA GPU is asleep right now; no app is holding it.");
        if (c.GpuWeek.Count > 0) sb.AppendLine("On battery this week it was woken by " + string.Join(", ", c.GpuWeek.Take(4).Select(g => $"{g.App} ({g.Times} min)")) + ".");
        sb.AppendLine("\nThe RTX GPU can use 10–30 W on its own, which can halve battery time. On battery:");
        sb.AppendLine("• Close games and 3D apps when unplugged.");
        sb.AppendLine("• In Windows Settings > System > Display > Graphics, set browsers and chat apps to Power saving so they use the built-in GPU.");
        sb.AppendLine("• Guardian X alerts you when an app wakes the GPU on battery.");
        return new(sb.ToString());
    }

    private static Composed Heat(AssistantContext c, string q)
    {
        var sb = new StringBuilder();
        sb.AppendLine(c.Battery?.TemperatureC is double t
            ? $"The battery is at {t:0.0} °C right now ({(t >= 40 ? "hot" : t >= 35 ? "warm" : "fine")})."
            : "This battery doesn't report its temperature to Windows, so I can't measure it directly.");
        if (c.Today is { TemperatureKnown: true } today) sb.AppendLine($"Today it spent {today.HoursHot:0.0} h above 40 °C.");
        sb.AppendLine("\nHeat ages a battery as much as sitting at 100%, and the two together are the worst case. To keep it cooler:");
        sb.AppendLine("• Use it on a hard surface with the vents clear, and clean the fans every few months.");
        sb.AppendLine("• Use Balanced or Efficiency power mode when you aren't gaming (Desk or Saver mode).");
        sb.AppendLine("• When gaming plugged in, keep the charge under 80% so heat and full charge don't stack.");
        return new(sb.ToString(), c.Battery?.TemperatureC is not null);
    }

    private static Composed ModesAnswer(AssistantContext c, string q)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You're in {c.Mode.Name} mode: {c.Mode.Description}");
        string pick = c.Battery switch
        {
            { OnAc: true } when c.GpuNow.Count > 0 => "gaming",
            { OnAc: true } => "desk",
            { OnAc: false, Percent: < 40 } => "saver",
            _ => c.Mode.Id,
        };
        var p = Modes.Get(pick);
        if (pick != c.Mode.Id) sb.AppendLine($"\nRight now I'd suggest {p.Name}: {p.Description}");
        sb.AppendLine("\n• Desk (60–80%, Balanced): most days at the desk.");
        sb.AppendLine("• Gaming (60–80%, Best performance): full speed, still keeps the charge low because games heat it.");
        sb.AppendLine("• Travel (to 100%): before you leave, for the full runtime.");
        sb.AppendLine("• Saver (50–80%, Best efficiency): longest time on battery.");
        sb.AppendLine("\nAuto-actions (Settings) can switch modes for you when you plug in, unplug or start a game.");
        return new(sb.ToString());
    }

    private static Composed Alerts(AssistantContext c, string q)
    {
        var st = c.Settings;
        var sb = new StringBuilder();
        sb.AppendLine($"Alerts: charged to {st.HighPercent}% {(st.NotifyHigh ? "on" : "off")}, low battery at {st.LowPercent}% {(st.NotifyLow ? "on" : "off")}, " +
                      $"sitting full for {st.FullHoldMinutes / 60.0:0.#} h {(st.NotifyFullHold ? "on" : "off")}, sleep drain {(st.NotifySleepDrain ? "on" : "off")}, NVIDIA GPU on battery {(st.NotifyGpu ? "on" : "off")}.");
        sb.AppendLine($"Sounds are {(st.SoundsOn ? "on, using the " + st.SoundPack + " pack" : "off")}.");
        if (st.QuietHoursOn) sb.AppendLine($"Quiet hours: {st.QuietFrom:00}:00–{st.QuietTo:00}:00, alerts stay silent then.");
        sb.AppendLine("\nChange them in Settings > Alerts and Settings > Sounds, where you can preview each sound.");
        return new(sb.ToString());
    }

    private static Composed Tips(AssistantContext c, string q)
    {
        var sb = new StringBuilder();
        sb.AppendLine("For longer battery life today and a longer-lasting battery overall:");
        int n = 1;
        if (c.Battery is { OnAc: false } && c.Mode.Id != "saver") sb.AppendLine($"{n++}. Switch to Saver mode on battery (Best power efficiency).");
        var heavy = c.Apps.Where(a => a.Kind != "system" && a.EstimatedWatts is double w && w >= 1).OrderByDescending(a => a.EstimatedWatts).FirstOrDefault();
        if (heavy is not null) sb.AppendLine($"{n++}. {heavy.Name} is the biggest drain right now (about {heavy.EstimatedWatts:0.0} W). Close it or turn on Efficiency mode for it.");
        if (c.GpuNow.Count > 0) sb.AppendLine($"{n++}. The NVIDIA GPU is awake ({string.Join(", ", c.GpuNow)}). Close that app when on battery.");
        sb.AppendLine($"{n++}. Lower screen brightness. The display is one of the biggest consumers.");
        sb.AppendLine($"{n++}. Keep the charge between 40% and 80% day to day, and charge to 100% only before you need it.");
        sb.AppendLine($"{n++}. Keep it cool: hard surface, clear vents.");
        if (c.ChargerHoursPerDay is double ch && ch >= 12) sb.AppendLine($"{n++}. It's on the charger about {ch:0} h a day. A smart plug with the 80% limit would protect it the most.");
        return new(sb.ToString());
    }
}

/// <summary>Turns Guardian X's live data into short text sections. Used by the MCP server's briefing tool.</summary>
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
            var mode0 = Modes.Get(settings.Mode);
            var f = BatteryInsights.Forecast(wear, s, settings.ChargeLimitOn ? mode0.ChargeLimit : 100, now);
            sb.AppendLine("[LIVE: battery now]");
            sb.AppendLine($"Charge {s.Percent:0}%, {(s.OnAc ? (s.Charging ? "charging" : "on the charger, not charging") : "on battery")}, power {(s.RateMw is int r ? $"{r / 1000.0:0.0} W" : "unknown")}.");
            sb.AppendLine($"Health {wear.HealthPercent:0.0}% ({s.FullMWh} of {s.DesignMWh} mWh design), {s.Cycles} cycles, {wear.WearPercent:0.0}% worn" +
                          (wear.WearPer100Cycles is double w ? $", {w:0.0}% per 100 cycles (typical about 4%)." : "."));
            sb.AppendLine(s.TemperatureC is double t ? $"Battery temperature {t:0.0} °C." : "This battery does not report its temperature.");
            if (wear.WearPerMonth is double m) sb.AppendLine($"Measured trend: losing {m:0.0}% of capacity a month since {daily[0].Day:d MMM yyyy}.");
            if (f.HealthAt80On is DateOnly at80) sb.AppendLine($"Forecast: reaches 80% health around {at80:MMMM yyyy} at the current trend.");
            if (f.HoursToEmpty is double e) sb.AppendLine($"Forecast: about {BatteryInsights.FormatHours(e)} to empty at the current draw.");
            if (f.HoursToFull is double fl) sb.AppendLine($"Forecast: about {BatteryInsights.FormatHours(fl)} to full.");
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

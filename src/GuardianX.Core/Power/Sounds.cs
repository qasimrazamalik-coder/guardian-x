using System.Media;

namespace GuardianX.Core.Power;

public enum Cue { Charged, Low, Unplugged, PluggedIn, Alert }

/// <summary>
/// Guardian X's own sounds, synthesised on this PC (no stock Windows sounds, no audio files to license).
/// Stereo 44.1 kHz: layered, slightly detuned voices with proper envelopes, a warmth filter and a small-room reverb.
/// Every pack plays the same melody for a cue, so the meaning is learnt once: rising when power arrives, falling
/// when it leaves, a resolving arpeggio when charged, an urgent minor figure for low battery, two soft pings for news.
/// </summary>
public static class Sounds
{
    public const int Rate = 44100;
    public const int Version = 3; // bump to regenerate files after a sound change
    public static readonly string[] Packs = { "aurora", "crystal", "pulse", "nebula" };

    public static string Folder => Path.Combine(Data.Store.DefaultFolder, "sounds");

    public static string FileFor(string pack, Cue cue) => Path.Combine(Folder, $"v{Version}-{pack}-{cue.ToString().ToLowerInvariant()}.wav");

    public static void Play(Cue cue, string pack = "aurora")
    {
        try
        {
            if (!Packs.Contains(pack)) pack = "aurora";
            var path = FileFor(pack, cue);
            if (!File.Exists(path)) Generate();
            using var player = new SoundPlayer(path);
            player.Play();
        }
        catch { }
    }

    public static void Generate()
    {
        Directory.CreateDirectory(Folder);
        foreach (var pack in Packs)
            foreach (Cue cue in Enum.GetValues(typeof(Cue)))
            {
                var path = FileFor(pack, cue);
                if (!File.Exists(path)) File.WriteAllBytes(path, Wav(Render(cue, pack)));
            }
        // Remove sounds from older versions.
        foreach (var old in Directory.EnumerateFiles(Folder, "*.wav").Where(f => !Path.GetFileName(f).StartsWith($"v{Version}-")))
            try { File.Delete(old); } catch { }
    }

    // ---------- melodies ----------

    private readonly record struct Note(double Start, double Midi, double Length, double Gain = 1, double Pan = 0);

    private static Note[] Motif(Cue cue) => cue switch
    {
        // C5 E5 G5 then C6 over a soft G4/C5 bed: "done, resolved".
        Cue.Charged => new Note[] { new(0.00, 72, 0.9, 0.85, -0.35), new(0.11, 76, 0.9, 0.8, -0.1), new(0.22, 79, 0.9, 0.8, 0.15), new(0.36, 84, 1.4, 1.0, 0.3), new(0.36, 67, 1.4, 0.35, -0.2) },
        // E5 C5 A4, twice, the second a little softer: "attention, act soon".
        Cue.Low => new Note[] { new(0.00, 76, 0.32, 0.9, 0.2), new(0.14, 72, 0.32, 0.85, -0.1), new(0.28, 69, 0.6, 0.9, -0.3), new(0.62, 76, 0.32, 0.6, 0.2), new(0.76, 72, 0.32, 0.55, -0.1), new(0.90, 69, 0.9, 0.6, -0.3) },
        // D5 then G4: power leaving.
        Cue.Unplugged => new Note[] { new(0.00, 74, 0.55, 0.9, 0.3), new(0.13, 67, 0.95, 0.9, -0.3) },
        // G4 then D5 then a light G5 sparkle: power arriving.
        Cue.PluggedIn => new Note[] { new(0.00, 67, 0.5, 0.85, -0.3), new(0.12, 74, 0.7, 0.9, 0.2), new(0.26, 79, 0.9, 0.45, 0.4) },
        // Two soft pings, B5 then E6.
        _ => new Note[] { new(0.00, 83, 0.6, 0.8, -0.25), new(0.16, 88, 0.9, 0.7, 0.25) },
    };

    private static double Hz(double midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    // ---------- timbres ----------

    private sealed record Voice(double Attack, double Decay, double Brightness, double Detune, double Room, double Wet, Func<double, double, double, double> Wave);

    private static Voice VoiceFor(string pack) => pack switch
    {
        // Glassy chime: sine with a soft FM sheen and gentle second partial; long, lush room.
        "aurora" => new(0.012, 2.6, 0.55, 0.06, 0.82, 0.32, (f, t, ph) =>
            Math.Sin(ph + 0.9 * Math.Exp(-t * 6) * Math.Sin(ph * 2.0)) + 0.22 * Math.Sin(ph * 2.0) * Math.Exp(-t * 3) + 0.08 * Math.Sin(ph * 3.0) * Math.Exp(-t * 5)),
        // Glass bell: inharmonic partials of a struck bar, each decaying at its own rate.
        "crystal" => new(0.004, 2.2, 0.85, 0.02, 0.7, 0.26, (f, t, ph) =>
            Math.Sin(ph) + 0.5 * Math.Sin(ph * 2.756) * Math.Exp(-t * 4) + 0.28 * Math.Sin(ph * 5.404) * Math.Exp(-t * 9) + 0.12 * Math.Sin(ph * 8.933) * Math.Exp(-t * 16)),
        // Modern pluck: band-limited saw shape that closes quickly, with a sub an octave down; tight room.
        "pulse" => new(0.005, 5.5, 0.7, 0.1, 0.45, 0.18, (f, t, ph) =>
        {
            double v = 0;
            int harmonics = (int)Math.Clamp(9000 / f, 1, 12);
            double close = Math.Exp(-t * 14);
            for (int k = 1; k <= harmonics; k++) v += Math.Sin(ph * k) / k * Math.Pow(close, (k - 1) * 0.35);
            return 0.75 * v + 0.35 * Math.Sin(ph * 0.5);
        }),
        // Soft pad: fundamental plus octave and fifth, slow swell, very wide room.
        _ => new(0.045, 1.6, 0.35, 0.12, 0.88, 0.38, (f, t, ph) =>
            Math.Sin(ph) + 0.35 * Math.Sin(ph * 2.0) + 0.12 * Math.Sin(ph * 1.5) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 4.5 * t))),
    };

    /// <summary>Renders a cue to stereo samples (interleaved L, R) in -1..1.</summary>
    internal static float[] Render(Cue cue, string pack)
    {
        var voice = VoiceFor(pack);
        var notes = Motif(cue);
        double tail = 0.35 + voice.Room * 1.1;
        int frames = (int)((notes.Max(n => n.Start + n.Length) + tail) * Rate);
        var left = new double[frames];
        var right = new double[frames];
        
        foreach (var n in notes)
        {
            double f = Hz(n.Midi);
            int s0 = (int)(n.Start * Rate), len = (int)(n.Length * Rate);
            // Three voices a few cents apart, spread across the stereo field, for width and life.
            double[] cents = { -voice.Detune * 100, 0, voice.Detune * 100 };
            double[] pans = { n.Pan - 0.35, n.Pan, n.Pan + 0.35 };
            double[] gains = { 0.3, 0.45, 0.3 };
            double lp = 0; // one-pole low-pass state, per note
            for (int v = 0; v < 3; v++)
            {
                double fv = f * Math.Pow(2, cents[v] / 1200.0);
                double phase = v * 0.7; // small fixed offsets: voices don't start in lockstep, and no step at the onset
                double panL = Math.Cos((Math.Clamp(pans[v], -1, 1) + 1) * Math.PI / 4), panR = Math.Sin((Math.Clamp(pans[v], -1, 1) + 1) * Math.PI / 4);
                lp = 0;
                for (int i = 0; i < len && s0 + i < frames; i++)
                {
                    double t = i / (double)Rate;
                    double env = (t < voice.Attack ? t / voice.Attack : Math.Exp(-(t - voice.Attack) * voice.Decay));
                    double release = i > len - 0.03 * Rate ? Math.Max(0, (len - i) / (0.03 * Rate)) : 1; // no clicks
                    double raw = voice.Wave(fv, t, phase + 2 * Math.PI * fv * t);
                    // Warmth: the filter opens with the attack and closes as the note decays.
                    double cutoff = 1200 + voice.Brightness * 9000 * Math.Exp(-t * 3);
                    double a = 1 - Math.Exp(-2 * Math.PI * cutoff / Rate);
                    lp += a * (raw - lp);
                    double s = lp * env * release * gains[v] * n.Gain;
                    left[s0 + i] += s * panL;
                    right[s0 + i] += s * panR;
                }
            }
        }

        Reverb(left, right, voice.Room, voice.Wet);

        // Gentle saturation, a short fade-out, then normalise to -3 dBFS.
        int fade = (int)(0.25 * Rate);
        double peak = 1e-9;
        for (int i = 0; i < frames; i++)
        {
            double g = i > frames - fade ? (frames - i) / (double)fade : 1;
            left[i] = Math.Tanh(left[i] * 1.2) * g;
            right[i] = Math.Tanh(right[i] * 1.2) * g;
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        }
        double norm = 0.707 / peak;
        // Trim the silent end (below about -66 dB) but keep 60 ms of air.
        int last = frames - 1;
        while (last > 0 && Math.Max(Math.Abs(left[last]), Math.Abs(right[last])) * norm < 0.0005) last--;
        int keep = Math.Min(frames, last + (int)(0.06 * Rate));
        var output = new float[keep * 2];
        for (int i = 0; i < keep; i++)
        {
            double g = i > keep - 0.06 * Rate ? (keep - i) / (0.06 * Rate) : 1;
            output[2 * i] = (float)(left[i] * norm * g); output[2 * i + 1] = (float)(right[i] * norm * g);
        }
        return output;
    }

    /// <summary>Schroeder reverb (four damped combs and two all-passes per side, offset for stereo width).</summary>
    private static void Reverb(double[] left, double[] right, double room, double wet)
    {
        int[] combs = { 1116, 1188, 1277, 1356 };
        int[] allpasses = { 556, 441 };
        double feedback = 0.7 + room * 0.25, damp = 0.25;
        double[] Process(double[] input, int spread)
        {
            var output = new double[input.Length];
            foreach (var c in combs)
            {
                int d = c + spread;
                var buf = new double[d];
                double filt = 0;
                for (int i = 0, p = 0; i < input.Length; i++, p = (p + 1) % d)
                {
                    double y = buf[p];
                    filt = y * (1 - damp) + filt * damp;
                    buf[p] = input[i] * 0.015 + filt * feedback;
                    output[i] += y;
                }
            }
            foreach (var ap in allpasses)
            {
                int d = ap + spread;
                var buf = new double[d];
                for (int i = 0, p = 0; i < output.Length; i++, p = (p + 1) % d)
                {
                    double b = buf[p];
                    double x = output[i];
                    buf[p] = x + b * 0.5;
                    output[i] = b - x;
                }
            }
            return output;
        }
        var wl = Process(left, 0);
        var wr = Process(right, 23);
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = left[i] * (1 - wet * 0.5) + wl[i] * wet;
            right[i] = right[i] * (1 - wet * 0.5) + wr[i] * wet;
        }
    }

    /// <summary>16-bit stereo PCM WAV.</summary>
    internal static byte[] Wav(float[] interleaved)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = interleaved.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var s in interleaved) w.Write((short)Math.Clamp(Math.Round(s * 32767), -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}

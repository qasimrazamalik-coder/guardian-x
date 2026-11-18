using System.Media;

namespace GuardianX.Core.Power;

public enum Cue { Charged, Low, Unplugged, PluggedIn, Alert }

/// <summary>
/// Guardian X's own sounds, synthesised at first run (no stock Windows sounds, no audio files to license).
/// Two packs: Nebula (soft, airy) and Pulse (crisp, punchy).
/// </summary>
public static class Sounds
{
    private const int Rate = 44100;
    public static string Folder => Path.Combine(Data.Store.DefaultFolder, "sounds");

    public static void Play(Cue cue, string pack = "nebula")
    {
        try
        {
            var path = Path.Combine(Folder, $"{pack}-{cue.ToString().ToLowerInvariant()}.wav");
            if (!File.Exists(path)) Generate();
            using var player = new SoundPlayer(path);
            player.Play();
        }
        catch { }
    }

    public static void Generate()
    {
        Directory.CreateDirectory(Folder);
        foreach (var pack in new[] { "nebula", "pulse" })
            foreach (Cue cue in Enum.GetValues(typeof(Cue)))
                File.WriteAllBytes(Path.Combine(Folder, $"{pack}-{cue.ToString().ToLowerInvariant()}.wav"), Wav(Synth(cue, pack)));
    }

    /// <summary>Notes as (start s, frequency Hz, length s). Rising for good news, falling for warnings.</summary>
    internal static float[] Synth(Cue cue, string pack)
    {
        (double t, double f, double len)[] notes = cue switch
        {
            Cue.Charged => new[] { (0.0, 523.25, 0.5), (0.09, 783.99, 0.55), (0.18, 1046.5, 0.8) },   // C5 G5 C6
            Cue.Low => new[] { (0.0, 880.0, 0.35), (0.16, 659.25, 0.35), (0.32, 440.0, 0.7) },        // A5 E5 A4
            Cue.Unplugged => new[] { (0.0, 659.25, 0.3), (0.12, 493.88, 0.45) },
            Cue.PluggedIn => new[] { (0.0, 493.88, 0.3), (0.12, 739.99, 0.45) },
            _ => new[] { (0.0, 987.77, 0.18), (0.22, 987.77, 0.18), (0.44, 987.77, 0.35) },
        };
        bool pulse = pack == "pulse";
        double total = notes.Max(n => n.t + n.len) + (pulse ? 0.25 : 0.6);
        var buf = new float[(int)(total * Rate)];
        foreach (var (start, freq, len) in notes)
        {
            int s0 = (int)(start * Rate), n = (int)(len * Rate);
            for (int i = 0; i < n && s0 + i < buf.Length; i++)
            {
                double t = i / (double)Rate;
                double attack = Math.Min(1, t / (pulse ? 0.004 : 0.02));
                double env = attack * Math.Exp(-t * (pulse ? 7.5 : 4.0));
                double v;
                if (pulse)
                {
                    // FM bell: crisp and bright.
                    double mod = Math.Sin(2 * Math.PI * freq * 2.0 * t) * 2.2 * Math.Exp(-t * 9);
                    v = Math.Sin(2 * Math.PI * freq * t + mod);
                }
                else
                {
                    // Soft pad: fundamental, gentle octave and a slow detune shimmer.
                    v = 0.7 * Math.Sin(2 * Math.PI * freq * t)
                        + 0.2 * Math.Sin(2 * Math.PI * freq * 2.002 * t)
                        + 0.1 * Math.Sin(2 * Math.PI * freq * 0.5 * t) * Math.Sin(2 * Math.PI * 3 * t);
                }
                buf[s0 + i] += (float)(v * env * 0.28);
            }
        }
        // Simple feedback echo for space.
        int d = (int)((pulse ? 0.09 : 0.16) * Rate);
        double fb = pulse ? 0.22 : 0.35;
        for (int i = d; i < buf.Length; i++) buf[i] += (float)(buf[i - d] * fb);
        float peak = buf.Max(Math.Abs);
        if (peak > 0.9f) for (int i = 0; i < buf.Length; i++) buf[i] *= 0.9f / peak;
        return buf;
    }

    internal static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}

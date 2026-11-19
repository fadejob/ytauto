namespace YtAuto.Core.Media;

/// <summary>
/// Montaj uchun qisqa SFX (sahna o'tishi, bosish, element paydo bo'lishi). Tashqi fayl va litsenziya talab qilmasligi uchun
/// kod bilan sintez qilinadi: deterministik (bir xil urug'), 48 kHz mono 16-bit PCM WAV, cho'qqisi -6 dBFS atrofida.
/// </summary>
public static class Sfx
{
    public const int SampleRate = 48000;

    /// <summary>Fayl nomi → tavsif (Claude'ga ko'rsatiladi).</summary>
    public static readonly IReadOnlyDictionary<string, string> Catalog = new Dictionary<string, string>
    {
        ["whoosh.wav"] = "soft airy whoosh, 0.45 s — scene transitions / big zoom moves",
        ["click.wav"] = "short UI click, 0.06 s — the moment a button or link is pressed in a screenshot or recording",
        ["pop.wav"] = "light pop, 0.12 s — a label, callout or highlight box appears",
    };

    /// <summary>SFX fayllarini papkaga yozadi (borlari qayta yoziladi - natija bir xil). Yozilgan fayllar ro'yxati.</summary>
    public static IReadOnlyList<string> WriteAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var list = new List<string>();
        foreach (var (name, samples) in new[] { ("whoosh.wav", Whoosh()), ("click.wav", Click()), ("pop.wav", Pop()) })
        {
            var p = Path.Combine(dir, name);
            WritePcm16(p, samples);
            list.Add(p);
        }
        return list;
    }

    internal static float[] Whoosh()
    {
        var n = (int)(SampleRate * 0.45);
        var s = new float[n];
        var rng = new Random(7);
        double lp = 0, bp = 0;
        for (int i = 0; i < n; i++)
        {
            var t = i / (double)n;
            // Filtrlangan shovqin: kesish chastotasi ko'tarilib-tushadi (havo o'tgandek).
            var cutoff = 300 + 2600 * Math.Sin(Math.PI * t);
            var a = 1 - Math.Exp(-2 * Math.PI * cutoff / SampleRate);
            var noise = rng.NextDouble() * 2 - 1;
            lp += a * (noise - lp);
            bp += a * (lp - bp);
            var env = Math.Pow(Math.Sin(Math.PI * t), 1.6);
            s[i] = (float)((lp - bp * 0.6) * env * 1.8);
        }
        return Normalize(s, 0.5f);
    }

    internal static float[] Click()
    {
        var n = (int)(SampleRate * 0.06);
        var s = new float[n];
        var rng = new Random(11);
        for (int i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var env = Math.Exp(-t * 140);
            var tone = Math.Sin(2 * Math.PI * 2200 * t) * 0.6 + Math.Sin(2 * Math.PI * 4100 * t) * 0.25;
            s[i] = (float)((tone + (rng.NextDouble() * 2 - 1) * 0.35 * Math.Exp(-t * 400)) * env);
        }
        return Normalize(s, 0.45f);
    }

    internal static float[] Pop()
    {
        var n = (int)(SampleRate * 0.12);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var f = 900 - 500 * (t / 0.12); // tez pasayuvchi ton
            var env = Math.Min(1, t / 0.004) * Math.Exp(-t * 38);
            s[i] = (float)(Math.Sin(2 * Math.PI * f * t) * env);
        }
        return Normalize(s, 0.45f);
    }

    private static float[] Normalize(float[] s, float peak)
    {
        var max = s.Length == 0 ? 0 : s.Max(v => Math.Abs(v));
        if (max <= 0) return s;
        var k = peak / max;
        for (int i = 0; i < s.Length; i++) s[i] *= k;
        // Boshi va oxiri chertmasligi uchun 3 ms fade.
        var fade = Math.Min(s.Length / 4, SampleRate * 3 / 1000);
        for (int i = 0; i < fade; i++) { var g = i / (float)fade; s[i] *= g; s[^(i + 1)] *= g; }
        return s;
    }

    internal static void WritePcm16(string path, float[] samples)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var dataLen = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var v in samples) w.Write((short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue));
    }
}

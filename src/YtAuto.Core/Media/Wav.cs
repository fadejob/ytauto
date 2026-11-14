using System.Buffers.Binary;
using System.Text;

namespace YtAuto.Core.Media;

public sealed class WavInfo
{
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public int BitsPerSample { get; init; }
    public bool IsFloat { get; init; }
    public long DataOffset { get; init; }
    public long DataLength { get; init; }
    public double Duration => SampleRate <= 0 || Channels <= 0 ? 0 : DataLength / (double)(SampleRate * Channels * (BitsPerSample / 8));
    public int BlockAlign => Channels * (BitsPerSample / 8);
}

public sealed class WavAnalysis
{
    public double Duration { get; init; }
    public double RmsDb { get; init; }
    public double PeakDb { get; init; }
    public double ClippedRatio { get; init; }
    public double SpeechRatio { get; init; }
    /// <summary>Oxirgi 80 ms dagi RMS (dB): yuqori bo'lsa nutq o'rtasida kesilgan bo'lishi mumkin.</summary>
    public double TailRmsDb { get; init; }
    public List<string> Problems { get; } = new();
}

/// <summary>PCM16/PCM24/float32 WAV o'qish, tahlil va birlashtirish (tashqi dasturlarsiz, stream bilan).</summary>
public static class Wav
{
    public static WavInfo ReadInfo(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("RIFF emas");
        br.ReadUInt32();
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("WAVE emas");
        int ch = 0, sr = 0, bits = 0; bool isFloat = false; long dataOff = -1, dataLen = 0;
        while (fs.Position + 8 <= fs.Length)
        {
            var id = Encoding.ASCII.GetString(br.ReadBytes(4));
            long size = br.ReadUInt32();
            var next = fs.Position + size + (size & 1);
            if (id == "fmt ")
            {
                var fmt = br.ReadUInt16(); ch = br.ReadUInt16(); sr = (int)br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt16(); bits = br.ReadUInt16();
                if (fmt == 0xFFFE && size >= 40) { br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt32(); fmt = br.ReadUInt16(); }
                isFloat = fmt == 3;
                if (fmt != 1 && fmt != 3) throw new InvalidDataException($"Qo'llab-quvvatlanmaydigan WAV formati: {fmt}");
            }
            else if (id == "data")
            {
                dataOff = fs.Position;
                // Ba'zi streaming WAV'larda data hajmi 0 yoki 0xFFFFFFFF bo'ladi: fayl oxirigacha olamiz.
                dataLen = size == 0 || size == 0xFFFFFFFF || dataOff + size > fs.Length ? fs.Length - dataOff : size;
                break;
            }
            fs.Position = next;
        }
        if (dataOff < 0 || ch == 0) throw new InvalidDataException("WAV fmt/data bo'limi topilmadi");
        return new WavInfo { Channels = ch, SampleRate = sr, BitsPerSample = bits, IsFloat = isFloat, DataOffset = dataOff, DataLength = dataLen - dataLen % (ch * (bits / 8)) };
    }

    private static float Sample(ReadOnlySpan<byte> b, WavInfo w) => (w.BitsPerSample, w.IsFloat) switch
    {
        (16, false) => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        (24, false) => ((b[0] | (b[1] << 8) | ((sbyte)b[2] << 16))) / 8388608f,
        (32, true) => BinaryPrimitives.ReadSingleLittleEndian(b),
        (32, false) => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
        (8, false) => (b[0] - 128) / 128f,
        _ => throw new InvalidDataException($"{w.BitsPerSample}-bit qo'llab-quvvatlanmaydi"),
    };

    public static WavAnalysis Analyze(string path, double minDurationSec = 0.5)
    {
        var w = ReadInfo(path);
        int bps = w.BitsPerSample / 8;
        double sumSq = 0; double peak = 0; long n = 0, clipped = 0;
        // 30 ms oynalar: nutq ulushi (energiya bo'yicha)
        int win = Math.Max(1, (int)(w.SampleRate * 0.03));
        double winSum = 0; int winCount = 0; long speechWins = 0, totalWins = 0;
        long tailSamples = (long)(w.SampleRate * 0.08);
        long totalFrames = w.DataLength / w.BlockAlign;
        double tailSum = 0; long tailN = 0;
        using var fs = File.OpenRead(path);
        fs.Position = w.DataOffset;
        var buf = new byte[w.BlockAlign * 8192];
        long remaining = w.DataLength; long frame = 0;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buf.Length, remaining);
            int read = fs.Read(buf, 0, want);
            if (read <= 0) break;
            remaining -= read;
            for (int off = 0; off + w.BlockAlign <= read; off += w.BlockAlign, frame++)
            {
                double mono = 0;
                for (int c = 0; c < w.Channels; c++)
                {
                    var s = Sample(buf.AsSpan(off + c * bps, bps), w);
                    var a = Math.Abs(s);
                    if (a > peak) peak = a;
                    if (a >= 0.999) clipped++;
                    mono += s;
                }
                mono /= w.Channels;
                sumSq += mono * mono; n++;
                winSum += mono * mono; winCount++;
                if (winCount == win)
                {
                    var rms = Math.Sqrt(winSum / win);
                    if (rms > 0.01) speechWins++;
                    totalWins++; winSum = 0; winCount = 0;
                }
                if (frame >= totalFrames - tailSamples) { tailSum += mono * mono; tailN++; }
            }
        }
        double Db(double x) => x <= 1e-9 ? -120 : 20 * Math.Log10(x);
        var res = new WavAnalysis
        {
            Duration = w.Duration,
            RmsDb = Db(Math.Sqrt(sumSq / Math.Max(1, n))),
            PeakDb = Db(peak),
            ClippedRatio = n == 0 ? 0 : clipped / (double)(n * w.Channels),
            SpeechRatio = totalWins == 0 ? 0 : speechWins / (double)totalWins,
            TailRmsDb = Db(Math.Sqrt(tailSum / Math.Max(1, tailN))),
        };
        if (res.Duration < minDurationSec) res.Problems.Add($"Audio juda qisqa ({res.Duration:0.00}s).");
        if (res.RmsDb < -50 || res.SpeechRatio < 0.2) res.Problems.Add($"Audio deyarli jim (RMS {res.RmsDb:0.0} dB, nutq ulushi {res.SpeechRatio:P0}).");
        if (res.ClippedRatio > 0.0005) res.Problems.Add($"Clipping: namunalarning {res.ClippedRatio:P2} qismi maksimal darajada.");
        return res;
    }

    /// <summary>Bir xil formatdagi WAV fayllarni orasiga jimlik qo'shib birlashtiradi (16-bit PCM yoki float, formatni saqlaydi).</summary>
    public static void Concat(IReadOnlyList<string> inputs, string output, double gapSec)
    {
        if (inputs.Count == 0) throw new ArgumentException("bo'sh ro'yxat");
        var infos = inputs.Select(ReadInfo).ToList();
        var f = infos[0];
        foreach (var i in infos)
            if (i.Channels != f.Channels || i.SampleRate != f.SampleRate || i.BitsPerSample != f.BitsPerSample || i.IsFloat != f.IsFloat)
                throw new InvalidDataException("TTS bo'laklari formati har xil");
        long gapBytes = (long)(gapSec * f.SampleRate) * f.BlockAlign;
        long dataLen = infos.Sum(i => i.DataLength) + gapBytes * (inputs.Count - 1);
        var tmp = output + ".partial";
        using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(outFs))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write((uint)(36 + dataLen)); bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt ")); bw.Write(16u); bw.Write((ushort)(f.IsFloat ? 3 : 1)); bw.Write((ushort)f.Channels);
            bw.Write((uint)f.SampleRate); bw.Write((uint)(f.SampleRate * f.BlockAlign)); bw.Write((ushort)f.BlockAlign); bw.Write((ushort)f.BitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write((uint)dataLen);
            var silence = new byte[Math.Min(gapBytes, 1 << 20)];
            for (int k = 0; k < inputs.Count; k++)
            {
                using (var inFs = File.OpenRead(inputs[k]))
                {
                    inFs.Position = infos[k].DataOffset;
                    CopyBytes(inFs, outFs, infos[k].DataLength);
                }
                if (k < inputs.Count - 1)
                {
                    long left = gapBytes;
                    while (left > 0) { int c = (int)Math.Min(left, silence.Length); outFs.Write(silence, 0, c); left -= c; }
                }
            }
        }
        File.Move(tmp, output, overwrite: true);
    }

    private static void CopyBytes(Stream src, Stream dst, long count)
    {
        var buf = new byte[1 << 16];
        while (count > 0)
        {
            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (r <= 0) break;
            dst.Write(buf, 0, r); count -= r;
        }
    }
}

public static class TextChunker
{
    /// <summary>Matnni gap chegaralarida maksimal uzunlikdagi bo'laklarga ajratadi (TTS so'rovlari uchun).</summary>
    public static List<string> Split(string text, int maxChars)
    {
        var sentences = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?…])\s+")
            .Where(s => s.Trim().Length > 0).ToList();
        var chunks = new List<string>(); var cur = new StringBuilder();
        foreach (var s0 in sentences)
        {
            var s = s0.Trim();
            if (s.Length > maxChars)
            {
                if (cur.Length > 0) { chunks.Add(cur.ToString()); cur.Clear(); }
                // juda uzun gap: vergul/bo'shliqda bo'lamiz
                var parts = System.Text.RegularExpressions.Regex.Split(s, @"(?<=[,;:])\s+");
                foreach (var p in parts)
                {
                    if (cur.Length + p.Length + 1 > maxChars && cur.Length > 0) { chunks.Add(cur.ToString()); cur.Clear(); }
                    if (p.Length > maxChars)
                    {
                        foreach (var w in p.Split(' '))
                        {
                            if (cur.Length + w.Length + 1 > maxChars && cur.Length > 0) { chunks.Add(cur.ToString()); cur.Clear(); }
                            if (cur.Length > 0) cur.Append(' '); cur.Append(w);
                        }
                    }
                    else { if (cur.Length > 0) cur.Append(' '); cur.Append(p); }
                }
                continue;
            }
            if (cur.Length + s.Length + 1 > maxChars && cur.Length > 0) { chunks.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(s);
        }
        if (cur.Length > 0) chunks.Add(cur.ToString());
        return chunks;
    }
}

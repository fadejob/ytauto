using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Qa;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Infrastructure.Media;

/// <summary>OpenFilm ichidagi ffmpeg/ffprobe orqali tekshiruvlar (alohida render pipeline yaratilmaydi).</summary>
public sealed class FfmpegMediaProbe : IMediaProbe
{
    private readonly Func<string> _ffDir;
    private readonly Dictionary<string, AnalysisCache> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string? _version;

    public FfmpegMediaProbe(Func<string> ffmpegDir) => _ffDir = ffmpegDir;

    private string Ffmpeg => Path.Combine(_ffDir(), "ffmpeg.exe");
    private string Ffprobe => Path.Combine(_ffDir(), "ffprobe.exe");

    private void Ensure()
    {
        if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe))
            throw PipelineException.Config("ffmpeg_missing", $"OpenFilm ichidagi ffmpeg/ffprobe topilmadi: {_ffDir()}. OpenFilm o'rnatilganini tekshiring.");
    }

    private static string Tmp(string ext) { var d = Path.Combine(Path.GetTempPath(), "YtAuto"); Directory.CreateDirectory(d); return Path.Combine(d, $"ff-{Guid.NewGuid():N}{ext}"); }

    private async Task<(ToolRunResult R, string Out, string Err)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, bool binaryOut = false)
    {
        Ensure();
        var o = Tmp(binaryOut ? ".bin" : ".out"); var e = Tmp(".err");
        try
        {
            var r = await ToolRunner.RunAsync(new LaunchSpec { Exe = exe, Args = args, StdoutPath = o, StderrPath = e, Tool = Path.GetFileNameWithoutExtension(exe) },
                timeout, null, ct).ConfigureAwait(false);
            var outText = binaryOut ? o : await File.ReadAllTextAsync(o, ct).ConfigureAwait(false);
            var errText = Core.Files.FileOps.ReadTail(e, 256 * 1024);
            return (r, outText, errText);
        }
        finally
        {
            try { if (!binaryOut) File.Delete(o); File.Delete(e); } catch { }
        }
    }

    public string Version
    {
        get
        {
            if (_version is not null) return _version;
            try
            {
                var (r, o, _) = RunAsync(Ffmpeg, new[] { "-hide_banner", "-version" }, TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
                _version = r.ExitCode == 0 ? o.Split('\n')[0].Trim() : $"exit {r.ExitCode}";
            }
            catch (Exception e) { _version = "error: " + e.Message; }
            return _version;
        }
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var (r, o, err) = await RunAsync(Ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw PipelineException.Transient("ffprobe_failed", $"ffprobe {Path.GetFileName(path)}: {err.Trim()}");
        var j = JsonNode.Parse(o)!;
        var streams = j["streams"] as JsonArray ?? new JsonArray();
        var v = streams.FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "video" && s?["disposition"]?["attached_pic"]?.GetValue<int>() != 1);
        var a = streams.FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "audio");
        double dur = D(j["format"]?["duration"]);
        if (dur <= 0) dur = Math.Max(D(v?["duration"]), D(a?["duration"]));
        double fps = 0;
        var rate = v?["avg_frame_rate"]?.GetValue<string>();
        if (rate is not null && rate.Contains('/')) { var p = rate.Split('/'); if (double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0) fps = double.Parse(p[0], CultureInfo.InvariantCulture) / den; }
        return new MediaInfo
        {
            Duration = dur, HasVideo = v is not null, HasAudio = a is not null,
            Width = v?["width"]?.GetValue<int>() ?? 0, Height = v?["height"]?.GetValue<int>() ?? 0,
            VideoCodec = v?["codec_name"]?.GetValue<string>() ?? "", AudioCodec = a?["codec_name"]?.GetValue<string>() ?? "", Fps = fps,
        };
    }

    private static double D(JsonNode? n)
    {
        if (n is null) return 0;
        var s = n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToString();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private sealed class AnalysisCache
    {
        public DateTime Mtime; public long Size;
        public bool DecodeOk; public string DecodeErrors = "";
        public List<BlackSegment> Black = new();
        public double MaxVolumeDb = -91; public double? Lufs;
    }

    /// <summary>Bitta dekod o'tishida: to'liq dekod, blackdetect, volumedetect, ebur128.</summary>
    private async Task<AnalysisCache> AnalyzeAsync(string path, CancellationToken ct)
    {
        var fi = new FileInfo(path);
        if (_cache.TryGetValue(path, out var c) && c.Mtime == fi.LastWriteTimeUtc && c.Size == fi.Length) return c;
        var args = new[] { "-hide_banner", "-nostats", "-v", "info", "-i", path,
            "-vf", "blackdetect=d=0.25:pix_th=0.10:pic_th=0.98", "-af", "volumedetect,ebur128=framelog=quiet", "-f", "null", "-" };
        var (r, _, err) = await RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        c = new AnalysisCache { Mtime = fi.LastWriteTimeUtc, Size = fi.Length };
        var errorLines = err.Split('\n').Where(l => Regex.IsMatch(l, @"(?i)\berror\b|corrupt|invalid data|non-existing|concealing")).Take(10).ToList();
        c.DecodeOk = r.ExitCode == 0 && errorLines.Count == 0;
        c.DecodeErrors = r.ExitCode != 0 ? $"exit {r.ExitCode}: {string.Join(" | ", err.Split('\n').TakeLast(5))}" : string.Join(" | ", errorLines);
        foreach (Match m in Regex.Matches(err, @"black_start:(?<s>[\d.]+)\s+black_end:(?<e>[\d.]+)"))
            c.Black.Add(new BlackSegment { Start = double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), End = double.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) });
        var mv = Regex.Match(err, @"max_volume:\s*(?<v>-?[\d.]+|-inf) dB");
        if (mv.Success) c.MaxVolumeDb = mv.Groups["v"].Value == "-inf" ? -91 : double.Parse(mv.Groups["v"].Value, CultureInfo.InvariantCulture);
        var lufs = Regex.Matches(err, @"I:\s*(?<v>-?[\d.]+) LUFS");
        if (lufs.Count > 0) c.Lufs = double.Parse(lufs[^1].Groups["v"].Value, CultureInfo.InvariantCulture);
        _cache[path] = c;
        return c;
    }

    public async Task<(bool Ok, string Errors)> DecodeCheckAsync(string path, CancellationToken ct)
    { var a = await AnalyzeAsync(path, ct).ConfigureAwait(false); return (a.DecodeOk, a.DecodeErrors); }

    public async Task<IReadOnlyList<BlackSegment>> BlackDetectAsync(string path, CancellationToken ct)
        => (await AnalyzeAsync(path, ct).ConfigureAwait(false)).Black;

    public async Task<(double MaxVolumeDb, double? IntegratedLufs)> AudioLevelsAsync(string path, CancellationToken ct)
    { var a = await AnalyzeAsync(path, ct).ConfigureAwait(false); return (a.MaxVolumeDb, a.Lufs); }

    /// <summary>Istalgan audio/video faylning ovozini 48 kHz, mono, 16-bit PCM WAV'ga o'tkazadi (pipeline va whisper shu formatni o'qiydi).</summary>
    public async Task ConvertToWavAsync(string input, string outWav, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outWav)!);
        var args = new[] { "-hide_banner", "-v", "error", "-i", input, "-vn", "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", "-y", outWav };
        var (r, _, err) = await RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || !File.Exists(outWav))
            throw PipelineException.Quality("audio_convert", $"Audio faylni o'qib bo'lmadi (ffmpeg): {err.Trim()}");
    }

    public async Task ExtractFrameAsync(string video, double atSec, string outImage, int width, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outImage)!);
        var args = new List<string> { "-hide_banner", "-v", "error", "-ss", atSec.ToString("0.###", CultureInfo.InvariantCulture), "-i", video,
            "-frames:v", "1", "-vf", $"scale={width}:-2", "-y" };
        if (outImage.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) { args.Add("-q:v"); args.Add("3"); }
        args.Add(outImage);
        var (r, _, err) = await RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || !File.Exists(outImage)) throw PipelineException.Transient("ffmpeg_frame", $"Kadr ajratilmadi ({atSec:0.0}s): {err.Trim()}");
    }

    /// <summary>
    /// Muqova MP4 ichiga attached_pic oqimi bo'lib qo'shiladi (-c copy: video/audio qayta kodlanmaydi). Fayl menejeri, telefon galereyasi
    /// va pleyerlar videoni ochmasdan shu rasmni ko'rsatadi. Probe/QA attached_pic oqimini hisobga olmaydi.
    /// </summary>
    public async Task EmbedCoverAsync(string mp4, string image, string outMp4, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
        var args = new[] { "-hide_banner", "-v", "error", "-i", mp4, "-i", image, "-map", "0", "-map", "1", "-c", "copy",
            "-disposition:v:1", "attached_pic", "-movflags", "+faststart", "-y", outMp4 };
        var (r, _, err) = await RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || !File.Exists(outMp4) || new FileInfo(outMp4).Length < new FileInfo(mp4).Length)
            throw PipelineException.Transient("ffmpeg_cover", $"Muqova videoga qo'shilmadi: {err.Trim()}");
    }

    public async Task MakeAnalysisClipAsync(string video, double startSec, double durationSec, string outMp4, int width, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
        var inv = CultureInfo.InvariantCulture;
        // 15 fps yetarli: Gemini o'zi so'ralgan fps bilan kadr oladi; ovoz kerak emas (token tejaladi).
        var args = new[] { "-hide_banner", "-v", "error", "-ss", startSec.ToString("0.###", inv), "-i", video, "-t", durationSec.ToString("0.###", inv),
            "-an", "-vf", $"scale={width}:-2,fps=15", "-c:v", "libx264", "-preset", "veryfast", "-crf", "32", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", "-y", outMp4 };
        var (r, _, err) = await RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || !File.Exists(outMp4) || new FileInfo(outMp4).Length == 0)
            throw PipelineException.Transient("ffmpeg_clip", $"Tahlil klipi yasalmadi: {err.Trim()}");
    }

    public async Task<(double Mean, double Std)> FrameLumaAsync(string video, double atSec, CancellationToken ct)
    {
        var args = new[] { "-hide_banner", "-v", "error", "-ss", atSec.ToString("0.###", CultureInfo.InvariantCulture), "-i", video,
            "-frames:v", "1", "-vf", "scale=96:-2,format=gray", "-f", "rawvideo", "-" };
        var (r, outPath, err) = await RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(2), ct, binaryOut: true).ConfigureAwait(false);
        try
        {
            if (r.ExitCode != 0) throw PipelineException.Transient("ffmpeg_luma", $"Kadr o'qilmadi ({atSec:0.0}s): {err.Trim()}");
            var bytes = await File.ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return (0, 0);
            double sum = 0, sq = 0;
            foreach (var b in bytes) { sum += b; sq += b * (double)b; }
            var mean = sum / bytes.Length;
            return (mean, Math.Sqrt(Math.Max(0, sq / bytes.Length - mean * mean)));
        }
        finally { try { File.Delete(outPath); } catch { } }
    }
}

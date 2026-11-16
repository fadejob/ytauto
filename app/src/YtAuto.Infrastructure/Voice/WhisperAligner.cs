using System.Text.Json;
using System.Text.Json.Nodes;
using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Infrastructure.Voice;

/// <summary>
/// Sinovda ishlagan usul: VoiceStudio Python muhiti + faster-whisper (offline, yuklab olingan model) + so'z vaqtlari.
/// Alohida jarayon (Job Object): tugagach model xotirada qolmaydi. VoiceStudio yangilanib muhit buzilsa, diagnostika beriladi.
/// </summary>
public sealed class WhisperAligner : IAligner
{
    private readonly Func<VoiceStudioSettings> _settings;
    private readonly string _script;
    public Dictionary<string, double> LastMetrics { get; } = new();

    public WhisperAligner(Func<VoiceStudioSettings> settings, string? scriptPath = null)
    {
        _settings = settings;
        _script = scriptPath ?? ResolveScript();
    }

    /// <summary>
    /// align_words.py: exe yonidagi Resources papkasida bo'lsa o'sha; bitta exe (single-file) distributivida esa exe ichidagi nusxa
    /// %LOCALAPPDATA%\YtAuto\Resources ga chiqariladi (mazmuni o'zgargan bo'lsa yangilanadi).
    /// </summary>
    private static string ResolveScript()
    {
        var beside = Path.Combine(AppPaths.ResourcesDir, "align_words.py");
        if (File.Exists(beside)) return beside;
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto", "Resources", "align_words.py");
        try
        {
            using var res = typeof(WhisperAligner).Assembly.GetManifestResourceStream("YtAuto.align_words.py");
            if (res is null) return beside;
            using var ms = new MemoryStream(); res.CopyTo(ms);
            var bytes = ms.ToArray();
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            return target;
        }
        catch { return File.Exists(target) ? target : beside; }
    }

    public string Describe() { var s = _settings(); return $"faster-whisper {s.WhisperModel} via {s.PythonPath}"; }

    private LaunchSpec Spec(VoiceStudioSettings s, IReadOnlyList<string> args, string stdout, string stderr) => new()
    {
        Exe = s.PythonPath, Args = new[] { "-I", _script }.Concat(args).ToList(), Tool = "aligner",
        WorkingDirectory = Path.GetDirectoryName(stdout),
        Env = new Dictionary<string, string?>
        {
            ["HF_HUB_CACHE"] = s.HfCacheDir, ["HF_HUB_OFFLINE"] = "1", ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1",
            ["HF_HUB_DISABLE_TELEMETRY"] = "1", ["PYTHONDONTWRITEBYTECODE"] = "1",
        },
        RemoveEnv = new[] { "PYTHONPATH", "PYTHONHOME", "GEMINI_API_KEY", "GOOGLE_API_KEY" },
        StdoutPath = stdout, StderrPath = stderr,
    };

    public async Task<(bool Ok, string Message)> CheckAsync(string workDir, CancellationToken ct)
    {
        var s = _settings();
        if (!File.Exists(s.PythonPath)) return (false, $"VoiceStudio Python topilmadi: {s.PythonPath}");
        if (!File.Exists(_script)) return (false, $"align_words.py topilmadi: {_script}");
        Directory.CreateDirectory(workDir);
        var outp = Path.Combine(workDir, "align-check.stdout.txt"); var errp = Path.Combine(workDir, "align-check.stderr.txt");
        var r = await ToolRunner.RunAsync(Spec(s, new[] { "--check", s.WhisperModel }, outp, errp), TimeSpan.FromMinutes(2), null, ct).ConfigureAwait(false);
        var text = File.Exists(outp) ? File.ReadAllText(outp).Trim() : "";
        if (r.ExitCode == 0) return (true, $"Alignment muhiti tayyor: {text}");
        return (false, $"Alignment muhiti ishlamayapti (exit {r.ExitCode}). VoiceStudio yangilangan bo'lsa, faster-whisper yoki model yo'qolgan bo'lishi mumkin. {text} {r.StderrTail}".Trim());
    }

    public async Task<IReadOnlyList<WordTiming>> TranscribeWordsAsync(string wavPath, string language, string outJson, IJobLog log, CancellationToken ct)
    {
        var s = _settings();
        LastMetrics.Clear();
        if (!File.Exists(s.PythonPath))
            throw PipelineException.Config("align_python_missing", $"VoiceStudio Python muhiti topilmadi: {s.PythonPath}. VoiceStudio o'rnatilganini/yangilanganini tekshiring.");
        if (!File.Exists(_script)) throw PipelineException.Config("align_script_missing", $"align_words.py topilmadi: {_script}");
        var dir = Path.GetDirectoryName(outJson)!;
        var stdout = Path.Combine(dir, "align.stdout.txt"); var stderr = Path.Combine(dir, "align.stderr.txt");
        if (File.Exists(outJson)) File.Delete(outJson);
        var lang = string.IsNullOrWhiteSpace(language) ? "auto" : language.Split('-')[0].ToLowerInvariant();
        log.Info($"Alignment: {s.WhisperModel} (CPU, int8), til '{lang}'.", "Align");
        var r = await ToolRunner.RunAsync(Spec(s, new[] { wavPath, outJson, lang, s.WhisperModel }, stdout, stderr),
            TimeSpan.FromSeconds(Math.Max(60, s.AlignTimeoutSec)), log, ct).ConfigureAwait(false);
        LastMetrics["sec"] = r.Elapsed.TotalSeconds;
        LastMetrics["peak_mb"] = r.PeakMemoryBytes / 1048576.0;
        LastMetrics["cpu_sec"] = r.CpuSeconds;
        LastMetrics["leftover_killed"] = r.LeftoverProcessesKilled;
        if (r.TimedOut) throw PipelineException.Transient("align_timeout", "Alignment vaqtida tugamadi; jarayon yopildi.");
        if (r.ExitCode != 0 || !File.Exists(outJson))
        {
            var tail = r.StderrTail;
            if (tail.Contains("ModuleNotFoundError") || tail.Contains("No module named"))
                throw PipelineException.Config("align_env_broken", $"VoiceStudio Python muhitida faster-whisper yo'q (VoiceStudio yangilanishi integratsiyani buzgan bo'lishi mumkin): {Last(tail)}");
            if (tail.Contains("LocalEntryNotFoundError") || tail.Contains("snapshot") || tail.Contains("offline"))
                throw PipelineException.Config("align_model_missing", $"Whisper modeli ({s.WhisperModel}) keshda topilmadi: {s.HfCacheDir}. {Last(tail)}");
            throw PipelineException.Transient("align_failed", $"Alignment xato bilan tugadi (exit {r.ExitCode}): {Last(tail)}");
        }
        var json = JsonNode.Parse(await File.ReadAllTextAsync(outJson, ct).ConfigureAwait(false))!;
        var words = (json["words"] as JsonArray ?? new JsonArray())
            .Select(w => new WordTiming { Word = w!["word"]!.GetValue<string>(), Start = w["start"]!.GetValue<double>(), End = w["end"]!.GetValue<double>() })
            .ToList();
        LastMetrics["words"] = words.Count;
        log.Info($"Alignment tugadi: {words.Count} so'z, til {json["language"]}, {r.Elapsed.TotalSeconds:0}s, peak {r.PeakMemoryBytes / 1048576.0:0} MB. Jarayon yopildi (model xotirada qolmadi).", "Align");
        if (words.Count == 0) throw PipelineException.Transient("align_empty", "Transkripsiyada birorta so'z topilmadi (audio jim bo'lishi mumkin).");
        return words;
    }

    private static string Last(string s) { var lines = s.Trim().Split('\n'); return string.Join(" | ", lines.TakeLast(3)).Trim(); }
}

using System.Text;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Infrastructure.OpenFilm;

/// <summary>
/// O'rnatilgan OpenFilm ichidagi CLI (sinovda ishlagan yo'l): OpenFilm.exe + ELECTRON_RUN_AS_NODE + app.asar/.../openfilm.mjs.
/// npx ishlatilmaydi, hech narsa yuklab olinmaydi. ffmpeg/ffprobe - OpenFilm'ning resources/runtime/ffmpeg papkasidan.
/// </summary>
public sealed class OpenFilmTool : IFilmTool
{
    private readonly Func<OpenFilmSettings> _settings;
    private readonly IJobStore? _store;
    private readonly string _runId;
    private string? _version;

    public OpenFilmTool(Func<OpenFilmSettings> settings, IJobStore? store = null, string runId = "")
    { _settings = settings; _store = store; _runId = runId; }

    public string ExePath => _settings().ExePath;
    public string InstallDir => Path.GetDirectoryName(_settings().ExePath) ?? "";
    public string CliScript => Path.Combine(InstallDir, "resources", "app.asar", "node_modules", "openfilm", "bin", "openfilm.mjs");
    public string FfmpegDir => Path.Combine(InstallDir, "resources", "runtime", "ffmpeg");
    public string PlaywrightDir => Path.Combine(InstallDir, "resources", "runtime", "ms-playwright");

    internal LaunchSpec Spec(IReadOnlyList<string> args, string? cwd, string stdout, string stderr, string tool) => new()
    {
        Exe = _settings().ExePath,
        Args = new[] { CliScript }.Concat(args).ToList(),
        WorkingDirectory = cwd,
        Tool = tool,
        Env = new Dictionary<string, string?>
        {
            ["ELECTRON_RUN_AS_NODE"] = "1", ["OPENFILM_NO_UPDATE_CHECK"] = "1", ["OPENFILM_NO_BROWSER"] = "1",
            ["PLAYWRIGHT_BROWSERS_PATH"] = PlaywrightDir,
            ["PATH"] = FfmpegDir + ";" + Environment.GetEnvironmentVariable("PATH"),
        },
        RemoveEnv = ClaudeRemove(),
        StdoutPath = stdout, StderrPath = stderr,
    };

    private static IReadOnlyList<string> ClaudeRemove() => Claude.ClaudeEditorAgent.BuildRemoveList();

    private void EnsureInstalled()
    {
        if (!File.Exists(_settings().ExePath)) throw PipelineException.Config("openfilm_missing", $"OpenFilm topilmadi: {_settings().ExePath}");
    }

    public string Version
    {
        get
        {
            if (_version is not null) return _version;
            try { _version = RunSimple(new[] { "--version" }, TimeSpan.FromSeconds(60)).Trim(); }
            catch (Exception e) { _version = "error: " + e.Message; }
            return _version;
        }
    }

    private string RunSimple(IReadOnlyList<string> args, TimeSpan timeout)
    {
        EnsureInstalled();
        var dir = Path.Combine(Path.GetTempPath(), "YtAuto"); Directory.CreateDirectory(dir);
        var o = Path.Combine(dir, $"of-{Guid.NewGuid():N}.out"); var e = Path.Combine(dir, $"of-{Guid.NewGuid():N}.err");
        try
        {
            var r = ToolRunner.RunAsync(Spec(args, dir, o, e, "openfilm"), timeout, null, CancellationToken.None).GetAwaiter().GetResult();
            if (r.ExitCode != 0) throw new InvalidOperationException($"openfilm {string.Join(' ', args)} exit {r.ExitCode}: {r.StderrTail}");
            return File.ReadAllText(o, Encoding.UTF8);
        }
        finally { try { File.Delete(o); File.Delete(e); } catch { } }
    }

    public Task<string> GetSkillTextAsync(string name, CancellationToken ct) =>
        Task.Run(() => RunSimple(new[] { "skill", name }, TimeSpan.FromMinutes(2)), ct);

    public string WriteShims(string dir)
    {
        Directory.CreateDirectory(dir);
        string Bash(string p) => "'" + p.Replace("\\", "/").Replace("'", "'\\''") + "'";
        var sh = new StringBuilder();
        sh.Append("#!/bin/sh\n# YtAuto: OpenFilm'ning o'rnatilgan CLI'si (npx emas)\n");
        sh.Append("export ELECTRON_RUN_AS_NODE=1 OPENFILM_NO_UPDATE_CHECK=1 OPENFILM_NO_BROWSER=1\n");
        sh.Append($"export PLAYWRIGHT_BROWSERS_PATH={Bash(PlaywrightDir)}\n");
        sh.Append($"exec {Bash(_settings().ExePath)} {Bash(CliScript)} \"$@\"\n");
        File.WriteAllText(Path.Combine(dir, "openfilm"), sh.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
        var cmd = new StringBuilder();
        cmd.Append("@echo off\r\nsetlocal\r\n");
        cmd.Append("set ELECTRON_RUN_AS_NODE=1\r\nset OPENFILM_NO_UPDATE_CHECK=1\r\nset OPENFILM_NO_BROWSER=1\r\n");
        cmd.Append($"set \"PLAYWRIGHT_BROWSERS_PATH={PlaywrightDir}\"\r\nset \"PATH={FfmpegDir};%PATH%\"\r\n");
        cmd.Append($"\"{_settings().ExePath}\" \"{CliScript}\" %*\r\n");
        File.WriteAllText(Path.Combine(dir, "openfilm.cmd"), cmd.ToString(), new UTF8Encoding(false));
        return dir;
    }

    public Task<IFilmSession> OpenSessionAsync(IJobLog log, CancellationToken ct)
    {
        EnsureInstalled();
        return Task.FromResult<IFilmSession>(new OpenFilmSession(this, log, _store, _runId));
    }
}

/// <summary>Bitta bosqichdagi OpenFilm buyruqlari bitta Job Object'da; dispose - Studio server va headless brauzerlarni yopadi.</summary>
internal sealed class OpenFilmSession : IFilmSession
{
    private readonly OpenFilmTool _tool;
    private readonly IJobLog _log;
    private readonly IJobStore? _store;
    private readonly string _runId;
    private readonly JobObject _job = new("openfilm-session");
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly HashSet<(int, DateTime)> _pre;

    public OpenFilmSession(OpenFilmTool tool, IJobLog log, IJobStore? store, string runId)
    {
        _tool = tool; _log = log; _store = store; _runId = runId;
        _pre = ProcessSnapshot.Under(_tool.PlaywrightDir).Select(p => (p.Pid, p.StartUtc)).ToHashSet();
    }

    public async Task<ToolRunResult> LookAsync(string filmDir, IReadOnlyList<string> args, string logPrefix, TimeSpan timeout, CancellationToken ct)
    {
        var logs = Path.Combine(Path.GetDirectoryName(filmDir)!, "logs"); Directory.CreateDirectory(logs);
        var spec = _tool.Spec(new[] { "look" }.Concat(args).ToList(), filmDir, Path.Combine(logs, $"{logPrefix}.stdout.txt"), Path.Combine(logs, $"{logPrefix}.stderr.txt"), "openfilm-look");
        var r = await ToolRunner.RunAsync(spec, timeout, _log, ct, sharedJob: _job).ConfigureAwait(false);
        _log.Info($"openfilm look {string.Join(' ', args)} → exit {r.ExitCode} ({r.Elapsed.TotalSeconds:0}s).", "Render");
        return r;
    }

    public async Task<ToolRunResult> RenderAsync(string filmDir, string outMp4, RenderOptions o, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
        var logs = Path.Combine(Path.GetDirectoryName(filmDir)!, "logs"); Directory.CreateDirectory(logs);
        var args = new List<string> { "render", "--out", outMp4, "--fps", o.Fps.ToString(), "--workers", o.Workers.ToString(),
            $"--loudness={o.LoudnessLufs.ToString(System.Globalization.CultureInfo.InvariantCulture)}" };
        if (o.BurnSubtitles) args.Add("--subtitles");
        var spec = _tool.Spec(args, filmDir, Path.Combine(logs, "render.stdout.txt"), Path.Combine(logs, "render.stderr.txt"), "openfilm-render");
        _log.Info($"OpenFilm render: {o.Workers} parallel renderer, {o.Fps} fps, {o.LoudnessLufs} LUFS, subtitrlar {(o.BurnSubtitles ? "kuydiriladi" : "yo'q")}.", "Render");
        OwnedProcess? p = null;
        var r = await ToolRunner.RunAsync(spec, o.Timeout, _log, ct, x => p = x, _job).ConfigureAwait(false);
        if (p is not null)
            _store?.RecordProcess(new OwnedProcessRecord { RunId = _runId, Stage = "Render", Tool = "openfilm-render", Pid = p.Pid, ProcessStartUtc = p.StartTimeUtc,
                ExePath = spec.Exe, EndedAtUtc = DateTimeOffset.UtcNow, EndReason = $"exit {r.ExitCode}", PeakMemoryBytes = r.PeakMemoryBytes });
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        var ids = _job.ProcessIds();
        if (ids.Length > 0)
        {
            _job.RequestCloseWindows();
            await Task.Delay(1000).ConfigureAwait(false);
            var n = _job.Terminate();
            _log.Info($"OpenFilm: worker ochgan Studio server va child jarayonlar yopildi ({n} ta).", "Render");
        }
        // Job'dan qochgan headless brauzerlar (faqat OpenFilm'ning playwright papkasidagi, sessiyadan keyin paydo bo'lganlar).
        var escaped = ProcessSnapshot.Under(_tool.PlaywrightDir).Where(p => p.StartUtc >= _startUtc && !_pre.Contains((p.Pid, p.StartUtc))).ToList();
        foreach (var e in escaped) ProcessSnapshot.KillExact(e);
        if (escaped.Count > 0) _log.Warn($"Job'dan tashqariga chiqqan {escaped.Count} ta headless brauzer yopildi.", "Render");
        _job.Dispose();
    }
}

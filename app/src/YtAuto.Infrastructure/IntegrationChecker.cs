using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Processes;
using YtAuto.Infrastructure.Scheduling;

namespace YtAuto.Infrastructure;

public enum CheckLevel { Ok, Warning, Failed }

public sealed record CheckResult(string Name, CheckLevel Level, string Message);

/// <summary>"Integratsiyalarni tekshirish": har bir tashqi bog'liqlikni og'ir ish boshlamasdan tekshiradi.</summary>
public sealed class IntegrationChecker
{
    private readonly AppServices _svc;
    public IntegrationChecker(AppServices svc) => _svc = svc;

    /// <param name="deepClaude">true - Claude'ga juda kichik so'rov yuborib, aynan shu kontekstda autentifikatsiya ishlashini tekshiradi.</param>
    public async Task<List<CheckResult>> RunAsync(bool deepClaude, string? workerExe, CancellationToken ct, IProgress<CheckResult>? progress = null)
    {
        var s = _svc.Settings.Load();
        var list = new List<CheckResult>();
        void Add(CheckResult r) { list.Add(r); progress?.Report(r); }

        // RAM/disk
        var total = _svc.Resources.TotalRamGb(); var free = _svc.Resources.AvailableRamGb();
        Add(new("RAM", total < 7.5 ? CheckLevel.Warning : CheckLevel.Ok, $"Jami {total:0.0} GB, bo'sh {free:0.0} GB. Og'ir bosqichlar ketma-ket bajariladi."));

        foreach (var (name, dir) in new[] { ("Ish papkasi", s.Paths.WorkDir), ("Video arxiv", s.Paths.VideoArchiveDir), ("Ovoz arxiv", s.Paths.AudioArchiveDir) })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) { Add(new(name, CheckLevel.Failed, "Sozlanmagan")); continue; }
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".ytauto-check-{Guid.NewGuid():N}"); File.WriteAllText(probe, "ok"); File.Delete(probe);
                var gb = _svc.Resources.FreeDiskGb(dir);
                Add(new(name, gb < s.Limits.MinFreeDiskGb ? CheckLevel.Warning : CheckLevel.Ok, $"{dir} - yozish mumkin, bo'sh {gb:0.0} GB"));
            }
            catch (Exception e) { Add(new(name, CheckLevel.Failed, $"{dir}: {e.Message}")); }
        }
        if (!string.IsNullOrWhiteSpace(s.Paths.WorkDir) && (PathGuard.IsUnder(s.Paths.WorkDir, s.Paths.VideoArchiveDir) || PathGuard.IsUnder(s.Paths.WorkDir, s.Paths.AudioArchiveDir)))
            Add(new("Papkalar", CheckLevel.Failed, "Arxiv vaqtinchalik ish papkasi ichida bo'lmasligi kerak."));

        // Gemini
        var (gOk, gMsg, _) = await _svc.Gemini.CheckAsync(s.Gemini.ModelChain(Gemini.GeminiModelRouter.DefaultChain), ct);
        Add(new("Gemini", gOk ? CheckLevel.Ok : CheckLevel.Failed, gMsg));

        // VoiceStudio (ishga tushirilmaydi - faqat holat)
        if (!File.Exists(s.VoiceStudio.ExePath)) Add(new("VoiceStudio", CheckLevel.Failed, $"Topilmadi: {s.VoiceStudio.ExePath}"));
        else
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(s.VoiceStudio.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
                var h = await http.GetFromJsonAsync<JsonNode>("health", ct);
                var models = await http.GetFromJsonAsync<JsonNode>("models", ct);
                var installed = (models?["models"] as JsonArray)?.Where(m => m?["installed"]?.GetValue<bool>() == true).Select(m => m!["repo_id"]!.GetValue<string>()).ToList() ?? new();
                Add(new("VoiceStudio", CheckLevel.Ok, $"Ishlayapti v{h?["version"]}, o'rnatilgan modellar: {string.Join(", ", installed)}"));
            }
            catch
            {
                Add(new("VoiceStudio", CheckLevel.Ok, $"O'rnatilgan ({s.VoiceStudio.ExePath}); hozir ishlamayapti - worker kerak bo'lganda o'zi ishga tushiradi va keyin yopadi."));
            }
        }

        // Alignment muhiti
        var (aOk, aMsg) = await _svc.Aligner().CheckAsync(Path.Combine(_svc.Paths.DataRoot, "checks"), ct);
        Add(new("Subtitr timing (faster-whisper)", aOk ? CheckLevel.Ok : CheckLevel.Failed, aMsg));

        // OpenFilm + ffmpeg
        var film = _svc.FilmTool();
        if (!File.Exists(s.OpenFilm.ExePath)) Add(new("OpenFilm", CheckLevel.Failed, $"Topilmadi: {s.OpenFilm.ExePath}"));
        else
        {
            var v = film.Version;
            Add(new("OpenFilm CLI", v.StartsWith("error") ? CheckLevel.Failed : CheckLevel.Ok, $"Bundled CLI versiyasi: {v} ({film.CliScript})"));
            var media = _svc.Media(film);
            var fv = media.Version;
            Add(new("ffmpeg/ffprobe", fv.StartsWith("error") || fv.StartsWith("exit") ? CheckLevel.Failed : CheckLevel.Ok, $"{fv} ({film.FfmpegDir})"));
            Add(new("Headless brauzer", Directory.Exists(film.PlaywrightDir) ? CheckLevel.Ok : CheckLevel.Failed, film.PlaywrightDir));
        }

        // Claude
        var claude = _svc.Claude();
        if (!File.Exists(s.Claude.ExecutablePath)) Add(new("Claude Code", CheckLevel.Failed, $"Topilmadi: {s.Claude.ExecutablePath}"));
        else
        {
            Add(new("Claude Code", CheckLevel.Ok, $"{claude.Version} ({s.Claude.ExecutablePath})"));
            var auth = await claude.AuthStatusAsync(ct);
            Add(new("Claude'ga kirish", auth.LoggedIn switch { true => CheckLevel.Ok, false => CheckLevel.Failed, _ => CheckLevel.Warning }, auth.Message));
            if (deepClaude)
            {
                var dir = Path.Combine(_svc.Paths.DataRoot, "checks", "claude");
                Directory.CreateDirectory(dir);
                var log = new RunLog(new RollingFileLog(Path.Combine(_svc.Paths.LogsDir, "checks.log")));
                try
                {
                    var r = await claude.RunAsync(dir, "Reply with exactly: OK", TimeSpan.FromMinutes(3), "auth-check", Array.Empty<string>(), log, ct);
                    var ok = r.Run.ExitCode == 0 && !r.IsError && r.ResultText.Contains("OK");
                    Add(new("Claude autentifikatsiyasi", ok ? CheckLevel.Ok : CheckLevel.Failed,
                        ok ? $"Shu Windows kontekstida ishlaydi ({r.Run.Elapsed.TotalSeconds:0}s, sessiya {r.SessionId})." : $"Ishlamadi (exit {r.Run.ExitCode}): {r.ResultText} {r.Run.StderrTail}".Trim()));
                }
                catch (Exception e) { Add(new("Claude autentifikatsiyasi", CheckLevel.Failed, e.Message)); }
            }
        }

        // YouTube
        if (!_svc.Uploader.IsConnected) Add(new("YouTube", CheckLevel.Warning, "Kanal ulanmagan (Telegram bot → Sozlamalar → YouTube → YouTube'ni ulash)."));
        else
        {
            try { Add(new("YouTube", CheckLevel.Ok, $"Ulangan kanal: {await _svc.YouTubeAuth.ChannelTitleAsync(ct)}")); }
            catch (Exception e) { Add(new("YouTube", CheckLevel.Failed, e.Message)); }
        }

        // Task Scheduler
        try
        {
            var t = new TaskSchedulerService().Query();
            if (!t.Exists) Add(new("Windows jadvali", s.Schedule.Enabled ? CheckLevel.Failed : CheckLevel.Warning, "Task Scheduler'da vazifa yo'q (jadval o'chiq)."));
            else
            {
                var same = workerExe is null || string.Equals(Path.GetFullPath(t.Command), Path.GetFullPath(workerExe), StringComparison.OrdinalIgnoreCase);
                Add(new("Windows jadvali", same ? CheckLevel.Ok : CheckLevel.Warning,
                    $"{t.State}, keyingi: {t.NextRunTime:yyyy-MM-dd HH:mm}, oxirgi natija: {TaskSchedulerService.DescribeResult(t.LastResult)}{(same ? "" : $". Diqqat: boshqa exe'ga ishora qiladi: {t.Command}")}"));
            }
        }
        catch (Exception e) { Add(new("Windows jadvali", CheckLevel.Warning, e.Message)); }
        return list;
    }
}

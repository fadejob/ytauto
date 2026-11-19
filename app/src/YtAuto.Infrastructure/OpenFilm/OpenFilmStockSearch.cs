using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Media;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Infrastructure.OpenFilm;

/// <summary>
/// Stok rasm va video parchalarini OpenFilm'ning o'zi qidiradi: <c>openfilm get image-search|video-search --via pexels|pixabay</c>.
/// OpenFilm natijani faylga yuklab, JSON bilan (o'lcham, davomiylik, muallif, sahifa) javob beradi.
/// <para>
/// `get` OpenFilm Studio server ichida bajariladi va manba kalitini Studio ishga tushgandagi muhitdan oladi. Shuning uchun worker
/// alohida uy papkasi (OPENFILM_HOME) bilan o'z Studio'sini ochadi: kalitlar faqat shu jarayon muhitida bo'ladi, foydalanuvchining
/// OpenFilm sozlamalariga (~/.openfilm) hech narsa yozilmaydi va ochiq turgan Studio'siga tegilmaydi. Qidiruv tugagach Studio yopiladi.
/// </para>
/// </summary>
public sealed class OpenFilmStockSearch : IStockMediaSource
{
    private readonly OpenFilmTool _tool;
    private readonly Func<StockPhotoKeys> _keys;
    private readonly string _home;

    /// <summary>OpenFilm bir so'rovda ko'pi bilan shuncha beradi (image-search 1-8, video-search 1-4).</summary>
    internal const int MaxImagesPerCall = 8, MaxClipsPerCall = 4;

    public OpenFilmStockSearch(OpenFilmTool tool, Func<StockPhotoKeys> keys, string homeDir)
    { _tool = tool; _keys = keys; _home = homeDir; }

    private StockPhotoKeys Keys() { try { return _keys() ?? StockPhotoKeys.None; } catch { return StockPhotoKeys.None; } }

    public bool IsConfigured
    {
        get
        {
            var k = Keys();
            // CLI skripti app.asar arxivi ichida (Windows uchun u fayl emas): arxivning o'zi tekshiriladi.
            return File.Exists(_tool.ExePath) && File.Exists(_tool.AsarPath) && (!string.IsNullOrWhiteSpace(k.Pexels) || !string.IsNullOrWhiteSpace(k.Pixabay));
        }
    }

    /// <summary>Kaliti bor manbalar, sifat tartibida: Pexels (1880 px foto, Full HD video), keyin Pixabay.</summary>
    internal static List<string> Providers(StockPhotoKeys k)
    {
        var l = new List<string>();
        if (!string.IsNullOrWhiteSpace(k.Pexels)) l.Add("pexels");
        if (!string.IsNullOrWhiteSpace(k.Pixabay)) l.Add("pixabay");
        return l;
    }

    public async Task<StockCandidates> FetchAsync(IReadOnlyList<StockRequest> scenes, string workDir, StockOptions o, IJobLog log, CancellationToken ct)
    {
        var keys = Keys();
        var providers = Providers(keys);
        var result = new StockCandidates();
        if (providers.Count == 0 || scenes.Count == 0) return result;

        // `get` faqat OpenFilm loyihasi ichida ishlaydi: workDir - shu maqsad uchun bo'sh loyiha (film.html). Natija assets/image|video/ ga tushadi.
        Directory.CreateDirectory(workDir);
        var stub = Path.Combine(workDir, "film.html");
        if (!File.Exists(stub)) File.WriteAllText(stub, "<!doctype html><meta name=\"viewport\" content=\"width=1920, height=1080\"><title>stock</title>\n", new UTF8Encoding(false));
        var logs = Path.Combine(workDir, "logs"); Directory.CreateDirectory(logs);
        Directory.CreateDirectory(_home);
        StopStaleStudio(log);

        var env = new Dictionary<string, string?>
        {
            ["OPENFILM_HOME"] = _home,
            ["PEXELS_API_KEY"] = string.IsNullOrWhiteSpace(keys.Pexels) ? null : keys.Pexels.Trim(),
            ["PIXABAY_API_KEY"] = string.IsNullOrWhiteSpace(keys.Pixabay) ? null : keys.Pixabay.Trim(),
        };
        var off = new HashSet<string>(); // kalitni rad etgan yoki limiti tugagan manba - shu video uchun o'chiriladi
        using var job = new JobObject("openfilm-stock");
        var sw = Stopwatch.StartNew();
        try
        {
            var n = 0;
            foreach (var req in scenes)
            {
                ct.ThrowIfCancellationRequested();
                var scene = new StockScene { Id = req.Id, Query = req.Query, Purpose = req.Purpose, At = req.At };
                result.Scenes.Add(scene);
                await SearchAsync(scene, "image-search", Math.Clamp(o.Images, 0, MaxImagesPerCall), null, providers, off, env, workDir, logs, job, log, ct).ConfigureAwait(false);
                await SearchAsync(scene, "video-search", Math.Clamp(o.Clips, 0, MaxClipsPerCall), o.Orientation, providers, off, env, workDir, logs, job, log, ct).ConfigureAwait(false);
                n++;
                log.Info($"OpenFilm stok [{n}/{scenes.Count}] «{req.Query}»: {scene.Images} rasm, {scene.Clips} video" +
                         (scene.Notes.Count > 0 ? $" ({string.Join("; ", scene.Notes)})" : "") + ".", "Media");
            }
        }
        finally
        {
            // CLI ochgan Studio server (va uning child'lari) shu job'da: yopiladi, kalitli jarayon qolib ketmaydi.
            if (job.ProcessIds().Length > 0) { job.RequestCloseWindows(); await Task.Delay(500, CancellationToken.None).ConfigureAwait(false); job.Terminate(); }
            StopStaleStudio(log);
        }
        log.Info($"OpenFilm stok qidiruvi: {result.Scenes.Count} sahna, {result.Scenes.Sum(s => s.Images)} rasm va {result.Scenes.Sum(s => s.Clips)} video nomzod ({sw.Elapsed.TotalSeconds:0}s).", "Media");
        return result;
    }

    /// <summary>Bitta sahna uchun bitta tur: avval birinchi manbadan, yetmasa keyingisidan (Pexels → Pixabay).</summary>
    private async Task SearchAsync(StockScene scene, string verb, int want, string? orientation, List<string> providers, HashSet<string> off,
        Dictionary<string, string?> env, string workDir, string logs, JobObject job, IJobLog log, CancellationToken ct)
    {
        if (want <= 0) return;
        var type = verb == "video-search" ? StockTypes.Video : StockTypes.Image;
        foreach (var p in providers)
        {
            var have = scene.Candidates.Count(c => c.Type == type);
            if (have >= want) return;
            if (off.Contains(p)) continue;
            var outName = $"{scene.Id}_{p}_{(type == StockTypes.Video ? "clip" : "img")}";
            var args = new List<string> { "get", verb, "--via", p, "--out", outName, "--prompt", scene.Query, "--n", (want - have).ToString(), "--yes" };
            if (type == StockTypes.Video && !string.IsNullOrWhiteSpace(orientation)) { args.Add("--orientation"); args.Add(orientation); }
            var prefix = $"stock-{outName}";
            var spec = _tool.Spec(args, workDir, Path.Combine(logs, prefix + ".stdout.json"), Path.Combine(logs, prefix + ".stderr.txt"), "openfilm-get");
            spec = WithEnv(spec, env);
            ToolRunResult r;
            try { r = await ToolRunner.RunAsync(spec, TimeSpan.FromMinutes(type == StockTypes.Video ? 8 : 4), null, ct, sharedJob: job).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { scene.Notes.Add($"{p} {verb}: {e.Message}"); continue; }
            var stdout = File.Exists(spec.StdoutPath) ? await File.ReadAllTextAsync(spec.StdoutPath!, ct).ConfigureAwait(false) : "";
            if (r.TimedOut) { scene.Notes.Add($"{p} {verb}: vaqt tugadi"); continue; }
            if (r.ExitCode != 0)
            {
                var why = Shorten((r.StderrTail + " " + r.StdoutTail).Trim(), 200);
                scene.Notes.Add($"{p}: {why}");
                if (IsProviderRefusal(why)) { off.Add(p); log.Warn($"OpenFilm stok: {p} rad etdi (kalit yoki limit), shu video uchun o'chirildi: {why}", "Media"); }
                continue;
            }
            foreach (var c in ParseOutput(stdout, type, p))
            {
                var abs = Path.GetFullPath(Path.Combine(workDir, c.File.Replace('/', Path.DirectorySeparatorChar)));
                if (!abs.StartsWith(Path.GetFullPath(workDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(abs)) continue;
                c.File = abs;
                c.Bytes = new FileInfo(abs).Length;
                scene.Candidates.Add(c);
            }
        }
    }

    private static LaunchSpec WithEnv(LaunchSpec s, Dictionary<string, string?> extra)
    {
        var env = new Dictionary<string, string?>(s.Env);
        foreach (var (k, v) in extra) env[k] = v;
        return new LaunchSpec
        {
            Exe = s.Exe, Args = s.Args, WorkingDirectory = s.WorkingDirectory, Tool = s.Tool, Env = env, RemoveEnv = s.RemoveEnv,
            StdoutPath = s.StdoutPath, StderrPath = s.StderrPath,
        };
    }

    /// <summary>Kalit qabul qilinmadi yoki limit tugadi - bu manbani qayta urinish befoyda.</summary>
    internal static bool IsProviderRefusal(string message) =>
        System.Text.RegularExpressions.Regex.IsMatch(message, @"(?i)\b(401|403|429)\b|did not accept|no \w+ key|not connected|rate limit|too many requests|quota");

    /// <summary>
    /// `openfilm get` JSON javobi: image-search - {"provider","images":[{src,width,height,credit,page}]},
    /// video-search - {"provider","clips":[{src,dur,w,h,credit,page}]}. Oldida boshqa matn bo'lishi mumkin (birinchi '{' dan o'qiladi).
    /// </summary>
    internal static List<StockCandidate> ParseOutput(string stdout, string type, string provider)
    {
        var list = new List<StockCandidate>();
        var start = stdout.IndexOf('{');
        if (start < 0) return list;
        JsonNode? j;
        try { j = JsonNode.Parse(stdout[start..]); } catch (System.Text.Json.JsonException) { return list; }
        var arr = (type == StockTypes.Video ? j?["clips"] : j?["images"]) as JsonArray;
        if (arr is null) return list;
        foreach (var x in arr)
        {
            var src = Str(x?["src"]);
            if (src.Length == 0) continue;
            list.Add(new StockCandidate
            {
                Type = type, File = src, Provider = Str(j?["provider"]) is { Length: > 0 } pv ? pv : provider,
                Width = Int(x?["width"] ?? x?["w"]), Height = Int(x?["height"] ?? x?["h"]),
                Duration = Num(x?["dur"]), Credit = Str(x?["credit"]), Page = Str(x?["page"]),
            });
        }
        return list;
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
    private static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    private static int Int(JsonNode? n) => (int)Math.Round(Num(n));
    private static string Shorten(string s, int n) { s = s.ReplaceLineEndings(" "); return s.Length > n ? s[..n] + "…" : s; }

    /// <summary>
    /// Oldingi (masalan, uzilib qolgan) qidiruvdan qolgan Studio: eski kalitlar bilan ishlamasligi uchun yopiladi. Faqat shu uy papkasining
    /// run.json'ida yozilgan va nomi OpenFilm bo'lgan jarayonlar.
    /// </summary>
    private void StopStaleStudio(IJobLog log)
    {
        var run = Path.Combine(_home, "run.json");
        if (!File.Exists(run)) return;
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(run));
            foreach (var key in new[] { "supervisor", "pid" })
            {
                if (j?[key] is not JsonValue v || !v.TryGetValue<int>(out var pid) || pid <= 0) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.ProcessName.Equals("OpenFilm", StringComparison.OrdinalIgnoreCase)) { p.Kill(true); log.Debug($"OpenFilm stok: eski Studio yopildi (pid {pid})."); }
                }
                catch (ArgumentException) { } // jarayon yo'q
            }
            File.Delete(run);
        }
        catch (Exception e) { log.Debug("OpenFilm stok: eski Studio'ni tekshirib bo'lmadi: " + e.Message); }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Scheduling;
using YtAuto.Infrastructure;

namespace YtAuto.Worker;

/// <summary>
/// YtAuto worker. Oynasiz. Rejimlar:
/// --scheduled: bazadan sinxronlash → navbatdagi mavzular soniga qarab 1-3 ta video (orasida 15 daqiqa tanaffus, <see cref="BatchPlanner"/>);
/// --poll: faqat "Hozir chiqarish" so'rovlari (masofaviy buyruq; so'rov bo'lmasa darhol jim chiqadi);
/// --manual: bitta vazifa. Har rejimda "Hozir chiqarish" so'rovlari birinchi bajariladi.
/// Exit kodlar: 0 - muvaffaqiyatli yoki navbat bo'sh, 1 - xato (keyingi safar qayta uriniladi), 2 - e'tibor kerak,
/// 3 - boshqa worker ishlamoqda, 4 - kutilmagan xato.
/// Argumentlar: --scheduled | --poll | --manual | --selftest-local [--data DIR] | --check | --refresh | --console
/// </summary>
internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    private static async Task<int> Main(string[] args)
    {
        var console = args.Contains("--console") || args.Contains("--selftest-local") || args.Contains("--check");
        if (console) AttachConsole(-1);
        var dataIdx = Array.IndexOf(args, "--data");
        var dataRoot = dataIdx >= 0 && dataIdx + 1 < args.Length ? args[dataIdx + 1] : null;
        if (args.Contains("--selftest-local") && dataRoot is null)
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto-selftest");

        // OS darajasidagi yagona nusxa blokirovkasi (bazadagi atomar band qilishdan tashqari).
        var mutexName = dataRoot is null ? @"Global\YtAuto.Worker" : @"Global\YtAuto.Worker." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot))))[..16];
        using var mutex = new Mutex(false, mutexName);
        var poll = args.Contains("--poll");
        var scheduled = args.Contains("--scheduled");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // oldingi worker kutilmaganda o'lgan
        // --poll har 2 daqiqada ishga tushadi: boshqa worker ishlayotgan bo'lsa (u "hozir chiqarish"ni ham tekshiradi) jim chiqamiz.
        if (!owned && poll) return 3;
        AppServices svc;
        try { svc = new AppServices(dataRoot is null ? "worker" : "worker-selftest", dataRoot, console); }
        catch (Exception e) { Console.Error.WriteLine("Ishga tushmadi: " + e); return 4; }
        if (!owned && scheduled)
        {
            // Masalan, "hozir chiqarish" videosi tayyorlanmoqda: kunlik ishga tushish yo'qolmasligi uchun kutamiz.
            svc.Log.Info("Boshqa worker nusxasi ishlamoqda — u tugashini kutamiz (eng ko'pi 4 soat).");
            try { owned = mutex.WaitOne(TimeSpan.FromHours(4)); }
            catch (AbandonedMutexException) { owned = true; }
        }
        if (!owned)
        {
            svc.Log.Warn("Boshqa worker nusxasi ishlamoqda - bu nusxa hech narsa qilmasdan chiqadi.");
            return 3;
        }
        var trigger = scheduled ? "scheduled" : poll ? "remote" : args.Contains("--selftest-local") ? "selftest" : args.Contains("--check") ? "check" : "manual";
        using var cts = new CancellationTokenSource();
        // ProcessExit Main tugaganidan keyin ham chaqiriladi: yopilgan CTS ga tegmaslik kerak (aks holda exit kodi buziladi).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        try
        {
            if (args.Contains("--schedule-install") || args.Contains("--schedule-remove"))
            {
                var ts = new Infrastructure.Scheduling.TaskSchedulerService();
                var s = svc.Settings.Load();
                if (args.Contains("--schedule-remove")) { ts.Unregister(); s.Schedule.Enabled = false; }
                else { ts.Register(s.Schedule, Environment.ProcessPath!); s.Schedule.Enabled = true; }
                svc.Settings.Save(s);
                var info = ts.Query();
                svc.Log.Info($"Jadval: exists={info.Exists}, next={info.NextRunTime:yyyy-MM-dd HH:mm}, command={info.Command}");
                return 0;
            }
            if (args.Contains("--import-config"))
                return ImportConfig(svc, args.SkipWhile(a => a != "--import-config").Skip(1).FirstOrDefault());
            if (args.Contains("--check")) return await IntegrationCheck.RunAsync(svc, cts.Token);
            if (args.Contains("--selftest-local")) return await SelfTest.RunAsync(svc, cts.Token);

            // Masofaviy buyruq kutuvchi: so'rov bo'lmasa hech narsa yozmasdan chiqadi (har 2 daqiqada log/baza to'lmasligi uchun).
            if (poll && svc.Store.CountPublishNow(DateTimeOffset.UtcNow) == 0) return 0;

            using var self = Process.GetCurrentProcess();
            svc.Store.StartRun(svc.RunId, Environment.ProcessId, self.StartTime.ToUniversalTime(), trigger);
            svc.Log.Info($"Worker boshlandi ({trigger}), run {svc.RunId}. Bo'sh RAM {svc.Resources.AvailableRamGb():0.00}/{svc.Resources.TotalRamGb():0.0} GB.");
            var runner = new JobRunner(svc.Pipeline());

            await RefreshYouTubeAsync(svc, runner, cts.Token);
            if (args.Contains("--refresh")) { svc.Store.FinishRun(svc.RunId, "refresh", null, null); return 0; }

            List<RunSummary> results;
            if (poll) results = await RunPublishNowAsync(svc, runner, cts.Token);
            else if (scheduled) results = await RunBatchAsync(svc, runner, cts.Token);
            else results = new List<RunSummary> { await runner.RunOnceAsync(svc.RunId, cts.Token) };

            var last = results.LastOrDefault(r => r.Outcome != RunOutcome.QueueEmpty) ?? results.LastOrDefault() ?? new RunSummary { Outcome = RunOutcome.QueueEmpty };
            var worked = results.Where(r => r.Outcome != RunOutcome.QueueEmpty).ToList();
            var outcome = worked.Count > 1 ? $"{worked.Count(r => r.Outcome == RunOutcome.Completed)}/{worked.Count} video tayyor" : last.Outcome.ToString();
            var message = worked.Count > 1
                ? string.Join("; ", worked.Select(r => $"#{r.JobId}{(r.PublishNow ? " (hozir)" : "")}: {r.Outcome}{(r.Message is null ? "" : " - " + r.Message)}"))
                : last.Message;
            svc.Store.FinishRun(svc.RunId, outcome, last.JobId, message);
            svc.Log.Info($"Worker tugadi: {outcome}, vazifalar {(worked.Count == 0 ? "-" : string.Join(", ", worked.Select(r => "#" + r.JobId)))}, " +
                         $"{results.Sum(r => r.Elapsed.TotalMinutes):0.0} daqiqa ish.");
            return results.Count == 0 ? 0 : results.Max(r => ExitCode(r.Outcome));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            svc.Log.Warn("Worker to'xtatildi (Windows/Task Scheduler jarayonni yakunladi).");
            try { svc.Store.FinishRun(svc.RunId, "stopped", null, "Jarayon tashqaridan to'xtatildi"); } catch { }
            return 1;
        }
        catch (Exception e)
        {
            svc.Log.Error("Kutilmagan xato: " + e);
            try { svc.Store.FinishRun(svc.RunId, "crash", null, e.Message); } catch { }
            return 4;
        }
        finally
        {
            if (owned) try { mutex.ReleaseMutex(); } catch { }
        }
    }

    /// <summary>
    /// Bir martalik import: JSON fayldan Gemini kaliti va YouTube OAuth client'ni oladi (Google'ning client_secret_*.json
    /// formati ham qabul qilinadi), sirlarni DPAPI'ga yozadi va faylni ustiga yozib o'chiradi. Qiymatlar logga yozilmaydi.
    /// </summary>
    private static int ImportConfig(AppServices svc, string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { svc.Log.Error("--import-config: fayl topilmadi."); return 4; }
        var imported = new List<string>();
        try
        {
            var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var client = j["installed"] ?? j;
            var s = svc.Settings.Load();
            if (j["gemini_api_key"]?.GetValue<string>() is { Length: > 0 } g) { svc.Secrets.Set(SecretNames.GeminiApiKey, g.Trim()); imported.Add("gemini_api_key"); }
            if (j["gemini_model"]?.GetValue<string>() is { Length: > 0 } gm) { s.Gemini.Model = gm.Trim(); imported.Add("gemini_model"); }
            if (client["client_id"]?.GetValue<string>() is { Length: > 0 } id) { s.YouTube.ClientId = id.Trim(); imported.Add("youtube_client_id"); }
            if (client["client_secret"]?.GetValue<string>() is { Length: > 0 } cs) { svc.Secrets.Set(SecretNames.YouTubeClientSecret, cs.Trim()); imported.Add("youtube_client_secret"); }
            if (j["postgres_connection"]?.GetValue<string>() is { Length: > 0 } pg)
            {
                var (ok, msg) = Infrastructure.Data.PostgresJobStore.Check(pg);
                if (!ok) { svc.Log.Error("Online baza ulanish satri saqlanmadi: " + msg); return 1; }
                svc.Secrets.Set(SecretNames.PostgresConnection, pg.Trim());
                var remote = new Infrastructure.Data.PostgresJobStore(pg.Trim());            // sxema yaratiladi
                var moved = remote.ImportFrom(svc.LocalDb);
                var shared = new Infrastructure.Data.SqliteSettingsStore(svc.LocalDb, AppServices.DefaultSettings, remote);
                shared.Save(shared.Load());                                                    // promptlar online bazaga
                imported.Add($"postgres_connection ({msg}; ko'chirilgan mavzular: {moved})");
            }
            svc.Settings.Save(s);
            svc.Log.Info($"Konfiguratsiya import qilindi: {string.Join(", ", imported)} (qiymatlar logga yozilmaydi).");
            return 0;
        }
        catch (Exception e) { svc.Log.Error("Import xatosi: " + e.Message); return 4; }
        finally
        {
            try
            {
                var len = new FileInfo(path).Length;
                File.WriteAllBytes(path, new byte[len]);
                File.Delete(path);
            }
            catch { }
        }
    }

    private static int ExitCode(RunOutcome o) => o switch
    {
        RunOutcome.QueueEmpty or RunOutcome.Completed or RunOutcome.StoppedBeforeUpload or RunOutcome.Cancelled => 0,
        RunOutcome.Failed => 1,
        _ => 2,
    };

    /// <summary>
    /// Jadval bo'yicha ishga tushish: avval "hozir chiqarish" so'rovlari, so'ng bazadan sinxronlab navbatdagi mavzular soniga qarab
    /// 1-3 ta video (<see cref="BatchPlanner"/>). Videolar orasida 15 daqiqa tanaffus; tanaffusda kelgan "hozir chiqarish" so'rovlari
    /// darhol bajariladi (ular reja soniga kirmaydi). Vaqtinchalik xato (tarmoq, limit) bo'lsa, qolgan videolar keyingi jadvalga qoladi.
    /// </summary>
    private static async Task<List<RunSummary>> RunBatchAsync(AppServices svc, JobRunner runner, CancellationToken ct)
    {
        var results = await RunPublishNowAsync(svc, runner, ct);
        var pending = svc.Store.CountPending(DateTimeOffset.UtcNow);
        var plan = BatchPlanner.VideosFor(pending);
        svc.Log.Info($"Bazadan sinxronlandi ({svc.Store.Kind}): navbatda {pending} ta mavzu — {BatchPlanner.Describe(pending)}.");
        int made = 0;
        for (int guard = 0; made < plan && guard < plan + 10; guard++)
        {
            var r = await runner.RunOnceAsync(svc.RunId, ct);
            results.Add(r);
            if (r.Outcome == RunOutcome.QueueEmpty) { svc.Log.Info("Navbat bo'shadi — ishga tushish yakunlandi."); break; }
            if (!r.PublishNow) made++;
            svc.Log.Info($"{(r.PublishNow ? "Hozir chiqarish" : $"Video {made}/{plan}")}: #{r.JobId} {r.Outcome} ({r.Elapsed.TotalMinutes:0.0} daqiqa).");
            if (r.Outcome == RunOutcome.Failed)
            {
                svc.Log.Warn("Vaqtinchalik xato — qolgan videolar keyingi jadvalda tayyorlanadi.");
                break;
            }
            if (made >= plan) break;
            svc.Log.Info($"{BatchPlanner.PauseBetweenVideos.TotalMinutes:0} daqiqa tanaffus, so'ng keyingi video.");
            results.AddRange(await PauseAsync(svc, runner, BatchPlanner.PauseBetweenVideos, ct));
        }
        return results;
    }

    /// <summary>Tanaffus; har daqiqada online bazadan "hozir chiqarish" so'rovi tekshiriladi va bo'lsa darhol bajariladi.</summary>
    private static async Task<List<RunSummary>> PauseAsync(AppServices svc, JobRunner runner, TimeSpan pause, CancellationToken ct)
    {
        var results = new List<RunSummary>();
        var until = DateTimeOffset.UtcNow + pause;
        while (true)
        {
            try
            {
                if (svc.Store.CountPublishNow(DateTimeOffset.UtcNow) > 0) results.AddRange(await RunPublishNowAsync(svc, runner, ct));
            }
            catch (Exception e) when (e is not OperationCanceledException) { svc.Log.Warn("Tanaffusda 'hozir chiqarish' tekshiruvi o'tmadi: " + e.Message); }
            var left = until - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return results;
            await Task.Delay(left < TimeSpan.FromMinutes(1) ? left : TimeSpan.FromMinutes(1), ct);
        }
    }

    /// <summary>Barcha "hozir chiqarish" so'rovlarini ketma-ket bajaradi (eng ko'pi 10 ta, vaqtinchalik xatoda to'xtaydi).</summary>
    private static async Task<List<RunSummary>> RunPublishNowAsync(AppServices svc, JobRunner runner, CancellationToken ct)
    {
        var list = new List<RunSummary>();
        for (int i = 0; i < 10; i++)
        {
            var r = await runner.RunOnceAsync(svc.RunId, publishNowOnly: true, ct);
            if (r.Outcome == RunOutcome.QueueEmpty) break;
            list.Add(r);
            svc.Log.Info($"Hozir chiqarish: #{r.JobId} {r.Outcome} ({r.Elapsed.TotalMinutes:0.0} daqiqa).");
            if (r.Outcome == RunOutcome.Failed) break;
        }
        return list;
    }

    /// <summary>Processing/Scheduled videolar holatini yengil API so'rovi bilan yangilaydi (og'ir dasturlar ochilmaydi).</summary>
    private static async Task RefreshYouTubeAsync(AppServices svc, JobRunner runner, CancellationToken ct)
    {
        if (!svc.Uploader.IsConnected) return;
        var jobs = svc.Store.ListByStatus(JobStatus.Processing, JobStatus.Scheduled).Take(25).ToList();
        if (jobs.Count == 0) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        foreach (var j in jobs)
        {
            try { await runner.RefreshYouTubeStatusAsync(j, new JobLog(j.Id, svc.Paths.RecordDirFor(j.Id), svc.Store, svc.Log), budget.Token); }
            catch (Exception e) { svc.Log.Warn($"#{j.Id} YouTube holati yangilanmadi: {e.Message}"); }
        }
    }
}

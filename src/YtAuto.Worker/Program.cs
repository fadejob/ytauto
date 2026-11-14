using System.Diagnostics;
using System.Runtime.InteropServices;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure;

namespace YtAuto.Worker;

/// <summary>
/// YtAuto worker: bitta ishga tushishda navbatdan BITTA vazifa. Oynasiz. Exit kodlar:
/// 0 - muvaffaqiyatli yoki navbat bo'sh, 1 - xato (keyingi safar qayta uriniladi), 2 - e'tibor kerak, 3 - boshqa worker ishlamoqda, 4 - kutilmagan xato.
/// Argumentlar: --scheduled | --manual | --selftest-local [--data DIR] | --check | --refresh | --console
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
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // oldingi worker kutilmaganda o'lgan
        AppServices svc;
        try { svc = new AppServices(dataRoot is null ? "worker" : "worker-selftest", dataRoot, console); }
        catch (Exception e) { Console.Error.WriteLine("Ishga tushmadi: " + e); return 4; }
        if (!owned)
        {
            svc.Log.Warn("Boshqa worker nusxasi ishlamoqda - bu nusxa hech narsa qilmasdan chiqadi.");
            return 3;
        }
        var trigger = args.Contains("--scheduled") ? "scheduled" : args.Contains("--selftest-local") ? "selftest" : args.Contains("--check") ? "check" : "manual";
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

            using var self = Process.GetCurrentProcess();
            svc.Store.StartRun(svc.RunId, Environment.ProcessId, self.StartTime.ToUniversalTime(), trigger);
            svc.Log.Info($"Worker boshlandi ({trigger}), run {svc.RunId}. Bo'sh RAM {svc.Resources.AvailableRamGb():0.00}/{svc.Resources.TotalRamGb():0.0} GB.");
            var runner = new JobRunner(svc.Pipeline());

            await RefreshYouTubeAsync(svc, runner, cts.Token);
            if (args.Contains("--refresh")) { svc.Store.FinishRun(svc.RunId, "refresh", null, null); return 0; }

            var result = await runner.RunOnceAsync(svc.RunId, cts.Token);
            svc.Store.FinishRun(svc.RunId, result.Outcome.ToString(), result.JobId, result.Message);
            svc.Log.Info($"Worker tugadi: {result.Outcome}, vazifa {result.JobId?.ToString() ?? "-"}, {result.Elapsed.TotalMinutes:0.0} daqiqa.");
            return result.Outcome switch
            {
                RunOutcome.QueueEmpty or RunOutcome.Completed or RunOutcome.StoppedBeforeUpload or RunOutcome.Cancelled => 0,
                RunOutcome.Failed => 1,
                _ => 2,
            };
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

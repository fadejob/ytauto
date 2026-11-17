using System.Diagnostics;
using System.Runtime.InteropServices;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Scheduling;
using YtAuto.Infrastructure;

namespace YtAuto.Worker;

/// <summary>
/// YtAuto worker. Oynasiz. Rejimlar:
/// --scheduled: bazadan sinxronlash → navbatdagi barcha mavzular, ish oynasi tugaguncha (chiqish vaqti faqat YouTube uchun, <see cref="BatchPlanner"/>);
/// --poll: faqat masofaviy (bot) so'rovlar: "Tayyor videoni YouTube'ga yuklash" va "Hozir chiqarish" (so'rov bo'lmasa darhol jim chiqadi);
/// --manual: bitta vazifa. Har rejimda "Hozir chiqarish" so'rovlari birinchi bajariladi.
/// Exit kodlar: 0 - muvaffaqiyatli yoki navbat bo'sh, 1 - xato (keyingi safar qayta uriniladi), 2 - e'tibor kerak,
/// 3 - boshqa worker ishlamoqda, 4 - kutilmagan xato.
/// Argumentlar: --scheduled | --poll | --manual | --selftest-local [--data DIR] | --check | --refresh | --console
/// </summary>
public static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    /// <summary>Yagona YtAuto.exe ichidan ham chaqiriladi (worker rejimi: --poll, --scheduled, --batch-now va h.k.).</summary>
    private static Task<int> Main(string[] args) => RunAsync(args);

    public static async Task<int> RunAsync(string[] args)
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
        if (poll) { try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } } // yengil: kompyuter sezmasligi uchun
        var batchNow = args.Contains("--batch-now"); // botdan "Hozir ishga tushirish": jadvalni kutmasdan, mavzular soniga qarab ish (scheduled bilan bir xil)
        var scheduled = args.Contains("--scheduled") || batchNow;
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // oldingi worker kutilmaganda o'lgan
        // --poll har 10 daqiqada ishga tushadi: boshqa worker ishlayotgan bo'lsa (u "hozir chiqarish"ni ham tekshiradi) jim chiqamiz.
        if (!owned && (poll || batchNow)) return 3;
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
        var trigger = batchNow ? "bot" : scheduled ? "scheduled" : poll ? "remote" : args.Contains("--selftest-local") ? "selftest" : args.Contains("--check") ? "check" : "manual";
        using var cts = new CancellationTokenSource();
        Infrastructure.Stats.RunStatsSampler? sampler = null;
        Infrastructure.Heartbeat.MachineBeat.WorkerBeat? beat = null;
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
            if (args.Contains("--publish-catalog"))
            {
                // Faqat Telegram bot katalogi (ovoz profillari, shablonlar, standart chiqish vaqti) — video yasamaydi, hech narsa o'chirmaydi.
                if (!svc.IsOnline) { svc.Log.Warn("Online baza ulanmagan — katalog yuborilmadi."); return 2; }
                svc.Settings.PublishCatalog();
                svc.Log.Info("Bot katalogi online bazaga yozildi (bot.catalog).");
                return 0;
            }
            if (args.Contains("--check")) return await IntegrationCheck.RunAsync(svc, cts.Token);
            if (args.Contains("--selftest-local")) return await SelfTest.RunAsync(svc, cts.Token);

            // Ko'p foydalanuvchili rejim: hisob sozlanmagan bo'lsa (ilovada "Sozlash" bosilmagan) worker hech narsa qilmaydi.
            if (dataRoot is null && !svc.IsAccountReady)
            {
                if (!poll) svc.Log.Warn("Sozlash tugallanmagan: YtAuto oynasini ochib, «Kerakli fayllarni yuklash» tugmasini bosing. Worker chiqadi.");
                return 0;
            }

            // Jadval botda o'zgargan bo'lsa Task Scheduler shu yerda yangilanadi (har --poll da, ~10 daqiqada).
            if (dataRoot is null && (poll || scheduled)) Infrastructure.Scheduling.ScheduleSync.Apply(svc, Environment.ProcessPath);

            // Heartbeat: poll har 10 daqiqada "kompyuter yoniq va online" deb yozadi (yengil: faqat holat, og'ir ish yo'q); botdan tekshiruv so'rovi bo'lsa bajaradi.
            if (dataRoot is null && (poll || scheduled))
            {
                if (poll) Infrastructure.Heartbeat.MachineBeat.Beat(svc, workerState: "idle", jobId: null, detail: "bo'sh (har 10 daqiqada holat yuboradi)");
                if (poll) Infrastructure.Heartbeat.ToolStatus.Publish(svc); // dasturlar holati (bot ko'rsatadi)
                await Infrastructure.Heartbeat.MachineBeat.RunCheckIfRequestedAsync(svc, Environment.ProcessPath, cts.Token);
            }

            // Botdan kelgan shu kompyuterga buyruqlar (shablonni o'chirish, upload sessiyasini tozalash).
            if (dataRoot is null && (poll || scheduled)) Infrastructure.Commands.RemoteCommands.Run(svc);

            // Botdan "Hozir ishga tushirish": yengil poll og'ir ishga o'tadi - jadvaldagi kabi mavzular soniga qarab videolar tayyorlanadi.
            if (poll && dataRoot is null && svc.IsOnline && svc.Store.TakeRunRequest(Environment.MachineName))
            {
                poll = false; scheduled = true; trigger = "bot";
                svc.Log.Info("Botdan 'Hozir ishga tushirish' so'rovi keldi - ish boshlanmoqda.");
            }

            // Masofaviy buyruq kutuvchi: so'rov bo'lmasa hech narsa yozmasdan chiqadi (har 10 daqiqada log/baza to'lmasligi uchun).
            // Yengil: YouTube holati (Processing/Scheduled -> Published) har poll'da 1-2 ta kichik so'rov bilan yangilanadi, og'ir ish yo'q.
            if (poll && dataRoot is null) await LightYouTubeRefreshAsync(svc, cts.Token);
            if (poll && !HasRemoteRequests(svc)) return 0;

            using var self = Process.GetCurrentProcess();
            svc.Store.StartRun(svc.RunId, Environment.ProcessId, self.StartTime.ToUniversalTime(), trigger);
            sampler = Infrastructure.Stats.RunStatsSampler.Start(svc); // RAM/CPU kuzatuvi; tugaganda disk hajmlari bilan bazaga yoziladi
            if (dataRoot is null) beat = new Infrastructure.Heartbeat.MachineBeat.WorkerBeat(svc); // botga "worker band: vazifa #N, bosqich"
            svc.Log.Info($"Worker boshlandi ({trigger}), run {svc.RunId}. Bo'sh RAM {svc.Resources.AvailableRamGb():0.00}/{svc.Resources.TotalRamGb():0.0} GB.");
            var runner = new JobRunner(svc.Pipeline());

            await RefreshYouTubeAsync(svc, runner, cts.Token);
            Housekeeping(svc, runner);
            PublishCatalogs(svc);
            if (args.Contains("--refresh")) { await RecordStatsAsync(svc, sampler); svc.Store.FinishRun(svc.RunId, "refresh", null, null); return 0; }

            List<RunSummary> results;
            if (poll) results = await RunPublishNowAsync(svc, runner, cts.Token);
            else if (scheduled) results = await RunBatchAsync(svc, runner, cts.Token, respectWindow: trigger != "bot"); // jadval ish oynasiga bo'ysunadi, botdan "Hozir ishga tushirish" - yo'q
            else results = new List<RunSummary> { await runner.RunOnceAsync(svc.RunId, cts.Token) };

            var last = results.LastOrDefault(r => r.Outcome != RunOutcome.QueueEmpty) ?? results.LastOrDefault() ?? new RunSummary { Outcome = RunOutcome.QueueEmpty };
            var worked = results.Where(r => r.Outcome != RunOutcome.QueueEmpty).ToList();
            var outcome = worked.Count > 1 ? $"{worked.Count(r => r.Outcome == RunOutcome.Completed)}/{worked.Count} video tayyor" : last.Outcome.ToString();
            var message = worked.Count > 1
                ? string.Join("; ", worked.Select(r => $"#{r.JobId}{(r.PublishNow ? " (hozir)" : "")}: {r.Outcome}{(r.Message is null ? "" : " - " + r.Message)}"))
                : last.Message;
            await Infrastructure.Templates.JobTemplates.SyncAsync(svc, cts.Token); // tayyor videolar avtomatik shablon bo'ladi (nusxalanmaydi)
            await RecordStatsAsync(svc, sampler);
            svc.Store.FinishRun(svc.RunId, outcome, last.JobId, message);
            svc.Log.Info($"Worker tugadi: {outcome}, vazifalar {(worked.Count == 0 ? "-" : string.Join(", ", worked.Select(r => "#" + r.JobId)))}, " +
                         $"{results.Sum(r => r.Elapsed.TotalMinutes):0.0} daqiqa ish.");
            return results.Count == 0 ? 0 : results.Max(r => ExitCode(r.Outcome));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            svc.Log.Warn("Worker to'xtatildi (Windows/Task Scheduler jarayonni yakunladi).");
            await RecordStatsAsync(svc, sampler);
            try { svc.Store.FinishRun(svc.RunId, "stopped", null, "Jarayon tashqaridan to'xtatildi"); } catch { }
            return 1;
        }
        catch (Exception e)
        {
            svc.Log.Error("Kutilmagan xato: " + e);
            await RecordStatsAsync(svc, sampler);
            try { svc.Store.FinishRun(svc.RunId, "crash", null, e.Message); } catch { }
            return 4;
        }
        finally
        {
            try { beat?.Dispose(); } catch { }
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
                // Bazaning egasi ulanish satri: faqat eski (v1) ma'lumotlarni shu kompyuter hisobiga o'tkazish uchun. Ilovadagi
                // "Sozlash" uni ishlatib hisob yaratadi va keyin o'chiradi; worker o'zi bu satr bilan ishlamaydi.
                var (ok, msg) = Infrastructure.Data.PostgresJobStore.Check(pg);
                if (!ok) { svc.Log.Error("Online baza ulanish satri saqlanmadi: " + msg); return 1; }
                svc.Secrets.Set(SecretNames.PostgresConnection, pg.Trim());
                imported.Add("postgres_connection (eski ma'lumotlar ilovadagi sozlashda hisobga o'tkaziladi)");
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

    private static async Task LightYouTubeRefreshAsync(AppServices svc, CancellationToken ct)
    {
        try
        {
            if (!svc.IsOnline) return;
            JobRunner? runner = null;
            await RefreshYouTubeAsync(svc, () => runner ??= new JobRunner(svc.Pipeline()), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { svc.Log.Warn("YouTube holatini yengil yangilashda xato: " + e.Message); }
    }

    /// <summary>Resurs va hajm statistikasini bazaga yozadi (xato bo'lsa worker natijasiga ta'sir qilmaydi).</summary>
    private static async Task RecordStatsAsync(AppServices svc, Infrastructure.Stats.RunStatsSampler? sampler)
    {
        if (sampler is null) return;
        try
        {
            var json = await sampler.FinishAsync(svc);
            svc.Store.SaveRunStats(svc.RunId, json);
            svc.Log.Info("Resurs va hajm statistikasi yozildi.");
        }
        catch (Exception e) { svc.Log.Warn("Statistikani yozib bo'lmadi: " + e.Message); }
    }

    private static int ExitCode(RunOutcome o) => o switch
    {
        RunOutcome.QueueEmpty or RunOutcome.Completed or RunOutcome.StoppedBeforeUpload or RunOutcome.Cancelled => 0,
        RunOutcome.Failed => 1,
        _ => 2,
    };

    /// <summary>
    /// Jadval bo'yicha ishga tushish: avval "hozir chiqarish" so'rovlari, so'ng bazadan sinxronlab navbatdagi BARCHA mavzular
    /// (<see cref="BatchPlanner"/>): chiqish vaqti uzoq bo'lsa ham video oldindan tayyorlanadi va YouTube'ga rejalashtirilgan holda yuklanadi.
    /// Navbat har videodan keyin qayta o'qiladi (ish paytida qo'shilgan mavzular ham kiradi). Ish oynasi tugasa yangi video boshlanmaydi.
    /// Har video tayyor bo'lgach 15 daqiqa tanaffus (kompyuter sovib oladi); tanaffusda kelgan "hozir chiqarish" so'rovlari darhol bajariladi.
    /// Vaqtinchalik xato (tarmoq, limit) bo'lsa, qolgan videolar keyingi jadvalga qoladi.
    /// </summary>
    private static async Task<List<RunSummary>> RunBatchAsync(AppServices svc, JobRunner runner, CancellationToken ct, bool respectWindow = false)
    {
        var results = await RunPublishNowAsync(svc, runner, ct);
        if (!svc.HasGeminiKey()) return results; // mavzular muzlatilgan (RunPublishNowAsync logga yozgan)
        var pending = svc.Store.CountPending(DateTimeOffset.UtcNow);
        svc.Log.Info($"Bazadan sinxronlandi ({svc.Store.Kind}): navbatda {pending} ta mavzu — {BatchPlanner.Describe(pending)}.");
        int made = 0;
        var sched = svc.Settings.Load().Schedule;
        while (made < BatchPlanner.MaxVideosPerRun)
        {
            // Ish oynasi (masalan 01:00-04:00) tugagan bo'lsa yangi video boshlanmaydi; boshlangan ish esa oxirigacha bajariladi (to'xtatilmaydi).
            if (respectWindow && !BatchPlanner.InWorkWindow(sched, DateTimeOffset.UtcNow))
            {
                var left = svc.Store.CountPending(DateTimeOffset.UtcNow);
                svc.Log.Info($"Ish oynasi ({sched.StartTime}-{sched.EndTime}) tugadi - yangi video boshlanmaydi{(left > 0 ? $", qolgan {left} ta mavzu keyingi kechaga" : "")}.");
                break;
            }
            var r = await runner.RunOnceAsync(svc.RunId, ct);
            results.Add(r);
            if (r.Outcome == RunOutcome.QueueEmpty) { svc.Log.Info("Navbat bo'shadi — ishga tushish yakunlandi."); break; }
            if (!r.PublishNow) made++;
            var remaining = svc.Store.CountPending(DateTimeOffset.UtcNow);
            svc.Log.Info($"{(r.PublishNow ? "Hozir chiqarish" : $"Video {made}")}: #{r.JobId} {r.Outcome} ({r.Elapsed.TotalMinutes:0.0} daqiqa), navbatda yana {remaining} ta.");
            if (r.Outcome == RunOutcome.Failed)
            {
                svc.Log.Warn("Vaqtinchalik xato — qolgan videolar keyingi jadvalda tayyorlanadi.");
                break;
            }
            if (remaining == 0) { svc.Log.Info("Navbatdagi barcha mavzular tayyorlandi."); break; }
            var pause = BatchPlanner.PauseFor(remaining);
            svc.Log.Info($"{pause.TotalMinutes:0} daqiqa tanaffus (kompyuter sovib oladi), so'ng keyingi video.");
            results.AddRange(await PauseAsync(svc, runner, pause, ct));
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
                if (HasRemoteRequests(svc)) results.AddRange(await RunPublishNowAsync(svc, runner, ct));
            }
            catch (Exception e) when (e is not OperationCanceledException) { svc.Log.Warn("Tanaffusda 'hozir chiqarish' tekshiruvi o'tmadi: " + e.Message); }
            var left = until - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return results;
            await Task.Delay(left < TimeSpan.FromMinutes(1) ? left : TimeSpan.FromMinutes(1), ct);
        }
    }

    /// <summary>
    /// Masofaviy (bot) so'rovlar bormi: "hozir chiqarish" (faqat asosiy hisob) yoki shu kompyuterdagi tayyor videoni "YouTube'ga yuklash"
    /// (asosiy va mehmon hisoblari).
    /// </summary>
    private static bool HasRemoteRequests(AppServices svc)
    {
        if (svc.Store.CountPublishNow(DateTimeOffset.UtcNow) > 0 || JobRunner.LocalUploadRequests(svc.Store).Count > 0) return true;
        try
        {
            foreach (var (account, _) in AccountScopes(svc).Skip(1))
                using (EnterScope(svc, account))
                    if (JobRunner.LocalUploadRequests(svc.Store).Count > 0) return true;
        }
        catch (Exception e) when (e is not OperationCanceledException) { svc.Log.Warn("Mehmon hisoblari so'rovlari o'qilmadi: " + e.Message); }
        return false;
    }

    /// <summary>
    /// Masofaviy so'rovlar: avval "tayyor videoni YouTube'ga yuklash" (tez, qayta tayyorlamaydi), so'ng barcha "hozir chiqarish"
    /// so'rovlari ketma-ket (eng ko'pi 10 ta, vaqtinchalik xatoda to'xtaydi).
    /// </summary>
    private static async Task<List<RunSummary>> RunPublishNowAsync(AppServices svc, JobRunner runner, CancellationToken ct)
    {
        var list = await runner.RunUploadRequestsAsync(svc.RunId, ct); // tayyor videoni yuklash Gemini'siz ham ishlaydi
        // Mehmon hisoblarining "tayyor videoni YouTube'ga yuklash" so'rovlari (o'z kanaliga, o'z chiqish vaqtiga). "Hozir chiqarish"
        // mehmonlarga berilmaydi - quyidagi tsikl faqat asosiy hisobniki.
        try
        {
            foreach (var (account, _) in AccountScopes(svc).Skip(1))
                using (EnterScope(svc, account))
                    list.AddRange(await runner.RunUploadRequestsAsync(svc.RunId, ct));
        }
        catch (Exception e) when (e is not OperationCanceledException) { svc.Log.Warn("Mehmon hisoblari yuklash so'rovlari bajarilmadi: " + e.Message); }
        if (!svc.HasGeminiKey())
        {
            svc.Log.Warn("Gemini kaliti yo'q - mavzular muzlatilgan: yangi video tayyorlanmaydi. Botda Sozlamalar → Gemini'ga kalit qo'shing.");
            return list;
        }
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

    /// <summary>
    /// Hisoblar: shu kompyuterning o'z hisobi (null) va u mezbonlik qiladigan mehmon (qo'shimcha token) hisoblari.
    /// Mehmonlar ro'yxatini o'qib bo'lmasa xato chiqadi (chaqiruvchi tozalash kabi xavfli ishni o'tkazib yuboradi).
    /// </summary>
    private static List<(Guid? Id, string Label)> AccountScopes(AppServices svc)
    {
        var list = new List<(Guid?, string)> { (null, "") };
        if (svc.Store is Infrastructure.Data.PostgresJobStore pg)
            foreach (var (id, label) in pg.HostedAccounts()) list.Add((id, label));
        return list;
    }

    /// <summary>Oqimni mehmon hisobiga o'tkazadi (null - o'z hisobi, hech narsa qilinmaydi).</summary>
    private static IDisposable? EnterScope(AppServices svc, Guid? account) =>
        account is null ? null : (svc.Store as Infrastructure.Data.PostgresJobStore)?.UseScope(account);

    private static Task RefreshYouTubeAsync(AppServices svc, JobRunner runner, CancellationToken ct) => RefreshYouTubeAsync(svc, () => runner, ct);

    /// <summary>
    /// Processing/Scheduled videolar holatini yengil API so'rovi bilan yangilaydi (og'ir dasturlar ochilmaydi). Har hisob o'z
    /// kanali bilan: mehmonlarning videolari ularning YouTube kanalidan tekshiriladi.
    /// </summary>
    private static async Task RefreshYouTubeAsync(AppServices svc, Func<JobRunner> runner, CancellationToken ct)
    {
        List<(Guid? Id, string Label)> scopes;
        try { scopes = AccountScopes(svc); }
        catch (Exception e) when (e is not OperationCanceledException) { svc.Log.Warn("Mehmon hisoblari o'qilmadi: " + e.Message); scopes = new() { (null, "") }; }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        foreach (var (account, _) in scopes)
        {
            using (EnterScope(svc, account))
            {
                try
                {
                    if (!svc.Uploader.IsConnected) continue;
                    var jobs = svc.Store.ListByStatus(JobStatus.Processing, JobStatus.Scheduled).Take(25).ToList();
                    foreach (var j in jobs)
                    {
                        try { await runner().RefreshYouTubeStatusAsync(j, new JobLog(j.Id, svc.Paths.RecordDirFor(j.Id), svc.Store, svc.Log), budget.Token); runner().PurgeFinishedJob(j); }
                        catch (Exception e) { svc.Log.Warn($"#{j.Id} YouTube holati yangilanmadi: {e.Message}"); }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { svc.Log.Warn("YouTube holati yangilanmadi: " + e.Message); }
            }
        }
    }

    /// <summary>Bot katalogi (ovozlar, shablonlar, standartlar) har hisob uchun: mehmonlar ham botda shu kompyuter ovozlari/shablonlarini tanlaydi.</summary>
    private static void PublishCatalogs(AppServices svc)
    {
        try { svc.Settings.PublishCatalog(); } catch (Exception e) { svc.Log.Warn("Bot katalogi yuborilmadi: " + e.Message); }
        try
        {
            foreach (var (account, _) in AccountScopes(svc).Skip(1))
                using (EnterScope(svc, account))
                    try { svc.Settings.PublishCatalog(); } catch (Exception e) { svc.Log.Warn($"Mehmon hisobi katalogi yuborilmadi: {e.Message}"); }
        }
        catch (Exception e) { svc.Log.Warn("Mehmon hisoblari o'qilmadi: " + e.Message); }
    }

    /// <summary>
    /// Uy tozalash (og'ir dasturlarsiz): tugagan vazifalarda faqat video + audio qoldiriladi; bazadan o'chirilgan
    /// (masalan Telegram bot orqali) mavzularning jobs\job-N va records\job-N papkalari olib tashlanadi.
    /// </summary>
    private static void Housekeeping(AppServices svc, JobRunner runner)
    {
        try
        {
            // Mehmon hisoblari vazifalari ham shu kompyuterda: ularning papkalari "o'chirilgan mavzu" deb tozalanmasligi uchun
            // barcha hisoblar ro'yxati olinadi (o'qib bo'lmasa xato - hech narsa o'chirilmaydi).
            var ids = new HashSet<long>();
            foreach (var (account, _) in AccountScopes(svc))
                using (EnterScope(svc, account))
                {
                    foreach (var j in svc.Store.ListByStatus(JobStatus.Scheduled, JobStatus.Published))
                        try { runner.PurgeFinishedJob(j); } catch (Exception e) { svc.Log.Warn($"#{j.Id} tozalanmadi: {e.Message}"); }
                    ids.UnionWith(svc.Store.ListByStatus(Enum.GetValues<JobStatus>()).Select(j => j.Id));
                }
            var workRoot = svc.Settings.Load().Paths.WorkDir;
            foreach (var root in new[] { workRoot, svc.Paths.RecordsDir })
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root, "job-*"))
                {
                    if (!long.TryParse(Path.GetFileName(dir)["job-".Length..], out var id) || ids.Contains(id)) continue;
                    try { Core.Files.PathGuard.SafeDeleteDirectory(root, dir); svc.Log.Info($"O'chirilgan mavzu #{id} papkasi tozalandi: {dir}"); }
                    catch (Exception e) { svc.Log.Warn($"{dir} tozalanmadi: {e.Message}"); }
                }
            }
        }
        catch (Exception e) { svc.Log.Warn("Uy tozalashda xato: " + e.Message); }
    }
}

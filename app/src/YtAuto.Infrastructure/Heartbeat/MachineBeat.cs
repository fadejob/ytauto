using System.Diagnostics;
using System.Text.Json;

namespace YtAuto.Infrastructure.Heartbeat;

/// <summary>
/// Kompyuter "yuragi urishi": ilova, worker va Task Scheduler'dagi yengil poll vazifasi online bazaga kim/qachon/nima qilayotganini yozadi.
/// Bot shu yozuvlar bo'yicha kompyuter online-offline ekanini, ilova ochiq-yopiqligini va worker faoliyatini ko'rsatadi.
/// Online bazasiz (lokal rejim) hech narsa qilmaydi.
/// </summary>
public static class MachineBeat
{
    public static string Version => typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "";

    public static void Beat(AppServices svc, bool? appOpen = null, string? workerState = null, long? jobId = null, string? detail = null)
    {
        if (!svc.IsOnline) return;
        try { svc.Store.Heartbeat(Environment.MachineName, appOpen, workerState, jobId, detail, Version); }
        catch (Exception e) { svc.Log.Debug("Heartbeat yozilmadi: " + e.Message); }
    }

    private static string StatusText(int s) => s switch
    {
        0 => "navbatda", 1 => "ssenariy yozilmoqda", 2 => "ovoz yaratilmoqda", 3 => "subtitr vaqtlari hisoblanmoqda", 4 => "montaj (Claude)",
        5 => "render", 6 => "sifat tekshiruvi", 7 => "arxivlanmoqda", 8 => "YouTube'ga yuklanmoqda", 9 => "YouTube qayta ishlamoqda",
        _ => "ishlanmoqda",
    };

    /// <summary>
    /// Og'ir ish (video tayyorlash) davomida kompyuter o'z ishi bilan band: holat har necha soniyada YUBORILMAYDI.
    /// Faqat boshida bir marta "band" va tugaganda bir marta "bo'sh" deb yoziladi (bot shu orasida "ish jarayonida" ko'rsatadi).
    /// </summary>
    public sealed class WorkerBeat : IDisposable
    {
        private readonly AppServices _svc;

        public WorkerBeat(AppServices svc)
        {
            _svc = svc;
            Beat(svc, workerState: "busy", jobId: null, detail: "Og'ir ish boshlandi (video tayyorlanmoqda). Tugagach holat yangilanadi.");
        }

        public void Dispose() => Beat(_svc, workerState: "idle", jobId: null, detail: "bo'sh (ish tugadi)");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// Ilova ochiq bo'lganda: botdan "Hozir ishga tushirish" kelgan bo'lsa worker'ni alohida jarayon sifatida (--batch-now) ishga tushiradi.
    /// Worker allaqachon ishlayotgan bo'lsa yangi nusxa darhol chiqadi (mutex), ish ikki marta bajarilmaydi.
    /// </summary>
    public static void StartBatchIfRequested(AppServices svc, string? workerExe)
    {
        if (!svc.IsOnline || workerExe is null || !File.Exists(workerExe)) return;
        bool requested;
        try { requested = svc.Store.TakeRunRequest(Environment.MachineName); } catch { return; }
        if (!requested) return;
        try
        {
            Process.Start(new ProcessStartInfo(workerExe, "--batch-now") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(workerExe)! });
            svc.Log.Info("Botdan 'Hozir ishga tushirish' so'rovi keldi - worker ishga tushirildi.");
        }
        catch (Exception e) { svc.Log.Warn("Workerni ishga tushirib bo'lmadi: " + e.Message); }
    }

    /// <summary>Bot "integratsiyalarni tekshirish" so'rovi yuborgan bo'lsa shu kompyuterda tekshiruvni bajaradi va natijani bazaga yozadi.</summary>
    public static async Task RunCheckIfRequestedAsync(AppServices svc, string? workerExe, CancellationToken ct)
    {
        if (!svc.IsOnline) return;
        bool requested;
        try { requested = svc.Store.TakeCheckRequest(Environment.MachineName); } catch { return; }
        if (!requested) return;
        svc.Log.Info("Botdan integratsiyalarni tekshirish so'rovi keldi - tekshirilmoqda.");
        var sw = Stopwatch.StartNew();
        string json;
        try
        {
            var results = await new IntegrationChecker(svc).RunAsync(false, workerExe, ct);
            json = JsonSerializer.Serialize(new
            {
                machine = Environment.MachineName, at = DateTimeOffset.UtcNow, durationSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
                results = results.Select(r => new { name = r.Name, level = r.Level.ToString(), message = r.Message }),
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception e)
        {
            json = JsonSerializer.Serialize(new
            {
                machine = Environment.MachineName, at = DateTimeOffset.UtcNow, durationSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
                results = new[] { new { name = "Tekshiruv", level = "Failed", message = "Tekshiruv bajarilmadi: " + e.Message } },
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        try { svc.Store.SaveCheckResult(Environment.MachineName, json); svc.Log.Info("Tekshiruv natijasi bazaga yozildi."); }
        catch (Exception e) { svc.Log.Warn("Tekshiruv natijasini yozib bo'lmadi: " + e.Message); }
    }
}

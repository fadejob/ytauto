using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Infrastructure.Voice;

/// <summary>
/// MOSS-TTS-Nano VoiceStudio'da "sidecar" dvigatel: o'z Python muhiti (torch 2.7, ~8 GB) bilan alohida papkaga o'rnatiladi
/// (VoiceStudio 0.5.7: POST /engines/sidecar/moss-tts-nano/install). Papka: OMNIVOICE_ENGINES_DIR (C: kichik bo'lsa D: ga
/// yo'naltiriladi) yoki %APPDATA%\OmniVoice\engines. O'rnatish tugagach VoiceStudio checkout ichiga belgi fayl yozadi.
/// </summary>
public static class MossEngine
{
    private const string EngineId = TtsVoices.Model;
    private const string CompleteMarker = ".voicestudio_install_complete";
    private static readonly TimeSpan PollGiveUp = TimeSpan.FromMinutes(3);

    public static string EnginesDir()
    {
        var custom = Environment.GetEnvironmentVariable("OMNIVOICE_ENGINES_DIR")
                     ?? Environment.GetEnvironmentVariable("OMNIVOICE_ENGINES_DIR", EnvironmentVariableTarget.User);
        return !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniVoice", "engines");
    }

    public static string CheckoutDir() => Path.Combine(EnginesDir(), EngineId, "MOSS-TTS-Nano");

    /// <summary>Fayllar bo'yicha tekshiruv (VoiceStudio ochilmaydi).</summary>
    public static bool IsInstalled()
    {
        var dir = CheckoutDir();
        return File.Exists(Path.Combine(dir, CompleteMarker)) && File.Exists(Path.Combine(dir, ".venv", "Scripts", "python.exe"));
    }

    /// <summary>
    /// VoiceStudio orqali o'rnatadi va tugashini kutadi (yuklash ~8 GB, internetga qarab o'nlab daqiqa).
    /// Disk yetmasa yoki boshqa xato bo'lsa VoiceStudio xabari bilan istisno tashlanadi.
    /// </summary>
    public static async Task InstallAsync(VoiceStudioSettings settings, SqliteJobStore store, IJobLog log, IProgress<string>? progress, CancellationToken ct)
    {
        if (IsInstalled()) return;
        await using var session = (VoiceStudioSession)await new VoiceStudioTtsService(store, "moss-install").OpenAsync(settings, log, ct).ConfigureAwait(false);
        var (code, body) = await session.PostAsync($"engines/sidecar/{EngineId}/install", TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (code >= 400) throw new InvalidOperationException($"VoiceStudio {TtsVoices.DisplayName} o'rnatishni boshlamadi ({code}): {Detail(body)}");
        // Holat so'rovidagi bitta tarmoq xatosi o'rnatishni to'xtatmasin: xato bilan chiqsak session VoiceStudio'ni yopadi va
        // ~8 GB yuklash yarim yo'lda o'ladi. Faqat uzoq vaqt (PollGiveUp) javob bo'lmasa voz kechamiz.
        var lastOk = DateTime.UtcNow;
        Exception? lastError = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            JsonNode? st;
            try
            {
                var json = await session.GetStringAsync($"engines/sidecar/{EngineId}/install/status", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)
                           ?? throw new HttpRequestException("VoiceStudio o'rnatish holatini qaytarmadi.");
                st = JsonNode.Parse(json);
                lastOk = DateTime.UtcNow; lastError = null;
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                if (lastError is null) log.Warn($"{TtsVoices.DisplayName}: o'rnatish holati o'qilmadi, qayta urinilmoqda: {e.Message}", "Voice");
                lastError = e;
                if (DateTime.UtcNow - lastOk > PollGiveUp)
                    throw new InvalidOperationException($"VoiceStudio {PollGiveUp.TotalMinutes:0} daqiqa javob bermadi ({TtsVoices.DisplayName} o'rnatilishi to'xtadi): {e.Message}", e);
                continue;
            }
            if (st?["installed"]?.GetValue<bool>() == true) { progress?.Report("O'rnatildi"); return; }
            var job = st?["job"];
            var state = job?["state"]?.GetValue<string>() ?? "";
            var running = (job?["steps"] as JsonArray)?.FirstOrDefault(x => x?["state"]?.GetValue<string>() == "running")?["id"]?.GetValue<string>();
            if (running is not null) progress?.Report($"{TtsVoices.DisplayName}: {running}…");
            if (state is "failed" or "error" or "cancelled")
                throw new InvalidOperationException($"{TtsVoices.DisplayName} o'rnatilmadi: {job?["error"]} {job?["remediation"]}".Trim());
            if (state == "done" || state == "succeeded") return;
        }
    }

    private static string Detail(string body)
    {
        try { return JsonNode.Parse(body)?["detail"]?.ToString() ?? body; } catch { return body; }
    }
}

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

    // ---------------------------------------------------------------- VoiceStudio sidecar mosligi
    // VoiceStudio 0.5.8 MOSS sidecar'i (backend\engines\moss_tts_nano_subprocess\main.py) transformers 5 uchun
    // _keys_to_ignore_on_load_unexpected ro'yxatini har doim set'ga aylantiradi. MOSS venv'ida esa transformers==4.57.1
    // (MOSS-TTS-Nano requirements.txt), u yerda bu maydon ro'yxat bilan qo'shiladi: set + list → TypeError va har sintez
    // "unsupported operand type(s) for +: 'set' and 'list'" (HTTP 500) bilan tushadi. Tuzatish: set'ga aylantirish faqat
    // transformers >= 5 da. VoiceStudio har ishga tushganda resources\backend'ni runtime\project\backend'ga ko'chiradi,
    // shuning uchun ikkala nusxa ham tuzatiladi (VoiceStudio o'zi tuzatsa - qidirilgan satr topilmaydi, hech narsa qilinmaydi).
    internal const string SidecarPatchMarker = "YtAuto: transformers 4";
    private const string SidecarAnchor = "original = getattr(PreTrainedModel, \"_adjust_missing_and_unexpected_keys\", None)";

    /// <summary>Tuzatilishi kerak bo'lgan sidecar main.py nusxalari (o'rnatish papkasidagi manba va ishlab turgan runtime nusxasi).</summary>
    public static IEnumerable<string> SidecarMainFiles(VoiceStudioSettings s)
    {
        const string rel = @"backend\engines\moss_tts_nano_subprocess\main.py";
        var exeDir = string.IsNullOrWhiteSpace(s.ExePath) ? null : Path.GetDirectoryName(s.ExePath);
        if (!string.IsNullOrEmpty(exeDir)) yield return Path.Combine(exeDir, "resources", rel);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceStudio", "runtime", "project", rel);
    }

    /// <summary>Matnga tuzatishni qo'llaydi. O'zgarmasa (allaqachon tuzatilgan yoki boshqa versiya) null qaytaradi.</summary>
    public static string? PatchSidecarSource(string src)
    {
        if (src.Contains(SidecarPatchMarker, StringComparison.Ordinal)) return null;
        var at = src.IndexOf(SidecarAnchor, StringComparison.Ordinal);
        if (at < 0) return null;
        // Bu blok faqat ro'yxatni set'ga aylantiruvchi o'ram ichida bo'lishi kerak (boshqa kodga tegmaymiz).
        var fn = src.LastIndexOf("def _accept_list_ignore_patterns", at, StringComparison.Ordinal);
        if (fn < 0 || src.IndexOf("set(patterns)", at, StringComparison.Ordinal) < 0) return null;
        var lineStart = src.LastIndexOf('\n', at) + 1;
        var indent = src[lineStart..at];
        if (indent.Trim().Length > 0) return null;
        var nl = src.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insert =
            $"{indent}# {SidecarPatchMarker}.x ro'yxatlarni qo'shadi (set + list = TypeError): o'ram faqat transformers 5 uchun.{nl}" +
            $"{indent}import transformers as _ytauto_tf  # noqa: PLC0415{nl}" +
            $"{indent}if int(str(getattr(_ytauto_tf, \"__version__\", \"0\")).split(\".\")[0] or 0) < 5:{nl}" +
            $"{indent}    return{nl}";
        return src[..lineStart] + insert + src[lineStart..];
    }

    /// <summary>
    /// Sidecar fayllarini tuzatadi. Ishlab turgan runtime nusxasi o'zgargan bo'lsa true (yuklangan sidecar eski kodni
    /// xotirada saqlaydi - chaqiruvchi uni VoiceStudio'da qayta yuklashi kerak). Xatolar logga yoziladi, sintezni to'xtatmaydi.
    /// </summary>
    public static bool EnsureSidecarCompat(VoiceStudioSettings s, IJobLog log)
    {
        var runtimeChanged = false;
        foreach (var file in SidecarMainFiles(s))
        {
            try
            {
                if (!File.Exists(file)) continue;
                var src = File.ReadAllText(file);
                var patched = PatchSidecarSource(src);
                if (patched is null) continue;
                File.WriteAllText(file, patched, new System.Text.UTF8Encoding(false));
                var pycache = Path.Combine(Path.GetDirectoryName(file)!, "__pycache__");
                foreach (var pyc in Directory.Exists(pycache) ? Directory.GetFiles(pycache, "main.*.pyc") : Array.Empty<string>())
                    try { File.Delete(pyc); } catch { }
                log.Info($"{TtsVoices.DisplayName}: VoiceStudio sidecar transformers 4 bilan mos qilindi ({file}).", "Voice");
                if (file.Contains(Path.Combine("runtime", "project"), StringComparison.OrdinalIgnoreCase)) runtimeChanged = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn($"{TtsVoices.DisplayName}: VoiceStudio sidecar fayli tuzatilmadi ({file}): {e.Message}", "Voice");
            }
        }
        return runtimeChanged;
    }

    private static string Detail(string body)
    {
        try { return JsonNode.Parse(body)?["detail"]?.ToString() ?? body; } catch { return body; }
    }
}

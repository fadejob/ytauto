using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Infrastructure.Voice;

/// <summary>
/// VoiceStudio lokal API (0.5.7 da tasdiqlangan: GET /health, GET /models, POST /v1/audio/speech → audio/wav).
/// Ishlab turgan bo'lsa - foydalanuvchiniki deb hisoblanadi va yopilmaydi. Aks holda worker o'zi ishga tushiradi
/// (Job Object ichida) va ish tugagach faqat o'zi ochgan jarayonlar daraxtini yopadi.
/// </summary>
public sealed class VoiceStudioTtsService : ITtsService
{
    private readonly IJobStore? _store;
    private readonly string _runId;
    public VoiceStudioTtsService(IJobStore? store = null, string runId = "") { _store = store; _runId = runId; }

    public async Task<ITtsSession> OpenAsync(VoiceStudioSettings s, IJobLog log, CancellationToken ct)
    {
        // VoiceStudio (uvicorn) bo'sh keep-alive ulanishni 5 soniyada yopadi. .NET standarti (1 daqiqa) bilan yopilayotgan
        // ulanish qayta ishlatilib "An error occurred while sending the request" chiqardi (MOSS o'rnatishi 5 s'lik so'rovda uzilardi).
        var handler = new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2) };
        var http = new HttpClient(handler) { BaseAddress = new Uri(s.BaseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        var session = new VoiceStudioSession(s, http, log, _store, _runId);
        try
        {
            await session.StartOrAttachAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class VoiceStudioSession : ITtsSession
{
    private readonly VoiceStudioSettings _s;
    private readonly HttpClient _http;
    private readonly IJobLog _log;
    private readonly IJobStore? _store;
    private readonly string _runId;
    private JobObject? _job;
    private OwnedProcess? _proc;
    private HashSet<(int, DateTime)> _preExisting = new();
    private DateTime _ourStartUtc;
    public bool StartedByUs { get; private set; }
    public string Version { get; private set; } = "unknown";
    public Dictionary<string, double> Metrics { get; } = new();

    public VoiceStudioSession(VoiceStudioSettings s, HttpClient http, IJobLog log, IJobStore? store, string runId)
    { _s = s; _http = http; _log = log; _store = store; _runId = runId; }

    private string[] OwnedDirs => new[]
    {
        Path.GetDirectoryName(_s.ExePath) ?? "",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceStudio"),
    };

    private async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var r = await _http.GetAsync("health", cts.Token).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return false;
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            Version = j?["version"]?.GetValue<string>() ?? Version;
            return (j?["status"]?.GetValue<string>() ?? "") == "ok";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async Task StartOrAttachAsync(CancellationToken ct)
    {
        if (await HealthyAsync(ct).ConfigureAwait(false))
        {
            _log.Info($"VoiceStudio allaqachon ishlayapti (v{Version}) - foydalanuvchiniki, worker uni yopmaydi.", "Voice");
            return;
        }
        var existing = ProcessSnapshot.Under(OwnedDirs);
        if (existing.Count > 0)
        {
            // Foydalanuvchi ochgan, lekin API hali tayyor emas: o'zimiz ikkinchi nusxa ochmaymiz, faqat kutamiz.
            _log.Warn($"VoiceStudio jarayonlari bor ({existing.Count} ta), lekin API javob bermayapti. Tayyor bo'lishi kutiladi; ular yopilmaydi.", "Voice");
            if (await WaitHealthyAsync(TimeSpan.FromSeconds(_s.StartupTimeoutSec), ct).ConfigureAwait(false)) return;
            throw PipelineException.Transient("voicestudio_unresponsive", "Ochiq VoiceStudio API'si javob bermadi. Worker foydalanuvchi ochgan dasturni yopmaydi.");
        }
        await StartOursAsync(ct).ConfigureAwait(false);
    }

    private async Task StartOursAsync(CancellationToken ct)
    {
        if (!File.Exists(_s.ExePath))
            throw PipelineException.Config("voicestudio_missing", $"VoiceStudio topilmadi: {_s.ExePath}");
        _preExisting = ProcessSnapshot.Under(OwnedDirs).Select(p => (p.Pid, p.StartUtc)).ToHashSet();
        _job?.Dispose();
        _job = new JobObject("voicestudio");
        var logDir = Path.Combine(Path.GetTempPath(), "YtAuto");
        var sw = Stopwatch.StartNew();
        _ourStartUtc = DateTime.UtcNow;
        _proc = ProcessLauncher.Start(new LaunchSpec
        {
            Exe = _s.ExePath, WorkingDirectory = Path.GetDirectoryName(_s.ExePath), Tool = "voicestudio",
            HideWindow = false, StartMinimized = true,
            StdoutPath = Path.Combine(logDir, "voicestudio.stdout.txt"), StderrPath = Path.Combine(logDir, "voicestudio.stderr.txt"),
        }, _job);
        StartedByUs = true;
        _log.Info($"VoiceStudio worker tomonidan ishga tushirildi (pid {_proc.Pid}); API tayyorligi kutilmoqda (≤{_s.StartupTimeoutSec}s).", "Voice");
        _store?.RecordProcess(new OwnedProcessRecord { RunId = _runId, Stage = "Voice", Tool = "voicestudio", Pid = _proc.Pid, ProcessStartUtc = _proc.StartTimeUtc, ExePath = _s.ExePath });
        if (!await WaitHealthyAsync(TimeSpan.FromSeconds(_s.StartupTimeoutSec), ct).ConfigureAwait(false))
        {
            await StopOursAsync("startup timeout").ConfigureAwait(false);
            throw PipelineException.Transient("voicestudio_start_timeout", $"VoiceStudio API {_s.StartupTimeoutSec}s ichida tayyor bo'lmadi.");
        }
        Metrics["startup_sec"] = sw.Elapsed.TotalSeconds;
        _log.Info($"VoiceStudio tayyor: v{Version}, {sw.Elapsed.TotalSeconds:0}s.", "Voice");
    }

    private async Task<bool> WaitHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await HealthyAsync(ct).ConfigureAwait(false)) return true;
            if (_proc is not null && _proc.HasExited && _job!.ProcessIds().Length == 0)
            {
                _log.Warn($"VoiceStudio jarayoni ishga tushish paytida yopildi (exit {_proc.ExitCode}).", "Voice");
                return false;
            }
            await Task.Delay(3000, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Katalog o'qish uchun oddiy GET (JSON matn).</summary>
    internal async Task<string?> GetStringAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        try
        {
            using var r = await _http.GetAsync(path, cts.Token).ConfigureAwait(false);
            return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"VoiceStudio /{path} {timeout.TotalSeconds:0} soniyada javob bermadi.");
        }
    }

    /// <summary>Bo'sh tanali POST (masalan, dvigatel o'rnatishni boshlash). Javob matni va HTTP kodi qaytadi.</summary>
    internal async Task<(int Status, string Body)> PostAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        using var r = await _http.PostAsync(path, new StringContent("", Encoding.UTF8, "application/json"), cts.Token).ConfigureAwait(false);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- klon ovoz profillari («Mening ovozim»)
    // VoiceStudio 0.5.7: GET /profiles, POST /profiles (multipart: name, kind=clone, ref_audio), PUT /profiles/{id}/audio, DELETE /profiles/{id}.
    // /v1/audio/speech "voice" profil nomi bilan topadi va uning namunasini MOSS'ga prompt audio sifatida beradi.

    /// <summary>Profil id'si (nomi bo'yicha, katta-kichik harfsiz). Yo'q bo'lsa null.</summary>
    internal async Task<string?> FindProfileIdAsync(string name, CancellationToken ct)
    {
        var json = await GetStringAsync("profiles", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)
                   ?? throw PipelineException.Transient("voice_profiles", "VoiceStudio ovoz profillari ro'yxatini bermadi.");
        var root = JsonNode.Parse(json);
        var arr = root as JsonArray ?? root?["profiles"] as JsonArray ?? root?["items"] as JsonArray ?? new JsonArray();
        var hit = arr.FirstOrDefault(p => string.Equals(p?["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
        return hit?["id"]?.ToString();
    }

    public async Task EnsureCloneProfileAsync(string name, string refWav, string refText, string version, CancellationToken ct)
    {
        var marker = VoiceCloneCache.AppliedMarker(refWav);
        var id = await FindProfileIdAsync(name, ct).ConfigureAwait(false);
        if (id is not null && File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return; // shu namuna allaqachon qo'shilgan

        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(await File.ReadAllBytesAsync(refWav, ct).ConfigureAwait(false));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "ref_audio", "sample.wav");
        // Namunada aytilgan matn: model ovozni aniqroq ko'chiradi. Inglizcha matn - til ham aniq.
        if (!string.IsNullOrWhiteSpace(refText)) { form.Add(new StringContent(refText.Trim()), "ref_text"); form.Add(new StringContent("English"), "language"); }
        if (id is null) { form.Add(new StringContent(name), "name"); form.Add(new StringContent("clone"), "kind"); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromMinutes(2));
        HttpResponseMessage r;
        try { r = id is null ? await _http.PostAsync("profiles", form, cts.Token).ConfigureAwait(false) : await _http.PutAsync($"profiles/{Uri.EscapeDataString(id)}/audio", form, cts.Token).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw PipelineException.Transient("voice_clone_network", "VoiceStudio bilan aloqa uzildi: " + e.Message, e); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PipelineException.Transient("voice_clone_timeout", "VoiceStudio ovoz profilini 2 daqiqada saqlamadi."); }
        using (r)
        {
            if (!r.IsSuccessStatusCode)
            {
                var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.Length > 400) body = body[..400];
                var code = (int)r.StatusCode;
                if (code == 409) throw PipelineException.Config("voice_clone_conflict", $"VoiceStudio'da \"{name}\" nomli ikki profil bor - ortiqchasini VoiceStudio'da o'chiring.");
                if (code is >= 400 and < 500) throw PipelineException.Config("voice_clone_rejected", $"VoiceStudio ovoz namunasini qabul qilmadi ({code}): {body}");
                throw PipelineException.Transient("voice_clone_http", $"VoiceStudio HTTP {code}: {body}");
            }
        }
        File.WriteAllText(marker, version);
        _log.Info($"VoiceStudio: \"{name}\" ovoz profili {(id is null ? "yaratildi" : "yangi namuna bilan yangilandi")}.", "Voice");
    }

    public async Task DeleteCloneProfileAsync(string name, CancellationToken ct)
    {
        var id = await FindProfileIdAsync(name, ct).ConfigureAwait(false);
        if (id is null) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var r = await _http.DeleteAsync($"profiles/{Uri.EscapeDataString(id)}", cts.Token).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode && (int)r.StatusCode != 404)
            throw new InvalidOperationException($"VoiceStudio \"{name}\" profilini o'chirmadi (HTTP {(int)r.StatusCode}).");
        _log.Info($"VoiceStudio: \"{name}\" ovoz profili o'chirildi (foydalanuvchi namunani o'chirgan).", "Voice");
    }

    public async Task EnsureModelAvailableAsync(string model, CancellationToken ct)
    {
        // MOSS-TTS-Nano - sidecar dvigatel: o'z venv'i (MossEngine) o'rnatilgan bo'lsa, og'irliklarni (OpenMOSS-Team/MOSS-TTS-Nano va
        // MOSS-Audio-Tokenizer-Nano, ~310 MB) birinchi sintezda o'zi HF keshiga yuklaydi. VoiceStudio /models katalogi esa uni yangi
        // "MOSS-TTS-Nano-100M" nomi bilan tekshiradi - sidecar eski nom bilan keshlagani uchun u yerda doim installed=false bo'ladi.
        // Shuning uchun MOSS uchun tekshiruv - ilova holati bilan bir xil: dvigatel o'rnatilganmi.
        if (string.Equals(model, TtsVoices.Model, StringComparison.OrdinalIgnoreCase))
        {
            if (!MossEngine.IsInstalled())
                throw PipelineException.Config("tts_model_missing", $"{TtsVoices.DisplayName} dvigateli bu kompyuterda o'rnatilmagan. YtAuto ilovasida «Kerakli fayllarni yuklash va sozlash» ni bosing.");
            _log.Info($"TTS: {TtsVoices.DisplayName} dvigateli o'rnatilgan (model fayllari birinchi sintezda VoiceStudio tomonidan yuklanadi).", "Voice");
            return;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var r = await _http.GetAsync("models", cts.Token).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) { _log.Warn($"/models HTTP {(int)r.StatusCode}: model mavjudligini tekshirib bo'lmadi.", "Voice"); return; }
            var arr = JsonNode.Parse(await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false))?["models"] as JsonArray;
            var m = arr?.FirstOrDefault(x =>
                (x?["engines"] as JsonArray)?.Any(e => string.Equals(e?.GetValue<string>(), model, StringComparison.OrdinalIgnoreCase)) == true
                || (x?["repo_id"]?.GetValue<string>() ?? "").Contains(model, StringComparison.OrdinalIgnoreCase));
            if (m is null) { _log.Warn($"VoiceStudio /models ro'yxatida '{model}' topilmadi; sintez urinib ko'riladi.", "Voice"); return; }
            if (m["installed"]?.GetValue<bool>() == false)
                throw PipelineException.Config("tts_model_missing", $"VoiceStudio'da '{model}' modeli o'rnatilmagan ({m["repo_id"]}). VoiceStudio'da modelni o'rnating.");
            _log.Info($"TTS modeli o'rnatilgan: {m["repo_id"]}.", "Voice");
        }
        catch (PipelineException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { _log.Warn($"Model tekshiruvi o'tkazib yuborildi: {e.Message}", "Voice"); }
    }

    public async Task SynthesizeAsync(string text, VoiceProfile voice, string outWav, CancellationToken ct)
    {
        if (!await HealthyAsync(ct).ConfigureAwait(false))
        {
            if (StartedByUs || ProcessSnapshot.Under(OwnedDirs).Count == 0)
            {
                _log.Warn("VoiceStudio javob bermayapti (yopilib qolgan bo'lishi mumkin) - worker uni qayta ishga tushiradi.", "Voice");
                Metrics["restarts"] = Metrics.GetValueOrDefault("restarts") + 1;
                if (StartedByUs) await StopOursAsync("health failed").ConfigureAwait(false);
                await StartOursAsync(ct).ConfigureAwait(false);
            }
            else throw PipelineException.Transient("voicestudio_down", "Foydalanuvchi ochgan VoiceStudio javob bermayapti.");
        }
        var body = new JsonObject { ["model"] = voice.Model, ["voice"] = voice.Voice, ["input"] = text, ["response_format"] = "wav" };
        if (Math.Abs(voice.Speed - 1.0) > 0.001) body["speed"] = voice.Speed;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(_s.RequestTimeoutSec));
        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync("v1/audio/speech", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PipelineException.Transient("tts_timeout", $"TTS so'rovi {_s.RequestTimeoutSec}s da tugamadi."); }
        catch (HttpRequestException e) { throw PipelineException.Transient("tts_network", $"VoiceStudio bilan aloqa uzildi: {e.Message}", e); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (err.Length > 500) err = err[..500];
                var code = (int)resp.StatusCode;
                if (code is 400 or 404 or 409 or 422) throw PipelineException.Config("tts_rejected", $"VoiceStudio so'rovni rad etdi ({code}): {err}. Ovoz profili (model/voice) to'g'riligini tekshiring.");
                throw PipelineException.Transient("tts_http", $"VoiceStudio HTTP {code}: {err}");
            }
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!ctype.StartsWith("audio/")) throw PipelineException.Transient("tts_not_audio", $"VoiceStudio audio o'rniga '{ctype}' qaytardi.");
            await using var fs = new FileStream(outWav, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        Metrics["synth_sec"] = Metrics.GetValueOrDefault("synth_sec") + sw.Elapsed.TotalSeconds;
        Metrics["chars"] = Metrics.GetValueOrDefault("chars") + text.Length;
        if (_job is not null) Metrics["peak_mb"] = Math.Max(Metrics.GetValueOrDefault("peak_mb"), _job.Accounting().PeakJobMemory / 1048576.0);
    }

    private async Task StopOursAsync(string reason)
    {
        if (_job is null) return;
        var before = Native.Memory().Avail / 1073741824.0;
        var acct = _job.Accounting();
        var sent = _job.RequestCloseWindows();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(sent > 0 ? 20 : 2) && _job.ProcessIds().Length > 0) await Task.Delay(500).ConfigureAwait(false);
        var forced = _job.Terminate();
        await Task.Delay(1500).ConfigureAwait(false);
        // Job'dan tashqariga chiqib ketgan (detached) jarayonlar: faqat biz ishga tushirgandan keyin paydo bo'lganlar.
        var escaped = ProcessSnapshot.Under(OwnedDirs).Where(p => p.StartUtc >= _ourStartUtc.AddSeconds(-1) && !_preExisting.Contains((p.Pid, p.StartUtc))).ToList();
        foreach (var e in escaped) ProcessSnapshot.KillExact(e);
        await Task.Delay(1000).ConfigureAwait(false);
        var after = Native.Memory().Avail / 1073741824.0;
        Metrics["closed_peak_mb"] = acct.PeakJobMemory / 1048576.0;
        Metrics["ram_freed_gb"] = Math.Round(after - before, 2);
        _log.Info($"VoiceStudio yopildi ({reason}): WM_CLOSE {sent} oynaga, majburan {forced} jarayon, tashqariga chiqqan {escaped.Count}. Peak {acct.PeakJobMemory / 1048576.0:0} MB, bo'sh RAM {before:0.00} → {after:0.00} GB.", "Voice");
        if (_store is not null && _proc is not null)
            _store.RecordProcess(new OwnedProcessRecord { RunId = _runId, Stage = "Voice", Tool = "voicestudio", Pid = _proc.Pid, ProcessStartUtc = _proc.StartTimeUtc, ExePath = _s.ExePath, EndedAtUtc = DateTimeOffset.UtcNow, EndReason = reason, PeakMemoryBytes = acct.PeakJobMemory });
        _proc?.Dispose(); _proc = null;
        _job.Dispose(); _job = null;
        StartedByUs = false;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (StartedByUs) await StopOursAsync("stage finished").ConfigureAwait(false); }
        finally { _http.Dispose(); }
    }
}

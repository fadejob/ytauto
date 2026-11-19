using System.Text;
using System.Text.Json;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Media;
using YtAuto.Core.Qa;
using YtAuto.Core.Script;

namespace YtAuto.Core.Pipeline;

public sealed partial class JobRunner
{
    // ---------------------------------------------------------------- input hashes
    // Format bu yerga kirmaydi: format (yoki shablon) o'zgarsa ssenariy va ovoz saqlanadi, faqat subtitr qatorlari va montaj qayta qilinadi.
    // Qo'lda kiritilgan matn/ovoz uchun alohida hash: oddiy vazifalarning hash'i o'zgarmaydi (tayyor bosqichlar qayta bajarilmaydi).
    private static string ScriptInputHash(JobContext c) => c.Job.HasManualScript
        ? Hashing.Sha256Text("script-manual-v1", c.Job.ManualTitle, c.Job.ManualScript)
          // Avval qo'lda kiritilgan matnga metadata yozilmasdi: yozish kerak bo'lgandagina hash o'zgaradi.
          + (WriteDescription(c) || WriteTags(c) ? $":meta-d{WriteDescription(c)}-t{WriteTags(c)}" : "")
        : Hashing.Sha256Text("script-v2", c.Job.Topic, c.Job.Notes, c.Job.Language,
            c.Job.TargetDurationSec.ToString(), c.S.Gemini.ScriptPrompt, c.S.Gemini.Model, c.S.Gemini.UseSearchGrounding.ToString())
          + ChannelHashSuffix(c.S.ChannelContext.ScriptBlock)
          // Avval Gemini har doim ikkalasini yozardi: standart bor bo'lgandagina hash o'zgaradi.
          + (WriteDescription(c) && WriteTags(c) ? "" : $":meta-d{WriteDescription(c)}-t{WriteTags(c)}");

    /// <summary>Kanalda standart tavsif yo'q - Gemini videoga tavsif yozadi (bor bo'lsa standart ishlatiladi).</summary>
    internal static bool WriteDescription(JobContext c) => string.IsNullOrWhiteSpace(c.S.YouTube.DefaultDescription);
    /// <summary>Kanalda standart taglar yo'q - Gemini videoga 5 ta tag yozadi (bor bo'lsa standartlar ishlatiladi).</summary>
    internal static bool WriteTags(JobContext c) => ScriptValidator.CleanTags((c.S.YouTube.DefaultTags ?? "").Split(','), 100).Count == 0;

    /// <summary>Kanal konteksti bo'lmasa hash o'zgarmaydi (oldingi joblar qayta bajarilmaydi); bo'lsa uning o'zgarishi bosqichni qayta ishlatadi.</summary>
    private static string ChannelHashSuffix(string block) => block.Length == 0 ? "" : ":" + Hashing.Sha256Text("channel-v1", block);

    private string VoiceInputHash(JobContext c, VideoScript s)
    {
        // Imzolangan havolaning token qismi (query) hash'ga kirmaydi: havola yangilansa ham ovoz qayta yuklanmaydi.
        if (c.Job.HasManualAudio) return Hashing.Sha256Text("voice-manual-v1", c.Job.ManualAudioUrl!.Split('?')[0]);
        var v = c.S.FindVoice(c.Job.VoiceProfileName);
        var h = Hashing.Sha256Text("voice-v2", s.SubtitleText, v?.Model, v?.Voice, v?.Speed.ToString(), c.S.VoiceStudio.ChunkChars.ToString(), c.S.VoiceStudio.PauseBetweenChunksSec.ToString());
        // Klon ovoz namunasi almashsa ovoz qayta yaratiladi (boshqa ovozlar uchun hash o'zgarmaydi).
        return c.S.VoiceClone.Matches(v) ? h + ":clone-" + c.S.VoiceClone.Version : h;
    }

    private string AlignInputHash(JobContext c, VideoScript s) =>
        Hashing.Sha256Text("align-v1", OutputSha(c, StageKind.Voice, "voice"), s.SubtitleText, c.S.VoiceStudio.WhisperModel, c.SubtitleMaxChars.ToString());

    private string EditInputHash(JobContext c, VideoScript s) => Hashing.Sha256Text("edit-v2",
        OutputSha(c, StageKind.Voice, "voice"), OutputSha(c, StageKind.Align, "vtt"),
        s.SubtitleText, c.S.Claude.MontagePrompt, c.Snapshot.TemplateVersion, c.Job.Format.ToString(), c.Job.QaFixCycles.ToString())
        + ChannelHashSuffix(c.S.ChannelContext.MontageBlock);

    private string RenderInputHash(JobContext c) => Hashing.Sha256Text("render-v1",
        StageValue(c, StageKind.Edit, "film_manifest"), c.S.OpenFilm.Fps.ToString(), c.S.OpenFilm.LoudnessLufs.ToString(), c.S.OpenFilm.BurnSubtitles.ToString(), c.Job.QaFixCycles.ToString());

    private string ValidateInputHash(JobContext c) => Hashing.Sha256Text("validate-v1", OutputSha(c, StageKind.Render, "video"));

    private string ArchiveInputHash(JobContext c) => Hashing.Sha256Text("archive-v1", OutputSha(c, StageKind.Render, "video"),
        OutputSha(c, StageKind.Voice, "voice"), c.S.Paths.VideoArchiveDir, c.S.Paths.AudioArchiveDir);

    private string? OutputSha(JobContext c, StageKind k, string role) =>
        Deserialize(_x.Store.GetStage(c.Job.Id, k).OutputsJson).Files.FirstOrDefault(f => f.Role == role)?.Sha256;

    private string? StageValue(JobContext c, StageKind k, string key) => Deserialize(_x.Store.GetStage(c.Job.Id, k).OutputsJson).Get(key);

    private static VideoScript LoadScript(JobContext c) { var s = VideoScript.FromJson(File.ReadAllText(c.ScriptJson)); ScriptValidator.Canonicalize(s); return s; }

    // ---------------------------------------------------------------- B: Gemini
    private async Task<StageOutputs> ScriptStageAsync(JobContext c)
    {
        if (c.Job.HasManualScript) return await ManualScriptStageAsync(c).ConfigureAwait(false);
        bool writeDesc = WriteDescription(c), writeTags = WriteTags(c);
        var req = new ScriptRequest
        {
            Topic = c.Job.Topic, Notes = c.Job.Notes, Language = c.Job.Language, TargetDurationSec = c.Job.TargetDurationSec,
            Format = c.Job.Format switch { VideoFormat.Shorts9x16 => "vertical 9:16 short", VideoFormat.Square1x1 => "square 1:1", _ => "horizontal 16:9" },
            PromptTemplate = ChannelPrompt.ApplyToScript(string.IsNullOrWhiteSpace(c.S.Gemini.ScriptPrompt) ? DefaultPrompts.Script : c.S.Gemini.ScriptPrompt, c.S.ChannelContext),
            FactSheetPromptTemplate = DefaultPrompts.FactSheet,
            UseSearchGrounding = c.S.Gemini.UseSearchGrounding, Model = c.S.Gemini.Model, Temperature = c.S.Gemini.Temperature,
            WriteDescription = writeDesc, WriteTags = writeTags,
        };
        VideoScript? script = null; List<string> problems = new(); ScriptResult? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var r = req;
            last = await Retry.TransientAsync(_ => _x.ScriptGenerator.GenerateAsync(r, c.Log, c.Ct), c.S.Limits.MaxTransientRetries,
                (n, e) => c.Log.Warn($"Gemini vaqtinchalik xato ({n}): {e.Message}", "Script"), c.Ct).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(c.ScriptDir, $"gemini_raw_{attempt}.json"), last.RawModelOutput);
            var prevGrounded = script?.GroundedSources; var prevFacts = script?.FactSheet;
            script = last.Script;
            if (script.GroundedSources.Count == 0 && prevGrounded is { Count: > 0 }) { script.GroundedSources = prevGrounded; script.FactSheet = prevFacts ?? ""; }
            foreach (var n in ScriptValidator.SanitizeSources(script)) c.Log.Warn(n, "Script");
            problems = ScriptValidator.Validate(script, c.Job.Language, c.Job.TargetDurationSec);
            if (problems.Count == 0) break;
            c.Log.Warn($"Ssenariy talablarga mos emas (urinish {attempt}/3): {string.Join(" | ", problems)}", "Script");
            req = new ScriptRequest
            {
                Topic = req.Topic, Notes = req.Notes, Language = req.Language, TargetDurationSec = req.TargetDurationSec, Format = req.Format,
                PromptTemplate = req.PromptTemplate, FactSheetPromptTemplate = req.FactSheetPromptTemplate, UseSearchGrounding = false,
                Model = req.Model, Temperature = req.Temperature, PreviousProblems = problems,
                WriteDescription = writeDesc, WriteTags = writeTags,
            };
            // Fakt varag'ini qayta ishlatamiz (qayta qidiruv qilmaymiz)
            if (!string.IsNullOrEmpty(script.FactSheet)) req = WithFactSheet(req, script);
        }
        if (script is null || problems.Count > 0)
            throw PipelineException.Quality("script_invalid", "Gemini ssenariysi 3 urinishda ham talablarga mos kelmadi: " + string.Join(" | ", problems));

        if (script.UnverifiedClaims.Count > 0)
            c.Log.Warn($"Model manbasiz deb belgilagan da'volar: {string.Join(" | ", script.UnverifiedClaims)}", "Script");
        ScriptValidator.Canonicalize(script);
        ApplyMetadataRule(c, script, writeDesc, writeTags);
        File.WriteAllText(c.ScriptJson, script.ToJson(), new UTF8Encoding(false));
        File.Copy(c.ScriptJson, Path.Combine(c.RecordDir, "script.json"), true);

        c.Job.Title = script.YouTubeTitle.Trim();
        c.Job.Description = writeDesc ? BuildDescription(script) : "";
        _x.Store.Update(c.Job);
        c.StageMetrics["words"] = ScriptValidator.CountWords(script.SubtitleText);
        c.StageMetrics["prompt_tokens"] = last!.PromptTokens;
        c.StageMetrics["output_tokens"] = last.OutputTokens;
        c.Log.Info($"Ssenariy tayyor: title \"{script.YouTubeTitle}\" (YouTube'ga chiqarishgacha saqlanadi), subtitr {c.StageMetrics["words"]} so'z, tasdiqlangan manbalar: {script.Sources.Count}.", "Script");
        return new StageOutputs { Files = { FileOps.Describe(c.ScriptJson, "script") }, Values = { ["title"] = script.YouTubeTitle, ["model"] = last.ModelUsed } };
    }

    /// <summary>
    /// Botda kiritilgan subtitr va title'dan ssenariy: Gemini chaqirilmaydi. Matn foydalanuvchiniki - faqat aniq xatolar
    /// (bo'sh, title uzun yoki &lt;&gt; belgili) to'xtatadi; uslubiy kamchiliklar ogohlantirish sifatida yoziladi.
    /// </summary>
    private async Task<StageOutputs> ManualScriptStageAsync(JobContext c)
    {
        var script = new VideoScript
        {
            SubtitleText = c.Job.ManualScript!.Replace("\r\n", "\n").Trim(),
            YouTubeTitle = c.Job.ManualTitle!.Trim(),
        };
        ScriptValidator.Canonicalize(script);
        var problems = ScriptValidator.Validate(script, c.Job.Language, 0);
        var fatal = problems.Where(p => p.StartsWith("subtitle_text bo'sh") || p.StartsWith("youtube_title")).ToList();
        if (fatal.Count > 0)
            throw PipelineException.Config("manual_script_invalid", "Botda kiritilgan subtitr/title yaroqsiz: " + string.Join(" | ", fatal) + " Mavzuni botda tahrirlang.");
        foreach (var p in problems.Except(fatal)) c.Log.Warn("Qo'lda kiritilgan matn: " + p, "Script");

        // Matn va title foydalanuvchiniki; kanalda standart tavsif/taglar bo'lmasa Gemini faqat ularni yozadi (matn o'zgarmaydi).
        bool writeDesc = WriteDescription(c), writeTags = WriteTags(c);
        if (writeDesc || writeTags)
        {
            try
            {
                var meta = await Retry.TransientAsync(_ => _x.ScriptGenerator.DescribeAsync(new MetadataRequest
                {
                    Title = script.YouTubeTitle, Text = script.SubtitleText, Language = c.Job.Language, Model = c.S.Gemini.Model,
                    WriteDescription = writeDesc, WriteTags = writeTags,
                }, c.Log, c.Ct), c.S.Limits.MaxTransientRetries, (n, e) => c.Log.Warn($"Gemini (tavsif/tag) vaqtinchalik xato ({n}): {e.Message}", "Script"), c.Ct).ConfigureAwait(false);
                script.YouTubeDescription = meta.Description.Replace("<", "‹").Replace(">", "›");
                script.Tags = meta.Tags;
                ScriptValidator.Canonicalize(script);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.Log.Warn($"Tavsif/taglarni Gemini yozib bera olmadi - video ularsiz yuklanadi: {e.Message}", "Script");
            }
        }
        ApplyMetadataRule(c, script, writeDesc, writeTags);

        File.WriteAllText(c.ScriptJson, script.ToJson(), new UTF8Encoding(false));
        File.Copy(c.ScriptJson, Path.Combine(c.RecordDir, "script.json"), true);
        c.Job.Title = script.YouTubeTitle;
        c.Job.Description = writeDesc ? BuildDescription(script) : "";
        _x.Store.Update(c.Job);
        c.StageMetrics["words"] = ScriptValidator.CountWords(script.SubtitleText);
        c.Log.Info($"Ssenariy botda qo'lda kiritilgan: matn Gemini'ga yozdirilmadi. Title \"{script.YouTubeTitle}\", subtitr {c.StageMetrics["words"]} so'z.", "Script");
        return new StageOutputs { Files = { FileOps.Describe(c.ScriptJson, "script") }, Values = { ["title"] = script.YouTubeTitle, ["model"] = "manual" } };
    }

    private static ScriptRequest WithFactSheet(ScriptRequest r, VideoScript s) => new()
    {
        Topic = r.Topic, Notes = r.Notes + "\n\n[FACT SHEET FROM PREVIOUS STEP]\n" + s.FactSheet + "\n[SOURCES]\n" + string.Join("\n", s.GroundedSources.Select(g => $"{g.Title}: {g.Url}")),
        Language = r.Language, TargetDurationSec = r.TargetDurationSec, Format = r.Format, PromptTemplate = r.PromptTemplate,
        FactSheetPromptTemplate = r.FactSheetPromptTemplate, UseSearchGrounding = false, Model = r.Model, Temperature = r.Temperature,
        PreviousProblems = r.PreviousProblems,
    };

    /// <summary>
    /// Kanal standarti bor bo'lsa Gemini yozgani olinmaydi (model baribir yozib yuborgan bo'lsa ham); yo'q bo'lsa yozilmagani logga.
    /// </summary>
    private static void ApplyMetadataRule(JobContext c, VideoScript script, bool writeDesc, bool writeTags)
    {
        if (!writeDesc) script.YouTubeDescription = "";
        if (!writeTags) script.Tags.Clear();
        if (writeDesc && script.YouTubeDescription.Length == 0) c.Log.Warn("Kanalda standart tavsif yo'q, Gemini ham tavsif yozmadi - video tavsifsiz yuklanadi.", "Script");
        if (writeTags && script.Tags.Count < DefaultPrompts.TagsPerVideo)
            c.Log.Warn($"Kanalda standart taglar yo'q, Gemini {script.Tags.Count} ta tag yozdi ({DefaultPrompts.TagsPerVideo} ta kutilgan).", "Script");
        c.Log.Info($"Tavsif: {(writeDesc ? "Gemini yozdi" : "kanal standarti ishlatiladi")}; taglar: {(writeTags ? $"Gemini yozdi ({script.Tags.Count} ta)" : "kanal standarti ishlatiladi")}.", "Script");
    }

    internal static string BuildDescription(VideoScript s)
    {
        var sb = new StringBuilder(s.YouTubeDescription.Trim());
        if (s.Sources.Count > 0)
        {
            sb.Append("\n\nManbalar / Sources:\n");
            foreach (var src in s.Sources.Take(10)) sb.Append("• ").Append(string.IsNullOrWhiteSpace(src.Title) ? src.Url : $"{src.Title}: {src.Url}").Append('\n');
        }
        var d = sb.ToString().Replace("<", "‹").Replace(">", "›");
        return d.Length > 4900 ? d[..4900] : d;
    }

    // ---------------------------------------------------------------- C: VoiceStudio
    private async Task<StageOutputs> VoiceStageAsync(JobContext c, VideoScript script)
    {
        if (c.Job.HasManualAudio) return await ManualVoiceStageAsync(c, script).ConfigureAwait(false);
        var voice = c.S.FindVoice(c.Job.VoiceProfileName) ?? throw PipelineException.Config("voice_profile", $"Ovoz profili topilmadi: {c.Job.VoiceProfileName}");
        if (!TtsVoices.IsAllowed(voice))
            throw PipelineException.Config("voice_model", $"Ovoz profili '{voice.Name}' '{voice.Model}' modelini ishlatadi. Faqat {TtsVoices.DisplayName} ({TtsVoices.Model}) ruxsat etilgan.");
        var ram = _x.Resources.AvailableRamGb();
        if (ram < c.S.Limits.MinFreeRamGbForTts)
            c.Log.Warn($"Bo'sh RAM kam ({ram:0.00} GB < {c.S.Limits.MinFreeRamGbForTts} GB). Boshqa og'ir dasturlarni yopish tavsiya etiladi; TTS ketma-ket, bo'laklab bajariladi.", "Voice");

        var chunks = TextChunker.Split(script.SubtitleText, Math.Max(150, c.S.VoiceStudio.ChunkChars));
        var manifestPath = Path.Combine(c.VoiceChunksDir, "chunks.json");
        var prevManifest = File.Exists(manifestPath) ? JsonSerializer.Deserialize<List<ChunkInfo>>(File.ReadAllText(manifestPath)) ?? new() : new();
        var cloneTag = c.S.VoiceClone.Matches(voice) ? c.S.VoiceClone.Version : "";
        var manifest = chunks.Select((t, i) => new ChunkInfo { Index = i, TextHash = Hashing.Sha256Text(t, voice.Model, voice.Voice, voice.Speed.ToString(), cloneTag), Chars = t.Length }).ToList();
        var paths = manifest.Select(m => Path.Combine(c.VoiceChunksDir, $"chunk_{m.Index:000}.wav")).ToList();
        var todo = new List<int>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var prev = prevManifest.FirstOrDefault(p => p.Index == i);
            if (prev is not null && prev.TextHash == manifest[i].TextHash && File.Exists(paths[i]) && ChunkOk(paths[i], chunks[i], out _)) continue;
            todo.Add(i);
        }
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        c.Log.Info($"TTS: {chunks.Count} bo'lak, {todo.Count} tasi yaratiladi ({chunks.Count - todo.Count} tasi oldingi urinishdan tayyor).", "Voice");

        if (todo.Count > 0)
        {
            await using var session = await _x.Tts.OpenAsync(_x.Settings.Load().VoiceStudio, c.Log, c.Ct).ConfigureAwait(false);
            c.Snapshot.IntegrationVersions["voicestudio"] = session.Version;
            await session.EnsureModelAvailableAsync(voice.Model, c.Ct).ConfigureAwait(false);
            if (c.S.VoiceClone.Matches(voice)) await PrepareVoiceCloneAsync(c, session).ConfigureAwait(false);
            foreach (var i in todo)
            {
                var text = chunks[i];
                await Retry.TransientAsync(async attempt =>
                {
                    var tmp = paths[i] + ".tmp";
                    await session.SynthesizeAsync(text, voice, tmp, c.Ct).ConfigureAwait(false);
                    if (!ChunkOk(tmp, text, out var why)) { File.Delete(tmp); throw PipelineException.Transient("tts_chunk_bad", $"{i + 1}-bo'lak audio yaroqsiz: {why}"); }
                    File.Move(tmp, paths[i], true);
                    return true;
                }, c.S.Limits.MaxTransientRetries, (n, e) => c.Log.Warn($"TTS qayta urinish ({n}): {e.Message}", "Voice"), c.Ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                c.Log.Info($"TTS bo'lak {i + 1}/{chunks.Count} tayyor ({Wav.ReadInfo(paths[i]).Duration:0.0}s).", "Voice");
            }
            foreach (var kv in session.Metrics) c.StageMetrics["tts_" + kv.Key] = kv.Value;
        } // session dispose: worker ochgan VoiceStudio va model jarayonlari shu yerda yopiladi

        Wav.Concat(paths, c.VoiceWav, chunks.Count > 1 ? c.S.VoiceStudio.PauseBetweenChunksSec : 0);
        var a = Wav.Analyze(c.VoiceWav, 1.0);
        if (a.Problems.Count > 0) throw PipelineException.Transient("tts_bad_audio", "Yakuniy ovoz yaroqsiz: " + string.Join(" ", a.Problems));
        var probe = await _x.Media.ProbeAsync(c.VoiceWav, c.Ct).ConfigureAwait(false);
        if (!probe.HasAudio || Math.Abs(probe.Duration - a.Duration) > 0.25)
            throw PipelineException.Transient("tts_probe", $"ffprobe ovoz faylini boshqacha o'qidi (davomiylik {probe.Duration:0.00}s vs {a.Duration:0.00}s).");
        c.Job.AudioDurationSec = Math.Round(a.Duration, 3);
        _x.Store.Update(c.Job);
        c.StageMetrics["audio_sec"] = a.Duration; c.StageMetrics["peak_db"] = a.PeakDb; c.StageMetrics["rms_db"] = a.RmsDb; c.StageMetrics["speech_ratio"] = a.SpeechRatio;
        var expected = script.SubtitleText.Length / 15.0;
        if (a.Duration < expected * 0.45) c.Log.Warn($"Ovoz kutilganidan ancha qisqa ({a.Duration:0.0}s, ~{expected:0}s kutilgan). Matn qisman o'qilmagan bo'lishi mumkin - alignment tekshiradi.", "Voice");
        c.Log.Info($"Ovoz tayyor: {a.Duration:0.00}s, peak {a.PeakDb:0.0} dB, RMS {a.RmsDb:0.0} dB.", "Voice");
        return new StageOutputs { Files = { FileOps.Describe(c.VoiceWav, "voice") }, Values = { ["duration"] = a.Duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture) } };
    }

    /// <summary>
    /// Botdan yuklangan tayyor ovoz: VoiceStudio umuman ochilmaydi. Fayl yuklab olinib WAV'ga o'tkaziladi va tekshiriladi.
    /// Ovoz yaroqsiz bo'lsa qayta urinish foyda bermaydi - "E'tibor kerak" (foydalanuvchi boshqa fayl yuklaydi).
    /// </summary>
    private async Task<StageOutputs> ManualVoiceStageAsync(JobContext c, VideoScript script)
    {
        var importer = _x.AudioImporter ?? throw PipelineException.Config("audio_importer_missing", "Tayyor ovozni yuklab olish servisi ulanmagan.");
        c.Log.Info("Ovoz botdan tayyor yuklangan: VoiceStudio TTS o'tkazib yuboriladi, fayl yuklab olinmoqda.", "Voice");
        var tmp = c.VoiceWav + ".tmp.wav";
        await Retry.TransientAsync(async _ => { await importer.ImportAsync(c.Job.ManualAudioUrl!, tmp, c.Log, c.Ct).ConfigureAwait(false); return true; },
            c.S.Limits.MaxTransientRetries, (n, e) => c.Log.Warn($"Ovozni yuklab olish qayta urinish ({n}): {e.Message}", "Voice"), c.Ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        File.Move(tmp, c.VoiceWav, true);

        var a = Wav.Analyze(c.VoiceWav, 1.0);
        if (a.Problems.Count > 0) throw PipelineException.Quality("manual_audio_bad", "Yuklangan ovoz yaroqsiz: " + string.Join(" ", a.Problems) + " Botda boshqa audio fayl yuklang.");
        c.Job.AudioDurationSec = Math.Round(a.Duration, 3);
        _x.Store.Update(c.Job);
        c.StageMetrics["audio_sec"] = a.Duration; c.StageMetrics["peak_db"] = a.PeakDb; c.StageMetrics["rms_db"] = a.RmsDb; c.StageMetrics["speech_ratio"] = a.SpeechRatio;
        var expected = script.SubtitleText.Length / 15.0;
        if (a.Duration < expected * 0.45) c.Log.Warn($"Yuklangan ovoz subtitr matniga nisbatan ancha qisqa ({a.Duration:0.0}s, ~{expected:0}s kutilgan) - alignment tekshiradi.", "Voice");
        c.Log.Info($"Tayyor ovoz qabul qilindi: {a.Duration:0.00}s, peak {a.PeakDb:0.0} dB, RMS {a.RmsDb:0.0} dB.", "Voice");
        return new StageOutputs { Files = { FileOps.Describe(c.VoiceWav, "voice") }, Values = { ["duration"] = a.Duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture), ["source"] = "manual" } };
    }

    private sealed class ChunkInfo { public int Index { get; set; } public string TextHash { get; set; } = ""; public int Chars { get; set; } }

    private static bool ChunkOk(string path, string text, out string why)
    {
        try
        {
            var a = Wav.Analyze(path, 0.3);
            var expectedMin = text.Length / 15.0 * 0.35; // juda qisqa bo'lsa - kesilgan
            if (a.Problems.Count > 0) { why = string.Join(" ", a.Problems); return false; }
            if (a.Duration < expectedMin) { why = $"juda qisqa ({a.Duration:0.0}s, matn {text.Length} belgi)"; return false; }
            why = ""; return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    // ---------------------------------------------------------------- D: timing
    private async Task<StageOutputs> AlignStageAsync(JobContext c, VideoScript script)
    {
        var words = await _x.Aligner.TranscribeWordsAsync(c.VoiceWav, c.Job.Language, c.AsrJson, c.Log, c.Ct).ConfigureAwait(false);
        foreach (var kv in _x.Aligner.LastMetrics) c.StageMetrics["align_" + kv.Key] = kv.Value;
        var duration = c.Job.AudioDurationSec ?? Wav.ReadInfo(c.VoiceWav).Duration;
        var res = TranscriptAligner.Align(script.SubtitleText, words, duration);
        File.WriteAllText(c.AlignmentJson, JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true }));
        c.StageMetrics["similarity"] = Math.Round(res.Similarity, 3);
        c.StageMetrics["matched_ratio"] = Math.Round(res.MatchedRatio, 3);
        if (res.Similarity < 0.55 && c.Job.HasManualAudio)
            throw PipelineException.Quality("voice_text_mismatch",
                $"Yuklangan ovoz botda kiritilgan subtitr matni bilan mos emas (o'xshashlik {res.Similarity:P0}). Subtitr aynan ovozda aytilgan matn bo'lishi kerak — botda matnni tuzating yoki boshqa audio yuklang. Farqlar: {string.Join("; ", res.Mismatches.Take(8))}");
        if (res.Similarity < 0.55)
            throw PipelineException.Quality("voice_text_mismatch",
                $"Ovozdagi nutq ssenariy bilan mos emas (o'xshashlik {res.Similarity:P0}). TTS boshqa matn o'qigan yoki til/ASR modeli mos emas. Farqlar: {string.Join("; ", res.Mismatches.Take(8))}");
        if (res.Similarity < 0.9 || res.Mismatches.Count > 0)
            c.Log.Warn($"Transkripsiya asl matndan farq qiladi (o'xshashlik {res.Similarity:P0}, mos so'zlar {res.MatchedRatio:P0}). Subtitr asl matndan olinadi, mos kelmagan so'zlar vaqti qo'shnilaridan interpolyatsiya qilindi. Farqlar: {string.Join("; ", res.Mismatches.Take(12))}", "Align");

        // Subtitr ovozga mos bo'lishi uchun aynan o'qilgan matn (narration) va uning haqiqiy vaqtlari ishlatiladi.
        var cues = SubtitleBuilder.BuildCues(script.SubtitleText, res.Words, c.SubtitleMaxChars);
        File.WriteAllText(c.VoiceVtt, SubtitleBuilder.ToVtt(cues), new UTF8Encoding(false));
        File.WriteAllText(c.VoiceSrt, SubtitleBuilder.ToSrt(cues), new UTF8Encoding(false));

        var timing = new
        {
            audio_duration = duration,
            source = $"faster-whisper ({c.S.VoiceStudio.WhisperModel}) word timestamps, mapped onto the original subtitle text",
            similarity = res.Similarity,
            cues = cues.Select(q => new { start = Math.Round(q.Start, 3), end = Math.Round(q.End, 3), text = q.Text }),
            words = res.Words.Select(w => new { w.Word, start = Math.Round(w.Start, 3), end = Math.Round(w.End, 3), w.Matched }),
        };
        File.WriteAllText(c.TimingJson, JsonSerializer.Serialize(timing, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        c.StageMetrics["cues"] = cues.Count;
        return new StageOutputs
        {
            Files = { FileOps.Describe(c.VoiceVtt, "vtt"), FileOps.Describe(c.VoiceSrt, "srt"), FileOps.Describe(c.TimingJson, "timing") },
            Values = { ["similarity"] = res.Similarity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) },
        };
    }

    // ---------------------------------------------------------------- E: Claude montaji
    private async Task<StageOutputs> EditStageAsync(JobContext c, VideoScript script)
    {
        var fixMode = c.Job.QaFixCycles > 0 && File.Exists(c.FilmHtml) && File.Exists(c.QaFeedback);
        var tpl = c.S.FindTemplate(c.Job.TemplateName);
        var assets = Path.Combine(c.FilmDir, "assets");
        if (!fixMode)
        {
            if (Directory.Exists(c.FilmDir)) PathGuard.SafeDeleteDirectory(c.WorkRoot, c.FilmDir, m => c.Log.Debug(m));
            if (File.Exists(c.QaFeedback)) File.Delete(c.QaFeedback);
            Directory.CreateDirectory(c.FilmDir);
            if (tpl is { Kind: TemplateKind.OpenFilmProject })
            {
                // Asl shablon o'zgartirilmaydi: nusxasi bilan ishlanadi.
                PathGuard.CopyDirectory(tpl.Path, c.FilmDir, f =>
                {
                    var rel = Path.GetRelativePath(tpl.Path, f).Replace('\\', '/');
                    return !(rel.StartsWith(".git") || rel.StartsWith(".film/cache") || rel == ".film/cache");
                });
                c.Log.Info($"OpenFilm shabloni nusxalandi: {tpl.Path} -> {c.FilmDir}", "Edit");
            }
            Directory.CreateDirectory(assets);
        }
        File.Copy(c.VoiceWav, Path.Combine(assets, "voice.wav"), true);
        File.Copy(c.VoiceVtt, Path.Combine(assets, "voice.vtt"), true);
        var voiceSha = Hashing.Sha256File(Path.Combine(assets, "voice.wav"));

        // MP4 reference: Claude videoni ko'ra olmaydi - 12 kadr va Gemini dizayn qo'llanmasi (STYLE_GUIDE.md), shablon bo'yicha keshlanadi.
        var style = await PrepareTemplateStyleAsync(c, tpl).ConfigureAwait(false);

        var skillPath = Path.Combine(c.EditDir, "openfilm-edit-skill.md");
        if (!File.Exists(skillPath))
            File.WriteAllText(skillPath, await _x.Film.GetSkillTextAsync("edit", c.Ct).ConfigureAwait(false), new UTF8Encoding(false));

        // Claude'ga: subtitr (matn + vaqtli cue'lar), ovoz, shablon (bo'lsa) va saqlangan Claude prompti.
        var duration = c.Job.AudioDurationSec ?? Wav.ReadInfo(c.VoiceWav).Duration;
        var cues = VttParser.Parse(File.ReadAllText(c.VoiceVtt));
        var media = await PrepareMediaAsync(c, script, cues).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(c.EditDir, "subtitles.txt"), script.SubtitleText, new UTF8Encoding(false));
        File.Copy(c.VoiceSrt, Path.Combine(c.EditDir, "subtitles.srt"), true);
        var stale = Path.Combine(c.EditDir, "script.json");
        if (File.Exists(stale)) File.Delete(stale);
        File.WriteAllText(Path.Combine(c.EditDir, "instructions.md"),
            EditorInstructions.Build(c, script, cues, duration, tpl, style.Frames, style.Guide.Length > 0, media, style.Motion.Length > 0), new UTF8Encoding(false));
        File.Copy(Path.Combine(c.EditDir, "instructions.md"), Path.Combine(c.RecordDir, "instructions.md"), true);

        var shimDir = _x.Film.WriteShims(Path.Combine(_x.Paths.ToolsDir, "openfilm-shim"));
        var prompt = fixMode
            ? "Read qa_feedback.md and then instructions.md in the current folder. Fix every problem listed in qa_feedback.md inside film/, following instructions.md. Never ask questions. Reply DONE when finished."
            : "Read instructions.md in the current folder and carry it out exactly. Never ask questions. Reply DONE when finished.";
        var timeout = TimeSpan.FromMinutes(Math.Max(5, c.S.Claude.TimeoutMinutes));
        var prefix = fixMode ? $"claude-fix{c.Job.QaFixCycles}" : "claude";
        var r = await _x.Editor.RunAsync(c.EditDir, prompt, timeout, prefix, new[] { shimDir }, c.Log, c.Ct).ConfigureAwait(false);
        c.StageMetrics["claude_sec"] = r.Run.Elapsed.TotalSeconds;
        c.StageMetrics["claude_peak_mb"] = r.Run.PeakMemoryBytes / 1048576.0;
        if (r.CostUsd is double cost) c.StageMetrics["claude_cost_usd"] = cost;
        CopyIfExists(r.Run.StdoutPath, Path.Combine(c.RecordDir, Path.GetFileName(r.Run.StdoutPath)));
        CopyIfExists(r.Run.StderrPath, Path.Combine(c.RecordDir, Path.GetFileName(r.Run.StderrPath)));

        if (r.Run.TimedOut) throw PipelineException.Transient("claude_timeout", $"Claude sessiyasi {timeout.TotalMinutes:0} daqiqada tugamadi; jarayon daraxti yopildi.");
        if (r.Run.ExitCode != 0 || r.IsError)
        {
            var text = (r.ResultText + " " + r.Run.StderrTail).ToLowerInvariant();
            if (text.Contains("login") || text.Contains("authenticat") || text.Contains("401") || text.Contains("api key") || text.Contains("not logged"))
                throw PipelineException.Config("claude_auth", $"Claude Code autentifikatsiyasi ishlamadi (exit {r.Run.ExitCode}). Worker kontekstida `claude` ga kirilganini tekshiring. {Shorten(r.ResultText + " " + r.Run.StderrTail, 300)}");
            throw PipelineException.Transient("claude_failed", $"Claude sessiyasi xato bilan tugadi (exit {r.Run.ExitCode}): {Shorten(r.ResultText + " " + r.Run.StderrTail, 400)}");
        }
        if (r.PermissionDenials > 0)
        {
            var shown = r.DeniedActions.Take(5).Select(a => "«" + a + "»").ToList();
            var more = r.DeniedActions.Count > 5 ? $" va yana {r.DeniedActions.Count - 5} ta" : "";
            c.Log.Warn($"Claude {r.PermissionDenials} ta ruxsat etilmagan amalni urindi (rad etildi)" +
                       (shown.Count > 0 ? $": {string.Join(", ", shown)}{more}." : "."), "Edit");
        }
        if (!File.Exists(c.FilmHtml)) throw PipelineException.Transient("claude_no_film", "Claude tugadi, lekin film/film.html yaratilmadi.");

        var voicePath = Path.Combine(assets, "voice.wav");
        if (!File.Exists(voicePath) || Hashing.Sha256File(voicePath) != voiceSha)
        {
            c.Log.Warn("Claude assets/voice.wav ni o'zgartirgan yoki o'chirgan - asl nusxa tiklandi.", "Edit");
            File.Copy(c.VoiceWav, voicePath, true);
        }
        File.Copy(c.VoiceVtt, Path.Combine(assets, "voice.vtt"), true);
        CopyIfExists(Path.Combine(c.FilmDir, "BUILD_NOTES.md"), Path.Combine(c.RecordDir, "BUILD_NOTES.md"));
        CopyIfExists(c.FilmHtml, Path.Combine(c.RecordDir, "film.html"));

        var manifest = Hashing.DirectoryManifest(c.FilmDir, f => !f.Contains(Path.DirectorySeparatorChar + ".film" + Path.DirectorySeparatorChar) && !f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        c.Log.Info($"Claude montaj loyihasini yaratdi ({r.Run.Elapsed.TotalSeconds:0}s, sessiya {r.SessionId}).", "Edit");
        return new StageOutputs
        {
            Files = { FileOps.Describe(c.FilmHtml, "film") },
            Values = { ["film_manifest"] = manifest, ["claude_session"] = r.SessionId, ["fix_mode"] = fixMode.ToString() },
        };
    }

    private static void CopyIfExists(string src, string dst) { try { if (File.Exists(src)) File.Copy(src, dst, true); } catch { } }

    // ---------------------------------------------------------------- F: OpenFilm eksporti
    private async Task<StageOutputs> RenderStageAsync(JobContext c)
    {
        if (File.Exists(c.FinalMp4)) File.Delete(c.FinalMp4);
        var opts = new RenderOptions
        {
            Fps = c.S.OpenFilm.Fps, Workers = Math.Clamp(c.S.OpenFilm.RenderWorkers, 1, 4), LoudnessLufs = c.S.OpenFilm.LoudnessLufs,
            BurnSubtitles = c.S.OpenFilm.BurnSubtitles, Timeout = TimeSpan.FromMinutes(Math.Max(10, c.S.OpenFilm.RenderTimeoutMinutes)),
        };
        if (_x.Resources.TotalRamGb() < 10 && opts.Workers > 1)
        {
            c.Log.Warn($"RAM {_x.Resources.TotalRamGb():0.0} GB: parallel renderer soni 1 ga tushirildi.", "Render");
            opts = new RenderOptions { Fps = opts.Fps, Workers = 1, LoudnessLufs = opts.LoudnessLufs, BurnSubtitles = opts.BurnSubtitles, Timeout = opts.Timeout };
        }
        await using (var film = await _x.Film.OpenSessionAsync(c.Log, c.Ct).ConfigureAwait(false))
        {
            var look = await film.LookAsync(c.FilmDir, Array.Empty<string>(), "look", TimeSpan.FromMinutes(15), c.Ct).ConfigureAwait(false);
            c.StageMetrics["look_sec"] = look.Elapsed.TotalSeconds;
            if (look.TimedOut) throw PipelineException.Transient("look_timeout", "openfilm look vaqtida tugamadi.");
            if (look.ExitCode != 0)
            {
                var rep = new QaReport();
                rep.Add("openfilm_look", QaSeverity.Error, $"`openfilm look` exit {look.ExitCode}: {Shorten((look.StderrTail + "\n" + look.StdoutTail).Trim(), 1500)}");
                throw new EditorFixableException("look_failed", "openfilm look loyihada xato topdi.", rep);
            }

            ToolRunResult? render = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                render = await film.RenderAsync(c.FilmDir, c.FinalMp4, opts, c.Ct).ConfigureAwait(false);
                if (render.ExitCode == 0 && !render.TimedOut) break;
                c.Log.Warn($"Render urinish {attempt} muvaffaqiyatsiz (exit {render.ExitCode}, timeout {render.TimedOut}). {Shorten(render.StderrTail, 500)}", "Render");
            }
            c.StageMetrics["render_sec"] = render!.Elapsed.TotalSeconds;
            c.StageMetrics["render_peak_mb"] = render.PeakMemoryBytes / 1048576.0;
            c.StageMetrics["render_cpu_sec"] = render.CpuSeconds;
            CopyIfExists(render.StderrPath, Path.Combine(c.RecordDir, "render.stderr.txt"));
            if (render.TimedOut) throw PipelineException.Transient("render_timeout", $"Render {opts.Timeout.TotalMinutes:0} daqiqada tugamadi.");
            if (render.ExitCode != 0)
            {
                var rep = new QaReport();
                rep.Add("render_failed", QaSeverity.Error, $"`openfilm render` exit {render.ExitCode}: {Shorten(render.StderrTail, 1500)}");
                throw new EditorFixableException("render_failed", "OpenFilm render xato bilan tugadi.", rep);
            }
            await RenderCoverAsync(c, film).ConfigureAwait(false); // muqova: cover.html yoki loyiha kadri (subtitrsiz)
        } // OpenFilm Studio server va headless brauzerlar shu yerda yopiladi
        if (!File.Exists(c.FinalMp4) || new FileInfo(c.FinalMp4).Length < 10_000)
            throw PipelineException.Transient("render_no_output", "Render exit 0, lekin MP4 fayl yo'q yoki bo'sh.");
        await EnsureCoverFromVideoAsync(c).ConfigureAwait(false); // har videoda muqova bo'lishi uchun oxirgi chora
        var outputs = new StageOutputs { Files = { FileOps.Describe(c.FinalMp4, "video") } };
        if (File.Exists(c.CoverJpg)) outputs.Files.Add(FileOps.Describe(c.CoverJpg, "cover", hash: false));
        return outputs;
    }

    // ---------------------------------------------------------------- QA
    private async Task<StageOutputs> ValidateStageAsync(JobContext c, VideoScript script)
    {
        var r = new QaReport();
        var duration = c.Job.AudioDurationSec ?? Wav.ReadInfo(c.VoiceWav).Duration;
        var info = await _x.Media.ProbeAsync(c.FinalMp4, c.Ct).ConfigureAwait(false);
        var (decodeOk, decodeErr) = await _x.Media.DecodeCheckAsync(c.FinalMp4, c.Ct).ConfigureAwait(false);
        if (!decodeOk) r.Add("decode", QaSeverity.Error, $"MP4 to'liq dekodlanmadi: {Shorten(decodeErr, 400)}", fixable: false);
        QualityRules.CheckRenderedStreams(info.Duration, duration, info.HasVideo, info.HasAudio, info.Width, info.Height, c.Job.Format, r);

        var html = File.ReadAllText(c.FilmHtml);
        var mediaDur = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in FilmTimelineParser.Parse(html).Clips.Where(cl => cl.Out is null))
        {
            if (mediaDur.ContainsKey(clip.Src)) continue;
            var p = Path.Combine(c.FilmDir, clip.Src.Replace('/', Path.DirectorySeparatorChar));
            double? d = null;
            if (clip.Kind is ClipKind.Audio or ClipKind.Video && File.Exists(p))
                try { d = (await _x.Media.ProbeAsync(p, c.Ct).ConfigureAwait(false)).Duration; } catch { }
            mediaDur[clip.Src] = d;
        }
        var tl = FilmTimelineParser.Parse(html, src => mediaDur.GetValueOrDefault(src));
        QualityRules.CheckTimeline(tl, duration, r, c.Job.Format);
        var cues = VttParser.Parse(File.ReadAllText(c.VoiceVtt));
        QualityRules.CheckCutsVsCues(tl, cues, r);
        QualityRules.CheckSubtitles(cues, duration, c.SubtitleMaxChars, r);

        var notes = File.Exists(Path.Combine(c.FilmDir, "BUILD_NOTES.md")) ? File.ReadAllText(Path.Combine(c.FilmDir, "BUILD_NOTES.md")) : "";
        var intentional = System.Text.RegularExpressions.Regex.Matches(notes, @"intentionally black:\s*(?<f>[^\s,;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups["f"].Value.Trim('`', '"', '\'')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var black = await _x.Media.BlackDetectAsync(c.FinalMp4, c.Ct).ConfigureAwait(false);
        QualityRules.CheckBlack(black, tl, duration, intentional, r);

        var (maxDb, lufs) = await _x.Media.AudioLevelsAsync(c.FinalMp4, c.Ct).ConfigureAwait(false);
        QualityRules.CheckAudioLevels(maxDb, lufs, c.S.OpenFilm.LoudnessLufs, r);

        // Tanlangan kadrlar: har vizual klip o'rtasi (ko'pi bilan 14 ta)
        var times = tl.Clips.Where(cl => cl.IsVisual && cl.Length is > 0).Select(cl => cl.At + cl.Length!.Value / 2)
            .Where(t => t < info.Duration).Distinct().OrderBy(t => t).ToList();
        if (times.Count > 14) times = Enumerable.Range(0, 14).Select(i => times[i * times.Count / 14]).ToList();
        var stats = new List<(double, double, double)>();
        foreach (var t in times)
        {
            var (mean, std) = await _x.Media.FrameLumaAsync(c.FinalMp4, t, c.Ct).ConfigureAwait(false);
            stats.Add((t, mean, std));
        }
        QualityRules.CheckFrameStats(stats, r);
        r.Metrics["sampled_frames"] = stats.Count;

        if (c.S.OpenFilm.LookFullAfterRender)
        {
            await using var film = await _x.Film.OpenSessionAsync(c.Log, c.Ct).ConfigureAwait(false);
            var lf = await film.LookAsync(c.FilmDir, new[] { "--full", c.FinalMp4 }, "look-full", TimeSpan.FromMinutes(30), c.Ct).ConfigureAwait(false);
            r.Checked.Add("openfilm_look_full");
            r.Metrics["look_full_sec"] = lf.Elapsed.TotalSeconds;
            var outText = (lf.StdoutTail + "\n" + lf.StderrTail).Trim();
            CopyIfExists(lf.StdoutPath, Path.Combine(c.RecordDir, "look-full.stdout.txt"));
            if (lf.TimedOut) r.Add("look_full_timeout", QaSeverity.Warning, "openfilm look --full vaqtida tugamadi.", fixable: false);
            else if (lf.ExitCode != 0) r.Add("look_full", QaSeverity.Warning, $"openfilm look --full exit {lf.ExitCode}: {Shorten(outText, 800)}");
            else foreach (var line in LookFullFindings(outText)) r.Add("look_full_finding", QaSeverity.Warning, line);
        }

        await ReviewStyleAsync(c, times, r).ConfigureAwait(false);

        Directory.CreateDirectory(c.QaDir);
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(c.QaReportJson, json);
        File.Copy(c.QaReportJson, Path.Combine(c.RecordDir, "qa_report.json"), true);
        foreach (var kv in r.Metrics) c.StageMetrics["qa_" + kv.Key] = kv.Value;

        foreach (var w in r.Issues.Where(i => i.Severity == QaSeverity.Warning)) c.Log.Warn("QA: " + w, "Validate");
        if (!r.Passed)
        {
            var fixable = r.Errors.Any(e => e.FixableByEditor);
            var msg = string.Join("; ", r.Errors.Select(e => e.ToString()));
            if (fixable) throw new EditorFixableException("qa_errors", msg, r);
            throw PipelineException.Quality("qa_errors_unfixable", "Sifat tekshiruvi o'tmadi (montaj bilan tuzatib bo'lmaydigan): " + msg);
        }
        c.Log.Info($"Sifat tekshiruvi o'tdi ({string.Join(", ", r.Checked)}); ogohlantirishlar: {r.Issues.Count(i => i.Severity == QaSeverity.Warning)}.", "Validate");
        return new StageOutputs { Files = { FileOps.Describe(c.QaReportJson, "qa", hash: false) } };
    }

    /// <summary>`look --full` chiqishidan muammo qatorlarini ajratadi (vaqt ko'rsatilgan qatorlar).</summary>
    private static IEnumerable<string> LookFullFindings(string output) =>
        output.Split('\n').Select(l => l.Trim())
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\b(white|flash|jump|still|black)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        && System.Text.RegularExpressions.Regex.IsMatch(l, @"\d+(\.\d+)?\s*s\b|\d+:\d\d"))
            .Take(15);

    // ---------------------------------------------------------------- Arxiv
    private async Task<StageOutputs> ArchiveStageAsync(JobContext c, VideoScript script)
    {
        var tz = Scheduling.TimeZones.Find(c.S.YouTube.TimeZoneId);
        var date = Scheduling.TimeZones.ToZone(c.Job.PublishAtUtc, tz).ToString("yyyy-MM-dd");
        var baseName = $"{date}_job{c.Job.Id}_{TextNormalizer.Slug(c.Job.DisplayTitle)}";
        var videoDir = c.S.Paths.VideoArchiveDir; var audioDir = c.S.Paths.AudioArchiveDir;
        Directory.CreateDirectory(videoDir); Directory.CreateDirectory(audioDir);
        var needGb = (new FileInfo(c.FinalMp4).Length + new FileInfo(c.VoiceWav).Length) / 1e9 * 1.1 + 0.05;
        if (_x.Resources.FreeDiskGb(videoDir) < needGb) throw PipelineException.Transient("archive_disk_low", $"Arxiv diskida joy yetarli emas ({needGb:0.00} GB kerak): {videoDir}");

        // Muqova: render bosqichida tayyorlangan (eski joblarda bo'lmasa - hozir videodan). Arxivdagi MP4 ichiga rasm bo'lib qo'shiladi:
        // fayl ochilmaganda (galereya, Telegram, pleyer) shu rasm ko'rinadi; YouTube'ga thumbnail bo'lib ham yuklanadi.
        await EnsureCoverFromVideoAsync(c).ConfigureAwait(false);
        var source = c.FinalMp4;
        if (File.Exists(c.CoverJpg))
        {
            var withCover = Path.Combine(c.RenderDir, "final.cover.mp4");
            try { await _x.Media.EmbedCoverAsync(c.FinalMp4, c.CoverJpg, withCover, c.Ct).ConfigureAwait(false); source = withCover; c.Log.Info("Muqova video ichiga qo'shildi.", "Archive"); }
            catch (Exception e) when (e is not OperationCanceledException) { c.Log.Warn($"Muqova video ichiga qo'shilmadi (video muqovasiz arxivlanadi): {e.Message}", "Archive"); }
        }
        var video = await FileOps.CopyVerifiedAsync(source, Path.Combine(videoDir, baseName + ".mp4"), "video", c.Ct).ConfigureAwait(false);
        var audio = await FileOps.CopyVerifiedAsync(c.VoiceWav, Path.Combine(audioDir, baseName + ".wav"), "audio", c.Ct).ConfigureAwait(false);
        var vstem = Path.Combine(Path.GetDirectoryName(video.Path)!, Path.GetFileNameWithoutExtension(video.Path));
        var srt = await FileOps.CopyVerifiedAsync(c.VoiceSrt, vstem + ".srt", "srt", c.Ct).ConfigureAwait(false);
        var vtt = await FileOps.CopyVerifiedAsync(c.VoiceVtt, vstem + ".vtt", "vtt", c.Ct).ConfigureAwait(false);

        var thumb = vstem + ".jpg";
        try { if (File.Exists(c.CoverJpg)) File.Copy(c.CoverJpg, thumb, true); }
        catch (Exception e) { c.Log.Warn($"Thumbnail arxivga yozilmadi: {e.Message}", "Archive"); }

        var meta = new
        {
            job_id = c.Job.Id, topic = c.Job.Topic, title = c.Job.Title, description = c.Job.Description, tags = script.Tags,
            language = c.Job.Language, format = c.Job.Format.ToString(), publish_at_utc = c.Job.PublishAtUtc,
            audio_duration_sec = c.Job.AudioDurationSec, sources = script.Sources, sources_verified = script.SourcesVerified,
            unverified_claims = script.UnverifiedClaims, video = video.Path, audio = audio.Path, subtitles = srt.Path,
            integration_versions = c.Snapshot.IntegrationVersions, archived_at_utc = _x.Clock.UtcNow,
        };
        var metaPath = vstem + ".json";
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        c.Job.VideoArchivePath = video.Path; c.Job.AudioArchivePath = audio.Path; c.Job.SubtitleArchivePath = srt.Path;
        c.Job.ThumbnailPath = File.Exists(thumb) ? thumb : null;
        _x.Store.Update(c.Job);
        c.Log.Info($"Arxivlandi va SHA-256 bilan tekshirildi: {video.Path}, {audio.Path}", "Archive");
        var outputs = new StageOutputs { Files = { video, audio, srt, vtt } };
        outputs.Values["meta"] = metaPath;
        return outputs;
    }
}

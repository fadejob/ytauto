using YtAuto.Core.Domain;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Gemini;
using YtAuto.Infrastructure.Templates;
using YtAuto.Infrastructure.Voice;

namespace YtAuto.Tests;

public class VoiceCatalogTests
{
    // VoiceStudio 0.5.7 /engines/tts va /models javoblarining qisqartirilgan ko'rinishi (real sinov loglaridan).
    private const string Engines = """
{"active":"omnivoice","active_model":"k2-fsa/OmniVoice","backends":[
 {"id":"omnivoice","display_name":"VoiceStudio (k2-fsa/OmniVoice, 600+ languages)","available":true,"reason":null,"supports_cloning":true,"preset_voices":[],"default_voice":null},
 {"id":"kittentts","display_name":"KittenTTS","available":true,"reason":null,"supports_cloning":false,"preset_voices":["expr-voice-2-m","expr-voice-2-f"],"default_voice":"expr-voice-2-f"},
 {"id":"supertonic3","display_name":"Supertonic 3","available":false,"reason":"This engine's package isn't installed yet.","preset_voices":["M1","F3"],"default_voice":"M1"}
]}
""";
    private const string Models = """
{"target":"local","models":[
 {"repo_id":"k2-fsa/OmniVoice","role":"TTS","engines":["omnivoice","omnivoice-subprocess"],"installed":false},
 {"repo_id":"KittenML/kitten-tts-mini-0.8","role":"TTS","engines":["kittentts"],"installed":true},
 {"repo_id":"Systran/faster-whisper-base","role":"ASR","engines":["faster-whisper"],"installed":true}
]}
""";

    [Fact]
    public void MergesEnginesWithInstalledModels_UsableFirst()
    {
        var c = VoiceCatalogService.Parse(Engines, Models);
        Assert.Equal(3, c.Engines.Count);
        var kitten = c.Engines[0];
        Assert.Equal("kittentts", kitten.Id);
        Assert.True(kitten.Usable);
        Assert.Equal(new[] { "KittenML/kitten-tts-mini-0.8" }, kitten.InstalledModels);
        Assert.Equal("expr-voice-2-f", kitten.DefaultVoice);
        var omni = c.Engines.Single(e => e.Id == "omnivoice");
        Assert.True(omni.Available);
        Assert.False(omni.Usable); // paket bor, lekin modeli o'rnatilmagan
        Assert.True(omni.Active);
        Assert.False(c.Engines.Single(e => e.Id == "supertonic3").Usable);
        Assert.DoesNotContain(c.Engines, e => e.InstalledModels.Contains("Systran/faster-whisper-base")); // ASR TTS ro'yxatiga tushmaydi
    }
}

public class DynamicLengthTests
{
    [Fact]
    public void PromptHasNoFixedDuration_LegacyLineReplaced()
    {
        var r = new ScriptRequest { Topic = "T", Language = "en", Format = "16:9", TargetDurationSec = 0 };
        var fresh = GeminiScriptGenerator.Fill(DefaultPrompts.Script, r, "");
        Assert.Contains("no fixed duration", fresh);
        Assert.DoesNotContain("{", fresh.Replace("{fact_sheet}", ""));
        var legacy = GeminiScriptGenerator.Fill("A\n" + DefaultPrompts.LegacyLengthLine + "\nB", r, "");
        Assert.DoesNotContain("seconds of narration", legacy);
        Assert.Contains("no fixed duration", legacy);
    }

    [Fact]
    public void ValidatorAcceptsAnyReasonableLengthWhenDynamic()
    {
        VideoScript Make(int words) => new()
        {
            NarrationText = string.Join(" ", Enumerable.Repeat("word", words)) + ".", YouTubeTitle = "t", YouTubeDescription = "d",
            Scenes = new() { new() { Index = 1, Narration = string.Join(" ", Enumerable.Repeat("word", words / 2)), Visual = "v" },
                             new() { Index = 2, Narration = string.Join(" ", Enumerable.Repeat("word", words - words / 2)) + ".", Visual = "v" } },
        };
        Assert.Empty(ScriptValidator.Validate(Make(120), "en", 0));
        Assert.Empty(ScriptValidator.Validate(Make(900), "en", 0));
        Assert.Contains(ScriptValidator.Validate(Make(20), "en", 0), p => p.Contains("juda qisqa"));
    }
}

public class TemplateStoreTests
{
    [Fact]
    public async Task ImportsVideoCopy_AndDeletesOnlyInsideTemplatesDir()
    {
        using var t = new TempDir();
        var src = Path.Combine(t.Path, "my style.mp4"); File.WriteAllBytes(src, new byte[50_000]);
        var store = Path.Combine(t.Path, "templates");
        var def = await new TemplateStore(null).ImportAsync(store, src, "My style", "", CancellationToken.None);
        Assert.Equal(TemplateKind.Mp4Reference, def.Kind);
        Assert.True(def.Managed);
        Assert.StartsWith(store, def.Path);
        Assert.True(File.Exists(def.Path));
        File.Delete(src);                       // asl fayl kerak emas
        Assert.True(File.Exists(def.Path));
        TemplateStore.DeleteFiles(store, def);
        Assert.False(Directory.Exists(Path.GetDirectoryName(def.Path)));
        Assert.True(Directory.Exists(store));   // shablonlar papkasining o'zi qoladi

        var outside = new TemplateDefinition { Managed = true, Path = Path.Combine(t.Path, "other", "x.mp4") };
        Directory.CreateDirectory(Path.Combine(t.Path, "other")); File.WriteAllText(outside.Path, "keep");
        TemplateStore.DeleteFiles(store, outside); // tashqaridagi faylga tegmaydi
        Assert.True(File.Exists(outside.Path));
    }

    [Fact]
    public async Task ProjectFolderNeedsFilmHtml_AndIsCopied()
    {
        using var t = new TempDir();
        var proj = Path.Combine(t.Path, "proj"); Directory.CreateDirectory(Path.Combine(proj, "assets"));
        File.WriteAllText(Path.Combine(proj, "assets", "logo.svg"), "<svg/>");
        var store = Path.Combine(t.Path, "templates");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TemplateStore(null).ImportAsync(store, proj, "P", "", CancellationToken.None));
        File.WriteAllText(Path.Combine(proj, "film.html"), "<section></section>");
        var def = await new TemplateStore(null).ImportAsync(store, proj, "P", "", CancellationToken.None);
        Assert.Equal(TemplateKind.OpenFilmProject, def.Kind);
        Assert.True(File.Exists(Path.Combine(def.Path, "film.html")));
        Assert.True(File.Exists(Path.Combine(def.Path, "assets", "logo.svg")));
        var txt = Path.Combine(t.Path, "notes.txt"); File.WriteAllText(txt, "x");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TemplateStore(null).ImportAsync(store, txt, "N", "", CancellationToken.None));
    }
}

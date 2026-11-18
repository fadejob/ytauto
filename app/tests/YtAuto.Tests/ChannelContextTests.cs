using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Tests;

/// <summary>Bot kanal tahlilidan yozgan kontekst ssenariy/montajga to'g'ri tushadi va video chiqqanda yangilash belgisi qo'yiladi.</summary>
public class ChannelContextTests
{
    private static ChannelContextSettings Ctx(bool enabled = true) => new() { Enabled = enabled, Script = "SCRIPT-STYLE: short punchy hooks", Montage = "MONTAGE-STYLE: dark navy 2D", VoiceProfile = "v" };

    [Fact]
    public void ApplyToScript_ReplacesPlaceholder()
    {
        var t = ChannelPrompt.ApplyToScript("Topic: {topic}\n{channel_context}\nEnd", Ctx());
        Assert.Equal("Topic: {topic}\nSCRIPT-STYLE: short punchy hooks\nEnd", t);
    }

    [Fact]
    public void ApplyToScript_AppendsSection_WhenNoPlaceholder()
    {
        var t = ChannelPrompt.ApplyToScript("Topic: {topic}", Ctx());
        Assert.StartsWith("Topic: {topic}\n\n" + ChannelPrompt.ScriptHeading, t);
        Assert.Contains("SCRIPT-STYLE", t);
    }

    [Fact]
    public void ApplyToScript_Disabled_RemovesPlaceholder_AndKeepsTemplate()
    {
        Assert.Equal("A  B", ChannelPrompt.ApplyToScript("A {channel_context} B", Ctx(enabled: false)));
        Assert.Equal("Topic: {topic}", ChannelPrompt.ApplyToScript("Topic: {topic}", Ctx(enabled: false)));
        Assert.Equal("Topic: {topic}", ChannelPrompt.ApplyToScript("Topic: {topic}", null));
    }

    [Fact]
    public void ReadChannelContext_ParsesBotJson()
    {
        var c = SqliteSettingsStore.ReadChannelContext("""{"enabled":true,"script":"S","montage":"M","voiceProfile":"Kitten EN erkak","updatedAt":"2026-11-18T10:00:00Z"}""");
        Assert.True(c.Enabled);
        Assert.Equal(("S", "M", "Kitten EN erkak"), (c.Script, c.Montage, c.VoiceProfile));
        Assert.False(SqliteSettingsStore.ReadChannelContext("not json").Enabled);
        Assert.False(SqliteSettingsStore.ReadChannelContext(null).Enabled);
    }

    [Fact]
    public async Task Pipeline_PutsContextIntoScriptPromptAndEditorInstructions()
    {
        using var h = new Harness();
        h.Settings.S.Gemini.ScriptPrompt = "Write about {topic}.\n{channel_context}";
        h.Settings.S.ChannelContext = Ctx();
        h.AddJob();
        await h.Run();
        Assert.Contains("SCRIPT-STYLE: short punchy hooks", h.Script.Last!.PromptTemplate);
        Assert.DoesNotContain("{channel_context}", h.Script.Last.PromptTemplate);
        Assert.Contains(EditorInstructions.MontageHeading, h.Editor.LastInstructions);
        Assert.Contains("MONTAGE-STYLE: dark navy 2D", h.Editor.LastInstructions);
    }

    [Fact]
    public async Task Pipeline_WithoutContext_LeavesPromptAndInstructionsUnchanged()
    {
        using var h = new Harness();
        h.Settings.S.Gemini.ScriptPrompt = "Write about {topic}.";
        h.AddJob();
        await h.Run();
        Assert.Equal("Write about {topic}.", h.Script.Last!.PromptTemplate);
        Assert.DoesNotContain(EditorInstructions.MontageHeading, h.Editor.LastInstructions);
    }

    [Fact]
    public async Task RefreshStatus_BecomingPublic_RaisesOnPublishedOnce()
    {
        using var h = new Harness();
        var calls = 0;
        h.OnPublished = _ => calls++;
        var job = h.AddJob();
        await h.Run();
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Scheduled, j.Status);
        Assert.Equal(0, calls);

        h.Uploader.Public.Add(j.YouTubeVideoId!); // YouTube vaqti keldi - video public
        Assert.Equal(JobStatus.Published, await h.Runner().RefreshYouTubeStatusAsync(j, h.Log, CancellationToken.None));
        Assert.Equal(1, calls);
        await h.Runner().RefreshYouTubeStatusAsync(j, h.Log, CancellationToken.None); // yana tekshiruv - belgi takrorlanmaydi
        Assert.Equal(1, calls);
    }
}

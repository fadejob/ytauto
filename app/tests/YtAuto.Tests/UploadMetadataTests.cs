using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Data;
using Xunit;

namespace YtAuto.Tests;

/// <summary>
/// Kanalda standart tavsif/taglar bo'lsa - faqat ular (Gemini yozmaydi); bo'lmasa Gemini ssenariy (yoki foydalanuvchi matni) bilan
/// tavsif va aynan 5 ta tag yozadi. AI belgisi standart o'chiq.
/// </summary>
public class UploadMetadataTests
{
    [Fact]
    public void ScriptTags_AreCleanedAndCappedAtFive()
    {
        var s = new VideoScript { SubtitleText = "x", YouTubeTitle = "t", Tags = new() { "#AI tools", "ai TOOLS", " ", "video  editing", "<b>", "youtube", "automation", "seo", "extra" } };
        ScriptValidator.Canonicalize(s);
        Assert.Equal(new[] { "AI tools", "video editing", "b", "youtube", "automation" }, s.Tags);
    }

    [Fact]
    public void Tags_ChannelDefaultsOnly_WhenSet()
    {
        Assert.Equal(new[] { "channel", "shorts" }, JobRunner.ComposeTags(new[] { "a", "b", "c", "d", "e" }, "channel, Channel , shorts"));
    }

    [Fact]
    public void Tags_GeneratedFive_WhenNoDefaults()
    {
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, JobRunner.ComposeTags(new[] { "a", "b", "c", "d", "e", "f" }, " , "));
        Assert.Empty(JobRunner.ComposeTags(null, ""));
    }

    [Fact]
    public void Tags_StayUnderYouTube500CharLimit()
    {
        var defaults = string.Join(",", Enumerable.Range(0, 60).Select(i => $"long default tag {i:00}"));
        var t = JobRunner.ComposeTags(new[] { "first" }, defaults);
        Assert.DoesNotContain("first", t);
        Assert.True(t.Sum(x => x.Length + (x.Contains(' ') ? 2 : 0)) + t.Count - 1 <= 500);
    }

    [Fact]
    public void Description_ChannelDefaultOnly_WhenSet_CreditsAlwaysKept()
    {
        Assert.Equal("Channel links", JobRunner.ComposeDescription("From Gemini", "", "Channel links"));
        Assert.Equal("Image credits: x\n\nChannel links", JobRunner.ComposeDescription("From Gemini", "Image credits: x", "Channel links"));
        Assert.Equal("From Gemini\n\nImage credits: x", JobRunner.ComposeDescription("From Gemini", "Image credits: x", ""));
        Assert.Equal("Taglar: kanal standarti (2 ta); tavsif: kanal standarti.", JobRunner.DescribeMetadataSources(0, false, "a,b", "links"));
        Assert.Equal("Taglar: Gemini yozgan (5 ta); tavsif: Gemini yozgan.", JobRunner.DescribeMetadataSources(5, true, "", ""));
    }

    // ---------------------------------------------------------------- Gemini so'rovi

    [Fact]
    public void Schema_And_Prompt_FollowChannelDefaults()
    {
        var none = YtAuto.Infrastructure.Gemini.GeminiScriptGenerator.ScriptSchema(writeDescription: false, writeTags: false);
        Assert.Null(none["properties"]!["youtube_description"]);
        Assert.Null(none["properties"]!["tags"]);
        Assert.Equal(new[] { "subtitle_text", "youtube_title" }, none["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        var only = YtAuto.Infrastructure.Gemini.GeminiScriptGenerator.ScriptSchema(writeDescription: false, writeTags: true);
        Assert.Null(only["properties"]!["youtube_description"]);
        Assert.NotNull(only["properties"]!["tags"]);

        var p = YtAuto.Infrastructure.Gemini.GeminiScriptGenerator.MetadataInstruction(false, true);
        Assert.Contains("do NOT write youtube_description", p);
        Assert.Contains("write exactly 5 tags", p);
    }

    // ---------------------------------------------------------------- butun oqim

    [Fact]
    public async Task NoChannelDefaults_GeminiWritesDescriptionAndFiveTags()
    {
        using var h = new Harness();
        h.AddJob();
        await h.Run();
        Assert.True(h.Script.Last!.WriteDescription);
        Assert.True(h.Script.Last.WriteTags);
        Assert.StartsWith("Short explainer.", h.Uploader.Last!.Description);
        Assert.Equal(new[] { "boiling water", "steam", "physics", "science explained", "kitchen science" }, h.Uploader.Last.Tags);
    }

    [Fact]
    public async Task ChannelDefaults_GeminiIsToldNotToWrite_AndOnlyDefaultsAreUploaded()
    {
        using var h = new Harness();
        h.Settings.S.YouTube.DefaultDescription = "Subscribe for more.";
        h.Settings.S.YouTube.DefaultTags = "science, explainers";
        h.AddJob();
        await h.Run();
        Assert.False(h.Script.Last!.WriteDescription);
        Assert.False(h.Script.Last.WriteTags);
        Assert.Equal("Subscribe for more.", h.Uploader.Last!.Description);
        Assert.DoesNotContain("Short explainer.", h.Uploader.Last.Description); // model yozib yuborsa ham olinmaydi
        Assert.Equal(new[] { "science", "explainers" }, h.Uploader.Last.Tags);
    }

    [Fact]
    public async Task OwnText_NoDefaults_GeminiWritesOnlyMetadata_TextUntouched()
    {
        using var h = new Harness();
        var j = new VideoJob
        {
            Topic = "Boiling water", Language = "en", VoiceProfileName = "v", Format = VideoFormat.Landscape16x9,
            PublishAtUtc = h.Clock.UtcNow.AddDays(3), Status = JobStatus.Queued, CreatedAtUtc = h.Clock.UtcNow.AddMinutes(-10),
            ManualScript = TestScript.Narration, ManualTitle = "My own title",
        };
        h.Store.Insert(j);
        await h.Run();
        Assert.Equal(0, h.Script.Calls);                              // matn Gemini'ga yozdirilmadi
        Assert.Equal(1, h.Script.DescribeCalls);
        Assert.Equal(TestScript.Narration, h.Script.LastMeta!.Text);
        Assert.Equal("My own title", h.Store.Get(j.Id)!.Title);
        Assert.StartsWith("Own text summary.", h.Uploader.Last!.Description);
        Assert.Equal(5, h.Uploader.Last.Tags.Count);
    }

    [Fact]
    public async Task OwnText_WithDefaults_NoGeminiCallAtAll()
    {
        using var h = new Harness();
        h.Settings.S.YouTube.DefaultDescription = "Subscribe for more.";
        h.Settings.S.YouTube.DefaultTags = "science";
        h.Store.Insert(new VideoJob
        {
            Topic = "Boiling water", Language = "en", VoiceProfileName = "v", Format = VideoFormat.Landscape16x9,
            PublishAtUtc = h.Clock.UtcNow.AddDays(3), Status = JobStatus.Queued, CreatedAtUtc = h.Clock.UtcNow.AddMinutes(-10),
            ManualScript = TestScript.Narration, ManualTitle = "My own title",
        });
        await h.Run();
        Assert.Equal(0, h.Script.Calls);
        Assert.Equal(0, h.Script.DescribeCalls);
        Assert.Equal("Subscribe for more.", h.Uploader.Last!.Description);
        Assert.Equal(new[] { "science" }, h.Uploader.Last.Tags);
    }

    [Fact]
    public void SavedPrompt_GetsDescriptionAndFiveTagLines_KeepingOwnerEdits()
    {
        var old = DefaultPrompts.Script;
        foreach (var (o, n) in DefaultPrompts.ScriptLineUpdates) old = old.Replace(n, o);
        Assert.Contains("up to 15 short search tags", old);
        var edited = old.Replace("Plain words a 14-year-old understands", "Plain words an adult understands");

        var migrated = SqliteSettingsStore.MigratePrompt(edited, true);
        Assert.Contains("exactly 5 short search tags", migrated);
        Assert.DoesNotContain("tags (optional)", migrated);
        Assert.DoesNotContain("youtube_description (optional)", migrated);
        Assert.Contains("Plain words an adult understands", migrated);         // egasining o'zgartirishi qoldi
        Assert.Equal(DefaultPrompts.Script, SqliteSettingsStore.MigratePrompt(old, true));
    }

    [Fact]
    public void AiSyntheticLabel_IsOffByDefault() => Assert.False(new AppSettings().YouTube.ContainsSyntheticMedia);
}

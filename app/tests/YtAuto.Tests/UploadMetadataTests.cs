using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Data;
using Xunit;

namespace YtAuto.Tests;

/// <summary>Tavsif va taglar ssenariy bilan yoziladi (aynan 5 tag); bo'lmasa kanal standarti. AI belgisi standart o'chiq.</summary>
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
    public void Tags_ScriptFirst_ThenChannelDefaults()
    {
        var t = JobRunner.ComposeTags(new[] { "a", "b", "c", "d", "e", "f" }, "channel, B , shorts");
        Assert.Equal(new[] { "a", "b", "c", "d", "e", "channel", "shorts" }, t); // 6-chisi tashlandi, takror "B" ham
    }

    [Fact]
    public void Tags_NoneFromScript_UsesChannelDefaultsOnly()
    {
        Assert.Equal(new[] { "channel", "shorts" }, JobRunner.ComposeTags(Array.Empty<string>(), "channel,shorts"));
        Assert.Empty(JobRunner.ComposeTags(null, ""));
    }

    [Fact]
    public void Tags_StayUnderYouTube500CharLimit()
    {
        var defaults = string.Join(",", Enumerable.Range(0, 60).Select(i => $"long default tag {i:00}"));
        var t = JobRunner.ComposeTags(new[] { "first" }, defaults);
        Assert.Equal("first", t[0]);
        Assert.True(t.Sum(x => x.Length + (x.Contains(' ') ? 2 : 0)) + t.Count - 1 <= 500);
    }

    [Fact]
    public void Description_ScriptPlusChannelDefault_OrDefaultOnly()
    {
        Assert.Equal("From script\n\nChannel links", JobRunner.ComposeDescription("From script", "", "Channel links"));
        Assert.Equal("Channel links", JobRunner.ComposeDescription("", "", "Channel links"));
        Assert.Contains("faqat kanal standarti", JobRunner.DescribeMetadataSources(0, false, "a,b", "links"));
        Assert.Contains("Taglar (5 ta): ssenariydan + kanal standarti", JobRunner.DescribeMetadataSources(5, true, "a", ""));
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

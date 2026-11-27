using YtAuto.Core.Domain;
using YtAuto.Infrastructure.Commands;

namespace YtAuto.Tests;

/// <summary>Botdan tarix tozalanganda / shablon o'chirilganda kompyuterdagi fayllar: faqat arxiv ildizlari ichida o'chadi.</summary>
public class LocalJobFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ytauto-ljf-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PathSettings _p;

    public LocalJobFilesTests()
    {
        _p = new PathSettings
        {
            VideoArchiveDir = Path.Combine(_root, "video"), AudioArchiveDir = Path.Combine(_root, "audio"), WorkDir = Path.Combine(_root, "jobs"),
        };
        Directory.CreateDirectory(_p.VideoArchiveDir); Directory.CreateDirectory(_p.AudioArchiveDir); Directory.CreateDirectory(_p.WorkDir);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static string Touch(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "x"); return path; }

    [Fact]
    public void DeleteVideoSet_RemovesVideoAndSidecarsOnly()
    {
        var video = Touch(Path.Combine(_p.VideoArchiveDir, "2026-11-27_job5_topic.mp4"));
        foreach (var ext in new[] { ".srt", ".vtt", ".jpg", ".json" }) Touch(Path.ChangeExtension(video, ext));
        var other = Touch(Path.Combine(_p.VideoArchiveDir, "2026-11-27_job6_other.mp4"));

        var n = LocalJobFiles.DeleteVideoSet(_p, video);

        Assert.Equal(5, n);
        Assert.Empty(Directory.GetFiles(_p.VideoArchiveDir, "*job5*"));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void DeleteFile_RefusesPathsOutsideArchiveRoots()
    {
        var outside = Touch(Path.Combine(_root, "elsewhere", "keep.mp4"));
        var traversal = Path.Combine(_p.VideoArchiveDir, "..", "elsewhere", "keep.mp4");

        Assert.Equal(0, LocalJobFiles.DeleteFile(_p, outside));
        Assert.Equal(0, LocalJobFiles.DeleteFile(_p, traversal));
        Assert.Equal(0, LocalJobFiles.DeleteVideoSet(_p, outside));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void DeleteFile_RemovesAudioInAudioArchive()
    {
        var wav = Touch(Path.Combine(_p.AudioArchiveDir, "a.wav"));
        Assert.Equal(1, LocalJobFiles.DeleteFile(_p, wav));
        Assert.False(File.Exists(wav));
        Assert.Equal(0, LocalJobFiles.DeleteFile(_p, wav)); // yo'q fayl - xato emas
    }
}

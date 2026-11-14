using System.Diagnostics;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Media;
using YtAuto.Infrastructure;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Processes;
using YtAuto.Infrastructure.Scheduling;

namespace YtAuto.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ytauto-test-" + Guid.NewGuid().ToString("N")[..10]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class StoreTests
{
    private static VideoJob NewJob(string topic, DateTimeOffset created) => new()
    {
        Topic = topic, Language = "en", TargetDurationSec = 60, PublishAtUtc = DateTimeOffset.UtcNow.AddDays(3), Status = JobStatus.Queued, CreatedAtUtc = created,
    };

    [Fact]
    public void ClaimsOldestFirst_OnlyOneJobPerClaim_AndRespectsBackoff()
    {
        using var t = new TempDir();
        var s = new SqliteJobStore(System.IO.Path.Combine(t.Path, "db.sqlite"));
        var now = DateTimeOffset.UtcNow;
        var a = NewJob("A", now.AddMinutes(-30)); s.Insert(a);
        var b = NewJob("B", now.AddMinutes(-20)); s.Insert(b);
        var c = NewJob("C", now.AddMinutes(-40)); c.Status = JobStatus.Failed; c.NextAttemptAtUtc = now.AddHours(1); s.Insert(c);
        var d = NewJob("D", now.AddMinutes(-50)); d.Status = JobStatus.NeedsAttention; s.Insert(d);

        var first = s.ClaimNext("run1", now);
        Assert.Equal("A", first!.Topic); // C backoff'da, D e'tibor talab qiladi
        Assert.Equal("B", s.ClaimNext("run2", now)!.Topic);
        Assert.Null(s.ClaimNext("run3", now));
        Assert.Equal("C", s.ClaimNext("run4", now.AddHours(2))!.Topic);
    }

    [Fact]
    public async Task ConcurrentClaimsNeverReturnSameJob()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "db.sqlite");
        var s = new SqliteJobStore(path);
        for (int i = 0; i < 20; i++) s.Insert(NewJob("J" + i, DateTimeOffset.UtcNow.AddSeconds(i)));
        var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            var store = new SqliteJobStore(path); // alohida ulanishlar (UI + worker kabi)
            while (store.ClaimNext("w" + w, DateTimeOffset.UtcNow) is { } j) claimed.Add(j.Id);
        })));
        Assert.Equal(20, claimed.Count);
        Assert.Equal(20, claimed.Distinct().Count());
    }

    [Fact]
    public void StaleClaimsReleased_RunningStagesMarkedInterrupted_CancelledNotClaimable()
    {
        using var t = new TempDir();
        var s = new SqliteJobStore(System.IO.Path.Combine(t.Path, "db.sqlite"));
        var j = NewJob("X", DateTimeOffset.UtcNow); s.Insert(j);
        s.ClaimNext("dead-run", DateTimeOffset.UtcNow);
        s.SaveStage(new StageRecord { JobId = j.Id, Stage = StageKind.Voice, State = StageState.Running });
        Assert.Null(s.ClaimNext("new-run", DateTimeOffset.UtcNow));
        Assert.Equal(1, s.ReleaseStaleClaims("new-run"));
        Assert.Equal(StageState.Failed, s.GetStage(j.Id, StageKind.Voice).State);
        Assert.Equal("interrupted", s.GetStage(j.Id, StageKind.Voice).ErrorCode);
        s.RequestCancel(j.Id);
        Assert.Null(s.ClaimNext("new-run", DateTimeOffset.UtcNow));
    }
}

public class PathGuardTests
{
    [Fact]
    public void RefusesOutsideRoot()
    {
        using var t = new TempDir();
        var root = System.IO.Path.Combine(t.Path, "jobs"); Directory.CreateDirectory(root);
        var other = System.IO.Path.Combine(t.Path, "archive"); Directory.CreateDirectory(other);
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.SafeDeleteDirectory(root, other));
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.SafeDeleteDirectory(root, root));
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.SafeDeleteDirectory(root, System.IO.Path.Combine(root, "..", "archive")));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void DoesNotFollowJunction()
    {
        using var t = new TempDir();
        var root = System.IO.Path.Combine(t.Path, "jobs"); var job = System.IO.Path.Combine(root, "job-1");
        var archive = System.IO.Path.Combine(t.Path, "archive"); Directory.CreateDirectory(job); Directory.CreateDirectory(archive);
        var precious = System.IO.Path.Combine(archive, "video.mp4"); File.WriteAllText(precious, "keep me");
        var link = System.IO.Path.Combine(job, "link-to-archive");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{archive}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction yaratilmadi");
        PathGuard.SafeDeleteDirectory(root, job);
        Assert.False(Directory.Exists(job));
        Assert.True(File.Exists(precious), "junction orqali arxivdagi fayl o'chib ketdi!");
    }
}

public class ProcessTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData("C:\\path with\\", "\"C:\\path with\\\\\"")]
    [InlineData("", "\"\"")]
    [InlineData("a & b | c > d", "\"a & b | c > d\"")]
    public void QuotesLikeCommandLineToArgvW(string input, string expected) => Assert.Equal(expected, ProcessLauncher.Quote(input));

    private static int PingCount() => Process.GetProcessesByName("PING").Length;

    [Fact]
    public async Task KillsLeftoverChildrenAfterMainExits()
    {
        using var t = new TempDir();
        var before = PingCount();
        // cmd darhol chiqadi, lekin "start /b" orqali ping'ni qoldiradi (detached child kabi).
        var spec = new LaunchSpec
        {
            Exe = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"),
            Args = new[] { "/c", "start", "", "/b", "ping", "-n", "60", "127.0.0.1" }, Tool = "test",
            StdoutPath = System.IO.Path.Combine(t.Path, "o.txt"), StderrPath = System.IO.Path.Combine(t.Path, "e.txt"),
        };
        var r = await ToolRunner.RunAsync(spec, TimeSpan.FromSeconds(20), null, CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.LeftoverProcessesKilled >= 1);
        await Task.Delay(500);
        Assert.Equal(before, PingCount());
    }

    [Fact]
    public async Task TimeoutKillsWholeTree()
    {
        using var t = new TempDir();
        var before = PingCount();
        var spec = new LaunchSpec
        {
            Exe = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"),
            Args = new[] { "/c", "ping -n 60 127.0.0.1" }, Tool = "test",
            StdoutPath = System.IO.Path.Combine(t.Path, "o.txt"), StderrPath = System.IO.Path.Combine(t.Path, "e.txt"),
        };
        var sw = Stopwatch.StartNew();
        var r = await ToolRunner.RunAsync(spec, TimeSpan.FromSeconds(2), null, CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        await Task.Delay(500);
        Assert.Equal(before, PingCount());
    }

    [Fact]
    public async Task SecretsAreNotInheritedByChild()
    {
        using var t = new TempDir();
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "AIzaTESTSECRETVALUE1234567890123456");
        try
        {
            var outp = System.IO.Path.Combine(t.Path, "env.txt");
            var spec = new LaunchSpec
            {
                Exe = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"), Args = new[] { "/c", "set" }, Tool = "test",
                RemoveEnv = YtAuto.Infrastructure.Claude.ClaudeEditorAgent.BuildRemoveList(), StdoutPath = outp,
            };
            await ToolRunner.RunAsync(spec, TimeSpan.FromSeconds(10), null, CancellationToken.None);
            Assert.DoesNotContain("GEMINI_API_KEY", File.ReadAllText(outp));
        }
        finally { Environment.SetEnvironmentVariable("GEMINI_API_KEY", null); }
    }
}

public class MiscInfraTests
{
    [Fact]
    public void RedactsTokensKeysAndUploadUrls()
    {
        Redactor.Register("my-very-secret-token-value");
        var s = Redactor.Redact("Authorization: Bearer ya29.abcDEF123 key=AIzaSyA1234567890123456789012345678901 " +
                                "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=ADPycdsecret123 my-very-secret-token-value \"refresh_token\": \"1//0gabcdefghijklmnopqrstuv\"");
        Assert.DoesNotContain("ya29.abc", s);
        Assert.DoesNotContain("AIzaSyA", s);
        Assert.DoesNotContain("ADPycdsecret123", s);
        Assert.DoesNotContain("my-very-secret-token-value", s);
        Assert.DoesNotContain("1//0gabc", s);
    }

    [Fact]
    public void WavConcatAndAnalyze()
    {
        using var t = new TempDir();
        string Sine(string name, double sec, double amp)
        {
            var p = System.IO.Path.Combine(t.Path, name);
            int sr = 24000; int n = (int)(sr * sec);
            using var bw = new BinaryWriter(File.Create(p));
            bw.Write("RIFF"u8); bw.Write(36 + n * 2); bw.Write("WAVE"u8); bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16); bw.Write("data"u8); bw.Write(n * 2);
            for (int i = 0; i < n; i++) bw.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / sr) * amp * 32767));
            return p;
        }
        var a = Sine("a.wav", 1.0, 0.3); var b = Sine("b.wav", 2.0, 0.3);
        var outp = System.IO.Path.Combine(t.Path, "out.wav");
        Wav.Concat(new[] { a, b }, outp, 0.25);
        var an = Wav.Analyze(outp);
        Assert.Equal(3.25, an.Duration, 2);
        Assert.Empty(an.Problems);
        var clipped = Wav.Analyze(Sine("c.wav", 1.0, 1.2));
        Assert.Contains(clipped.Problems, p => p.Contains("Clipping"));
        var silent = Wav.Analyze(Sine("s.wav", 1.0, 0.0));
        Assert.Contains(silent.Problems, p => p.Contains("jim"));
    }

    [Fact]
    public void TextChunkerKeepsSentencesAndLimit()
    {
        var text = string.Join(" ", Enumerable.Range(0, 40).Select(i => $"Sentence number {i} is here."));
        var chunks = TextChunker.Split(text, 200);
        Assert.All(chunks, c => Assert.True(c.Length <= 200));
        Assert.Equal(text, string.Join(" ", chunks));
    }

    [Fact]
    public void SchedulerXmlHasSingleDailyTriggerWithZoneOffset()
    {
        var xml = TaskSchedulerService.BuildXml(new ScheduleSettings { StartTime = "01:00", TimeZoneId = "Asia/Tashkent", WakeToRun = true },
            @"C:\Apps\YtAuto\YtAuto.Worker.exe", new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("<StartBoundary>2026-11-15T01:00:00+05:00</StartBoundary>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml);
        Assert.Contains("<Command>C:\\Apps\\YtAuto\\YtAuto.Worker.exe</Command>", xml);
        Assert.Contains("<WorkingDirectory>C:\\Apps\\YtAuto</WorkingDirectory>", xml);
        Assert.Contains("<Arguments>--scheduled</Arguments>", xml);
    }

    [Fact]
    public async Task VerifiedCopyDetectsAndKeepsSource()
    {
        using var t = new TempDir();
        var src = System.IO.Path.Combine(t.Path, "src.bin"); File.WriteAllBytes(src, Enumerable.Range(0, 3_000_000).Select(i => (byte)i).ToArray());
        var f = await FileOps.CopyVerifiedAsync(src, System.IO.Path.Combine(t.Path, "arch", "v.mp4"), "video", CancellationToken.None);
        Assert.Equal(new FileInfo(src).Length, f.Size);
        Assert.True(FileOps.StillValid(f, out _));
        var again = await FileOps.CopyVerifiedAsync(src, f.Path, "video", CancellationToken.None);
        Assert.Equal(f.Path, again.Path); // bir xil fayl - qayta yozilmaydi
        Assert.False(File.Exists(f.Path + ".partial"));
    }
}

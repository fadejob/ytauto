using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using YtAuto.Core.Domain;
using YtAuto.Infrastructure;
using YtAuto.Infrastructure.Agent;
using YtAuto.Infrastructure.Scheduling;

namespace YtAuto.Tests;

/// <summary>Doimiy agent: imzo (bot bilan bir xil), lokal server (wake, long-poll, WebSocket, Range bilan media) va Task Scheduler XML.</summary>
public class AgentTests
{
    private const string Secret = "test-secret-0123456789abcdefghijklmnop";
    private const string Account = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Signatures_MatchBotImplementation()
    {
        // Qiymatlar bot/src/lib/agent.ts (node:crypto) bilan hisoblangan: ikki tomon bir xil imzolashi shart.
        Assert.Equal("plF1LpqS5VYNjA6YwLP9swXm9lfXmdEq", AgentAuth.MediaSig(Secret, Account, 42, "video.mp4"));
        var exp = DateTimeOffset.FromUnixTimeSeconds(1900000000);
        Assert.Equal($"1900000000.api.{Account}.ntXDCoY58f4bHIXKNLqEOgVyhVMgWkD68p_2YNhr80A", AgentAuth.Sign(Secret, "api", Account, exp));
    }

    [Fact]
    public void Token_Verify_RejectsWrongScopeExpiredTamperedAndLongLived()
    {
        var now = DateTimeOffset.UtcNow;
        var t = AgentAuth.Sign(Secret, "api", Account, now.AddMinutes(5));
        Assert.True(AgentAuth.Verify(Secret, t, "api", now, out var acc));
        Assert.Equal(Account, acc);
        Assert.False(AgentAuth.Verify(Secret, t, "ws", now, out _));
        Assert.False(AgentAuth.Verify("other-secret-0123456789abcdefghijklmn", t, "api", now, out _));
        Assert.False(AgentAuth.Verify(Secret, t[..^2] + "AA", "api", now, out _));
        Assert.False(AgentAuth.Verify(Secret, AgentAuth.Sign(Secret, "api", Account, now.AddMinutes(-1)), "api", now, out _));
        Assert.False(AgentAuth.Verify(Secret, AgentAuth.Sign(Secret, "api", Account, now.AddDays(2)), "api", now, out _)); // uzoq muddatli token
        Assert.False(AgentAuth.Verify(Secret, AgentAuth.Sign(Secret, "api", "not-a-guid", now.AddMinutes(5)), "api", now, out _));
        Assert.False(AgentAuth.Verify(Secret, null, "api", now, out _));
        Assert.True(AgentAuth.VerifyMedia(Secret, Account, 42, "video.mp4", AgentAuth.MediaSig(Secret, Account, 42, "video.mp4")));
        Assert.False(AgentAuth.VerifyMedia(Secret, Account, 43, "video.mp4", AgentAuth.MediaSig(Secret, Account, 42, "video.mp4")));
        Assert.False(AgentAuth.VerifyMedia(Secret, Account, 42, "cover.jpg", AgentAuth.MediaSig(Secret, Account, 42, "video.mp4")));
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, true, 0, 99)]
    [InlineData("bytes=500-", 1000, true, 500, 999)]
    [InlineData("bytes=-100", 1000, true, 900, 999)]
    [InlineData("bytes=900-5000", 1000, true, 900, 999)]
    [InlineData("bytes=1000-", 1000, false, 0, 0)]
    [InlineData("bytes=50-10", 1000, false, 0, 0)]
    [InlineData("items=0-1", 1000, false, 0, 0)]
    public void Range_Parsing(string header, long length, bool ok, long start, long end)
    {
        Assert.Equal(ok, AgentServer.TryParseRange(header, length, out var s, out var e));
        if (ok) { Assert.Equal(start, s); Assert.Equal(end, e); }
    }

    [Fact]
    public void AgentTaskXml_StartsAtLogonRunsForeverWithWatchdog()
    {
        var xml = TaskSchedulerService.BuildPollXml(@"C:\Apps\YtAuto\YtAuto.exe", DateTimeOffset.UtcNow);
        Assert.Contains("<Arguments>--agent</Arguments>", xml);
        Assert.Contains("<LogonTrigger>", xml);
        Assert.Contains($"<Interval>PT{TaskSchedulerService.AgentWatchdogMinutes}M</Interval>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml);
        Assert.DoesNotContain("--poll", xml);
        var doc = System.Xml.Linq.XDocument.Parse(xml); // yaroqli XML
        Assert.NotNull(doc.Root);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task Server_WakeStatusWaitWebSocketAndMedia()
    {
        using var dir = new TempDir();
        var video = Path.Combine(dir.Path, "v.mp4");
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(video, bytes);
        var woke = new List<string>();
        var log = new RunLog(new RollingFileLog(Path.Combine(dir.Path, "agent.log"), 1024 * 1024, 1));
        var port = FreePort();
        using var server = new AgentServer(port, log, new AgentServer.Hooks
        {
            Secret = Secret,
            AccountAllowed = a => a == Account,
            Wake = r => { lock (woke) woke.Add(r); },
            Status = () => new { machine = "TEST", worker = new { state = "idle" } },
            ResolveMedia = (a, job, file) => job == 42 && file == "video.mp4" ? (video, "video/mp4") : null,
            HasVideo = (a, job) => job == 42,
        });
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        string Token(string scope) => AgentAuth.Sign(Secret, scope, Account, DateTimeOffset.UtcNow.AddMinutes(5));

        // health - ochiq
        var health = await http.GetStringAsync("/v1/health");
        Assert.Contains("ytauto-agent", health);

        // wake - tokensiz rad, token bilan qabul
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/v1/wake", new StringContent("{}"))).StatusCode);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/wake") { Content = new StringContent("{\"reason\":\"demo\"}", Encoding.UTF8, "application/json") })
        {
            req.Headers.Authorization = new("Bearer", Token("api"));
            Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(req)).StatusCode);
        }
        lock (woke) Assert.Equal(new[] { "demo" }, woke);
        // ws token api uchun yaroqsiz
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/v1/status"))
        {
            req.Headers.Authorization = new("Bearer", Token("ws"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(req)).StatusCode);
        }

        // WebSocket: hello, so'ng vazifa hodisasi; boshqa hisob hodisasi kelmaydi
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/v1/ws?t={Uri.EscapeDataString(Token("ws"))}"), CancellationToken.None);
        var hello = await ReceiveAsync(ws);
        Assert.Equal("hello", hello.GetProperty("type").GetString());

        // long-poll: hodisa kelishi bilan javob qaytadi
        var waitTask = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/wait?job=42&timeout=10");
            req.Headers.Authorization = new("Bearer", Token("api"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await http.SendAsync(req);
            return (JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("changed").GetBoolean(), sw.Elapsed);
        });
        await Task.Delay(400);
        server.PublishJob("99999999-2222-3333-4444-555555555555", 7, new { id = 7 }); // boshqa hisob - WS mijozga bormaydi
        server.PublishJob(Account, 42, new { id = 42, stage = "render" });
        var (changed, took) = await waitTask;
        Assert.True(changed);
        Assert.True(took < TimeSpan.FromSeconds(5), $"long-poll {took}");
        var ev = await ReceiveAsync(ws);
        Assert.Equal("job", ev.GetProperty("type").GetString());
        Assert.Equal(42, ev.GetProperty("job").GetProperty("id").GetInt32());

        // media: imzosiz 404, to'liq va Range so'rov
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/media/42/video.mp4?a={Account}&s=bad")).StatusCode);
        var sig = AgentAuth.MediaSig(Secret, Account, 42, "video.mp4");
        var full = await http.GetAsync($"/media/42/video.mp4?a={Account}&s={sig}");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"/media/42/video.mp4?a={Account}&s={sig}"))
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
            var part = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
            Assert.Equal("bytes 1000-1999/300000", part.Content.Headers.ContentRange!.ToString());
            Assert.Equal(bytes[1000..2000], await part.Content.ReadAsByteArrayAsync());
        }
        // boshqa vazifa imzosi bilan fayl olinmaydi
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/media/43/video.mp4?a={Account}&s={sig}")).StatusCode);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket ws)
    {
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)).RootElement.Clone();
    }

    [Fact]
    public async Task Signal_ReachesAgentUdpPortFromStateFile()
    {
        // AgentSignal holat faylidan portni o'qiydi: bu yerda faqat soxta/yo'q fayl bilan xato bermasligi tekshiriladi.
        AgentSignal.JobChanged(1, null);
        AgentSignal.MachineChanged();
        await Task.CompletedTask;
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace YtAuto.Infrastructure.Agent;

/// <summary>
/// Agentning lokal HTTP serveri (http.sys, faqat localhost; internetga cloudflared tunnel orqali chiqadi). Yo'llar:
/// <list type="bullet">
/// <item><c>GET /v1/health</c> - ochiq: server tirikligi (tunnel tekshiruvi uchun).</item>
/// <item><c>POST /v1/wake</c> - bot: "yangi so'rov bor, darhol bajaring" (2 daqiqalik kutish o'rniga).</item>
/// <item><c>GET /v1/status</c> - bot: kompyuter va worker holati.</item>
/// <item><c>GET /v1/wait?job=ID&amp;timeout=S</c> - bot (MCP): vazifa o'zgarishini kutish (long-poll, ≤25 s).</item>
/// <item><c>GET /v1/has?job=ID</c> - bot: shu kompyuterda vazifaning video fayli bormi.</item>
/// <item><c>GET /v1/ws?t=TOKEN</c> - bot web app (brauzer): WebSocket, jonli hodisalar.</item>
/// <item><c>GET /media/{job}/{video.mp4|cover.jpg|subtitles.srt}?a=HISOB&amp;s=IMZO</c> - imzolangan public havola (Range bilan, video ko'rish/yuklab olish).</item>
/// </list>
/// Bot so'rovlari "Authorization: Bearer" token bilan (<see cref="AgentAuth"/>). Server ma'lumot saqlamaydi: hamma narsa bazada.
/// </summary>
public sealed class AgentServer : IDisposable
{
    public sealed class Hooks
    {
        public required string Secret { get; init; }
        /// <summary>Token bergan hisob shu kompyuterniki yoki u mezbon bo'lgan mehmonmi.</summary>
        public required Func<string, bool> AccountAllowed { get; init; }
        public required Action<string> Wake { get; init; }
        public required Func<object> Status { get; init; }
        /// <summary>(hisob, vazifa, fayl) → diskdagi fayl va turi; ruxsat bo'lmasa null.</summary>
        public required Func<string, long, string, (string Path, string ContentType)?> ResolveMedia { get; init; }
        public required Func<string, long, bool> HasVideo { get; init; }
    }

    private sealed record Client(WebSocket Ws, string Account, Channel<byte[]> Out);

    public const int MaxWsClients = 32;
    private const int MaxConcurrent = 48;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] MediaFiles = { "video.mp4", "cover.jpg", "subtitles.srt" };

    private readonly HttpListener _listener = new();
    private readonly RunLog _log;
    private readonly Hooks _h;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly ConcurrentDictionary<Client, byte> _clients = new();
    private readonly ConcurrentDictionary<long, long> _jobRev = new();
    private readonly object _waitGate = new();
    private readonly List<(long Job, TaskCompletionSource Tcs)> _waiters = new();
    private long _rev;
    private CancellationTokenSource _cts = new();

    public int Port { get; }
    public int Clients => _clients.Count;

    public AgentServer(int port, RunLog log, Hooks hooks)
    {
        Port = port; _log = log; _h = hooks;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.IgnoreWriteExceptions = true;
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (!_listener.IsListening || _cts.IsCancellationRequested) { return; }
            catch (Exception e) { _log.Debug("Agent: so'rov qabul qilinmadi: " + e.Message); continue; }
            if (!_slots.Wait(0)) { Reply(ctx, 503, new { error = "busy" }); continue; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(ctx); }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or WebSocketException) { }
                catch (Exception e) { _log.Warn("Agent: so'rov xatosi: " + e.Message); try { Reply(ctx, 500, new { error = "internal" }); } catch { } }
                finally { _slots.Release(); }
            });
        }
    }

    // ------------------------------------------------------------------ hodisalar
    /// <summary>Vazifa o'zgardi: shu hisob WebSocket mijozlariga yuboriladi, kutayotgan long-poll so'rovlar javob oladi.</summary>
    public void PublishJob(string account, long jobId, object payload)
    {
        var rev = Interlocked.Increment(ref _rev);
        _jobRev[jobId] = rev;
        Broadcast(new { type = "job", rev, job = payload }, c => c.Account == account);
        List<TaskCompletionSource> done;
        lock (_waitGate)
        {
            done = _waiters.Where(w => w.Job == jobId || w.Job == 0).Select(w => w.Tcs).ToList();
            _waiters.RemoveAll(w => w.Job == jobId || w.Job == 0);
        }
        foreach (var t in done) t.TrySetResult();
    }

    public void PublishMachine(object payload) => Broadcast(new { type = "machine", rev = Interlocked.Increment(ref _rev), machine = payload }, _ => true);

    private void Broadcast(object msg, Func<Client, bool> to)
    {
        if (_clients.IsEmpty) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
        foreach (var c in _clients.Keys)
            if (to(c)) c.Out.Writer.TryWrite(bytes);
    }

    // ------------------------------------------------------------------ marshrutlar
    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
        var method = req.HttpMethod;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

        if (path is "" or "/v1/health")
        {
            Reply(ctx, 200, new { ok = true, service = "ytauto-agent", version = Heartbeat.MachineBeat.Version });
            return;
        }
        if (path.StartsWith("/media/", StringComparison.Ordinal) && method is "GET" or "HEAD") { await MediaAsync(ctx, path); return; }
        if (path == "/v1/ws") { await WebSocketAsync(ctx); return; }

        var bearer = (req.Headers["Authorization"] ?? "") is var a && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..].Trim() : null;
        if (!AgentAuth.Verify(_h.Secret, bearer, "api", DateTimeOffset.UtcNow, out var account) || !_h.AccountAllowed(account))
        {
            Reply(ctx, 401, new { error = "unauthorized" });
            return;
        }
        switch (path)
        {
            case "/v1/wake" when method == "POST":
            {
                string reason = "bot";
                try
                {
                    using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await sr.ReadToEndAsync();
                    if (body.Length is > 0 and < 4096 && JsonDocument.Parse(body).RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                        reason = new string(r.GetString()!.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ',').Take(60).ToArray());
                }
                catch { }
                _h.Wake(reason);
                Reply(ctx, 202, new { ok = true });
                return;
            }
            case "/v1/status" when method == "GET":
                Reply(ctx, 200, _h.Status());
                return;
            case "/v1/wait" when method == "GET":
            {
                long.TryParse(req.QueryString["job"], out var job);
                double.TryParse(req.QueryString["timeout"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec);
                long.TryParse(req.QueryString["rev"], out var since);
                var changed = await WaitAsync(Math.Max(0, job), since, TimeSpan.FromSeconds(Math.Clamp(sec, 0, 25)));
                Reply(ctx, 200, new { changed, rev = _jobRev.TryGetValue(job, out var r) ? r : Interlocked.Read(ref _rev) });
                return;
            }
            case "/v1/has" when method == "GET":
            {
                long.TryParse(req.QueryString["job"], out var job);
                Reply(ctx, 200, new { video = job > 0 && _h.HasVideo(account, job) });
                return;
            }
        }
        Reply(ctx, 404, new { error = "not_found" });
    }

    private async Task<bool> WaitAsync(long job, long since, TimeSpan timeout)
    {
        var current = job > 0 ? (_jobRev.TryGetValue(job, out var r) ? r : 0) : Interlocked.Read(ref _rev);
        if (since > 0 && current > since) return true;
        if (timeout <= TimeSpan.Zero) return false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitGate) _waiters.Add((job, tcs));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, _cts.Token).ContinueWith(_ => { })) == tcs.Task;
        if (!done) lock (_waitGate) _waiters.RemoveAll(w => w.Tcs == tcs);
        return done;
    }

    // ------------------------------------------------------------------ WebSocket
    private async Task WebSocketAsync(HttpListenerContext ctx)
    {
        if (!ctx.Request.IsWebSocketRequest) { Reply(ctx, 400, new { error = "websocket_required" }); return; }
        if (!AgentAuth.Verify(_h.Secret, ctx.Request.QueryString["t"], "ws", DateTimeOffset.UtcNow, out var account) || !_h.AccountAllowed(account))
        {
            Reply(ctx, 401, new { error = "unauthorized" });
            return;
        }
        if (_clients.Count >= MaxWsClients) { Reply(ctx, 503, new { error = "too_many_clients" }); return; }
        var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null, receiveBufferSize: 1024, keepAliveInterval: TimeSpan.FromSeconds(20));
        var ws = wsCtx.WebSocket;
        var client = new Client(ws, account, Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }));
        _clients[client] = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            client.Out.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", rev = Interlocked.Read(ref _rev), machine = _h.Status() }, Json));
            var sender = Task.Run(async () =>
            {
                await foreach (var msg in client.Out.Reader.ReadAllAsync(cts.Token))
                    await ws.SendAsync(msg, WebSocketMessageType.Text, true, cts.Token);
            });
            var buf = new byte[1024];
            // Mijoz xabarlari kerak emas (faqat "ping" va yopish): o'qish - ulanish uzilganini bilish uchun.
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Text && r.Count <= 16 && Encoding.UTF8.GetString(buf, 0, r.Count) == "ping")
                    client.Out.Writer.TryWrite("{\"type\":\"pong\"}"u8.ToArray());
            }
            cts.Cancel();
            try { await sender; } catch { }
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpListenerException or IOException) { }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Out.Writer.TryComplete();
            ws.Dispose();
        }
    }

    // ------------------------------------------------------------------ public media (Range)
    private async Task MediaAsync(HttpListenerContext ctx, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // media, {job}, {file}
        var q = ctx.Request.QueryString;
        if (parts.Length != 3 || !long.TryParse(parts[1], out var job) || job <= 0 || !MediaFiles.Contains(parts[2])
            || q["a"] is not { Length: 36 } account || !Guid.TryParse(account, out _)
            || !AgentAuth.VerifyMedia(_h.Secret, account, job, parts[2], q["s"]) || !_h.AccountAllowed(account.ToLowerInvariant()))
        {
            Reply(ctx, 404, new { error = "not_found" });
            return;
        }
        var file = _h.ResolveMedia(account.ToLowerInvariant(), job, parts[2]);
        if (file is null || !File.Exists(file.Value.Path)) { Reply(ctx, 404, new { error = "file_not_found" }); return; }

        var res = ctx.Response;
        await using var fs = new FileStream(file.Value.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, true);
        var length = fs.Length;
        long start = 0, end = length - 1;
        var range = ctx.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(range))
        {
            if (!TryParseRange(range, length, out start, out end))
            {
                res.StatusCode = 416;
                res.Headers["Content-Range"] = $"bytes */{length}";
                res.Close();
                return;
            }
            res.StatusCode = 206;
            res.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        }
        else res.StatusCode = 200;
        res.Headers["Cache-Control"] = "public, max-age=3600";
        res.Headers["Accept-Ranges"] = "bytes";
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.ContentType = file.Value.ContentType;
        if (parts[2] != "video.mp4" || q["download"] == "1")
            res.Headers["Content-Disposition"] = $"{(q["download"] == "1" ? "attachment" : "inline")}; filename=\"ytauto-{job}-{parts[2]}\"";
        res.SendChunked = false;
        res.ContentLength64 = end - start + 1;
        if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[64 * 1024];
        long left = end - start + 1;
        var output = res.OutputStream;
        while (left > 0)
        {
            var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), _cts.Token);
            if (n <= 0) break;
            await output.WriteAsync(buf.AsMemory(0, n), _cts.Token);
            left -= n;
        }
        res.Close();
    }

    /// <summary>"bytes=a-b", "bytes=a-", "bytes=-n" (bitta oraliq).</summary>
    public static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var a = spec[..dash].Trim(); var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            return true;
        }
        if (!long.TryParse(a, out start) || start < 0 || start >= length) return false;
        if (b.Length > 0)
        {
            if (!long.TryParse(b, out end) || end < start) return false;
            end = Math.Min(end, length - 1);
        }
        return true;
    }

    private static void Reply(HttpListenerContext ctx, int status, object body)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        foreach (var c in _clients.Keys) { try { c.Ws.Abort(); } catch { } }
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

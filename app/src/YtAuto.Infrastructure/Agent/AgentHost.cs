using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Infrastructure.Agent;

/// <summary>
/// Doimiy agent (<c>YtAuto.exe --agent</c>, Windows'ga kirilganda Task Scheduler ishga tushiradi): kompyuterni "real server"ga aylantiradi.
/// <list type="bullet">
/// <item>Lokal HTTP/WebSocket server (<see cref="AgentServer"/>) + Cloudflare quick tunnel (<see cref="TunnelSupervisor"/>). Tunnel manzili
/// o'zgarsa (kompyuter qayta yonganda, tunnel uzilganda) darhol bazaga yoziladi - bot doim to'g'ri manzilga so'rov yuboradi.</item>
/// <item>Bot so'rov yaratganda agentni "uyg'otadi" (POST /v1/wake): worker 2 daqiqa kutmasdan darhol ishga tushadi.</item>
/// <item>Worker (alohida jarayon) har bosqichda UDP signal beradi - agent botga WebSocket orqali jonli holat yuboradi.</item>
/// <item>Bo'sh paytda minimal resurs: og'ir ish yo'q, 30 soniyada bitta yengil yurak urishi, past ustuvorlik va EcoQoS, xotira qisqartiriladi.
/// Og'ir ish (video) alohida worker jarayonida - tugagach xotira to'liq bo'shaydi.</item>
/// </list>
/// Agent ma'lumot saqlamaydi: navbat, so'rovlar va holatlar bazada (agent o'chsa ham hech narsa yo'qolmaydi).
/// </summary>
public static class AgentHost
{
    public const string MutexName = @"Global\YtAuto.Agent";
    public const string WorkerMutexName = @"Global\YtAuto.Worker";
    private const string PortKey = "agent.port";
    private const int DefaultPort = 48731;
    private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PendingSpawnMin = TimeSpan.FromMinutes(3);

    /// <summary>Agent ishlayaptimi (shu foydalanuvchi sessiyasida).</summary>
    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>Worker jarayoni (har qanday rejim) ishlayaptimi: mutex'ni egallamasdan, faqat borligini tekshiradi.</summary>
    public static bool WorkerRunning()
    {
        if (!Mutex.TryOpenExisting(WorkerMutexName, out var m)) return false;
        m.Dispose();
        return true;
    }

    public static async Task<int> RunAsync(AppServices svc, string exe, CancellationToken ct)
    {
        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 3; // agent allaqachon ishlayapti (Task Scheduler qo'riqchisi har 5 daqiqada tekshiradi)
        try
        {
            var account = Accounts.DeviceAccount.Load(svc.LocalDb);
            if (!svc.IsOnline || account is null)
            {
                svc.Log.Info("Agent: hisob sozlanmagan yoki online baza yo'q — agent ishlamaydi.");
                return 0;
            }
            Lighten();
            var host = new Host(svc, exe, account.AccountId.ToString().ToLowerInvariant());
            return await host.RunAsync(ct);
        }
        finally { try { mutex.ReleaseMutex(); } catch { } }
    }

    private sealed class Host
    {
        private readonly AppServices _svc;
        private readonly string _exe;
        private readonly string _account;
        private readonly string _machine = Environment.MachineName;
        private readonly string _secret;
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private readonly Channel<string> _wake = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<long, (Guid? Scope, DateTime Due)> _dirty = new();
        private readonly SemaphoreSlim _dirtySignal = new(0);
        private volatile string[] _guests = Array.Empty<string>();
        private AgentServer? _server;
        private TunnelSupervisor? _tunnel;
        private Process? _child;
        private volatile bool _pendingWake;
        private DateTime _lastSpawn = DateTime.MinValue, _lastPendingSpawn = DateTime.MinValue;
        private long _lastJobId;
        private string? _lastJobText;
        private bool _schemaOk = true;

        public Host(AppServices svc, string exe, string account)
        {
            _svc = svc; _exe = exe; _account = account;
            _secret = AgentAuth.LoadOrCreateSecret(svc.Secrets);
        }

        private PostgresJobStore Pg => (PostgresJobStore)_svc.Store;

        public async Task<int> RunAsync(CancellationToken ct)
        {
            RefreshGuests();
            var port = StartServer();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            WriteState(port, udpPort);
            _svc.Log.Info($"Agent ishga tushdi: lokal server http://localhost:{port}, signal porti {udpPort}.");

            Beat(null);
            _tunnel = new TunnelSupervisor(port, _svc.Log, _svc.Http, url =>
            {
                Beat(url);
                _server?.PublishMachine(Status());
            });

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new[]
            {
                Task.Run(() => _tunnel.RunAsync(stop.Token)),
                Task.Run(() => UdpLoopAsync(udp, stop.Token)),
                Task.Run(() => DirtyLoopAsync(stop.Token)),
                Task.Run(() => WakeLoopAsync(stop.Token)),
                Task.Run(() => TimerLoopAsync(stop.Token)),
            };
            Wake("startup"); // kompyuter yoqilgandan beri yig'ilgan so'rovlar darhol bajariladi
            var exit = 0;
            try
            {
                var done = await Task.WhenAny(tasks);
                if (!ct.IsCancellationRequested)
                {
                    // Ichki tsikl kutilmaganda tugadi: xato kodi bilan chiqamiz - Task Scheduler qo'riqchisi (5 daqiqada) qayta ko'taradi.
                    _svc.Log.Error("Agent ichki tsikli to'xtadi: " + (done.Exception?.GetBaseException().ToString() ?? "sababsiz"));
                    exit = 1;
                }
            }
            finally
            {
                stop.Cancel();
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
                try { Pg.AgentStopped(_machine); } catch { }
                try { File.Delete(AgentSignal.StateFile); } catch { }
                _server?.Dispose();
                _svc.Log.Info("Agent to'xtadi.");
            }
            return exit;
        }

        // ------------------------------------------------------------------ server
        private int StartServer()
        {
            var hooks = new AgentServer.Hooks
            {
                Secret = _secret,
                AccountAllowed = a => a == _account || _guests.Contains(a),
                Wake = Wake,
                Status = Status,
                ResolveMedia = ResolveMedia,
                HasVideo = (a, job) => ResolveMedia(a, job, "video.mp4") is { } f && File.Exists(f.Path),
            };
            var saved = int.TryParse(_svc.LocalDb.GetSetting(PortKey), out var p) ? p : DefaultPort;
            var candidates = new[] { saved }.Concat(Enumerable.Range(DefaultPort, 30)).Distinct();
            foreach (var port in candidates)
            {
                var s = new AgentServer(port, _svc.Log, hooks);
                try
                {
                    s.Start();
                    _server = s;
                    if (port != saved) _svc.LocalDb.SetSetting(PortKey, port.ToString());
                    return port;
                }
                catch (HttpListenerException) { s.Dispose(); }
            }
            throw new InvalidOperationException("Agent uchun bo'sh port topilmadi (48731-48760).");
        }

        private object Status()
        {
            var busy = WorkerRunning();
            return new
            {
                machine = _machine, version = Heartbeat.MachineBeat.Version, startedAt = _started, tunnel = _tunnel?.Url, tunnelError = _tunnel?.Error,
                worker = new { state = busy ? "busy" : "idle", jobId = busy && _lastJobId > 0 ? _lastJobId : (long?)null, detail = busy ? _lastJobText : null },
                live = _server?.Clients ?? 0,
            };
        }

        private (string Path, string ContentType)? ResolveMedia(string account, long jobId, string file)
        {
            Guid? scope = null;
            if (account != _account)
            {
                if (!_guests.Contains(account) || !Guid.TryParse(account, out var g)) return null;
                scope = g;
            }
            VideoJob? job;
            using (Pg.UseScope(scope)) job = _svc.Store.Get(jobId);
            if (job is null) return null;
            var (path, types) = file switch
            {
                "video.mp4" => (job.VideoArchivePath, new Dictionary<string, string> { [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mov"] = "video/quicktime", [".mkv"] = "video/x-matroska" }),
                "cover.jpg" => (job.ThumbnailPath, new Dictionary<string, string> { [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp" }),
                "subtitles.srt" => (job.SubtitleArchivePath, new Dictionary<string, string> { [".srt"] = "application/x-subrip; charset=utf-8", [".vtt"] = "text/vtt; charset=utf-8" }),
                _ => (null, new Dictionary<string, string>()),
            };
            if (string.IsNullOrWhiteSpace(path) || !types.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out var type)) return null;
            // Yo'l bazadan keladi: faqat YtAuto papkalaridagi fayllar beriladi (bazadagi yo'l bilan ixtiyoriy faylni o'qitib bo'lmaydi).
            var s = _svc.Settings.Load().Paths;
            var roots = new[] { s.VideoArchiveDir, s.AudioArchiveDir, s.WorkDir, s.TemplatesDir, _svc.Paths.RecordsDir }.Where(r => !string.IsNullOrWhiteSpace(r));
            if (!roots.Any(r => PathGuard.IsUnder(r, path)) || PathGuard.IsReparsePoint(path)) return null;
            return (path, type);
        }

        // ------------------------------------------------------------------ bazaga yurak urishi
        private void Beat(string? url)
        {
            try
            {
                var ok = Pg.AgentBeat(_machine, url ?? _tunnel?.Url, _secret, Heartbeat.MachineBeat.Version);
                if (!ok && _schemaOk) _svc.Log.Warn("Agent: online baza sxemasi eski (v7 kerak) — tunnel manzili saqlanmadi. Botda «npm run db:migrate» bajaring.");
                _schemaOk = ok;
            }
            catch (Exception e) { _svc.Log.Debug("Agent: yurak urishi yozilmadi: " + e.Message); }
        }

        private void RefreshGuests()
        {
            try { _guests = Pg.HostedAccounts().Select(g => g.Id.ToString().ToLowerInvariant()).ToArray(); }
            catch (Exception e) { _svc.Log.Debug("Agent: mehmon hisoblari o'qilmadi: " + e.Message); }
        }

        private void WriteState(int http, int udp)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AgentSignal.StateFile)!);
                var tmp = AgentSignal.StateFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new { http, udp, pid = Environment.ProcessId, startedAt = _started }));
                File.Move(tmp, AgentSignal.StateFile, true);
            }
            catch (Exception e) { _svc.Log.Warn("Agent holat fayli yozilmadi: " + e.Message); }
        }

        // ------------------------------------------------------------------ uyg'otish → worker
        private void Wake(string reason) => _wake.Writer.TryWrite(reason);

        private async Task WakeLoopAsync(CancellationToken ct)
        {
            while (await _wake.Reader.WaitToReadAsync(ct))
            {
                // Bir nechta so'rov ketma-ket kelsa (masalan bir nechta mavzu) bitta worker ishga tushadi.
                await Task.Delay(800, ct);
                var reasons = new HashSet<string>();
                while (_wake.Reader.TryRead(out var r)) reasons.Add(r);
                SpawnWorker(string.Join(",", reasons));
            }
        }

        private void SpawnWorker(string reason)
        {
            if (_child is { HasExited: false } || WorkerRunning())
            {
                // Worker band: so'rov u tugagach bajariladi (worker video orasidagi tanaffusda ham "hozir chiqarish"ni tekshiradi).
                _pendingWake = true;
                return;
            }
            _pendingWake = false;
            try
            {
                var p = Process.Start(new ProcessStartInfo(_exe, "--poll") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_exe)! });
                if (p is null) return;
                _child?.Dispose();
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) =>
                {
                    _server?.PublishMachine(Status());
                    if (_pendingWake) Wake("after-worker");
                };
                _child = p;
                _lastSpawn = DateTime.UtcNow;
                _svc.Log.Debug($"Agent: worker ishga tushirildi ({reason}), pid {p.Id}.");
            }
            catch (Exception e) { _svc.Log.Warn("Agent: workerni ishga tushirib bo'lmadi: " + e.Message); }
        }

        // ------------------------------------------------------------------ taymerlar
        private async Task TimerLoopAsync(CancellationToken ct)
        {
            DateTime lastPending = DateTime.UtcNow, lastGuests = DateTime.UtcNow, lastTrim = DateTime.UtcNow;
            bool wasBusy = false;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(BeatEvery, ct); } catch (OperationCanceledException) { return; }
                Beat(null);
                var now = DateTime.UtcNow;
                var busy = _child is { HasExited: false } || WorkerRunning();
                if (busy != wasBusy) { _server?.PublishMachine(Status()); wasBusy = busy; }
                if (!busy && _pendingWake) Wake("pending");

                // Zaxira: uyg'otish yetib kelmagan bo'lsa ham (masalan tunnel qayta ulanayotgan payt) so'rovlar bir daqiqada topiladi.
                if (now - lastPending >= PendingEvery && !busy)
                {
                    lastPending = now;
                    try
                    {
                        if (now - _lastPendingSpawn >= PendingSpawnMin && Pg.HasPendingRemoteWork(_machine))
                        {
                            _lastPendingSpawn = now;
                            Wake("pending-db");
                        }
                    }
                    catch (Exception e) { _svc.Log.Debug("Agent: so'rovlar tekshirilmadi: " + e.Message); }
                }
                // Uy ishlari (YouTube holati, shablonlar, jadval, dasturlar holati) - avvalgi --poll kabi, lekin 10 daqiqada bir.
                if (!busy && now - _lastSpawn >= PollEvery) Wake("periodic");
                if (now - lastGuests >= TimeSpan.FromMinutes(5)) { lastGuests = now; RefreshGuests(); }
                if (!busy && now - lastTrim >= TimeSpan.FromMinutes(10)) { lastTrim = now; Lighten(); }
            }
        }

        // ------------------------------------------------------------------ worker signallari → jonli hodisalar
        private async Task UdpLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }
                if (r.Buffer.Length is 0 or > 512) continue;
                try
                {
                    using var doc = JsonDocument.Parse(r.Buffer);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("j", out var j) && j.TryGetInt64(out var jobId) && jobId > 0)
                    {
                        Guid? scope = root.TryGetProperty("a", out var a) && Guid.TryParse(a.GetString(), out var g) ? g : null;
                        // Bir vazifa bo'yicha ko'p signal (hodisa + bosqich + yangilash) bitta o'qishga birlashadi.
                        if (_dirty.TryAdd(jobId, (scope, DateTime.UtcNow.AddMilliseconds(250)))) _dirtySignal.Release();
                    }
                    else if (root.TryGetProperty("m", out _)) _server?.PublishMachine(Status());
                }
                catch (JsonException) { }
            }
        }

        private async Task DirtyLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _dirtySignal.WaitAsync(ct); } catch (OperationCanceledException) { return; }
                foreach (var (jobId, (scope, due)) in _dirty.ToArray())
                {
                    var wait = due - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    _dirty.TryRemove(jobId, out _);
                    PublishJob(jobId, scope);
                }
            }
        }

        private static readonly string[] StageNames = { "queue", "script", "voice", "align", "edit", "render", "validate", "archive", "upload" };

        private void PublishJob(long jobId, Guid? scope)
        {
            if (_server is null) return;
            var account = scope?.ToString().ToLowerInvariant() ?? _account;
            if (account != _account && !_guests.Contains(account)) return;
            AgentJobInfo? j;
            try { using (Pg.UseScope(scope)) j = _svc.Store.JobLive(jobId); }
            catch (Exception e) { _svc.Log.Debug($"Agent: #{jobId} holati o'qilmadi: " + e.Message); return; }
            if (j is null) return;
            var stage = j.RunningStage ?? j.DoneStage;
            if (j.Claimed)
            {
                _lastJobId = j.Id;
                _lastJobText = $"{(j.RunningStage is int s && s < StageNames.Length ? StageNames[s] : "ish")}: {j.Topic}";
            }
            _server.PublishJob(account, j.Id, new
            {
                id = j.Id, status = j.Status, topic = j.Topic, kind = j.Kind, claimed = j.Claimed,
                stage = stage is int st && st < StageNames.Length ? StageNames[st] : null, stageRunning = j.RunningStage is not null,
                doneStage = j.DoneStage, message = j.LastMessage, level = j.LastLevel, at = j.LastAt, hasVideo = j.HasVideo, youtubeId = j.YouTubeId,
            });
        }
    }

    // ------------------------------------------------------------------ minimal resurs
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>Past ustuvorlik, Windows 11 "samaradorlik rejimi" (EcoQoS), keraksiz xotirani OS'ga qaytarish.</summary>
    private static void Lighten()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            if (self.PriorityClass != ProcessPriorityClass.BelowNormal) self.PriorityClass = ProcessPriorityClass.BelowNormal;
            var t = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = 1 }; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
            SetProcessInformation(self.Handle, 4 /* ProcessPowerThrottling */, ref t, Marshal.SizeOf<PowerThrottling>());
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        }
        catch { }
    }
}

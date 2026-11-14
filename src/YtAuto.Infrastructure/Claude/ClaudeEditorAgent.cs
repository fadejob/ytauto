using System.Collections;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Infrastructure.Claude;

/// <summary>
/// Har chaqiruv - yangi, mustaqil `claude -p` sessiyasi (--no-session-persistence). Prompt qat'iy matn,
/// foydalanuvchi matni faqat fayllarda (instructions.md); shell ishlatilmaydi, argumentlar CreateProcess'ga to'g'ridan-to'g'ri.
/// API/OAuth sirlari Claude muhitiga berilmaydi.
/// </summary>
public sealed class ClaudeEditorAgent : IEditorAgent
{
    private readonly Func<ClaudeSettings> _settings;
    private readonly IJobStore? _store;
    private readonly string _runId;
    private string? _version;

    public static readonly string[] AllowedTools =
    {
        "Read", "Write", "Edit", "Glob", "Grep",
        "Bash(openfilm look:*)", "Bash(openfilm skill:*)", "Bash(openfilm skills:*)", "Bash(openfilm get:*)", "Bash(openfilm --help:*)",
        "Bash(cd:*)", "Bash(ls:*)", "Bash(pwd)",
    };

    /// <summary>Claude muhitiga o'tmasligi kerak bo'lgan o'zgaruvchilar (sirlar va ota-ona Claude sessiyasi belgilari).</summary>
    private static readonly string[] SecretEnvPrefixes = { "GEMINI_", "GOOGLE_API", "GOOGLE_APPLICATION", "YOUTUBE", "YTAUTO_", "OPENAI_API" };

    public ClaudeEditorAgent(Func<ClaudeSettings> settings, IJobStore? store = null, string runId = "")
    { _settings = settings; _store = store; _runId = runId; }

    public string Version
    {
        get
        {
            if (_version is not null) return _version;
            var exe = _settings().ExecutablePath;
            if (!File.Exists(exe)) return _version = "not found";
            var tmp = Path.Combine(Path.GetTempPath(), "YtAuto", $"claude-version-{Guid.NewGuid():N}.txt");
            try
            {
                var r = ToolRunner.RunAsync(new LaunchSpec { Exe = exe, Args = new[] { "--version" }, StdoutPath = tmp, Tool = "claude-version", RemoveEnv = BuildRemoveList() },
                    TimeSpan.FromSeconds(30), null, CancellationToken.None).GetAwaiter().GetResult();
                _version = r.ExitCode == 0 ? File.ReadAllText(tmp).Trim() : $"exit {r.ExitCode}";
            }
            catch (Exception e) { _version = "error: " + e.Message; }
            finally { try { File.Delete(tmp); } catch { } }
            return _version;
        }
    }

    internal static List<string> BuildRemoveList()
    {
        var remove = new List<string>();
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            var k = (string)kv.Key;
            var isClaude = (k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC_BASE", StringComparison.OrdinalIgnoreCase))
                           && !k.Equals("CLAUDE_CODE_GIT_BASH_PATH", StringComparison.OrdinalIgnoreCase)
                           && !k.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase);
            if (isClaude || SecretEnvPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))) remove.Add(k);
        }
        return remove;
    }

    public async Task<EditorRunResult> RunAsync(string workingDir, string prompt, TimeSpan timeout, string logPrefix,
        IReadOnlyList<string> extraPathDirs, IJobLog log, CancellationToken ct)
    {
        var s = _settings();
        if (!File.Exists(s.ExecutablePath))
            throw PipelineException.Config("claude_missing", $"Claude Code topilmadi: {s.ExecutablePath}");
        var logs = Path.Combine(workingDir, "logs");
        Directory.CreateDirectory(logs);
        var args = new List<string> { "-p", prompt, "--output-format", "json", "--permission-mode", "dontAsk",
            "--no-session-persistence", "--no-chrome", "--strict-mcp-config" };
        if (!string.IsNullOrWhiteSpace(s.Model)) { args.Add("--model"); args.Add(s.Model.Trim()); }
        if (s.MaxBudgetUsd > 0) { args.Add("--max-budget-usd"); args.Add(s.MaxBudgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        args.Add("--allowedTools"); args.AddRange(AllowedTools); // variadik - eng oxirida

        var path = string.Join(';', extraPathDirs.Append(Environment.GetEnvironmentVariable("PATH") ?? ""));
        var spec = new LaunchSpec
        {
            Exe = s.ExecutablePath, Args = args, WorkingDirectory = workingDir, Tool = "claude",
            Env = new Dictionary<string, string?> { ["PATH"] = path, ["OPENFILM_NO_BROWSER"] = "1", ["OPENFILM_NO_UPDATE_CHECK"] = "1", ["NO_COLOR"] = "1" },
            RemoveEnv = BuildRemoveList(),
            StdoutPath = Path.Combine(logs, $"{logPrefix}.stdout.json"), StderrPath = Path.Combine(logs, $"{logPrefix}.stderr.txt"),
        };
        log.Info($"Claude Code: yangi non-interactive sessiya ({string.Join(", ", AllowedTools.Take(5))} + openfilm), timeout {timeout.TotalMinutes:0} daqiqa.", "Edit");
        OwnedProcess? started = null;
        var r = await ToolRunner.RunAsync(spec, timeout, log, ct, p => started = p).ConfigureAwait(false);
        if (started is not null)
            _store?.RecordProcess(new OwnedProcessRecord { RunId = _runId, Stage = "Edit", Tool = "claude", Pid = started.Pid, ProcessStartUtc = started.StartTimeUtc,
                ExePath = s.ExecutablePath, EndedAtUtc = DateTimeOffset.UtcNow, EndReason = r.TimedOut ? "timeout" : $"exit {r.ExitCode}", PeakMemoryBytes = r.PeakMemoryBytes });

        bool isError = r.ExitCode != 0; string result = ""; string session = ""; double? cost = null; int denials = 0;
        try
        {
            var raw = File.Exists(spec.StdoutPath) ? await File.ReadAllTextAsync(spec.StdoutPath!, ct).ConfigureAwait(false) : "";
            var start = raw.IndexOf('{');
            if (start >= 0)
            {
                var j = JsonNode.Parse(raw[start..]);
                isError = j?["is_error"]?.GetValue<bool>() ?? isError;
                result = j?["result"]?.GetValue<string>() ?? "";
                session = j?["session_id"]?.GetValue<string>() ?? "";
                cost = j?["total_cost_usd"]?.GetValue<double>();
                denials = (j?["permission_denials"] as JsonArray)?.Count ?? 0;
            }
        }
        catch (Exception e) { log.Warn($"Claude JSON natijasini o'qib bo'lmadi: {e.Message}", "Edit"); }
        if (r.LeftoverProcessesKilled > 0) log.Info($"Claude sessiyasidan qolgan {r.LeftoverProcessesKilled} ta child jarayon (masalan, OpenFilm Studio) yopildi.", "Edit");
        return new EditorRunResult { Run = r, IsError = isError, ResultText = Redactor.Redact(result), SessionId = session, CostUsd = cost, PermissionDenials = denials };
    }
}

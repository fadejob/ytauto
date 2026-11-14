using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure;

/// <summary>
/// Loglardan sirlarni olib tashlaydi: ro'yxatga olingan qiymatlar (API kalit, tokenlar, upload session URI) va
/// umumiy naqshlar (Authorization header, access/refresh token, key=, upload_id=).
/// </summary>
public static class Redactor
{
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();
    private static readonly Regex[] Patterns =
    {
        new(@"(?i)(authorization\s*[:=]\s*)(bearer\s+)?[^\s""',]+", RegexOptions.Compiled),
        new(@"(?i)((?:access_token|refresh_token|id_token|client_secret|api[_-]?key|x-goog-api-key|key|code)\s*[""']?\s*[:=]\s*[""']?)([A-Za-z0-9._\-~+/]{8,})", RegexOptions.Compiled),
        new(@"(?i)(upload_id=)[^&\s""']+", RegexOptions.Compiled),
        new(@"\bya29\.[A-Za-z0-9._\-]+", RegexOptions.Compiled),
        new(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled),
        new(@"\b1//[0-9A-Za-z_\-]{20,}", RegexOptions.Compiled),
    };

    public static void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 6) Secrets.TryAdd(secret, 0);
    }

    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        foreach (var k in Secrets.Keys) s = s.Replace(k, "***");
        s = Patterns[0].Replace(s, "$1***");
        s = Patterns[1].Replace(s, "$1***");
        s = Patterns[2].Replace(s, "$1***");
        for (int i = 3; i < Patterns.Length; i++) s = Patterns[i].Replace(s, "***");
        return s;
    }
}

/// <summary>Hajmi cheklangan, aylanadigan matnli log fayli (RAM'da to'planmaydi).</summary>
public sealed class RollingFileLog
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly object _lock = new();

    public RollingFileLog(string path, long maxBytes = 5 * 1024 * 1024, int keep = 5)
    {
        _path = path; _maxBytes = maxBytes; _keep = keep;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string Path_ => _path;

    public void Write(string level, string? scope, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level,-5} {(scope is null ? "" : $"[{scope}] ")}{Redactor.Redact(message)}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > _maxBytes) Rotate();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch { /* log yozilmasa ham ish to'xtamaydi */ }
        }
    }

    private void Rotate()
    {
        for (int i = _keep - 1; i >= 1; i--)
        {
            var src = $"{_path}.{i}"; var dst = $"{_path}.{i + 1}";
            if (File.Exists(dst) && i + 1 > _keep - 1) File.Delete(dst);
            if (File.Exists(src)) File.Move(src, dst, true);
        }
        File.Move(_path, _path + ".1", true);
        var last = $"{_path}.{_keep}";
        if (File.Exists(last)) File.Delete(last);
    }
}

/// <summary>Worker/UI darajasidagi umumiy log.</summary>
public sealed class RunLog : IJobLog
{
    private readonly RollingFileLog _file;
    private readonly bool _console;
    public RunLog(RollingFileLog file, bool console = false) { _file = file; _console = console; }
    public void Info(string message, string? stage = null) => W("INFO", stage, message);
    public void Warn(string message, string? stage = null) => W("WARN", stage, message);
    public void Error(string message, string? stage = null) => W("ERROR", stage, message);
    public void Debug(string message, string? stage = null) => W("DEBUG", stage, message);
    private void W(string level, string? stage, string m)
    {
        _file.Write(level, stage, m);
        if (_console) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {level,-5} {(stage is null ? "" : $"[{stage}] ")}{Redactor.Redact(m)}");
    }
}

/// <summary>Vazifa logi: records/job-N/job.log (aylanuvchi) + bazadagi qisqa hodisalar (UI uchun) + umumiy run log.</summary>
public sealed class JobLog : IJobLog
{
    private readonly long _jobId;
    private readonly RollingFileLog _file;
    private readonly IJobStore _store;
    private readonly IJobLog? _run;

    public JobLog(long jobId, string recordDir, IJobStore store, IJobLog? run)
    {
        _jobId = jobId; _store = store; _run = run;
        _file = new RollingFileLog(Path.Combine(recordDir, "job.log"), 4 * 1024 * 1024, 3);
    }

    public void Info(string message, string? stage = null) => W("INFO", stage, message, true);
    public void Warn(string message, string? stage = null) => W("WARN", stage, message, true);
    public void Error(string message, string? stage = null) => W("ERROR", stage, message, true);
    public void Debug(string message, string? stage = null) => W("DEBUG", stage, message, false);

    private void W(string level, string? stage, string message, bool toDb)
    {
        var red = Redactor.Redact(message);
        _file.Write(level, stage, red);
        if (toDb) { try { _store.AddEvent(_jobId, level, stage, red.Length > 2000 ? red[..2000] + "…" : red); } catch { } }
        switch (level) // run log faqat yuqori darajali xabarlar uchun
        {
            case "INFO": _run?.Info($"#{_jobId} {red}", stage); break;
            case "WARN": _run?.Warn($"#{_jobId} {red}", stage); break;
            case "ERROR": _run?.Error($"#{_jobId} {red}", stage); break;
        }
    }
}

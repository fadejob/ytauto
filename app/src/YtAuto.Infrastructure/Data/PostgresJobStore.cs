using Npgsql;
using NpgsqlTypes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Data;

/// <summary>
/// Online ombor (Supabase PostgreSQL, "ytauto" sxemasi): mavzular CRUD, bosqichlar, hodisalar, worker ishga tushishlari va
/// global sozlamalar (promptlar). Kompyuter o'chsa yoki disk buzilsa ham mavzular yo'qolmaydi.
/// Bir nechta kompyuter bitta bazadan foydalansa: band qilish atomar (FOR UPDATE SKIP LOCKED), eskirgan band qilish
/// heartbeat bo'yicha aniqlanadi - boshqa kompyuterdagi tirik worker'ning vazifasi tortib olinmaydi.
/// </summary>
public sealed class PostgresJobStore : IAppStore
{
    private readonly NpgsqlDataSource _ds;
    private readonly string _schema;
    public static readonly TimeSpan StaleClaimAfter = TimeSpan.FromMinutes(15);
    public string Kind { get; }

    public PostgresJobStore(string connectionString, string schema = "ytauto")
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Supabase pooler session rejimi (5432) har ochiq ulanishni band qiladi va pool_size (15) bilan cheklangan (EMAXCONNSESSION):
            // ilova + bir nechta worker jarayoni bo'sh ulanishlarni ushlab turmasin - 30 soniya ishlatilmasa yopiladi.
            Pooling = true, MaxPoolSize = 5, ConnectionIdleLifetime = 30, ConnectionPruningInterval = 10,
            Timeout = 15, CommandTimeout = 30, KeepAlive = 30,
        };
        if (b.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer && !Accounts.DeviceAccount.IsLocalHost(b.Host)) b.SslMode = SslMode.Require;
        _ds = NpgsqlDataSource.Create(b.ConnectionString);
        _schema = schema;
        Kind = $"Online (PostgreSQL: {b.Host})";
        Migrate();
    }

    private string T(string table) => $"\"{_schema}\".{table}";

    private NpgsqlConnection Open()
    {
        var c = _ds.OpenConnection();
        if (IsDeviceRole) ApplyScope(c);
        return c;
    }

    // ------------------------------------------------------------------ mehmon (qo'shimcha token) hisoblari
    // Mezbon kompyuter mehmon vazifasini bajarayotganda shu async oqimdagi barcha so'rovlar mehmon hisobi nomidan ketadi:
    // ulanishda set_config('ytauto_ctx.guest', ...) qilinadi va baza (current_account) faqat shu kompyuter mezbon bo'lgan
    // mehmonni qabul qiladi. AsyncLocal: fon taymerlari (heartbeat) va boshqa oqimlar o'z (mezbon) hisobida qoladi.
    private readonly AsyncLocal<Guid?> _scope = new();
    private readonly HashSet<int> _guestBackends = new(); // mehmon konteksti o'rnatilgan (hali tozalanmagan) server ulanishlari
    private readonly object _scopeLock = new();

    /// <summary>Joriy oqim mehmon hisobi nomidan ishlayapti (null - o'z hisobi).</summary>
    public Guid? Scope => _scope.Value;
    public bool InGuestScope => _scope.Value is not null;

    /// <summary>Oqimni berilgan hisobga o'tkazadi (null - o'z hisobi); Dispose avvalgisini tiklaydi.</summary>
    public IDisposable UseScope(Guid? account)
    {
        var prev = _scope.Value;
        _scope.Value = account;
        return new ScopeRestore(() => _scope.Value = prev);
    }

    private sealed class ScopeRestore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }

    private void ApplyScope(NpgsqlConnection c)
    {
        var g = _scope.Value;
        var pid = c.ProcessID;
        bool dirty;
        lock (_scopeLock) dirty = _guestBackends.Contains(pid);
        if (g is null && !dirty) return; // pul ulanishi toza: qo'shimcha so'rov kerak emas
        using (var cmd = new NpgsqlCommand("SELECT set_config('ytauto_ctx.guest', @g, false)", c))
        {
            cmd.Parameters.AddWithValue("g", g?.ToString() ?? "");
            cmd.ExecuteNonQuery();
        }
        lock (_scopeLock) { if (g is null) _guestBackends.Remove(pid); else _guestBackends.Add(pid); }
    }

    /// <summary>
    /// Shu kompyuter hisobi mezbon bo'lgan mehmon hisoblari (asosiy hisob botda yaratgan qo'shimcha tokenlar). Ularning mavzulari ham
    /// shu kompyuterda, ish vaqtida tayyorlanadi. Baza eski (v3 dan oldingi) bo'lsa - bo'sh.
    /// </summary>
    public IReadOnlyList<(Guid Id, string Label)> HostedAccounts()
    {
        if (!IsDeviceRole) return Array.Empty<(Guid, string)>();
        try
        {
            using var _ = UseScope(null);
            using var c = Open();
            using var cmd = Cmd(c, $"SELECT account_id, label FROM {T("hosted_accounts")}()");
            using var r = cmd.ExecuteReader();
            var list = new List<(Guid, string)>();
            while (r.Read()) list.Add((r.GetGuid(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            return list;
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UndefinedFunction or PostgresErrorCodes.InsufficientPrivilege)
        {
            return Array.Empty<(Guid, string)>();
        }
    }

    /// <summary>Kompyuter egasi (mezbon) hisobining sozlamasi - mehmon kontekstida ham.</summary>
    public string? GetHostSetting(string key)
    {
        using (UseScope(null)) return GetSetting(key);
    }

    /// <summary>Navbat ko'radigan hisoblar: o'zi (null) va mezbonlik qilayotgan mehmonlar.</summary>
    private List<Guid?> QueueScopes()
    {
        var list = new List<Guid?> { null };
        foreach (var (id, _) in HostedAccounts()) list.Add(id);
        return list;
    }

    /// <summary>
    /// Sxema: yagona manba bot/db/schema.sql. Jadval egasi bilan ulanganda (bot yoki eski ulanish satri) kerak bo'lsa migratsiya qiladi;
    /// qurilma roli (ytd_*) bilan - yo'q (u sxemani o'zgartira olmaydi, faqat o'z hisobi qatorlarini ko'radi).
    /// </summary>
    private void Migrate()
    {
        using var c = Open();
        IsDeviceRole = SchemaMigrator.IsDeviceRole(c);
        SchemaMigrator.EnsureMigrated(c, _schema);
    }

    /// <summary>Qurilma roli bilan ulangan (ko'p foydalanuvchili rejim): barcha so'rovlar baza tomonidan shu hisobga cheklangan.</summary>
    public bool IsDeviceRole { get; private set; }

    /// <summary>Hisob tokenini almashtiradi (bazada faqat xeshi). Eski token bilan botga kirganlar chiqarib yuboriladi.</summary>
    public void RotateToken(string newTokenHash)
    {
        using var c = Open();
        Exec(c, null, $"SELECT {T("rotate_token")}(@h)", ("h", newTokenHash));
    }

    /// <summary>Shu qurilma hisobining ma'lumoti (id, qurilma nomi, botga token bilan kirgan Telegram foydalanuvchilar soni).</summary>
    public (Guid AccountId, string DeviceName, DateTimeOffset Created, int TelegramUsers)? AccountInfo()
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT account_id, device_name, created_at_utc, telegram_users FROM {T("account_info")}()");
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetInt32(3)) : null;
    }

    // ------------------------------------------------------------------ helpers
    private static void Exec(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params (string, object?)[] ps)
    {
        using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private NpgsqlCommand Cmd(NpgsqlConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private static DateTimeOffset U(DateTimeOffset t) => t.ToUniversalTime();
    private static object N(DateTimeOffset? t) => t is null ? DBNull.Value : t.Value.ToUniversalTime();
    private static object N(string? s) => (object?)s ?? DBNull.Value;
    private static DateTimeOffset? D(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i); }
    private static string? S(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }

    private const string JobColumns = "id, topic, notes, language, target_duration_sec, format, template_name, voice_profile, publish_at_utc, status, created_at_utc, updated_at_utc, run_attempts, next_attempt_at_utc, claimed_by, claimed_at_utc, cancel_requested, qa_fix_cycles, last_error_code, last_error_message, attention_reason, settings_snapshot, title, description, thumbnail_path, video_archive_path, audio_archive_path, subtitle_archive_path, audio_duration_sec, youtube_video_id, youtube_privacy, youtube_upload_status, youtube_processing_status, youtube_checked_at_utc, published_at_utc, work_dir, work_dir_cleaned, publish_now, manual_script, manual_title, manual_audio_url, kind, demo_spec, demo_result";

    private static VideoJob ReadJob(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")), Topic = S(r, "topic") ?? "", Notes = S(r, "notes") ?? "", Language = S(r, "language") ?? "en",
        TargetDurationSec = r.GetInt32(r.GetOrdinal("target_duration_sec")), Format = (VideoFormat)r.GetInt32(r.GetOrdinal("format")),
        TemplateName = S(r, "template_name") ?? "", VoiceProfileName = S(r, "voice_profile") ?? "",
        PublishAtUtc = D(r, "publish_at_utc")!.Value, Status = (JobStatus)r.GetInt32(r.GetOrdinal("status")),
        CreatedAtUtc = D(r, "created_at_utc")!.Value, UpdatedAtUtc = D(r, "updated_at_utc")!.Value,
        RunAttempts = r.GetInt32(r.GetOrdinal("run_attempts")), NextAttemptAtUtc = D(r, "next_attempt_at_utc"),
        ClaimedBy = S(r, "claimed_by"), ClaimedAtUtc = D(r, "claimed_at_utc"), CancelRequested = r.GetBoolean(r.GetOrdinal("cancel_requested")),
        QaFixCycles = r.GetInt32(r.GetOrdinal("qa_fix_cycles")), LastErrorCode = S(r, "last_error_code"), LastErrorMessage = S(r, "last_error_message"),
        AttentionReason = S(r, "attention_reason"), SettingsSnapshotJson = S(r, "settings_snapshot"), Title = S(r, "title"), Description = S(r, "description"),
        ThumbnailPath = S(r, "thumbnail_path"), VideoArchivePath = S(r, "video_archive_path"), AudioArchivePath = S(r, "audio_archive_path"),
        SubtitleArchivePath = S(r, "subtitle_archive_path"),
        AudioDurationSec = r.IsDBNull(r.GetOrdinal("audio_duration_sec")) ? null : r.GetDouble(r.GetOrdinal("audio_duration_sec")),
        YouTubeVideoId = S(r, "youtube_video_id"), YouTubePrivacy = S(r, "youtube_privacy"), YouTubeUploadStatus = S(r, "youtube_upload_status"),
        YouTubeProcessingStatus = S(r, "youtube_processing_status"), YouTubeCheckedAtUtc = D(r, "youtube_checked_at_utc"),
        PublishedAtUtc = D(r, "published_at_utc"), WorkDir = S(r, "work_dir"), WorkDirCleaned = r.GetBoolean(r.GetOrdinal("work_dir_cleaned")),
        PublishNow = r.GetBoolean(r.GetOrdinal("publish_now")),
        ManualScript = S(r, "manual_script"), ManualTitle = S(r, "manual_title"), ManualAudioUrl = S(r, "manual_audio_url"),
        Kind = S(r, "kind") ?? "", DemoSpecJson = S(r, "demo_spec"), DemoResultJson = S(r, "demo_result"),
    };

    private static void BindJob(NpgsqlCommand cmd, VideoJob j)
    {
        var p = cmd.Parameters;
        p.AddWithValue("topic", j.Topic); p.AddWithValue("notes", j.Notes ?? ""); p.AddWithValue("language", j.Language);
        p.AddWithValue("dur", j.TargetDurationSec); p.AddWithValue("format", (int)j.Format); p.AddWithValue("tpl", j.TemplateName ?? "");
        p.AddWithValue("voice", j.VoiceProfileName ?? ""); p.AddWithValue("pub", U(j.PublishAtUtc)); p.AddWithValue("status", (int)j.Status);
        p.AddWithValue("created", U(j.CreatedAtUtc == default ? DateTimeOffset.UtcNow : j.CreatedAtUtc)); p.AddWithValue("updated", DateTimeOffset.UtcNow);
        p.AddWithValue("attempts", j.RunAttempts); p.AddWithValue("next", NpgsqlDbType.TimestampTz, N(j.NextAttemptAtUtc));
        p.AddWithValue("cancel", j.CancelRequested); p.AddWithValue("qa", j.QaFixCycles);
        p.AddWithValue("errc", NpgsqlDbType.Text, N(j.LastErrorCode)); p.AddWithValue("errm", NpgsqlDbType.Text, N(j.LastErrorMessage));
        p.AddWithValue("attn", NpgsqlDbType.Text, N(j.AttentionReason)); p.AddWithValue("snap", NpgsqlDbType.Text, N(j.SettingsSnapshotJson));
        p.AddWithValue("title", NpgsqlDbType.Text, N(j.Title)); p.AddWithValue("desc", NpgsqlDbType.Text, N(j.Description));
        p.AddWithValue("thumb", NpgsqlDbType.Text, N(j.ThumbnailPath)); p.AddWithValue("va", NpgsqlDbType.Text, N(j.VideoArchivePath));
        p.AddWithValue("aa", NpgsqlDbType.Text, N(j.AudioArchivePath)); p.AddWithValue("sa", NpgsqlDbType.Text, N(j.SubtitleArchivePath));
        p.AddWithValue("adur", NpgsqlDbType.Double, (object?)j.AudioDurationSec ?? DBNull.Value); p.AddWithValue("yid", NpgsqlDbType.Text, N(j.YouTubeVideoId));
        p.AddWithValue("ypriv", NpgsqlDbType.Text, N(j.YouTubePrivacy)); p.AddWithValue("yup", NpgsqlDbType.Text, N(j.YouTubeUploadStatus));
        p.AddWithValue("yproc", NpgsqlDbType.Text, N(j.YouTubeProcessingStatus)); p.AddWithValue("ychk", NpgsqlDbType.TimestampTz, N(j.YouTubeCheckedAtUtc));
        p.AddWithValue("pubd", NpgsqlDbType.TimestampTz, N(j.PublishedAtUtc)); p.AddWithValue("wd", NpgsqlDbType.Text, N(j.WorkDir));
        p.AddWithValue("wdc", j.WorkDirCleaned);
    }

    private List<VideoJob> ReadJobs(NpgsqlCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<VideoJob>();
        while (r.Read()) list.Add(ReadJob(r));
        return list;
    }

    // ------------------------------------------------------------------ IJobStore
    public long Insert(VideoJob job)
    {
        using var c = Open();
        using var cmd = new NpgsqlCommand($"""
INSERT INTO {T("jobs")}(topic, notes, language, target_duration_sec, format, template_name, voice_profile, publish_at_utc, status, created_at_utc, updated_at_utc,
 run_attempts, next_attempt_at_utc, cancel_requested, qa_fix_cycles, last_error_code, last_error_message, attention_reason, settings_snapshot, title, description,
 thumbnail_path, video_archive_path, audio_archive_path, subtitle_archive_path, audio_duration_sec, youtube_video_id, youtube_privacy, youtube_upload_status,
 youtube_processing_status, youtube_checked_at_utc, published_at_utc, work_dir, work_dir_cleaned, publish_now, manual_script, manual_title, manual_audio_url,
 kind, demo_spec)
VALUES(@topic,@notes,@language,@dur,@format,@tpl,@voice,@pub,@status,@created,@updated,@attempts,@next,@cancel,@qa,@errc,@errm,@attn,@snap,@title,@desc,
 @thumb,@va,@aa,@sa,@adur,@yid,@ypriv,@yup,@yproc,@ychk,@pubd,@wd,@wdc,@pn,@ms,@mt,@ma,@kind,@dspec) RETURNING id
""", c);
        if (job.CreatedAtUtc == default) job.CreatedAtUtc = DateTimeOffset.UtcNow;
        BindJob(cmd, job);
        cmd.Parameters.AddWithValue("pn", job.PublishNow);
        cmd.Parameters.AddWithValue("ms", NpgsqlDbType.Text, N(job.ManualScript));
        cmd.Parameters.AddWithValue("mt", NpgsqlDbType.Text, N(job.ManualTitle));
        cmd.Parameters.AddWithValue("ma", NpgsqlDbType.Text, N(job.ManualAudioUrl));
        cmd.Parameters.AddWithValue("kind", job.Kind ?? "");
        cmd.Parameters.AddWithValue("dspec", NpgsqlDbType.Text, N(job.DemoSpecJson));
        job.Id = (long)cmd.ExecuteScalar()!;
        return job.Id;
    }

    public VideoJob? Get(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {JobColumns} FROM {T("jobs")} WHERE id=@id", ("id", id));
        return ReadJobs(cmd).FirstOrDefault();
    }

    public void Update(VideoJob job)
    {
        using var c = Open();
        using var cmd = new NpgsqlCommand($"""
UPDATE {T("jobs")} SET topic=@topic, notes=@notes, language=@language, target_duration_sec=@dur, format=@format, template_name=@tpl, voice_profile=@voice,
 publish_at_utc=@pub, status=@status, updated_at_utc=@updated, run_attempts=@attempts, next_attempt_at_utc=@next, qa_fix_cycles=@qa,
 last_error_code=@errc, last_error_message=@errm, attention_reason=@attn, settings_snapshot=@snap, title=@title, description=@desc, thumbnail_path=@thumb,
 video_archive_path=@va, audio_archive_path=@aa, subtitle_archive_path=@sa, audio_duration_sec=@adur, youtube_video_id=@yid, youtube_privacy=@ypriv,
 youtube_upload_status=@yup, youtube_processing_status=@yproc, youtube_checked_at_utc=@ychk, published_at_utc=@pubd, work_dir=@wd, work_dir_cleaned=@wdc
WHERE id=@id
""", c);
        BindJob(cmd, job);
        cmd.Parameters.AddWithValue("id", job.Id);
        cmd.ExecuteNonQuery();
    }

    public VideoJob? ClaimNext(string runId, DateTimeOffset nowUtc)
    {
        if (!IsDeviceRole) return ClaimNextIn(runId, nowUtc);
        // Mezbon kompyuter: o'z va mehmon hisoblari bitta navbat - chiqish vaqti (deadline) eng yaqini birinchi.
        // Har hisob o'z RLS kontekstida ko'riladi; band qilish atomar (boshqa worker oldin olsa keyingi nomzod tanlanadi).
        for (int attempt = 0; attempt < 5; attempt++)
        {
            (Guid? Scope, long Id, DateTimeOffset Pub, DateTimeOffset Created)? best = null;
            foreach (var scope in QueueScopes())
            {
                using var _ = UseScope(scope);
                using var c = Open();
                using var cmd = Cmd(c, $"""
SELECT id, publish_at_utc, created_at_utc FROM {T("jobs")}
WHERE status = ANY(@st) AND claimed_by IS NULL AND cancel_requested = FALSE AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)
ORDER BY publish_at_utc, created_at_utc, id LIMIT 1
""", ("st", Claimable), ("now", U(nowUtc)));
                using var r = cmd.ExecuteReader();
                if (!r.Read()) continue;
                var id = r.GetInt64(0); var pub = r.GetFieldValue<DateTimeOffset>(1); var created = r.GetFieldValue<DateTimeOffset>(2);
                if (best is null || (pub, created, id).CompareTo((best.Value.Pub, best.Value.Created, best.Value.Id)) < 0)
                    best = (scope, id, pub, created);
            }
            if (best is null) { _scope.Value = null; return null; }
            VideoJob? job;
            using (UseScope(best.Value.Scope))
            {
                using var c = Open();
                using var cmd = Cmd(c, $"""
UPDATE {T("jobs")} SET claimed_by=@run, claimed_at_utc=@now, updated_at_utc=@now
WHERE id=@id AND claimed_by IS NULL AND cancel_requested = FALSE AND status = ANY(@st)
RETURNING {JobColumns}
""", ("run", runId), ("now", U(nowUtc)), ("id", best.Value.Id), ("st", Claimable));
                job = ReadJobs(cmd).FirstOrDefault();
            }
            if (job is null) continue; // boshqa kompyuter oldinroq oldi
            // Vazifaning qolgan ishi (bosqichlar, loglar, sozlamalar, YouTube) shu hisob nomidan: kontekst chaqiruvchi async oqimda qoladi.
            _scope.Value = best.Value.Scope;
            return job;
        }
        _scope.Value = null;
        return null;
    }

    private VideoJob? ClaimNextIn(string runId, DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
UPDATE {T("jobs")} SET claimed_by=@run, claimed_at_utc=@now, updated_at_utc=@now
WHERE id = (
  SELECT id FROM {T("jobs")}
  WHERE status = ANY(@st) AND claimed_by IS NULL AND cancel_requested = FALSE
    AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)
  ORDER BY publish_at_utc, created_at_utc, id LIMIT 1 FOR UPDATE SKIP LOCKED)
  AND claimed_by IS NULL
RETURNING {JobColumns}
""", ("run", runId), ("now", U(nowUtc)), ("st", JobStateMachine.ClaimableStatuses.Select(s => (int)s).ToArray()));
        return ReadJobs(cmd).FirstOrDefault();
    }

    private static int[] Claimable => JobStateMachine.ClaimableStatuses.Select(s => (int)s).ToArray();
    private static readonly int[] FinalStatuses = { (int)JobStatus.Published, (int)JobStatus.Cancelled };

    public int CountPending(DateTimeOffset nowUtc)
    {
        if (IsDeviceRole && _scope.Value is null)
        {
            var total = 0;
            foreach (var scope in QueueScopes()) using (UseScope(scope)) total += CountPendingIn(nowUtc);
            return total;
        }
        return CountPendingIn(nowUtc);
    }

    private int CountPendingIn(DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT COUNT(*) FROM {T("jobs")} WHERE status = ANY(@st) AND cancel_requested = FALSE AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)",
            ("st", Claimable), ("now", U(nowUtc)));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountPublishNow(DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT COUNT(*) FROM {T("jobs")} WHERE publish_now AND NOT (status = ANY(@fin)) AND claimed_by IS NULL AND cancel_requested = FALSE AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)",
            ("fin", FinalStatuses), ("now", U(nowUtc)));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public VideoJob? ClaimPublishNow(string runId, DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
UPDATE {T("jobs")} SET claimed_by=@run, claimed_at_utc=@now, updated_at_utc=@now
WHERE id = (
  SELECT id FROM {T("jobs")}
  WHERE publish_now AND NOT (status = ANY(@fin)) AND claimed_by IS NULL AND cancel_requested = FALSE
    AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)
  ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
  AND claimed_by IS NULL
RETURNING {JobColumns}
""", ("run", runId), ("now", U(nowUtc)), ("fin", FinalStatuses));
        return ReadJobs(cmd).FirstOrDefault();
    }

    public void SetPublishNow(long jobId, bool value)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET publish_now=@v, updated_at_utc=@now" + (value ? ", next_attempt_at_utc=NULL" : "") + " WHERE id=@id",
            ("v", value), ("now", DateTimeOffset.UtcNow), ("id", jobId));
    }

    public void ReleaseClaim(long jobId, string runId)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET claimed_by=NULL, claimed_at_utc=NULL WHERE id=@id AND claimed_by=@run", ("id", jobId), ("run", runId));
    }

    public IReadOnlyList<long> UploadRequestIds()
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT id FROM {T("jobs")} WHERE upload_requested AND youtube_video_id IS NULL AND video_archive_path IS NOT NULL AND claimed_by IS NULL AND cancel_requested = FALSE AND NOT (status = ANY(@fin)) ORDER BY id",
            ("fin", FinalStatuses));
        var ids = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    public void SetUploadRequested(long jobId, bool value)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET upload_requested=@v, updated_at_utc=@now WHERE id=@id", ("v", value), ("now", DateTimeOffset.UtcNow), ("id", jobId));
    }

    /// <summary>
    /// MCP demo videolari: faqat joriy (o'z yoki band qilingan mehmon) hisob doirasida, yaratilgan tartibda. Mehmon hisoblarining demo
    /// so'rovlari oddiy navbat bilan (ClaimNext, ish vaqtida) bajariladi - mezbon kompyuter ularni navbatsiz olmaydi.
    /// </summary>
    public VideoJob? ClaimDemo(string runId, DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
UPDATE {T("jobs")} SET claimed_by=@run, claimed_at_utc=@now, updated_at_utc=@now
WHERE id = (
  SELECT id FROM {T("jobs")}
  WHERE kind = 'demo' AND status = ANY(@st) AND claimed_by IS NULL AND cancel_requested = FALSE
    AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)
  ORDER BY created_at_utc, id LIMIT 1 FOR UPDATE SKIP LOCKED)
  AND claimed_by IS NULL
RETURNING {JobColumns}
""", ("run", runId), ("now", U(nowUtc)), ("st", Claimable));
        return ReadJobs(cmd).FirstOrDefault();
    }

    public int CountDemo(DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT COUNT(*) FROM {T("jobs")} WHERE kind = 'demo' AND status = ANY(@st) AND claimed_by IS NULL AND cancel_requested = FALSE AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)",
            ("st", Claimable), ("now", U(nowUtc)));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void SetDemoResult(long jobId, string json)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET demo_result=@v, updated_at_utc=@now WHERE id=@id", ("v", json), ("now", DateTimeOffset.UtcNow), ("id", jobId));
    }

    public bool TryClaim(long jobId, string runId, DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"UPDATE {T("jobs")} SET claimed_by=@run, claimed_at_utc=@now WHERE id=@id AND claimed_by IS NULL",
            ("run", runId), ("now", U(nowUtc)), ("id", jobId));
        return cmd.ExecuteNonQuery() == 1;
    }

    public void Heartbeat(long jobId, string runId)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET claimed_at_utc=@now WHERE id=@id AND claimed_by=@run", ("id", jobId), ("run", runId), ("now", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Eskirgan band qilishlar: shu kompyuterning oldingi worker'lari (OS mutex ostida - ular o'lgan) yoki heartbeat'i
    /// <see cref="StaleClaimAfter"/> dan beri yangilanmagan boshqa kompyuterlarniki.
    /// </summary>
    public int ReleaseStaleClaims(string currentRunId)
    {
        if (IsDeviceRole && _scope.Value is null)
        {
            var total = 0;
            foreach (var scope in QueueScopes()) using (UseScope(scope)) total += ReleaseStaleClaimsIn(currentRunId);
            return total;
        }
        return ReleaseStaleClaimsIn(currentRunId);
    }

    private int ReleaseStaleClaimsIn(string currentRunId)
    {
        var machinePrefix = MachineRunPrefix(currentRunId);
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var sel = new NpgsqlCommand($"""
SELECT id FROM {T("jobs")} WHERE claimed_by IS NOT NULL AND claimed_by <> @run
  AND (claimed_by LIKE @machine OR claimed_at_utc < @stale) FOR UPDATE
""", c, tx);
        sel.Parameters.AddWithValue("run", currentRunId);
        sel.Parameters.AddWithValue("machine", machinePrefix + "%");
        sel.Parameters.AddWithValue("stale", DateTimeOffset.UtcNow - StaleClaimAfter);
        var ids = new List<long>();
        using (var r = sel.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
        if (ids.Count > 0)
        {
            Exec(c, tx, $"UPDATE {T("jobs")} SET claimed_by=NULL, claimed_at_utc=NULL WHERE id = ANY(@ids)", ("ids", ids.ToArray()));
            Exec(c, tx, $"UPDATE {T("job_stages")} SET state=@failed, error_code='interrupted', error_message='Worker yoki kompyuter kutilmaganda to''xtadi', finished_at_utc=@now WHERE state=@running AND job_id = ANY(@ids)",
                ("failed", (int)StageState.Failed), ("running", (int)StageState.Running), ("now", DateTimeOffset.UtcNow), ("ids", ids.ToArray()));
        }
        tx.Commit();
        return ids.Count;
    }

    /// <summary>RunId: "{component}-{machine}-{yyyyMMddHHmmss}-{pid}" → "{component}-{machine}-".</summary>
    internal static string MachineRunPrefix(string runId)
    {
        var parts = runId.Split('-');
        return parts.Length >= 4 ? $"{parts[0]}-{parts[1]}-" : runId;
    }

    public bool IsCancelRequested(long jobId)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT cancel_requested FROM {T("jobs")} WHERE id=@id", ("id", jobId));
        return cmd.ExecuteScalar() is true;
    }

    public void RequestCancel(long jobId)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET cancel_requested=TRUE, updated_at_utc=@now WHERE id=@id", ("id", jobId), ("now", DateTimeOffset.UtcNow));
    }

    public bool TryCancelUnclaimed(long jobId)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"UPDATE {T("jobs")} SET status=@st, cancel_requested=TRUE, updated_at_utc=@now WHERE id=@id AND claimed_by IS NULL AND status <> @pub",
            ("st", (int)JobStatus.Cancelled), ("now", DateTimeOffset.UtcNow), ("id", jobId), ("pub", (int)JobStatus.Published));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool IsClaimed(long jobId)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT claimed_by FROM {T("jobs")} WHERE id=@id", ("id", jobId));
        return cmd.ExecuteScalar() is string;
    }

    public bool Delete(long jobId)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"DELETE FROM {T("jobs")} WHERE id=@id AND claimed_by IS NULL", ("id", jobId));
        return cmd.ExecuteNonQuery() > 0;
    }

    public StageRecord GetStage(long jobId, StageKind stage)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT * FROM {T("job_stages")} WHERE job_id=@j AND stage=@s", ("j", jobId), ("s", (int)stage));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadStage(r) : new StageRecord { JobId = jobId, Stage = stage, State = StageState.Pending };
    }

    public IReadOnlyList<StageRecord> GetStages(long jobId)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT * FROM {T("job_stages")} WHERE job_id=@j ORDER BY stage", ("j", jobId));
        using var r = cmd.ExecuteReader();
        var list = new List<StageRecord>();
        while (r.Read()) list.Add(ReadStage(r));
        return list;
    }

    private static StageRecord ReadStage(NpgsqlDataReader r) => new()
    {
        JobId = r.GetInt64(r.GetOrdinal("job_id")), Stage = (StageKind)r.GetInt32(r.GetOrdinal("stage")), State = (StageState)r.GetInt32(r.GetOrdinal("state")),
        StartedAtUtc = D(r, "started_at_utc"), FinishedAtUtc = D(r, "finished_at_utc"), Attempts = r.GetInt32(r.GetOrdinal("attempts")),
        InputHash = S(r, "input_hash"), OutputsJson = S(r, "outputs_json"), ErrorCode = S(r, "error_code"), ErrorMessage = S(r, "error_message"),
        DurationMs = r.IsDBNull(r.GetOrdinal("duration_ms")) ? null : r.GetInt64(r.GetOrdinal("duration_ms")), MetricsJson = S(r, "metrics_json"),
    };

    public void SaveStage(StageRecord s)
    {
        using var c = Open();
        using var cmd = new NpgsqlCommand($"""
INSERT INTO {T("job_stages")}(job_id, stage, state, started_at_utc, finished_at_utc, attempts, input_hash, outputs_json, error_code, error_message, duration_ms, metrics_json)
VALUES(@j,@s,@st,@sa,@fa,@at,@ih,@o,@ec,@em,@d,@m)
ON CONFLICT(job_id, stage) DO UPDATE SET state=excluded.state, started_at_utc=excluded.started_at_utc, finished_at_utc=excluded.finished_at_utc,
 attempts=excluded.attempts, input_hash=excluded.input_hash, outputs_json=excluded.outputs_json, error_code=excluded.error_code,
 error_message=excluded.error_message, duration_ms=excluded.duration_ms, metrics_json=excluded.metrics_json
""", c);
        var p = cmd.Parameters;
        p.AddWithValue("j", s.JobId); p.AddWithValue("s", (int)s.Stage); p.AddWithValue("st", (int)s.State);
        p.AddWithValue("sa", NpgsqlDbType.TimestampTz, N(s.StartedAtUtc)); p.AddWithValue("fa", NpgsqlDbType.TimestampTz, N(s.FinishedAtUtc));
        p.AddWithValue("at", s.Attempts); p.AddWithValue("ih", NpgsqlDbType.Text, N(s.InputHash)); p.AddWithValue("o", NpgsqlDbType.Text, N(s.OutputsJson));
        p.AddWithValue("ec", NpgsqlDbType.Text, N(s.ErrorCode)); p.AddWithValue("em", NpgsqlDbType.Text, N(s.ErrorMessage));
        p.AddWithValue("d", NpgsqlDbType.Bigint, (object?)s.DurationMs ?? DBNull.Value); p.AddWithValue("m", NpgsqlDbType.Text, N(s.MetricsJson));
        cmd.ExecuteNonQuery();
    }

    public void AddEvent(long jobId, string level, string? stage, string message)
    {
        using var c = Open();
        Exec(c, null, $"INSERT INTO {T("job_events")}(job_id, at_utc, level, stage, message) VALUES(@j,@t,@l,@s,@m)",
            ("j", jobId), ("t", DateTimeOffset.UtcNow), ("l", level), ("s", stage), ("m", message));
        // Har vazifa uchun eng ko'pi 2000 hodisa (to'liq log - lokal faylda).
        if (Random.Shared.Next(50) == 0)
            Exec(c, null, $"DELETE FROM {T("job_events")} WHERE job_id=@j AND id < (SELECT id FROM {T("job_events")} WHERE job_id=@j ORDER BY id DESC OFFSET 2000 LIMIT 1)", ("j", jobId));
    }

    public IReadOnlyList<JobEvent> GetEvents(long jobId, int limit = 300)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT id, job_id, at_utc, level, stage, message FROM (SELECT * FROM {T("job_events")} WHERE job_id=@j ORDER BY id DESC LIMIT @n) x ORDER BY id",
            ("j", jobId), ("n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<JobEvent>();
        while (r.Read())
            list.Add(new JobEvent { Id = r.GetInt64(0), JobId = r.GetInt64(1), AtUtc = r.GetFieldValue<DateTimeOffset>(2), Level = r.GetString(3), Stage = r.IsDBNull(4) ? null : r.GetString(4), Message = r.GetString(5) });
        return list;
    }

    public IReadOnlyList<VideoJob> ListByStatus(params JobStatus[] statuses)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {JobColumns} FROM {T("jobs")} WHERE status = ANY(@st) ORDER BY created_at_utc, id", ("st", statuses.Select(s => (int)s).ToArray()));
        return ReadJobs(cmd);
    }

    public IReadOnlyList<VideoJob> Page(bool released, int offset, int limit, out int total)
    {
        var pub = (int)JobStatus.Published;
        var where = released ? $"status = {pub}" : $"status <> {pub}";
        using var c = Open();
        using (var cnt = Cmd(c, $"SELECT COUNT(*) FROM {T("jobs")} WHERE {where}")) total = Convert.ToInt32(cnt.ExecuteScalar());
        var order = released ? "publish_at_utc DESC, id DESC" : $"CASE WHEN status={(int)JobStatus.Cancelled} THEN 1 ELSE 0 END, publish_at_utc, id";
        using var cmd = Cmd(c, $"SELECT {JobColumns} FROM {T("jobs")} WHERE {where} ORDER BY {order} LIMIT @l OFFSET @o", ("l", limit), ("o", offset));
        return ReadJobs(cmd);
    }

    public IReadOnlyList<(JobStatus Status, DateTimeOffset PublishAtUtc)> PublishSlots()
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT status, publish_at_utc FROM {T("jobs")} WHERE status <> @c", ("c", (int)JobStatus.Cancelled));
        using var r = cmd.ExecuteReader();
        var list = new List<(JobStatus, DateTimeOffset)>();
        while (r.Read()) list.Add(((JobStatus)r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1)));
        return list;
    }

    public void RecordProcess(OwnedProcessRecord p)
    {
        using var c = Open();
        Exec(c, null, $"INSERT INTO {T("owned_processes")}(job_id, run_id, stage, tool, pid, process_start_utc, exe_path, ended_at_utc, end_reason, peak_memory_bytes) VALUES(@j,@r,@s,@t,@p,@ps,@e,@ea,@er,@pm)",
            ("j", p.JobId), ("r", p.RunId), ("s", p.Stage), ("t", p.Tool), ("p", p.Pid), ("ps", U(p.ProcessStartUtc)), ("e", p.ExePath),
            ("ea", p.EndedAtUtc?.ToUniversalTime()), ("er", p.EndReason), ("pm", p.PeakMemoryBytes));
    }

    public void StartRun(string runId, int pid, DateTimeOffset processStart, string trigger)
    {
        using var c = Open();
        Exec(c, null, $"INSERT INTO {T("worker_runs")}(run_id, pid, process_start_utc, started_at_utc, trigger, machine) VALUES(@r,@p,@ps,@s,@t,@m) ON CONFLICT(run_id) DO NOTHING",
            ("r", runId), ("p", pid), ("ps", U(processStart)), ("s", DateTimeOffset.UtcNow), ("t", trigger), ("m", Environment.MachineName));
    }

    public void FinishRun(string runId, string outcome, long? jobId, string? message)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("worker_runs")} SET finished_at_utc=@f, outcome=@o, job_id=@j, message=@m WHERE run_id=@r",
            ("r", runId), ("f", DateTimeOffset.UtcNow), ("o", outcome), ("j", jobId), ("m", message));
    }

    public void Heartbeat(string machine, bool? appOpen, string? workerState, long? jobId, string? detail, string? version)
    {
        using var c = Open();
        Exec(c, null, $"""
INSERT INTO {T("machines")}(machine, last_seen_utc, app_open, app_seen_utc, worker_state, worker_job_id, worker_detail, worker_seen_utc, version)
VALUES (@m, now(), COALESCE(@app::boolean, FALSE), CASE WHEN @app::boolean IS NOT NULL THEN now() END,
        @ws::text, @job::bigint, @det::text, CASE WHEN @ws::text IS NOT NULL THEN now() END, @ver::text)
ON CONFLICT (account_id, machine) DO UPDATE SET
  last_seen_utc = now(),
  app_open = COALESCE(@app::boolean, {T("machines")}.app_open),
  app_seen_utc = CASE WHEN @app::boolean IS NOT NULL THEN now() ELSE {T("machines")}.app_seen_utc END,
  worker_state = COALESCE(@ws::text, {T("machines")}.worker_state),
  worker_job_id = CASE WHEN @ws::text IS NOT NULL THEN @job::bigint ELSE {T("machines")}.worker_job_id END,
  worker_detail = CASE WHEN @ws::text IS NOT NULL THEN @det::text ELSE {T("machines")}.worker_detail END,
  worker_seen_utc = CASE WHEN @ws::text IS NOT NULL THEN now() ELSE {T("machines")}.worker_seen_utc END,
  version = COALESCE(@ver::text, {T("machines")}.version)
""", ("m", machine), ("app", appOpen), ("ws", workerState), ("job", jobId), ("det", detail), ("ver", version));
    }

    public bool TakeCheckRequest(string machine)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"UPDATE {T("machines")} SET check_requested = FALSE WHERE machine = @m AND check_requested = TRUE", ("m", machine));
        return cmd.ExecuteNonQuery() > 0;
    }

    public (long Id, string Name, string Kind, string Url, long Size)? ClaimTemplateUpload(string machine)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
UPDATE {T("template_uploads")} SET status = 'downloading', progress = 0, updated_at_utc = now()
WHERE id = (SELECT id FROM {T("template_uploads")} WHERE machine = @m AND status = 'pending' ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
RETURNING id, name, kind, download_url, size_bytes
""", ("m", machine));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetInt64(4)) : null;
    }

    public void UpdateTemplateUpload(long id, string status, int progress, string? message)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("template_uploads")} SET status=@s, progress=@p, message=@msg, updated_at_utc=now() WHERE id=@i",
            ("i", id), ("s", status), ("p", progress), ("msg", message));
    }

    public void ReplaceTemplates(string machine, IReadOnlyList<(string Name, string Kind, long Size, DateTimeOffset? Added, string Source, string Notes)> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, $"DELETE FROM {T("templates")} WHERE machine = @m", ("m", machine));
        foreach (var t in items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            Exec(c, tx, $"INSERT INTO {T("templates")}(machine, name, kind, size_bytes, added_at_utc, source, notes) VALUES(@m,@n,@k,@sz,@at::timestamptz,@src,@no) ON CONFLICT (account_id, machine, name) DO NOTHING",
                ("m", machine), ("n", t.Name), ("k", t.Kind), ("sz", t.Size), ("at", t.Added?.ToUniversalTime()), ("src", t.Source), ("no", t.Notes));
        tx.Commit();
    }

    public IReadOnlyList<(long Id, string Kind, string Arg)> TakeCommands(string machine)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"UPDATE {T("machine_commands")} SET done = TRUE WHERE machine = @m AND NOT done RETURNING id, kind, arg", ("m", machine));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list.OrderBy(x => x.Item1).ToList();
    }

    public bool TakeRunRequest(string machine)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"UPDATE {T("machines")} SET run_requested = FALSE WHERE machine = @m AND run_requested = TRUE", ("m", machine));
        return cmd.ExecuteNonQuery() > 0;
    }

    public void SaveCheckResult(string machine, string json)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("machines")} SET check_result_json = @j, check_finished_at_utc = now() WHERE machine = @m", ("m", machine), ("j", json));
    }

    public (long JobId, string Topic, int Status)? CurrentWork(string runId)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT id, topic, status FROM {T("jobs")} WHERE claimed_by = @r ORDER BY claimed_at_utc DESC LIMIT 1", ("r", runId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1), r.GetInt32(2)) : null;
    }

    public void SaveRunStats(string runId, string json)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("worker_runs")} SET stats_json=@s WHERE run_id=@r", ("r", runId), ("s", json));
    }

    public IReadOnlyList<(string RunId, DateTimeOffset Started, DateTimeOffset? Finished, string? Outcome, string? Machine, string? StatsJson)> RunStats(int n = 30)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT run_id, started_at_utc, finished_at_utc, outcome, machine, stats_json FROM {T("worker_runs")} ORDER BY started_at_utc DESC LIMIT @n", ("n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTimeOffset, DateTimeOffset?, string?, string?, string?)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public IReadOnlyList<(string RunId, DateTimeOffset Started, DateTimeOffset? Finished, string? Outcome, long? JobId, string? Message, string? Trigger)> RecentRuns(int n = 20)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT run_id, started_at_utc, finished_at_utc, outcome, job_id, message, COALESCE(trigger,'') || COALESCE(' @' || machine, '') FROM {T("worker_runs")} ORDER BY started_at_utc DESC LIMIT @n", ("n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTimeOffset, DateTimeOffset?, string?, long?, string?, string?)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT value FROM {T("shared_settings")} WHERE key=@k", ("k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        Exec(c, null, $"INSERT INTO {T("shared_settings")}(key,value,updated_at_utc) VALUES(@k,@v,@t) ON CONFLICT(account_id, key) DO UPDATE SET value=excluded.value, updated_at_utc=excluded.updated_at_utc",
            ("k", key), ("v", value), ("t", DateTimeOffset.UtcNow));
    }

    public void Requeue(long jobId, bool resetAttempts)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET status={(int)JobStatus.Queued}, cancel_requested=FALSE, next_attempt_at_utc=NULL, attention_reason=NULL, " +
                      (resetAttempts ? "run_attempts=0, " : "") + "updated_at_utc=@now WHERE id=@id AND claimed_by IS NULL",
            ("id", jobId), ("now", DateTimeOffset.UtcNow));
    }

    /// <summary>Online bazada mavzu umuman bormi (birinchi ulanishda lokal mavzularni ko'chirish uchun).</summary>
    public long CountJobs()
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT COUNT(*) FROM {T("jobs")}");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Lokal SQLite'dagi mavzular va bosqichlarini online bazaga ko'chiradi (faqat online baza bo'sh bo'lsa).</summary>
    public int ImportFrom(SqliteJobStore local)
    {
        if (CountJobs() > 0) return 0;
        int n = 0;
        foreach (var j in local.AllJobs().OrderBy(j => j.CreatedAtUtc))
        {
            var oldId = j.Id;
            j.ClaimedBy = null;
            var newId = Insert(j);
            foreach (var st in local.GetStages(oldId)) { st.JobId = newId; SaveStage(st); }
            n++;
        }
        return n;
    }

    /// <summary>Ulanishni tekshiradi (UI uchun).</summary>
    public static (bool Ok, string Message) Check(string connectionString)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 10, Pooling = false };
            using var c = new NpgsqlConnection(b.ConnectionString);
            c.Open();
            using var cmd = new NpgsqlCommand("select current_database() || ' / PostgreSQL ' || current_setting('server_version')", c);
            return (true, $"Ulandi: {b.Host} — {cmd.ExecuteScalar()}");
        }
        catch (Exception e) { return (false, "Ulanib bo'lmadi: " + e.Message.Split('\n')[0]); }
    }
}

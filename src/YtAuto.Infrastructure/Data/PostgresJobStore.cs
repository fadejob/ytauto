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
            Pooling = true, MaxPoolSize = 5, Timeout = 15, CommandTimeout = 30, KeepAlive = 30,
        };
        if (b.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer) b.SslMode = SslMode.Require;
        _ds = NpgsqlDataSource.Create(b.ConnectionString);
        _schema = schema;
        Kind = $"Online (PostgreSQL: {b.Host})";
        Migrate();
    }

    private string T(string table) => $"\"{_schema}\".{table}";

    private NpgsqlConnection Open() => _ds.OpenConnection();

    private void Migrate()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, $"""
CREATE SCHEMA IF NOT EXISTS "{_schema}";
CREATE TABLE IF NOT EXISTS {T("jobs")} (
  id BIGSERIAL PRIMARY KEY,
  topic TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', language TEXT NOT NULL, target_duration_sec INT NOT NULL DEFAULT 0,
  format INT NOT NULL, template_name TEXT NOT NULL DEFAULT '', voice_profile TEXT NOT NULL DEFAULT '',
  publish_at_utc TIMESTAMPTZ NOT NULL, status INT NOT NULL, created_at_utc TIMESTAMPTZ NOT NULL, updated_at_utc TIMESTAMPTZ NOT NULL,
  run_attempts INT NOT NULL DEFAULT 0, next_attempt_at_utc TIMESTAMPTZ, claimed_by TEXT, claimed_at_utc TIMESTAMPTZ,
  cancel_requested BOOLEAN NOT NULL DEFAULT FALSE, qa_fix_cycles INT NOT NULL DEFAULT 0,
  last_error_code TEXT, last_error_message TEXT, attention_reason TEXT, settings_snapshot TEXT,
  title TEXT, description TEXT, thumbnail_path TEXT, video_archive_path TEXT, audio_archive_path TEXT, subtitle_archive_path TEXT,
  audio_duration_sec DOUBLE PRECISION, youtube_video_id TEXT, youtube_privacy TEXT, youtube_upload_status TEXT, youtube_processing_status TEXT,
  youtube_checked_at_utc TIMESTAMPTZ, published_at_utc TIMESTAMPTZ, work_dir TEXT, work_dir_cleaned BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE INDEX IF NOT EXISTS ix_jobs_queue ON {T("jobs")}(status, created_at_utc, id);
CREATE TABLE IF NOT EXISTS {T("job_stages")} (
  job_id BIGINT NOT NULL REFERENCES {T("jobs")}(id) ON DELETE CASCADE, stage INT NOT NULL, state INT NOT NULL,
  started_at_utc TIMESTAMPTZ, finished_at_utc TIMESTAMPTZ, attempts INT NOT NULL DEFAULT 0, input_hash TEXT, outputs_json TEXT,
  error_code TEXT, error_message TEXT, duration_ms BIGINT, metrics_json TEXT, PRIMARY KEY(job_id, stage)
);
CREATE TABLE IF NOT EXISTS {T("job_events")} (
  id BIGSERIAL PRIMARY KEY, job_id BIGINT NOT NULL REFERENCES {T("jobs")}(id) ON DELETE CASCADE,
  at_utc TIMESTAMPTZ NOT NULL, level TEXT NOT NULL, stage TEXT, message TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_events_job ON {T("job_events")}(job_id, id);
CREATE TABLE IF NOT EXISTS {T("owned_processes")} (
  id BIGSERIAL PRIMARY KEY, job_id BIGINT, run_id TEXT NOT NULL, stage TEXT, tool TEXT, pid INT NOT NULL,
  process_start_utc TIMESTAMPTZ NOT NULL, exe_path TEXT, ended_at_utc TIMESTAMPTZ, end_reason TEXT, peak_memory_bytes BIGINT
);
CREATE TABLE IF NOT EXISTS {T("worker_runs")} (
  run_id TEXT PRIMARY KEY, pid INT NOT NULL, process_start_utc TIMESTAMPTZ NOT NULL, started_at_utc TIMESTAMPTZ NOT NULL,
  finished_at_utc TIMESTAMPTZ, outcome TEXT, job_id BIGINT, message TEXT, trigger TEXT, machine TEXT
);
CREATE TABLE IF NOT EXISTS {T("shared_settings")} (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at_utc TIMESTAMPTZ NOT NULL);
""");
        tx.Commit();
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

    private const string JobColumns = "id, topic, notes, language, target_duration_sec, format, template_name, voice_profile, publish_at_utc, status, created_at_utc, updated_at_utc, run_attempts, next_attempt_at_utc, claimed_by, claimed_at_utc, cancel_requested, qa_fix_cycles, last_error_code, last_error_message, attention_reason, settings_snapshot, title, description, thumbnail_path, video_archive_path, audio_archive_path, subtitle_archive_path, audio_duration_sec, youtube_video_id, youtube_privacy, youtube_upload_status, youtube_processing_status, youtube_checked_at_utc, published_at_utc, work_dir, work_dir_cleaned";

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
 youtube_processing_status, youtube_checked_at_utc, published_at_utc, work_dir, work_dir_cleaned)
VALUES(@topic,@notes,@language,@dur,@format,@tpl,@voice,@pub,@status,@created,@updated,@attempts,@next,@cancel,@qa,@errc,@errm,@attn,@snap,@title,@desc,
 @thumb,@va,@aa,@sa,@adur,@yid,@ypriv,@yup,@yproc,@ychk,@pubd,@wd,@wdc) RETURNING id
""", c);
        if (job.CreatedAtUtc == default) job.CreatedAtUtc = DateTimeOffset.UtcNow;
        BindJob(cmd, job);
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
        using var c = Open();
        using var cmd = Cmd(c, $"""
UPDATE {T("jobs")} SET claimed_by=@run, claimed_at_utc=@now, updated_at_utc=@now
WHERE id = (
  SELECT id FROM {T("jobs")}
  WHERE status = ANY(@st) AND claimed_by IS NULL AND cancel_requested = FALSE
    AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)
  ORDER BY created_at_utc, id LIMIT 1 FOR UPDATE SKIP LOCKED)
  AND claimed_by IS NULL
RETURNING {JobColumns}
""", ("run", runId), ("now", U(nowUtc)), ("st", JobStateMachine.ClaimableStatuses.Select(s => (int)s).ToArray()));
        return ReadJobs(cmd).FirstOrDefault();
    }

    public void ReleaseClaim(long jobId, string runId)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET claimed_by=NULL, claimed_at_utc=NULL WHERE id=@id AND claimed_by=@run", ("id", jobId), ("run", runId));
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
        Exec(c, null, $"INSERT INTO {T("shared_settings")}(key,value,updated_at_utc) VALUES(@k,@v,@t) ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at_utc=excluded.updated_at_utc",
            ("k", key), ("v", value), ("t", DateTimeOffset.UtcNow));
    }

    public void Requeue(long jobId, bool resetAttempts)
    {
        using var c = Open();
        Exec(c, null, $"UPDATE {T("jobs")} SET status={(int)JobStatus.Queued}, cancel_requested=FALSE, next_attempt_at_utc=NULL, attention_reason=NULL, " +
                      (resetAttempts ? "run_attempts=0, qa_fix_cycles=0, " : "") + "updated_at_utc=@now WHERE id=@id AND claimed_by IS NULL",
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

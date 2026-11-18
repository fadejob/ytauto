using System.Globalization;
using Microsoft.Data.Sqlite;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Data;

/// <summary>
/// SQLite (WAL) ombori. Har amal - alohida qisqa ulanish va tranzaksiya: UI va worker bir vaqtda ishlaydi.
/// Vazifani band qilish bitta atomar UPDATE ... RETURNING bilan.
/// </summary>
public sealed class SqliteJobStore : IAppStore
{
    private readonly string _cs;
    public const int SchemaVersion = 1;

    public SqliteJobStore(string dbPath)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = true, DefaultTimeout = 30 }.ToString();
        Migrate();
    }

    internal SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec(c, "CREATE TABLE IF NOT EXISTS app_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        var v = Scalar(c, "SELECT value FROM app_meta WHERE key='schema_version'") as string;
        int version = v is null ? 0 : int.Parse(v, CultureInfo.InvariantCulture);
        if (version < 1)
        {
            using var tx = c.BeginTransaction();
            Exec(c, """
CREATE TABLE IF NOT EXISTS jobs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  topic TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', language TEXT NOT NULL, target_duration_sec INTEGER NOT NULL,
  format INTEGER NOT NULL, template_name TEXT NOT NULL DEFAULT '', voice_profile TEXT NOT NULL DEFAULT '',
  publish_at_utc TEXT NOT NULL, status INTEGER NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
  run_attempts INTEGER NOT NULL DEFAULT 0, next_attempt_at_utc TEXT, claimed_by TEXT, claimed_at_utc TEXT,
  cancel_requested INTEGER NOT NULL DEFAULT 0, qa_fix_cycles INTEGER NOT NULL DEFAULT 0,
  last_error_code TEXT, last_error_message TEXT, attention_reason TEXT, settings_snapshot TEXT,
  title TEXT, description TEXT, thumbnail_path TEXT, video_archive_path TEXT, audio_archive_path TEXT, subtitle_archive_path TEXT,
  audio_duration_sec REAL, youtube_video_id TEXT, youtube_privacy TEXT, youtube_upload_status TEXT, youtube_processing_status TEXT,
  youtube_checked_at_utc TEXT, published_at_utc TEXT, work_dir TEXT, work_dir_cleaned INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_jobs_queue ON jobs(status, created_at_utc, id);
CREATE TABLE IF NOT EXISTS job_stages (
  job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE, stage INTEGER NOT NULL, state INTEGER NOT NULL,
  started_at_utc TEXT, finished_at_utc TEXT, attempts INTEGER NOT NULL DEFAULT 0, input_hash TEXT, outputs_json TEXT,
  error_code TEXT, error_message TEXT, duration_ms INTEGER, metrics_json TEXT,
  PRIMARY KEY(job_id, stage)
);
CREATE TABLE IF NOT EXISTS job_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  at_utc TEXT NOT NULL, level TEXT NOT NULL, stage TEXT, message TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_events_job ON job_events(job_id, id);
CREATE TABLE IF NOT EXISTS owned_processes (
  id INTEGER PRIMARY KEY AUTOINCREMENT, job_id INTEGER, run_id TEXT NOT NULL, stage TEXT, tool TEXT, pid INTEGER NOT NULL,
  process_start_utc TEXT NOT NULL, exe_path TEXT, ended_at_utc TEXT, end_reason TEXT, peak_memory_bytes INTEGER
);
CREATE TABLE IF NOT EXISTS worker_runs (
  run_id TEXT PRIMARY KEY, pid INTEGER NOT NULL, process_start_utc TEXT NOT NULL, started_at_utc TEXT NOT NULL,
  finished_at_utc TEXT, outcome TEXT, job_id INTEGER, message TEXT, trigger TEXT
);
CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
""", tx);
            Exec(c, "INSERT OR REPLACE INTO app_meta(key,value) VALUES('schema_version','1')", tx);
            tx.Commit();
        }
        if (version < 2)
        {
            // v2: "Hozir chiqarish" so'rovi.
            using var tx = c.BeginTransaction();
            Exec(c, "ALTER TABLE jobs ADD COLUMN publish_now INTEGER NOT NULL DEFAULT 0", tx);
            Exec(c, "INSERT OR REPLACE INTO app_meta(key,value) VALUES('schema_version','2')", tx);
            tx.Commit();
        }
        if (version < 3)
        {
            // v3: "Tayyor videoni YouTube'ga yuklash" so'rovi (bot).
            using var tx = c.BeginTransaction();
            Exec(c, "ALTER TABLE jobs ADD COLUMN upload_requested INTEGER NOT NULL DEFAULT 0", tx);
            Exec(c, "INSERT OR REPLACE INTO app_meta(key,value) VALUES('schema_version','3')", tx);
            tx.Commit();
        }
        if (version < 4)
        {
            // v4: har worker ishga tushishidagi resurs/hajm statistikasi (JSON).
            using var tx = c.BeginTransaction();
            Exec(c, "ALTER TABLE worker_runs ADD COLUMN stats_json TEXT", tx);
            Exec(c, "INSERT OR REPLACE INTO app_meta(key,value) VALUES('schema_version','4')", tx);
            tx.Commit();
        }
        if (version < 5)
        {
            // v5: qo'lda kiritilgan subtitr/title va tayyor ovoz (online bazadagi kabi; lokal rejimda faqat o'qiladi).
            using var tx = c.BeginTransaction();
            Exec(c, "ALTER TABLE jobs ADD COLUMN manual_script TEXT", tx);
            Exec(c, "ALTER TABLE jobs ADD COLUMN manual_title TEXT", tx);
            Exec(c, "ALTER TABLE jobs ADD COLUMN manual_audio_url TEXT", tx);
            Exec(c, "INSERT OR REPLACE INTO app_meta(key,value) VALUES('schema_version','5')", tx);
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------ helpers
    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    internal static string? Ts(DateTimeOffset? t) => t?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ParseTs(object? o) => o is string s && s.Length > 0 ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
    private static string? Str(SqliteDataReader r, string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetString(i); }

    private const string JobColumns = "id, topic, notes, language, target_duration_sec, format, template_name, voice_profile, publish_at_utc, status, created_at_utc, updated_at_utc, run_attempts, next_attempt_at_utc, claimed_by, claimed_at_utc, cancel_requested, qa_fix_cycles, last_error_code, last_error_message, attention_reason, settings_snapshot, title, description, thumbnail_path, video_archive_path, audio_archive_path, subtitle_archive_path, audio_duration_sec, youtube_video_id, youtube_privacy, youtube_upload_status, youtube_processing_status, youtube_checked_at_utc, published_at_utc, work_dir, work_dir_cleaned, publish_now, manual_script, manual_title, manual_audio_url";

    private static VideoJob ReadJob(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Topic = Str(r, "topic") ?? "", Notes = Str(r, "notes") ?? "", Language = Str(r, "language") ?? "en",
        TargetDurationSec = r.GetInt32(r.GetOrdinal("target_duration_sec")), Format = (VideoFormat)r.GetInt32(r.GetOrdinal("format")),
        TemplateName = Str(r, "template_name") ?? "", VoiceProfileName = Str(r, "voice_profile") ?? "",
        PublishAtUtc = ParseTs(Str(r, "publish_at_utc"))!.Value, Status = (JobStatus)r.GetInt32(r.GetOrdinal("status")),
        CreatedAtUtc = ParseTs(Str(r, "created_at_utc"))!.Value, UpdatedAtUtc = ParseTs(Str(r, "updated_at_utc"))!.Value,
        RunAttempts = r.GetInt32(r.GetOrdinal("run_attempts")), NextAttemptAtUtc = ParseTs(Str(r, "next_attempt_at_utc")),
        ClaimedBy = Str(r, "claimed_by"), ClaimedAtUtc = ParseTs(Str(r, "claimed_at_utc")),
        CancelRequested = r.GetInt32(r.GetOrdinal("cancel_requested")) != 0, QaFixCycles = r.GetInt32(r.GetOrdinal("qa_fix_cycles")),
        LastErrorCode = Str(r, "last_error_code"), LastErrorMessage = Str(r, "last_error_message"), AttentionReason = Str(r, "attention_reason"),
        SettingsSnapshotJson = Str(r, "settings_snapshot"), Title = Str(r, "title"), Description = Str(r, "description"),
        ThumbnailPath = Str(r, "thumbnail_path"), VideoArchivePath = Str(r, "video_archive_path"), AudioArchivePath = Str(r, "audio_archive_path"),
        SubtitleArchivePath = Str(r, "subtitle_archive_path"),
        AudioDurationSec = r.IsDBNull(r.GetOrdinal("audio_duration_sec")) ? null : r.GetDouble(r.GetOrdinal("audio_duration_sec")),
        YouTubeVideoId = Str(r, "youtube_video_id"), YouTubePrivacy = Str(r, "youtube_privacy"), YouTubeUploadStatus = Str(r, "youtube_upload_status"),
        YouTubeProcessingStatus = Str(r, "youtube_processing_status"), YouTubeCheckedAtUtc = ParseTs(Str(r, "youtube_checked_at_utc")),
        PublishedAtUtc = ParseTs(Str(r, "published_at_utc")), WorkDir = Str(r, "work_dir"), WorkDirCleaned = r.GetInt32(r.GetOrdinal("work_dir_cleaned")) != 0,
        PublishNow = r.GetInt32(r.GetOrdinal("publish_now")) != 0,
        ManualScript = Str(r, "manual_script"), ManualTitle = Str(r, "manual_title"), ManualAudioUrl = Str(r, "manual_audio_url"),
    };

    private static void BindJob(SqliteCommand cmd, VideoJob j)
    {
        var p = cmd.Parameters;
        p.AddWithValue("$topic", j.Topic); p.AddWithValue("$notes", j.Notes ?? ""); p.AddWithValue("$language", j.Language);
        p.AddWithValue("$dur", j.TargetDurationSec); p.AddWithValue("$format", (int)j.Format); p.AddWithValue("$tpl", j.TemplateName ?? "");
        p.AddWithValue("$voice", j.VoiceProfileName ?? ""); p.AddWithValue("$pub", Ts(j.PublishAtUtc)); p.AddWithValue("$status", (int)j.Status);
        p.AddWithValue("$created", Ts(j.CreatedAtUtc)); p.AddWithValue("$updated", Ts(DateTimeOffset.UtcNow));
        p.AddWithValue("$attempts", j.RunAttempts); p.AddWithValue("$next", (object?)Ts(j.NextAttemptAtUtc) ?? DBNull.Value);
        p.AddWithValue("$cancel", j.CancelRequested ? 1 : 0); p.AddWithValue("$qa", j.QaFixCycles);
        p.AddWithValue("$errc", (object?)j.LastErrorCode ?? DBNull.Value); p.AddWithValue("$errm", (object?)j.LastErrorMessage ?? DBNull.Value);
        p.AddWithValue("$attn", (object?)j.AttentionReason ?? DBNull.Value); p.AddWithValue("$snap", (object?)j.SettingsSnapshotJson ?? DBNull.Value);
        p.AddWithValue("$title", (object?)j.Title ?? DBNull.Value); p.AddWithValue("$desc", (object?)j.Description ?? DBNull.Value);
        p.AddWithValue("$thumb", (object?)j.ThumbnailPath ?? DBNull.Value); p.AddWithValue("$va", (object?)j.VideoArchivePath ?? DBNull.Value);
        p.AddWithValue("$aa", (object?)j.AudioArchivePath ?? DBNull.Value); p.AddWithValue("$sa", (object?)j.SubtitleArchivePath ?? DBNull.Value);
        p.AddWithValue("$adur", (object?)j.AudioDurationSec ?? DBNull.Value); p.AddWithValue("$yid", (object?)j.YouTubeVideoId ?? DBNull.Value);
        p.AddWithValue("$ypriv", (object?)j.YouTubePrivacy ?? DBNull.Value); p.AddWithValue("$yup", (object?)j.YouTubeUploadStatus ?? DBNull.Value);
        p.AddWithValue("$yproc", (object?)j.YouTubeProcessingStatus ?? DBNull.Value); p.AddWithValue("$ychk", (object?)Ts(j.YouTubeCheckedAtUtc) ?? DBNull.Value);
        p.AddWithValue("$pubd", (object?)Ts(j.PublishedAtUtc) ?? DBNull.Value); p.AddWithValue("$wd", (object?)j.WorkDir ?? DBNull.Value);
        p.AddWithValue("$wdc", j.WorkDirCleaned ? 1 : 0);
    }

    // ------------------------------------------------------------------ IJobStore
    public long Insert(VideoJob job)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (job.CreatedAtUtc == default) job.CreatedAtUtc = DateTimeOffset.UtcNow;
        cmd.CommandText = """
INSERT INTO jobs(topic, notes, language, target_duration_sec, format, template_name, voice_profile, publish_at_utc, status, created_at_utc, updated_at_utc,
 run_attempts, next_attempt_at_utc, cancel_requested, qa_fix_cycles, last_error_code, last_error_message, attention_reason, settings_snapshot, title, description,
 thumbnail_path, video_archive_path, audio_archive_path, subtitle_archive_path, audio_duration_sec, youtube_video_id, youtube_privacy, youtube_upload_status,
 youtube_processing_status, youtube_checked_at_utc, published_at_utc, work_dir, work_dir_cleaned, publish_now, manual_script, manual_title, manual_audio_url)
VALUES($topic,$notes,$language,$dur,$format,$tpl,$voice,$pub,$status,$created,$updated,$attempts,$next,$cancel,$qa,$errc,$errm,$attn,$snap,$title,$desc,
 $thumb,$va,$aa,$sa,$adur,$yid,$ypriv,$yup,$yproc,$ychk,$pubd,$wd,$wdc,$pn,$ms,$mt,$ma) RETURNING id;
""";
        BindJob(cmd, job);
        cmd.Parameters.AddWithValue("$pn", job.PublishNow ? 1 : 0);
        cmd.Parameters.AddWithValue("$ms", (object?)job.ManualScript ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mt", (object?)job.ManualTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ma", (object?)job.ManualAudioUrl ?? DBNull.Value);
        job.Id = (long)cmd.ExecuteScalar()!;
        return job.Id;
    }

    public VideoJob? Get(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {JobColumns} FROM jobs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadJob(r) : null;
    }

    /// <summary>Barcha maydonlarni yangilaydi. claimed_by/cancel_requested bu yerda o'zgartirilmaydi (alohida atomar amallar).</summary>
    public void Update(VideoJob job)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
UPDATE jobs SET topic=$topic, notes=$notes, language=$language, target_duration_sec=$dur, format=$format, template_name=$tpl, voice_profile=$voice,
 publish_at_utc=$pub, status=$status, updated_at_utc=$updated, run_attempts=$attempts, next_attempt_at_utc=$next, qa_fix_cycles=$qa,
 last_error_code=$errc, last_error_message=$errm, attention_reason=$attn, settings_snapshot=$snap, title=$title, description=$desc, thumbnail_path=$thumb,
 video_archive_path=$va, audio_archive_path=$aa, subtitle_archive_path=$sa, audio_duration_sec=$adur, youtube_video_id=$yid, youtube_privacy=$ypriv,
 youtube_upload_status=$yup, youtube_processing_status=$yproc, youtube_checked_at_utc=$ychk, published_at_utc=$pubd, work_dir=$wd, work_dir_cleaned=$wdc
WHERE id=$id;
""";
        BindJob(cmd, job);
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.ExecuteNonQuery();
    }

    public VideoJob? ClaimNext(string runId, DateTimeOffset nowUtc)
    {
        var statuses = string.Join(",", JobStateMachine.ClaimableStatuses.Select(s => (int)s));
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Bitta atomar UPDATE: chiqish vaqti eng yaqin yaroqli vazifa (teng bo'lsa eng eskisi), hali hech kim band qilmagan bo'lsa.
        // Chiqish vaqti uzoq bo'lsa ham vazifa olinadi: video oldindan tayyorlanadi, YouTube vaqti kelganda o'zi public qiladi.
        cmd.CommandText = $"""
UPDATE jobs SET claimed_by=$run, claimed_at_utc=$now, updated_at_utc=$now
WHERE id = (
  SELECT id FROM jobs
  WHERE status IN ({statuses}) AND claimed_by IS NULL AND cancel_requested = 0
    AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now)
  ORDER BY publish_at_utc, created_at_utc, id LIMIT 1)
  AND claimed_by IS NULL
RETURNING {JobColumns};
""";
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$now", Ts(nowUtc));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadJob(r) : null;
    }

    private static string ClaimableIn => string.Join(",", JobStateMachine.ClaimableStatuses.Select(s => (int)s));
    private static string NotFinalIn => $"{(int)JobStatus.Published},{(int)JobStatus.Cancelled}";

    public int CountPending(DateTimeOffset nowUtc)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, $"SELECT COUNT(*) FROM jobs WHERE status IN ({ClaimableIn}) AND cancel_requested = 0 AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now)",
            ("$now", Ts(nowUtc))));
    }

    public int CountPublishNow(DateTimeOffset nowUtc)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, $"SELECT COUNT(*) FROM jobs WHERE publish_now = 1 AND status NOT IN ({NotFinalIn}) AND claimed_by IS NULL AND cancel_requested = 0 AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now)",
            ("$now", Ts(nowUtc))));
    }

    public VideoJob? ClaimPublishNow(string runId, DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
UPDATE jobs SET claimed_by=$run, claimed_at_utc=$now, updated_at_utc=$now
WHERE id = (
  SELECT id FROM jobs
  WHERE publish_now = 1 AND status NOT IN ({NotFinalIn}) AND claimed_by IS NULL AND cancel_requested = 0
    AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now)
  ORDER BY id LIMIT 1)
  AND claimed_by IS NULL
RETURNING {JobColumns};
""";
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$now", Ts(nowUtc));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadJob(r) : null;
    }

    public void SetPublishNow(long jobId, bool value)
    {
        using var c = Open();
        // So'rov qo'yilganda backoff kutilmaydi: foydalanuvchi aniq buyruq berdi.
        Exec(c, "UPDATE jobs SET publish_now=$v, updated_at_utc=$now" + (value ? ", next_attempt_at_utc=NULL" : "") + " WHERE id=$id", null,
            ("$v", value ? 1 : 0), ("$now", Ts(DateTimeOffset.UtcNow)), ("$id", jobId));
    }

    public IReadOnlyList<long> UploadRequestIds()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id FROM jobs WHERE upload_requested = 1 AND youtube_video_id IS NULL AND video_archive_path IS NOT NULL AND claimed_by IS NULL AND cancel_requested = 0 AND status NOT IN ({NotFinalIn}) ORDER BY id";
        var ids = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    public void SetUploadRequested(long jobId, bool value)
    {
        using var c = Open();
        Exec(c, "UPDATE jobs SET upload_requested=$v, updated_at_utc=$now WHERE id=$id", null,
            ("$v", value ? 1 : 0), ("$now", Ts(DateTimeOffset.UtcNow)), ("$id", jobId));
    }

    public bool TryClaim(long jobId, string runId, DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET claimed_by=$run, claimed_at_utc=$now WHERE id=$id AND claimed_by IS NULL";
        cmd.Parameters.AddWithValue("$run", runId); cmd.Parameters.AddWithValue("$now", Ts(nowUtc)); cmd.Parameters.AddWithValue("$id", jobId);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void ReleaseClaim(long jobId, string runId)
    {
        using var c = Open();
        Exec(c, "UPDATE jobs SET claimed_by=NULL, claimed_at_utc=NULL WHERE id=$id AND claimed_by=$run", null, ("$id", jobId), ("$run", runId));
    }

    public int ReleaseStaleClaims(string currentRunId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE jobs SET claimed_by=NULL, claimed_at_utc=NULL WHERE claimed_by IS NOT NULL AND claimed_by <> $run";
        cmd.Parameters.AddWithValue("$run", currentRunId);
        var n = cmd.ExecuteNonQuery();
        // Uzilib qolgan "Running" bosqichlarni Failed deb belgilaymiz.
        Exec(c, "UPDATE job_stages SET state=$failed, error_code='interrupted', error_message='Worker yoki kompyuter kutilmaganda to''xtadi', finished_at_utc=$now WHERE state=$running",
            tx, ("$failed", (int)StageState.Failed), ("$running", (int)StageState.Running), ("$now", Ts(DateTimeOffset.UtcNow)));
        tx.Commit();
        return n;
    }

    public bool IsCancelRequested(long jobId)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT cancel_requested FROM jobs WHERE id=$id", ("$id", jobId)) ?? 0) != 0;
    }

    public void RequestCancel(long jobId)
    {
        using var c = Open();
        Exec(c, "UPDATE jobs SET cancel_requested=1, updated_at_utc=$now WHERE id=$id", null, ("$id", jobId), ("$now", Ts(DateTimeOffset.UtcNow)));
    }

    /// <summary>Hech kim band qilmagan vazifani darhol bekor qiladi (worker ishlayotgan bo'lsa false).</summary>
    public bool TryCancelUnclaimed(long jobId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status=$st, cancel_requested=1, updated_at_utc=$now WHERE id=$id AND claimed_by IS NULL AND status NOT IN ($pub)";
        cmd.Parameters.AddWithValue("$st", (int)JobStatus.Cancelled); cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", jobId); cmd.Parameters.AddWithValue("$pub", (int)JobStatus.Published);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool IsClaimed(long jobId)
    {
        using var c = Open();
        return Scalar(c, "SELECT claimed_by FROM jobs WHERE id=$id", ("$id", jobId)) is string;
    }

    public StageRecord GetStage(long jobId, StageKind stage)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM job_stages WHERE job_id=$j AND stage=$s";
        cmd.Parameters.AddWithValue("$j", jobId); cmd.Parameters.AddWithValue("$s", (int)stage);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadStage(r) : new StageRecord { JobId = jobId, Stage = stage, State = StageState.Pending };
    }

    public IReadOnlyList<StageRecord> GetStages(long jobId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM job_stages WHERE job_id=$j ORDER BY stage";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var r = cmd.ExecuteReader();
        var list = new List<StageRecord>();
        while (r.Read()) list.Add(ReadStage(r));
        return list;
    }

    private static StageRecord ReadStage(SqliteDataReader r) => new()
    {
        JobId = r.GetInt64(r.GetOrdinal("job_id")), Stage = (StageKind)r.GetInt32(r.GetOrdinal("stage")), State = (StageState)r.GetInt32(r.GetOrdinal("state")),
        StartedAtUtc = ParseTs(Str(r, "started_at_utc")), FinishedAtUtc = ParseTs(Str(r, "finished_at_utc")), Attempts = r.GetInt32(r.GetOrdinal("attempts")),
        InputHash = Str(r, "input_hash"), OutputsJson = Str(r, "outputs_json"), ErrorCode = Str(r, "error_code"), ErrorMessage = Str(r, "error_message"),
        DurationMs = r.IsDBNull(r.GetOrdinal("duration_ms")) ? null : r.GetInt64(r.GetOrdinal("duration_ms")), MetricsJson = Str(r, "metrics_json"),
    };

    public void SaveStage(StageRecord s)
    {
        using var c = Open();
        Exec(c, """
INSERT INTO job_stages(job_id, stage, state, started_at_utc, finished_at_utc, attempts, input_hash, outputs_json, error_code, error_message, duration_ms, metrics_json)
VALUES($j,$s,$st,$sa,$fa,$at,$ih,$o,$ec,$em,$d,$m)
ON CONFLICT(job_id, stage) DO UPDATE SET state=excluded.state, started_at_utc=excluded.started_at_utc, finished_at_utc=excluded.finished_at_utc,
 attempts=excluded.attempts, input_hash=excluded.input_hash, outputs_json=excluded.outputs_json, error_code=excluded.error_code,
 error_message=excluded.error_message, duration_ms=excluded.duration_ms, metrics_json=excluded.metrics_json;
""", null, ("$j", s.JobId), ("$s", (int)s.Stage), ("$st", (int)s.State), ("$sa", Ts(s.StartedAtUtc)), ("$fa", Ts(s.FinishedAtUtc)), ("$at", s.Attempts),
            ("$ih", s.InputHash), ("$o", s.OutputsJson), ("$ec", s.ErrorCode), ("$em", s.ErrorMessage), ("$d", s.DurationMs), ("$m", s.MetricsJson));
    }

    public void AddEvent(long jobId, string level, string? stage, string message)
    {
        using var c = Open();
        Exec(c, "INSERT INTO job_events(job_id, at_utc, level, stage, message) VALUES($j,$t,$l,$s,$m)", null,
            ("$j", jobId), ("$t", Ts(DateTimeOffset.UtcNow)), ("$l", level), ("$s", stage), ("$m", message));
        // Har vazifa uchun eng ko'pi 2000 hodisa saqlanadi (to'liq log faylda).
        Exec(c, "DELETE FROM job_events WHERE job_id=$j AND id <= (SELECT id FROM job_events WHERE job_id=$j ORDER BY id DESC LIMIT 1 OFFSET 2000)", null, ("$j", jobId));
    }

    public IReadOnlyList<JobEvent> GetEvents(long jobId, int limit = 300)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM (SELECT * FROM job_events WHERE job_id=$j ORDER BY id DESC LIMIT $n) ORDER BY id";
        cmd.Parameters.AddWithValue("$j", jobId); cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<JobEvent>();
        while (r.Read())
            list.Add(new JobEvent { Id = r.GetInt64(0), JobId = r.GetInt64(1), AtUtc = ParseTs(r.GetString(2))!.Value, Level = r.GetString(3), Stage = r.IsDBNull(4) ? null : r.GetString(4), Message = r.GetString(5) });
        return list;
    }

    public IReadOnlyList<VideoJob> ListByStatus(params JobStatus[] statuses)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {JobColumns} FROM jobs WHERE status IN ({string.Join(",", statuses.Select(s => (int)s))}) ORDER BY created_at_utc, id";
        using var r = cmd.ExecuteReader();
        var list = new List<VideoJob>();
        while (r.Read()) list.Add(ReadJob(r));
        return list;
    }

    /// <summary>UI uchun sahifalangan ro'yxat (settings_snapshot va description kabi katta maydonlarsiz emas, lekin LIMIT bilan).</summary>
    public IReadOnlyList<VideoJob> Page(bool released, int offset, int limit, out int total)
    {
        var pub = (int)JobStatus.Published;
        var where = released ? $"status = {pub}" : $"status <> {pub}";
        using var c = Open();
        total = Convert.ToInt32(Scalar(c, $"SELECT COUNT(*) FROM jobs WHERE {where}"));
        using var cmd = c.CreateCommand();
        cmd.CommandText = released
            ? $"SELECT {JobColumns} FROM jobs WHERE {where} ORDER BY publish_at_utc DESC, id DESC LIMIT $l OFFSET $o"
            : $"SELECT {JobColumns} FROM jobs WHERE {where} ORDER BY CASE WHEN status={(int)JobStatus.Cancelled} THEN 1 ELSE 0 END, publish_at_utc, id LIMIT $l OFFSET $o";
        cmd.Parameters.AddWithValue("$l", limit); cmd.Parameters.AddWithValue("$o", offset);
        using var r = cmd.ExecuteReader();
        var list = new List<VideoJob>();
        while (r.Read()) list.Add(ReadJob(r));
        return list;
    }

    public IReadOnlyList<(JobStatus Status, DateTimeOffset PublishAtUtc)> PublishSlots()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT status, publish_at_utc FROM jobs WHERE status <> {(int)JobStatus.Cancelled}";
        using var r = cmd.ExecuteReader();
        var list = new List<(JobStatus, DateTimeOffset)>();
        while (r.Read()) list.Add(((JobStatus)r.GetInt32(0), ParseTs(r.GetString(1))!.Value));
        return list;
    }

    public void RecordProcess(OwnedProcessRecord p)
    {
        using var c = Open();
        Exec(c, "INSERT INTO owned_processes(job_id, run_id, stage, tool, pid, process_start_utc, exe_path, ended_at_utc, end_reason, peak_memory_bytes) VALUES($j,$r,$s,$t,$p,$ps,$e,$ea,$er,$pm)", null,
            ("$j", p.JobId), ("$r", p.RunId), ("$s", p.Stage), ("$t", p.Tool), ("$p", p.Pid), ("$ps", Ts(p.ProcessStartUtc)), ("$e", p.ExePath),
            ("$ea", Ts(p.EndedAtUtc)), ("$er", p.EndReason), ("$pm", p.PeakMemoryBytes));
    }

    // ------------------------------------------------------------------ worker runs
    public void StartRun(string runId, int pid, DateTimeOffset processStart, string trigger)
    {
        using var c = Open();
        Exec(c, "INSERT INTO worker_runs(run_id, pid, process_start_utc, started_at_utc, trigger) VALUES($r,$p,$ps,$s,$t)", null,
            ("$r", runId), ("$p", pid), ("$ps", Ts(processStart)), ("$s", Ts(DateTimeOffset.UtcNow)), ("$t", trigger));
    }

    public void FinishRun(string runId, string outcome, long? jobId, string? message)
    {
        using var c = Open();
        Exec(c, "UPDATE worker_runs SET finished_at_utc=$f, outcome=$o, job_id=$j, message=$m WHERE run_id=$r", null,
            ("$r", runId), ("$f", Ts(DateTimeOffset.UtcNow)), ("$o", outcome), ("$j", jobId), ("$m", message));
    }

    // Heartbeat/tekshiruv so'rovi faqat online bazada ma'noli; lokal rejimda hech narsa qilmaydi.
    public void Heartbeat(string machine, bool? appOpen, string? workerState, long? jobId, string? detail, string? version) { }
    public bool TakeCheckRequest(string machine) => false;
    public bool TakeRunRequest(string machine) => false;
    public IReadOnlyList<(long Id, string Kind, string Arg)> TakeCommands(string machine) => Array.Empty<(long, string, string)>();
    public void ReplaceTemplates(string machine, IReadOnlyList<(string Name, string Kind, long Size, DateTimeOffset? Added, string Source, string Notes)> items) { }
    public (long Id, string Name, string Kind, string Url, long Size)? ClaimTemplateUpload(string machine) => null;
    public void UpdateTemplateUpload(long id, string status, int progress, string? message) { }
    public void SaveCheckResult(string machine, string json) { }
    public (long JobId, string Topic, int Status)? CurrentWork(string runId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, topic, status FROM jobs WHERE claimed_by = $r ORDER BY claimed_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$r", runId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1), r.GetInt32(2)) : null;
    }

    public void SaveRunStats(string runId, string json)
    {
        using var c = Open();
        Exec(c, "UPDATE worker_runs SET stats_json=$s WHERE run_id=$r", null, ("$r", runId), ("$s", json));
    }

    public IReadOnlyList<(string RunId, DateTimeOffset Started, DateTimeOffset? Finished, string? Outcome, string? Machine, string? StatsJson)> RunStats(int n = 30)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT run_id, started_at_utc, finished_at_utc, outcome, stats_json FROM worker_runs ORDER BY started_at_utc DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTimeOffset, DateTimeOffset?, string?, string?, string?)>();
        while (r.Read())
            list.Add((r.GetString(0), ParseTs(r.GetString(1))!.Value, ParseTs(r.IsDBNull(2) ? null : r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3),
                Environment.MachineName, r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    public IReadOnlyList<(string RunId, DateTimeOffset Started, DateTimeOffset? Finished, string? Outcome, long? JobId, string? Message, string? Trigger)> RecentRuns(int n = 20)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT run_id, started_at_utc, finished_at_utc, outcome, job_id, message, trigger FROM worker_runs ORDER BY started_at_utc DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTimeOffset, DateTimeOffset?, string?, long?, string?, string?)>();
        while (r.Read())
            list.Add((r.GetString(0), ParseTs(r.GetString(1))!.Value, ParseTs(r.IsDBNull(2) ? null : r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    // ------------------------------------------------------------------ settings (key/value)
    public string? GetSetting(string key)
    {
        using var c = Open();
        return Scalar(c, "SELECT value FROM settings WHERE key=$k", ("$k", key)) as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO settings(key,value,updated_at_utc) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at_utc=excluded.updated_at_utc",
            null, ("$k", key), ("$v", value), ("$t", Ts(DateTimeOffset.UtcNow)));
    }

    /// <summary>UI amallari (retry, yangi vaqt) uchun: vazifani qayta navbatga qo'yadi, tayyor bosqichlar saqlanadi.</summary>
    public void Requeue(long jobId, bool resetAttempts)
    {
        using var c = Open();
        Exec(c, $"UPDATE jobs SET status={(int)JobStatus.Queued}, cancel_requested=0, next_attempt_at_utc=NULL, attention_reason=NULL, " +
                (resetAttempts ? "run_attempts=0, " : "") + "updated_at_utc=$now WHERE id=$id AND claimed_by IS NULL", null,
            ("$id", jobId), ("$now", Ts(DateTimeOffset.UtcNow)));
    }

    public string Kind => "Lokal (SQLite)";

    public bool Delete(long jobId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE id=$id AND claimed_by IS NULL";
        cmd.Parameters.AddWithValue("$id", jobId);
        return cmd.ExecuteNonQuery() > 0; // job_stages/job_events - ON DELETE CASCADE
    }

    /// <summary>Online bazaga ko'chirish uchun barcha vazifalar.</summary>
    public IReadOnlyList<VideoJob> AllJobs() => ListByStatus(Enum.GetValues<JobStatus>());
}

/// <summary>
/// Sozlamalar: kompyuterga xos qismi (dastur yo'llari, papkalar, ...) lokal SQLite'da;
/// promptlar esa <paramref name="shared"/> (online baza) berilgan bo'lsa global saqlanadi - barcha kompyuterlar uchun bir xil.
/// </summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteJobStore _db;
    private readonly Func<AppSettings> _defaults;
    private readonly IAppStore? _shared;
    public const string ScriptPromptKey = "prompt.script";
    public const string MontagePromptKey = "prompt.montage";
    /// <summary>Jadval (yoqilgan/vaqt/zona/uyg'otish) bot orqali online bazada boshqariladi: JSON.</summary>
    public const string ScheduleKey = "schedule";
    public SqliteSettingsStore(SqliteJobStore db, Func<AppSettings> defaults, IAppStore? shared = null) { _db = db; _defaults = defaults; _shared = shared; }

    public bool PromptsAreGlobal => _shared is not null;

    public AppSettings Load()
    {
        var s = LoadLocal();
        if (_shared is not null)
        {
            var sp = _shared.GetSetting(ScriptPromptKey);
            var mp = _shared.GetSetting(MontagePromptKey);
            var guest = _shared.InGuestScope;
            if (guest)
            {
                // Mehmon hisobi vazifasi: uning o'z promptlari (bo'lmasa standart), kalitlari va kanali. Kompyuter egasining lokal
                // promptlari, Gemini kaliti va YouTube tokeni mehmonga o'tmaydi; mehmon sozlamalariga hech narsa yozilmaydi.
                s.Gemini.ScriptPrompt = sp is null ? DefaultPrompts.Script : MigratePrompt(sp, true);
                s.Claude.MontagePrompt = mp is null ? DefaultPrompts.Montage : MigratePrompt(mp, false);
                s.Gemini.RemoteOnly = true;
                s.YouTube.RemoteOnly = true;
                s.YouTube.ChannelTitle = "";
            }
            else
            {
                // Online bazada hali yo'q bo'lsa - lokaldagisi bir marta yuklanadi.
                if (sp is null) _shared.SetSetting(ScriptPromptKey, s.Gemini.ScriptPrompt); else s.Gemini.ScriptPrompt = MigratePrompt(sp, true);
                if (mp is null) _shared.SetSetting(MontagePromptKey, s.Claude.MontagePrompt); else s.Claude.MontagePrompt = MigratePrompt(mp, false);
            }
            ApplyBotConfig(s, _shared.GetSetting(ConfigKey));
            if (guest)
            {
                // Kompyuter darajasidagi sozlamalar (Claude modeli/vaqti/byudjeti, VoiceStudio, OpenFilm render, limitlar) - kompyuter
                // egasiniki: mehmon ularni o'z botida o'zgartirsa ham shu kompyuterga ta'sir qilmaydi.
                var host = LoadLocal();
                ApplyBotConfig(host, _shared.GetHostSetting(ConfigKey));
                var montage = s.Claude.MontagePrompt;
                s.Claude = host.Claude; s.Claude.MontagePrompt = montage;
                s.VoiceStudio = host.VoiceStudio; s.OpenFilm = host.OpenFilm; s.Limits = host.Limits;
            }
            try
            {
                var oauth = _shared.GetSetting("youtube.oauth");
                if (!string.IsNullOrWhiteSpace(oauth) && System.Text.Json.Nodes.JsonNode.Parse(oauth)?["refreshToken"]?.GetValue<string>() is { Length: > 0 } rt)
                {
                    s.YouTube.RemoteRefreshToken = rt.Trim();
                    if (System.Text.Json.Nodes.JsonNode.Parse(oauth)?["channelTitle"]?.GetValue<string>() is { Length: > 0 } ct) s.YouTube.ChannelTitle = ct; // bot ulagan kanal nomi
                }
            }
            catch { }
            var sch = _shared.GetSetting(ScheduleKey);
            if (!string.IsNullOrWhiteSpace(sch))
            {
                try { s.Schedule = System.Text.Json.JsonSerializer.Deserialize<ScheduleSettings>(sch, AppSettings.Json) ?? s.Schedule; } catch { }
            }
            // Kanal konteksti (bot kanal tahlilidan yozadi) - hisob doirasida: mehmon vazifasiga mehmonning o'z kanali konteksti.
            s.ChannelContext = ReadChannelContext(_shared.GetSetting(ChannelContextKey));
            // «Mening ovozim» namunasi - hisob doirasida (mehmon vazifasiga mehmonning o'z ovozi).
            s.VoiceClone = ReadVoiceClone(_shared.GetSetting(VoiceCloneKey));
            // Faqat MOSS-TTS-Nano ishlatiladi: botdagi eski (KittenTTS va h.k.) profillar MOSS ayol/erkak bilan almashtiriladi
            // va bot ro'yxati ham bir marta tozalanadi (kompyuter egasi; mehmon sozlamalariga yozilmaydi).
            if (TtsVoices.Normalize(s) && !guest)
            {
                try { PushVoiceProfiles(s.VoiceProfiles, s.Defaults.VoiceProfile); } catch { /* keyingi Load'da yana urinadi */ }
            }
            return s;
        }
        TtsVoices.Normalize(s);
        return s;
    }

    /// <summary>Bot boshqaradigan umumiy sozlamalar (JSON): gemini, limits, defaults. Bo'lmagan maydonlar o'zgarmaydi.</summary>
    public const string ConfigKey = "config";
    /// <summary>Bot kanal tahlilidan yozadigan kontekst: {enabled, script, montage, voiceProfile, updatedAt}.</summary>
    public const string ChannelContextKey = "channel.context";
    /// <summary>Worker video chiqqanini belgilaydi: bot kanal kontekstini qayta o'rganadi.</summary>
    public const string ChannelStaleKey = "channel.context.stale";

    /// <summary>Bot «Mening ovozim»: {version, profileName, url, status, …} yoki o'chirilgandan keyin {deleted:true, profileName}.</summary>
    public const string VoiceCloneKey = "voice.clone";

    internal static VoiceCloneSettings ReadVoiceClone(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var n = System.Text.Json.Nodes.JsonNode.Parse(json);
            string Str(string k) => n?[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
            var deleted = n?["deleted"] is System.Text.Json.Nodes.JsonValue d && d.TryGetValue<bool>(out var b) && b;
            return new VoiceCloneSettings { ProfileName = Str("profileName"), Url = deleted ? "" : Str("url"), Version = deleted ? "" : Str("version"), RefText = deleted ? "" : Str("refText"), Deleted = deleted };
        }
        catch { return new(); }
    }

    /// <summary>
    /// Kompyuter natijasini bot holatiga yozadi (faqat shu versiya hali amalda bo'lsa: foydalanuvchi orada yangi namuna yuklagan
    /// yoki o'chirgan bo'lsa, eski natija yozilmaydi). Joriy hisob doirasida.
    /// </summary>
    public static void WriteVoiceCloneStatus(IAppStore store, string version, string status, string message)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(store.GetSetting(VoiceCloneKey) ?? "null") is not System.Text.Json.Nodes.JsonObject o) return;
        if (o["deleted"]?.GetValue<bool>() == true || o["version"]?.ToString() != version) return;
        o["status"] = status;
        o["message"] = message.Length > 400 ? message[..400] : message;
        o["machine"] = Environment.MachineName;
        if (status == "ready") o["readyAt"] = DateTimeOffset.UtcNow.ToString("o");
        store.SetSetting(VoiceCloneKey, o.ToJsonString());
    }

    internal static ChannelContextSettings ReadChannelContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var n = System.Text.Json.Nodes.JsonNode.Parse(json);
            string Str(string k) => n?[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
            return new ChannelContextSettings
            {
                Enabled = n?["enabled"] is System.Text.Json.Nodes.JsonValue e && e.TryGetValue<bool>(out var b) && b,
                Script = Str("script"), Montage = Str("montage"), VoiceProfile = Str("voiceProfile"), UpdatedAt = Str("updatedAt"),
            };
        }
        catch { return new(); }
    }

    private static void ApplyBotConfig(AppSettings s, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (root?["gemini"] is { } g)
            {
                // apiKeys - bir nechta kalit (botdagi tartibda); apiKey - eski botlar yozgan bitta kalit.
                var keys = new List<string>();
                if (g["apiKeys"] is System.Text.Json.Nodes.JsonArray arr)
                    foreach (var n in arr) if (n?.GetValue<string>()?.Trim() is { Length: > 0 } ak && !keys.Contains(ak)) keys.Add(ak);
                if (g["apiKey"]?.GetValue<string>()?.Trim() is { Length: > 0 } k && !keys.Contains(k)) keys.Add(k);
                s.Gemini.RemoteApiKeys = keys;
                if (g["model"]?.GetValue<string>() is { Length: > 0 } m) s.Gemini.Model = m.Trim();
                if (g["temperature"] is { } t) s.Gemini.Temperature = t.GetValue<double>();
                if (g["useSearchGrounding"] is { } sg) s.Gemini.UseSearchGrounding = sg.GetValue<bool>();
            }
            if (root?["youtube"] is { } y)
            {
                static string? Str(System.Text.Json.Nodes.JsonNode? n) => n?.GetValue<string>();
                static bool? Bool(System.Text.Json.Nodes.JsonNode? n) => n?.GetValue<bool>();
                if (Str(y["clientId"]) is { Length: > 0 } cid) s.YouTube.ClientId = cid.Trim();
                if (Str(y["clientSecret"]) is { Length: > 0 } cs) s.YouTube.RemoteClientSecret = cs.Trim();
                if (Str(y["publishTime"]) is { Length: > 0 } pt) s.YouTube.PublishTime = pt;
                if (Str(y["timeZoneId"]) is { Length: > 0 } tz) s.YouTube.TimeZoneId = tz;
                if (Str(y["categoryId"]) is { Length: > 0 } cat) s.YouTube.CategoryId = cat;
                if (Str(y["regionCode"]) is { Length: > 0 } rc) s.YouTube.RegionCode = rc;
                if (Bool(y["madeForKids"]) is { } mk) s.YouTube.MadeForKids = mk;
                if (Bool(y["containsSyntheticMedia"]) is { } sm) s.YouTube.ContainsSyntheticMedia = sm;
                if (Str(y["license"]) is { Length: > 0 } lic) s.YouTube.License = lic;
                if (Bool(y["embeddable"]) is { } em) s.YouTube.Embeddable = em;
                if (Bool(y["publicStatsViewable"]) is { } ps) s.YouTube.PublicStatsViewable = ps;
                if (Bool(y["notifySubscribers"]) is { } ns) s.YouTube.NotifySubscribers = ns;
                if (Bool(y["setRecordingDate"]) is { } rd) s.YouTube.SetRecordingDate = rd;
                if (Str(y["locationDescription"]) is { } loc) s.YouTube.LocationDescription = loc;
                if (Str(y["defaultTags"]) is { } tags) s.YouTube.DefaultTags = tags;
                if (Str(y["defaultDescription"]) is { } dd) s.YouTube.DefaultDescription = dd;
                if (y["minLeadMinutes"] is { } ml) s.YouTube.MinLeadMinutes = ml.GetValue<int>();
            }
            if (root?["gemini"]?["timeoutSec"] is { } gts) s.Gemini.TimeoutSec = Math.Clamp(gts.GetValue<int>(), 30, 1800);
            if (root?["claude"] is { } cl)
            {
                if (cl["model"]?.GetValue<string>() is { } cm) s.Claude.Model = cm.Trim();
                if (cl["timeoutMinutes"] is { } ctm) s.Claude.TimeoutMinutes = Math.Clamp(ctm.GetValue<int>(), 5, 600);
                if (cl["maxBudgetUsd"] is { } cb) s.Claude.MaxBudgetUsd = Math.Max(0, cb.GetValue<double>());
            }
            if (root?["visuals"] is { } vis)
            {
                // Video sifati (bot → Sozlamalar → Video): real rasmlar, shablon uslubi, shablonga moslik tekshiruvi.
                if (vis["realMedia"] is System.Text.Json.Nodes.JsonValue rm && rm.TryGetValue<bool>(out var rmv)) s.Visuals.RealMedia = rmv;
                if (vis["maxMedia"] is System.Text.Json.Nodes.JsonValue mm && mm.TryGetValue<int>(out var mmv)) s.Visuals.MaxMedia = Math.Clamp(mmv, 0, 40);
                if (vis["templateStyleGuide"] is System.Text.Json.Nodes.JsonValue sg && sg.TryGetValue<bool>(out var sgv)) s.Visuals.TemplateStyleGuide = sgv;
                if (vis["styleReview"] is System.Text.Json.Nodes.JsonValue sr && sr.TryGetValue<bool>(out var srv)) s.Visuals.StyleReview = srv;
                if (vis["minStyleScore"] is System.Text.Json.Nodes.JsonValue ms && ms.TryGetValue<double>(out var msv)) s.Visuals.MinStyleScore = Math.Clamp(msv, 0, 10);
            }
            if (root?["voice"] is { } vc)
            {
                // Ovoz profillari botda boshqariladi: ro'yxat bo'sh bo'lmasa lokal ro'yxat o'rniga qo'llanadi.
                if (vc["profiles"] is System.Text.Json.Nodes.JsonArray arr && arr.Count > 0)
                {
                    var list = new List<VoiceProfile>();
                    foreach (var p in arr)
                    {
                        var name = p?["name"]?.GetValue<string>()?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;
                        list.Add(new VoiceProfile
                        {
                            Name = name, Model = p?["model"]?.GetValue<string>() ?? TtsVoices.Model, Voice = p?["voice"]?.GetValue<string>() ?? "",
                            Language = p?["language"]?.GetValue<string>() ?? "en", Speed = Math.Clamp(p?["speed"]?.GetValue<double>() ?? 1.0, 0.5, 2.0),
                        });
                    }
                    if (list.Count > 0) s.VoiceProfiles = list;
                }
                if (vc["settings"] is { } vs)
                {
                    if (vs["baseUrl"]?.GetValue<string>() is { Length: > 0 } bu) s.VoiceStudio.BaseUrl = bu.Trim();
                    if (vs["startupTimeoutSec"] is { } a1) s.VoiceStudio.StartupTimeoutSec = Math.Clamp(a1.GetValue<int>(), 30, 1800);
                    if (vs["requestTimeoutSec"] is { } a2) s.VoiceStudio.RequestTimeoutSec = Math.Clamp(a2.GetValue<int>(), 30, 3600);
                    if (vs["chunkChars"] is { } a3) s.VoiceStudio.ChunkChars = Math.Clamp(a3.GetValue<int>(), 100, 2000);
                    if (vs["pauseBetweenChunksSec"] is { } a4) s.VoiceStudio.PauseBetweenChunksSec = Math.Clamp(a4.GetValue<double>(), 0, 5);
                    if (vs["whisperModel"]?.GetValue<string>() is { Length: > 0 } wm) s.VoiceStudio.WhisperModel = wm.Trim();
                    if (vs["alignTimeoutSec"] is { } a5) s.VoiceStudio.AlignTimeoutSec = Math.Clamp(a5.GetValue<int>(), 60, 7200);
                }
            }
            if (root?["openFilm"] is { } of)
            {
                if (of["renderWorkers"] is { } rw) s.OpenFilm.RenderWorkers = Math.Clamp(rw.GetValue<int>(), 1, 4);
                if (of["fps"] is { } fps) s.OpenFilm.Fps = Math.Clamp(fps.GetValue<int>(), 1, 120);
                if (of["loudnessLufs"] is { } lu) s.OpenFilm.LoudnessLufs = lu.GetValue<double>();
                if (of["burnSubtitles"] is { } bs) s.OpenFilm.BurnSubtitles = bs.GetValue<bool>();
                if (of["lookFullAfterRender"] is { } lf) s.OpenFilm.LookFullAfterRender = lf.GetValue<bool>();
                if (of["renderTimeoutMinutes"] is { } rt) s.OpenFilm.RenderTimeoutMinutes = Math.Max(5, rt.GetValue<int>());
            }
            if (root?["limits"] is { } l)
            {
                if (l["maxRunAttempts"] is { } a) s.Limits.MaxRunAttempts = a.GetValue<int>();
                if (l["maxQaFixCycles"] is { } q) s.Limits.MaxQaFixCycles = q.GetValue<int>();
                if (l["minFreeDiskGb"] is { } d) s.Limits.MinFreeDiskGb = d.GetValue<double>();
                if (l["minFreeRamGbForTts"] is { } r) s.Limits.MinFreeRamGbForTts = r.GetValue<double>();
            }
            if (root?["defaults"] is { } df)
            {
                if (df["language"]?.GetValue<string>() is { Length: > 0 } lang) s.Defaults.Language = lang;
                if (df["format"] is { } f) s.Defaults.Format = (VideoFormat)f.GetValue<int>();
                if (df["template"]?.GetValue<string>() is { } tpl) s.Defaults.Template = tpl;
                if (df["voiceProfile"]?.GetValue<string>() is { Length: > 0 } vp) s.Defaults.VoiceProfile = vp;
            }
        }
        catch { /* buzilgan JSON: lokal qiymatlar ishlatiladi */ }
    }

    private static string MigratePrompt(string p, bool script)
    {
        if (script)
        {
            var old = new[] { DefaultPrompts.ScriptPrevious, DefaultPrompts.ScriptPrevious.Replace("{length_rule}", DefaultPrompts.LegacyLengthLine) };
            return string.IsNullOrWhiteSpace(p) || old.Any(o => Same(o, p)) ? DefaultPrompts.Script : p;
        }
        return string.IsNullOrWhiteSpace(p) || Same(DefaultPrompts.MontagePrevious, p) ? DefaultPrompts.Montage : p;
    }

    private AppSettings LoadLocal()
    {
        var json = _db.GetSetting("app_settings");
        AppSettings s;
        if (json is null) s = _defaults();
        else try { s = AppSettings.FromJson(json); } catch { s = _defaults(); }
        // Claude ilovasi yangilanganda versiyali yo'l o'zgaradi: sozlangan yo'l yo'q bo'lsa avtomatik topiladi.
        if (string.IsNullOrWhiteSpace(s.Claude.ExecutablePath) || !File.Exists(s.Claude.ExecutablePath))
        {
            var found = Claude.ClaudeLocator.Find();
            if (found is not null) s.Claude.ExecutablePath = found;
        }
        // VoiceStudio, OpenFilm, Python va Whisper keshi: yo'l yo'q yoki noto'g'ri bo'lsa kompyuterdan o'zi topiladi va saqlanadi.
        var pathsBefore = (s.VoiceStudio.ExePath, s.VoiceStudio.PythonPath, s.VoiceStudio.HfCacheDir, s.OpenFilm.ExePath, s.Claude.ExecutablePath);
        if (string.IsNullOrWhiteSpace(s.VoiceStudio.ExePath) || !File.Exists(s.VoiceStudio.ExePath))
            s.VoiceStudio.ExePath = ToolLocator.FindVoiceStudioExe() ?? s.VoiceStudio.ExePath;
        if (string.IsNullOrWhiteSpace(s.VoiceStudio.PythonPath) || !File.Exists(s.VoiceStudio.PythonPath))
            s.VoiceStudio.PythonPath = ToolLocator.FindVoicePython(s.VoiceStudio.ExePath) ?? s.VoiceStudio.PythonPath;
        if (string.IsNullOrWhiteSpace(s.VoiceStudio.HfCacheDir) || !Directory.Exists(Path.Combine(s.VoiceStudio.HfCacheDir, "models--" + s.VoiceStudio.WhisperModel.Replace("/", "--"))))
            s.VoiceStudio.HfCacheDir = ToolLocator.FindHfCache(s.VoiceStudio.WhisperModel) ?? s.VoiceStudio.HfCacheDir;
        if (string.IsNullOrWhiteSpace(s.OpenFilm.ExePath) || !File.Exists(s.OpenFilm.ExePath))
            s.OpenFilm.ExePath = ToolLocator.FindOpenFilmExe() ?? s.OpenFilm.ExePath;
        if (pathsBefore != (s.VoiceStudio.ExePath, s.VoiceStudio.PythonPath, s.VoiceStudio.HfCacheDir, s.OpenFilm.ExePath, s.Claude.ExecutablePath))
            _db.SetSetting("app_settings", s.ToJson());
        // Eski sozlamalar migratsiyasi: shablonlar papkasi va dinamik davomiylik.
        if (string.IsNullOrWhiteSpace(s.Paths.TemplatesDir)) s.Paths.TemplatesDir = _defaults().Paths.TemplatesDir;
        s.Defaults.TargetDurationSec = 0;
        // Foydalanuvchi o'zgartirmagan eski standart promptlar yangi oqimdagisiga (subtitr + title) almashtiriladi.
        var oldScripts = new[] { DefaultPrompts.ScriptPrevious, DefaultPrompts.ScriptPrevious.Replace("{length_rule}", DefaultPrompts.LegacyLengthLine) };
        if (string.IsNullOrWhiteSpace(s.Gemini.ScriptPrompt) || oldScripts.Any(o => Same(o, s.Gemini.ScriptPrompt))) s.Gemini.ScriptPrompt = DefaultPrompts.Script;
        if (string.IsNullOrWhiteSpace(s.Claude.MontagePrompt) || Same(DefaultPrompts.MontagePrevious, s.Claude.MontagePrompt)) s.Claude.MontagePrompt = DefaultPrompts.Montage;
        return s;
    }

    public void Save(AppSettings settings)
    {
        _db.SetSetting("app_settings", settings.ToJson());
        if (_shared is not null)
        {
            // Promptlar va jadval botda tahrirlanadi: ilova ularni qayta yozmaydi.
            PublishCatalog(settings);
        }
    }

    /// <summary>Ish jadvali (vaqt oynasi) online bazaga yoziladi - bot ham shu qiymatni ko'radi va o'zgartira oladi.</summary>
    public void SaveSchedule(ScheduleSettings schedule)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            enabled = schedule.Enabled, startTime = schedule.StartTime, endTime = schedule.EndTime, timeZoneId = schedule.TimeZoneId,
            wakeToRun = schedule.WakeToRun, executionLimitHours = schedule.ExecutionLimitHours,
        });
        if (_shared is not null) _shared.SetSetting(ScheduleKey, json);
        var local = LoadLocal(); local.Schedule = schedule; _db.SetSetting("app_settings", local.ToJson());
    }

    public const string BotCatalogKey = "bot.catalog";

    /// <summary>
    /// Telegram bot (web app) uchun katalog: ovoz profillari, shablonlar va standart qiymatlar (ular faqat shu kompyuterda saqlanadi,
    /// bot esa faqat online bazani ko'radi). Mavzu bot orqali qo'shilganda shular ishlatiladi. Sirlar va yo'llar yuborilmaydi.
    /// </summary>
    public void PublishCatalog(AppSettings? settings = null)
    {
        if (_shared is null) return;
        var s = settings ?? LoadLocal();
        var catalog = new
        {
            voice_profiles = s.VoiceProfiles.Select(v => v.Name).ToArray(),
            templates = s.Templates.Select(t => new { name = t.Name, kind = t.Kind.ToString() }).ToArray(),
            defaults = new
            {
                voice_profile = string.IsNullOrEmpty(s.Defaults.VoiceProfile) ? s.VoiceProfiles.FirstOrDefault()?.Name ?? "" : s.Defaults.VoiceProfile,
                template = s.Defaults.Template ?? "", format = (int)s.Defaults.Format,
            },
            youtube = new { publish_time = s.YouTube.PublishTime, time_zone = s.YouTube.TimeZoneId },
            updated_at_utc = DateTimeOffset.UtcNow,
            machine = Environment.MachineName,
        };
        _shared.SetSetting(BotCatalogKey, System.Text.Json.JsonSerializer.Serialize(catalog));
        PublishTemplates(s);
        PublishVoice(s);
        try
        {
            // Standart promptlar: botdagi "Standartga qaytarish" tugmasi shulardan foydalanadi.
            _shared.SetSetting("prompt.default.script", DefaultPrompts.Script);
            _shared.SetSetting("prompt.default.montage", DefaultPrompts.Montage);
        }
        catch { }
    }

    /// <summary>
    /// Ilovadagi "Ayol/Erkak ovozi" paneli profillarni o'zgartirganda ular bot konfigidagi (config.voice.profiles) ro'yxatga ham yoziladi:
    /// bot ro'yxati asosiy manba, shuning uchun lokal o'zgarish uni bekor qilib qo'ymasligi uchun.
    /// </summary>
    public void PushVoiceProfiles(IReadOnlyList<VoiceProfile> profiles, string? defaultProfile = null)
    {
        if (_shared is null) return;
        var root = System.Text.Json.Nodes.JsonNode.Parse(_shared.GetSetting(ConfigKey) ?? "{}") as System.Text.Json.Nodes.JsonObject ?? new();
        var voice = root["voice"] as System.Text.Json.Nodes.JsonObject ?? new();
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var p in profiles)
            arr.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = p.Name, ["model"] = p.Model, ["voice"] = p.Voice, ["language"] = p.Language, ["speed"] = p.Speed });
        voice["profiles"] = arr;
        root["voice"] = voice;
        if (!string.IsNullOrEmpty(defaultProfile))
        {
            var defaults = root["defaults"] as System.Text.Json.Nodes.JsonObject ?? new();
            defaults["voiceProfile"] = defaultProfile;
            root["defaults"] = defaults;
        }
        _shared.SetSetting(ConfigKey, root.ToJsonString());
    }

    /// <summary>Shablonlar ro'yxati (nom, tur, hajm, manba) shu kompyuter uchun online bazaga yoziladi: bot ko'rsatadi. Fayllar yuklanmaydi.</summary>
    private void PublishTemplates(AppSettings s)
    {
        try
        {
            _shared!.ReplaceTemplates(Environment.MachineName, s.Templates.Select(t =>
                (t.Name, t.Kind.ToString(), t.SizeBytes, t.AddedAtUtc, t.Managed ? "upload" : "video", t.Notes ?? "")).ToList());
        }
        catch { /* ro'yxat keyingi safar yoziladi */ }
    }

    /// <summary>
    /// Ovoz ma'lumotlari: VoiceStudio dvigatellari, ularning ovozlari/modellari va ovoz profillari (kompyuter bo'yicha)
    /// online bazaga yoziladi (shared_settings: voice.catalog.KOMPYUTER). Bot faqat ko'rsatadi.
    /// </summary>
    private void PublishVoice(AppSettings s)
    {
        try
        {
            VoiceCatalog? cat = null;
            var json = _db.GetSetting("voice_catalog");
            if (json is not null) try { cat = System.Text.Json.JsonSerializer.Deserialize<VoiceCatalog>(json); } catch { }
            var obj = new
            {
                machine = Environment.MachineName,
                fetchedAtUtc = cat?.FetchedAtUtc, voiceStudioVersion = cat?.VoiceStudioVersion ?? "",
                updatedAtUtc = DateTimeOffset.UtcNow,
                engines = (cat?.Engines ?? new()).Select(e => new
                {
                    id = e.Id, displayName = e.DisplayName, available = e.Available, usable = e.Usable, reason = e.Reason,
                    voices = e.Voices, installedModels = e.InstalledModels, knownModels = e.KnownModels, supportsCloning = e.SupportsCloning,
                }),
                profiles = s.VoiceProfiles.Select(v => new { name = v.Name, model = v.Model, voice = v.Voice, language = v.Language, speed = v.Speed }),
                defaultProfile = s.Defaults.VoiceProfile ?? "",
            };
            _shared!.SetSetting("voice.catalog." + Environment.MachineName, System.Text.Json.JsonSerializer.Serialize(obj,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        }
        catch { /* keyingi safar yoziladi */ }
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Replace("\r\n", "\n").Trim(), b.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal);
}

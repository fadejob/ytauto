import "server-only";
import { sql } from "./db";
import { demoLogs, demoRuns, isDemo } from "./demo";
import type { JobLogs, RunStats, WorkerRun } from "./types";

function parseStats(v: string | null): RunStats | null {
  if (!v) return null;
  try { return JSON.parse(v) as RunStats; } catch { return null; }
}

/** Oxirgi worker ishga tushishlari (barcha kompyuterlardan). */
export async function getRuns(limit = 40): Promise<WorkerRun[]> {
  if (isDemo()) return demoRuns();
  const rows = await sql()`
    SELECT run_id, started_at_utc, finished_at_utc, outcome, job_id::int AS job_id, message, trigger, machine, stats_json
    FROM ytauto.worker_runs ORDER BY started_at_utc DESC LIMIT ${limit}`;
  return rows.map((r) => ({
    stats: parseStats(r.stats_json as string | null),
    runId: r.run_id as string, startedAt: (r.started_at_utc as Date).toISOString(),
    finishedAt: r.finished_at_utc ? (r.finished_at_utc as Date).toISOString() : null,
    outcome: (r.outcome as string | null) ?? null, jobId: (r.job_id as number | null) ?? null,
    message: (r.message as string | null) ?? null, trigger: (r.trigger as string | null) ?? null, machine: (r.machine as string | null) ?? null,
  }));
}

/** Bitta video: bosqichlar va hodisalar (loglar) - desktop ilovadagi "Tafsilotlar" sahifasidek. */
export async function getJobLogs(id: number, limit = 400): Promise<JobLogs | null> {
  if (isDemo()) return demoLogs(id);
  const [job] = await sql()`SELECT id::int AS id, topic, status FROM ytauto.jobs WHERE id = ${id}`;
  if (!job) return null;
  const events = await sql()`
    SELECT id::int AS id, at_utc, level, stage, message FROM (
      SELECT * FROM ytauto.job_events WHERE job_id = ${id} ORDER BY id DESC LIMIT ${limit}
    ) x ORDER BY id`;
  const stages = await sql()`
    SELECT stage, state, attempts, duration_ms::float8 AS duration_ms, error_message FROM ytauto.job_stages WHERE job_id = ${id} ORDER BY stage`;
  return {
    id: job.id as number, topic: job.topic as string, status: job.status as number,
    events: events.map((e) => ({ id: e.id as number, at: (e.at_utc as Date).toISOString(), level: e.level as string, stage: (e.stage as string | null) ?? null, message: e.message as string })),
    stages: stages.map((s) => ({ stage: s.stage as number, state: s.state as number, attempts: s.attempts as number, durationMs: s.duration_ms as number | null, error: (s.error_message as string | null) ?? null })),
  };
}

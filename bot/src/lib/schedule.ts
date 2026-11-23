import "server-only";
import { sql, wakeAgents } from "./db";
import { isDemo } from "./demo";

export type Schedule = { enabled: boolean; startTime: string; endTime: string; timeZoneId: string; wakeToRun: boolean; executionLimitHours: number };

const KEY = "schedule";
const DEFAULT: Schedule = { enabled: false, startTime: "01:00", endTime: "04:00", timeZoneId: "Asia/Tashkent", wakeToRun: true, executionLimitHours: 6 };
let demo: Schedule = { ...DEFAULT };

// Kalitlar desktop ilovadagi ScheduleSettings (System.Text.Json, case-insensitive) bilan mos.
function parse(v: string | undefined): Schedule {
  if (!v) return { ...DEFAULT };
  try {
    const o = JSON.parse(v) as Record<string, unknown>;
    const get = (k: string) => o[k] ?? o[k.charAt(0).toUpperCase() + k.slice(1)];
    return {
      enabled: get("enabled") === true,
      startTime: typeof get("startTime") === "string" ? (get("startTime") as string) : DEFAULT.startTime,
      endTime: typeof get("endTime") === "string" ? (get("endTime") as string) : DEFAULT.endTime,
      timeZoneId: typeof get("timeZoneId") === "string" ? (get("timeZoneId") as string) : DEFAULT.timeZoneId,
      wakeToRun: get("wakeToRun") !== false,
      executionLimitHours: Number(get("executionLimitHours")) || DEFAULT.executionLimitHours,
    };
  } catch { return { ...DEFAULT }; }
}

export async function getSchedule(): Promise<Schedule> {
  if (isDemo()) return demo;
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  return parse(row?.value as string | undefined);
}

export async function saveSchedule(input: Partial<Schedule>): Promise<Schedule> {
  const cur = await getSchedule();
  const next: Schedule = {
    enabled: input.enabled ?? cur.enabled,
    startTime: input.startTime ?? cur.startTime,
    endTime: input.endTime ?? cur.endTime,
    timeZoneId: (input.timeZoneId ?? cur.timeZoneId).trim(),
    wakeToRun: input.wakeToRun ?? cur.wakeToRun,
    executionLimitHours: cur.executionLimitHours,
  };
  if (!/^([01]\d|2[0-3]):[0-5]\d$/.test(next.startTime)) throw new Error("Vaqt HH:mm formatida bo'lsin (masalan 01:00).");
  try { new Intl.DateTimeFormat("en", { timeZone: next.timeZoneId }); } catch { throw new Error("Vaqt zonasi noto'g'ri (masalan Asia/Tashkent)."); }
  if (isDemo()) { demo = next; return next; }
  const json = JSON.stringify({ enabled: next.enabled, startTime: next.startTime, endTime: next.endTime, timeZoneId: next.timeZoneId, wakeToRun: next.wakeToRun, executionLimitHours: next.executionLimitHours });
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${json}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
  wakeAgents("schedule"); // Windows jadvali darhol yangilanadi
  return next;
}

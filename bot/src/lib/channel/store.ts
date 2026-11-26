import "server-only";
import { sql } from "../db";
import type { ChannelContext, Suggestion } from "../types";

/** Bazadagi kanal konteksti (hammasi hisob konteksti ichida - withAccount; RLS boshqa hisobni ko'rsatmaydi). */
export const BLOCKS_KEY = "channel.context";
/** Worker video chiqqanda yozadi: kontekst shundan eski bo'lsa qayta tahlil qilinadi. */
export const STALE_KEY = "channel.context.stale";
/** "running" shundan uzoq tursa (server o'chib qolgan) - qayta boshlash mumkin. */
const RUNNING_TTL_MIN = 10;

export type ProfileRow = {
  channel_id: string; channel_title: string; status: "idle" | "running" | "failed"; error: string | null; enabled: boolean;
  context_json: string | null; last_video_id: string | null; analyzed_at_utc: Date | null; started_at_utc: Date | null; updated_at_utc: Date;
};
export type Blocks = { enabled: boolean; script: string; montage: string; voiceProfile: string; updatedAt: string };

export async function readProfile(): Promise<ProfileRow | null> {
  const [r] = await sql()<ProfileRow[]>`SELECT channel_id, channel_title, status, error, enabled, context_json, last_video_id, analyzed_at_utc, started_at_utc, updated_at_utc FROM ytauto.channel_profile`;
  return r ?? null;
}

export function parseContext(r: ProfileRow | null): ChannelContext | null {
  if (!r?.context_json) return null;
  try { return JSON.parse(r.context_json) as ChannelContext; } catch { return null; }
}

/** Tahlilni boshlash huquqi (bir vaqtda bitta): true - shu chaqiruv boshladi. */
export async function tryStart(): Promise<boolean> {
  const rows = await sql()`
    INSERT INTO ytauto.channel_profile(status, started_at_utc, updated_at_utc) VALUES ('running', now(), now())
    ON CONFLICT (account_id) DO UPDATE SET status = 'running', error = NULL, started_at_utc = now(), updated_at_utc = now()
    WHERE ytauto.channel_profile.status <> 'running' OR ytauto.channel_profile.started_at_utc < now() - make_interval(mins => ${RUNNING_TTL_MIN})
    RETURNING 1`;
  return rows.length > 0;
}

export async function saveResult(ctx: ChannelContext, lastVideoId: string, blocks: Omit<Blocks, "updatedAt" | "enabled">) {
  const json = JSON.stringify(ctx);
  const [p] = await sql()`
    UPDATE ytauto.channel_profile SET status = 'idle', error = NULL, channel_id = ${ctx.channel.id}, channel_title = ${ctx.channel.title},
      context_json = ${json}, last_video_id = ${lastVideoId}, analyzed_at_utc = now(), updated_at_utc = now()
    RETURNING enabled`;
  await writeBlocks({ ...blocks, enabled: p?.enabled !== false, updatedAt: new Date().toISOString() });
}

/**
 * Kanal almashdi yoki uzildi: shu hisobdagi eski kanal ma'lumotlari o'chadi - tahlil (channel_profile), promptlarga qo'shiladigan
 * kontekst bloklari va hali ishlatilmagan mavzu tavsiyalari. Mavzuga aylangan tavsiyalar (job_id) tarix sifatida qoladi.
 * Hammasi hisob konteksti ichida (RLS): boshqa hisoblarga tegmaydi.
 */
export async function resetChannelData() {
  await sql()`DELETE FROM ytauto.channel_profile`;
  await sql()`DELETE FROM ytauto.shared_settings WHERE key IN (${BLOCKS_KEY}, ${STALE_KEY})`;
  await sql()`DELETE FROM ytauto.topic_suggestions WHERE job_id IS NULL`;
}

/** Yangi video yo'q: faqat "tekshirildi" vaqti yangilanadi. */
export async function markChecked() {
  await sql()`UPDATE ytauto.channel_profile SET status = 'idle', error = NULL, updated_at_utc = now()`;
}

export async function saveError(message: string) {
  await sql()`UPDATE ytauto.channel_profile SET status = 'failed', error = ${message.slice(0, 500)}, updated_at_utc = now()`;
}

export async function readBlocks(): Promise<Blocks | null> {
  const [r] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${BLOCKS_KEY}`;
  try { return r ? (JSON.parse(r.value as string) as Blocks) : null; } catch { return null; }
}

async function writeBlocks(b: Blocks) {
  const json = JSON.stringify(b);
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${BLOCKS_KEY}, ${json}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

/** Kontekstni promptlarga qo'shish yoqilgan/o'chirilgan (tahlil saqlanib qoladi). */
export async function setEnabled(enabled: boolean) {
  await sql()`
    INSERT INTO ytauto.channel_profile(enabled, updated_at_utc) VALUES (${enabled}, now())
    ON CONFLICT (account_id) DO UPDATE SET enabled = EXCLUDED.enabled`;
  const b = await readBlocks();
  if (b) await writeBlocks({ ...b, enabled, updatedAt: new Date().toISOString() });
}

/** Worker "yangi video chiqdi" belgisini qo'ygan vaqt. */
export async function staleSince(): Promise<Date | null> {
  const [r] = await sql()`SELECT updated_at_utc FROM ytauto.shared_settings WHERE key = ${STALE_KEY}`;
  return (r?.updated_at_utc as Date | undefined) ?? null;
}

// ------------------------------------------------------------------ tavsiyalar
type SugRow = { id: number; for_date: Date | string; topic: string; notes: string; reason: string; status: Suggestion["status"] };
const toSug = (r: SugRow): Suggestion => ({
  id: r.id, forDate: typeof r.for_date === "string" ? r.for_date : r.for_date.toISOString().slice(0, 10), topic: r.topic, notes: r.notes, reason: r.reason, status: r.status,
});

export async function listSuggestions(date: string): Promise<Suggestion[]> {
  const rows = await sql()<SugRow[]>`SELECT id::int AS id, for_date::text AS for_date, topic, notes, reason, status FROM ytauto.topic_suggestions WHERE for_date = ${date}::date ORDER BY id`;
  return rows.map(toSug);
}

export async function getSuggestion(id: number): Promise<Suggestion | null> {
  const [r] = await sql()<SugRow[]>`SELECT id::int AS id, for_date::text AS for_date, topic, notes, reason, status FROM ytauto.topic_suggestions WHERE id = ${id}`;
  return r ? toSug(r) : null;
}

export async function setSuggestionStatus(id: number, status: Suggestion["status"], jobId?: number) {
  await sql()`UPDATE ytauto.topic_suggestions SET status = ${status}, job_id = ${jobId ?? null} WHERE id = ${id}`;
}

/** Oxirgi 30 kunda rad etilgan va qabul qilingan tavsiyalar (takrorlamaslik uchun). */
export async function recentSuggestionTopics(): Promise<{ dismissed: string[]; accepted: string[] }> {
  const rows = await sql()`SELECT topic, status FROM ytauto.topic_suggestions WHERE for_date > current_date - 30 AND status <> 'new' ORDER BY id DESC LIMIT 60`;
  return { dismissed: rows.filter((r) => r.status === "dismissed").map((r) => r.topic as string), accepted: rows.filter((r) => r.status === "accepted").map((r) => r.topic as string) };
}

/** Bugungi tavsiyalar bir marta: boshqa jarayon allaqachon yozgan bo'lsa yozilmaydi. */
export async function insertSuggestions(date: string, items: { topic: string; notes: string; reason: string }[]): Promise<boolean> {
  const [x] = await sql()`SELECT count(*)::int AS n FROM ytauto.topic_suggestions WHERE for_date = ${date}::date`;
  if ((x?.n as number) > 0) return false;
  if (!items.length) return false;
  await sql()`INSERT INTO ytauto.topic_suggestions ${sql()(items.map((i) => ({ for_date: date, topic: i.topic.slice(0, 300), notes: i.notes.slice(0, 2000), reason: i.reason.slice(0, 500) })))}`;
  return true;
}

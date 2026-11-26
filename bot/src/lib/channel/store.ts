import "server-only";
import { sql } from "../db";
import type { ChannelContext, Suggestion } from "../types";
import { activeChannelId } from "../youtube";
import { BLOCKS_KEY, blocksKey } from "./keys";

/**
 * Bazadagi kanal konteksti (hammasi hisob konteksti ichida - withAccount; RLS boshqa hisobni ko'rsatmaydi). Har YouTube kanali
 * alohida (ch - kanal id): kanal tahlili, promptlarga qo'shiladigan matnlar va kunlik tavsiyalar.
 */
export { BLOCKS_KEY };
/** Worker video chiqqanda yozadi: kontekst shundan eski bo'lsa qayta tahlil qilinadi. */
export const STALE_KEY = "channel.context.stale";
/** "running" shundan uzoq tursa (server o'chib qolgan) - qayta boshlash mumkin. */
const RUNNING_TTL_MIN = 10;

export type ProfileRow = {
  channel_id: string; channel_title: string; status: "idle" | "running" | "failed"; error: string | null; enabled: boolean;
  context_json: string | null; last_video_id: string | null; analyzed_at_utc: Date | null; started_at_utc: Date | null; updated_at_utc: Date;
};
export type Blocks = { enabled: boolean; script: string; montage: string; voiceProfile: string; updatedAt: string };

export async function readProfile(ch: string): Promise<ProfileRow | null> {
  const [r] = await sql()<ProfileRow[]>`SELECT channel_id, channel_title, status, error, enabled, context_json, last_video_id, analyzed_at_utc, started_at_utc, updated_at_utc
    FROM ytauto.channel_profile WHERE channel_id = ${ch}`;
  return r ?? null;
}

export function parseContext(r: ProfileRow | null): ChannelContext | null {
  if (!r?.context_json) return null;
  try { return JSON.parse(r.context_json) as ChannelContext; } catch { return null; }
}

/** Tahlilni boshlash huquqi (kanal bo'yicha bir vaqtda bitta): true - shu chaqiruv boshladi. */
export async function tryStart(ch: string): Promise<boolean> {
  const rows = await sql()`
    INSERT INTO ytauto.channel_profile(channel_id, status, started_at_utc, updated_at_utc) VALUES (${ch}, 'running', now(), now())
    ON CONFLICT (account_id, channel_id) DO UPDATE SET status = 'running', error = NULL, started_at_utc = now(), updated_at_utc = now()
    WHERE ytauto.channel_profile.status <> 'running' OR ytauto.channel_profile.started_at_utc < now() - make_interval(mins => ${RUNNING_TTL_MIN})
    RETURNING 1`;
  return rows.length > 0;
}

export async function saveResult(ch: string, ctx: ChannelContext, lastVideoId: string, blocks: Omit<Blocks, "updatedAt" | "enabled">) {
  const json = JSON.stringify(ctx);
  const [p] = await sql()`
    UPDATE ytauto.channel_profile SET status = 'idle', error = NULL, channel_title = ${ctx.channel.title},
      context_json = ${json}, last_video_id = ${lastVideoId}, analyzed_at_utc = now(), updated_at_utc = now()
    WHERE channel_id = ${ch}
    RETURNING enabled`;
  await writeBlocks(ch, { ...blocks, enabled: p?.enabled !== false, updatedAt: new Date().toISOString() });
}

/** Yangi video yo'q: faqat "tekshirildi" vaqti yangilanadi. */
export async function markChecked(ch: string) {
  await sql()`UPDATE ytauto.channel_profile SET status = 'idle', error = NULL, updated_at_utc = now() WHERE channel_id = ${ch}`;
}

export async function saveError(ch: string, message: string) {
  await sql()`UPDATE ytauto.channel_profile SET status = 'failed', error = ${message.slice(0, 500)}, updated_at_utc = now() WHERE channel_id = ${ch}`;
}

/** Kanal konteksti matnlari (promptlarga qo'shiladi). ch berilmasa - faol kanal. */
export async function readBlocks(ch?: string): Promise<Blocks | null> {
  const id = ch ?? (await activeChannelId());
  const [r] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${blocksKey(id)}`;
  try { return r ? (JSON.parse(r.value as string) as Blocks) : null; } catch { return null; }
}

/** Kanalning o'z kaliti; kanal hozir faol bo'lsa - faol kanal nusxasi (channel.context, eski desktop'lar uchun) ham. */
async function writeBlocks(ch: string, b: Blocks) {
  const json = JSON.stringify(b);
  const keys = new Set([blocksKey(ch)]);
  if (ch && ch === (await activeChannelId())) keys.add(BLOCKS_KEY);
  for (const key of keys)
    await sql()`
      INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${key}, ${json}, now())
      ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

/** Kontekstni promptlarga qo'shish yoqilgan/o'chirilgan (tahlil saqlanib qoladi). */
export async function setEnabled(ch: string, enabled: boolean) {
  await sql()`
    INSERT INTO ytauto.channel_profile(channel_id, enabled, updated_at_utc) VALUES (${ch}, ${enabled}, now())
    ON CONFLICT (account_id, channel_id) DO UPDATE SET enabled = EXCLUDED.enabled`;
  const b = await readBlocks(ch);
  if (b) await writeBlocks(ch, { ...b, enabled, updatedAt: new Date().toISOString() });
}

/** Worker "yangi video chiqdi" belgisini qo'ygan vaqt. */
export async function staleSince(): Promise<Date | null> {
  const [r] = await sql()`SELECT updated_at_utc FROM ytauto.shared_settings WHERE key = ${STALE_KEY}`;
  return (r?.updated_at_utc as Date | undefined) ?? null;
}

// ------------------------------------------------------------------ tavsiyalar (kanal bo'yicha)
type SugRow = { id: number; for_date: Date | string; topic: string; notes: string; hook: string; reason: string; status: Suggestion["status"] };
const toSug = (r: SugRow): Suggestion => ({
  id: r.id, forDate: typeof r.for_date === "string" ? r.for_date : r.for_date.toISOString().slice(0, 10), topic: r.topic, notes: r.notes, hook: r.hook, reason: r.reason, status: r.status,
});

export async function listSuggestions(ch: string, date: string): Promise<Suggestion[]> {
  const rows = await sql()<SugRow[]>`SELECT id::int AS id, for_date::text AS for_date, topic, notes, hook, reason, status FROM ytauto.topic_suggestions
    WHERE channel_id = ${ch} AND for_date = ${date}::date ORDER BY id`;
  return rows.map(toSug);
}

export async function getSuggestion(id: number): Promise<Suggestion | null> {
  const [r] = await sql()<SugRow[]>`SELECT id::int AS id, for_date::text AS for_date, topic, notes, hook, reason, status FROM ytauto.topic_suggestions WHERE id = ${id}`;
  return r ? toSug(r) : null;
}

export async function setSuggestionStatus(id: number, status: Suggestion["status"], jobId?: number) {
  await sql()`UPDATE ytauto.topic_suggestions SET status = ${status}, job_id = ${jobId ?? null} WHERE id = ${id}`;
}

/** Oxirgi 30 kunda rad etilgan va qabul qilingan tavsiyalar (takrorlamaslik uchun). */
export async function recentSuggestionTopics(ch: string): Promise<{ dismissed: string[]; accepted: string[] }> {
  const rows = await sql()`SELECT topic, status FROM ytauto.topic_suggestions WHERE channel_id = ${ch} AND for_date > current_date - 30 AND status <> 'new' ORDER BY id DESC LIMIT 60`;
  return { dismissed: rows.filter((r) => r.status === "dismissed").map((r) => r.topic as string), accepted: rows.filter((r) => r.status === "accepted").map((r) => r.topic as string) };
}

/** Bugungi tavsiyalar kanalga bir marta: boshqa jarayon allaqachon yozgan bo'lsa yozilmaydi. */
export async function insertSuggestions(ch: string, date: string, items: { topic: string; notes: string; hook: string; reason: string }[]): Promise<boolean> {
  const [x] = await sql()`SELECT count(*)::int AS n FROM ytauto.topic_suggestions WHERE channel_id = ${ch} AND for_date = ${date}::date`;
  if ((x?.n as number) > 0) return false;
  if (!items.length) return false;
  await sql()`INSERT INTO ytauto.topic_suggestions ${sql()(items.map((i) => ({
    channel_id: ch, for_date: date, topic: i.topic.slice(0, 300), notes: i.notes.slice(0, 2000), hook: i.hook.slice(0, 300), reason: i.reason.slice(0, 500),
  })))}`;
  return true;
}

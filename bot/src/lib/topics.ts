import "server-only";
import { sql } from "./db";
import { demoApply, demoCatalog, demoPrompts, demoTopics, isDemo } from "./demo";
import { addDays, parseLocal, utcToZoned, zonedToUtc } from "./time";
import { Status, type Action, type ActionResult, type Catalog, type NewTopic, type PromptKey, type Prompts, type Topic } from "./types";

const MAX_TOPICS_PER_REQUEST = 5;
const GAP_DAYS = 2; // desktop ilovadagi PublishDateSuggester bilan bir xil
const MIN_LEAD_MS = 30 * 60 * 1000;

/** Video hali tayyorlanmagan holatlar: shu holatlarda mavzuni tahrirlash/o'chirish mumkin. */
const EDITABLE = [Status.Queued, Status.Failed, Status.NeedsAttention];
const DELETABLE = [Status.Queued, Status.Failed, Status.NeedsAttention, Status.Cancelled];
const FINAL = [Status.Published, Status.Cancelled];

export const PROMPT_KEYS: Record<PromptKey, string> = { script: "prompt.script", montage: "prompt.montage" };

type Row = {
  id: number; topic: string; notes: string; title: string | null; status: number; publish_at_utc: Date;
  youtube_video_id: string | null; video_archive_path: string | null; publish_now: boolean; upload_requested: boolean; claimed_by: string | null;
  cancel_requested: boolean; last_error_message: string | null; attention_reason: string | null;
};

const cols = () => sql()`id::int AS id, topic, notes, title, status, publish_at_utc, youtube_video_id, video_archive_path, publish_now, upload_requested, claimed_by, cancel_requested, last_error_message, attention_reason`;

/** Tayyor (arxivlangan), YouTube'ga hali yuklanmagan video: kompyuter uni qayta tayyorlamasdan yuklaydi. */
function readyNotUploaded(r: Pick<Row, "claimed_by" | "youtube_video_id" | "video_archive_path" | "cancel_requested" | "status">) {
  return !r.claimed_by && !r.youtube_video_id && !!r.video_archive_path && !r.cancel_requested && !(FINAL as readonly number[]).includes(r.status);
}

function notMade(r: Pick<Row, "claimed_by" | "youtube_video_id" | "video_archive_path">) {
  return !r.claimed_by && !r.youtube_video_id && !r.video_archive_path;
}

function toTopic(r: Row, tz: string): Topic {
  const made = !notMade(r);
  return {
    id: r.id, topic: r.topic, notes: r.notes, title: r.title, status: r.status,
    publishAt: r.publish_at_utc.toISOString(), publishLocal: utcToZoned(r.publish_at_utc, tz),
    youtubeUrl: r.youtube_video_id ? `https://youtu.be/${r.youtube_video_id}` : null,
    publishNow: r.publish_now, claimed: !!r.claimed_by,
    error: r.status === Status.NeedsAttention ? r.attention_reason ?? r.last_error_message : r.status === Status.Failed ? r.last_error_message : null,
    editable: !made && (EDITABLE as readonly number[]).includes(r.status),
    deletable: !made && (DELETABLE as readonly number[]).includes(r.status),
    canPublishNow: !r.publish_now && !r.cancel_requested && !(FINAL as readonly number[]).includes(r.status),
    canUploadReady: !r.upload_requested && !r.publish_now && readyNotUploaded(r),
    uploadRequested: r.upload_requested && !r.youtube_video_id,
  };
}

// ------------------------------------------------------------------ katalog (desktop ilova yuboradi)
type CatalogJson = {
  voice_profiles?: string[];
  templates?: { name: string; kind: string }[];
  defaults?: { voice_profile?: string; template?: string; format?: number };
  youtube?: { publish_time?: string; time_zone?: string };
};

export async function getCatalog(): Promise<Catalog> {
  if (isDemo()) return demoCatalog;
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = 'bot.catalog'`;
  const c = row ? (JSON.parse(row.value as string) as CatalogJson) : null;
  return {
    voiceProfiles: c?.voice_profiles ?? [],
    templates: c?.templates ?? [],
    defaults: { voiceProfile: c?.defaults?.voice_profile ?? "", template: c?.defaults?.template ?? "", format: c?.defaults?.format ?? 0 },
    youtube: { publishTime: c?.youtube?.publish_time ?? "18:00", timeZone: c?.youtube?.time_zone ?? "Asia/Tashkent" },
    known: !!c,
  };
}

export async function listTopics(tz: string): Promise<Topic[]> {
  if (isDemo()) return demoTopics();
  // Chiqmaganlar hammasi + oxirgi 30 ta chiqqan.
  const rows = await sql()<Row[]>`
    (SELECT ${cols()} FROM ytauto.jobs WHERE status <> ${Status.Published} ORDER BY publish_at_utc, id LIMIT 300)
    UNION ALL
    (SELECT ${cols()} FROM ytauto.jobs WHERE status = ${Status.Published} ORDER BY publish_at_utc DESC LIMIT 30)`;
  return rows.map((r) => toTopic(r, tz));
}

async function getRow(id: number): Promise<Row | undefined> {
  const [r] = await sql()<Row[]>`SELECT ${cols()} FROM ytauto.jobs WHERE id = ${id}`;
  return r;
}

// ------------------------------------------------------------------ chiqish sanasi
async function suggestDates(count: number, cat: Catalog): Promise<Date[]> {
  const tz = cat.youtube.timeZone;
  const [row] = await sql()`SELECT max(publish_at_utc) AS last FROM ytauto.jobs WHERE status <> ${Status.Cancelled}`;
  const today = utcToZoned(new Date(), tz).date;
  let date = row?.last ? addDays(utcToZoned(row.last as Date, tz).date, GAP_DAYS) : today;
  if (date < today) date = today;
  const out: Date[] = [];
  for (let i = 0; i < count; i++) {
    let at = zonedToUtc(date, cat.youtube.publishTime, tz);
    while (at.getTime() <= Date.now() + MIN_LEAD_MS) { date = addDays(date, 1); at = zonedToUtc(date, cat.youtube.publishTime, tz); }
    out.push(at);
    date = addDays(date, GAP_DAYS);
  }
  return out;
}

function parsePublishAt(s: string | undefined, tz: string): Date | null | "invalid" {
  if (!s) return null;
  const p = parseLocal(s);
  if (!p) return "invalid";
  return zonedToUtc(p.date, p.time, tz);
}

const clean = (s: string | undefined | null) => (s ?? "").replace(/\s+\n/g, "\n").trim();

// ------------------------------------------------------------------ amallar
export async function applyAction(a: Action): Promise<ActionResult> {
  if (isDemo()) {
    if (a.type === "update_prompt") { const p = await previewPrompt(a); if ("error" in p) return { ok: false, message: p.error }; }
    return demoApply(a);
  }
  const cat = await getCatalog();
  const tz = cat.youtube.timeZone;
  switch (a.type) {
    case "create_topics": {
      const items = (a.topics ?? []).map((t) => ({ ...t, topic: clean(t.topic), notes: clean(t.notes) })).filter((t) => t.topic);
      if (items.length === 0) return { ok: false, message: "Mavzu kiritilmagan." };
      if (items.length > MAX_TOPICS_PER_REQUEST) return { ok: false, message: `Bir vaqtda eng ko'pi ${MAX_TOPICS_PER_REQUEST} ta mavzu qo'shiladi.` };
      const auto = await suggestDates(items.length, cat);
      const rows = [];
      for (let i = 0; i < items.length; i++) {
        const t = items[i] as NewTopic;
        const given = parsePublishAt(t.publishAt, tz);
        if (given === "invalid") return { ok: false, message: `Sana formati noto'g'ri: ${t.publishAt} (YYYY-MM-DD HH:mm).` };
        const at = given ?? auto[i];
        if (at.getTime() <= Date.now() + MIN_LEAD_MS) return { ok: false, message: `"${t.topic}": chiqish vaqti kamida 30 daqiqa kelajakda bo'lsin.` };
        const now = new Date();
        rows.push({
          topic: t.topic.slice(0, 500), notes: (t.notes ?? "").slice(0, 4000),
          language: "en", // video, subtitr va title har doim ingliz tilida
          target_duration_sec: 0, format: cat.defaults.format, template_name: cat.defaults.template, voice_profile: cat.defaults.voiceProfile,
          publish_at_utc: at, status: Status.Queued, created_at_utc: now, updated_at_utc: now,
        });
      }
      const ids = await sql()`INSERT INTO ytauto.jobs ${sql()(rows)} RETURNING id::int AS id`;
      return { ok: true, message: `${ids.length} ta mavzu qo'shildi (#${ids.map((r) => r.id).join(", #")}).` };
    }
    case "update_topic": {
      const r = await getRow(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      if (!notMade(r) || !(EDITABLE as readonly number[]).includes(r.status))
        return { ok: false, message: `#${a.id}: video allaqachon tayyorlangan yoki tayyorlanmoqda — o'zgartirib bo'lmaydi.` };
      const topic = a.topic !== undefined ? clean(a.topic) : r.topic;
      const notes = a.notes !== undefined ? clean(a.notes) : r.notes;
      if (!topic) return { ok: false, message: "Mavzu bo'sh bo'lmasligi kerak." };
      const given = parsePublishAt(a.publishAt, tz);
      if (given === "invalid") return { ok: false, message: `Sana formati noto'g'ri: ${a.publishAt}.` };
      const at = given ?? r.publish_at_utc;
      if (given && at.getTime() <= Date.now() + MIN_LEAD_MS) return { ok: false, message: "Chiqish vaqti kamida 30 daqiqa kelajakda bo'lsin." };
      const contentChanged = topic !== r.topic || notes !== r.notes;
      // Mazmun o'zgarsa: desktop ilovadagi kabi snapshot va title tozalanadi, vazifa navbatga qaytadi.
      const n = await sql()`
        UPDATE ytauto.jobs SET topic = ${topic}, notes = ${notes}, language = 'en', publish_at_utc = ${at}, updated_at_utc = now(),
          settings_snapshot = CASE WHEN ${contentChanged} THEN NULL ELSE settings_snapshot END,
          title = CASE WHEN ${contentChanged} THEN NULL ELSE title END,
          status = CASE WHEN ${contentChanged} OR status = ${Status.NeedsAttention} THEN ${Status.Queued} ELSE status END,
          run_attempts = CASE WHEN ${contentChanged} OR status = ${Status.NeedsAttention} THEN 0 ELSE run_attempts END,
          qa_fix_cycles = CASE WHEN ${contentChanged} THEN 0 ELSE qa_fix_cycles END,
          attention_reason = NULL, next_attempt_at_utc = NULL
        WHERE id = ${a.id} AND claimed_by IS NULL AND youtube_video_id IS NULL AND video_archive_path IS NULL`;
      return n.count ? { ok: true, message: `#${a.id} yangilandi.` } : { ok: false, message: `#${a.id}: worker shu payt band qildi — keyinroq urinib ko'ring.` };
    }
    case "delete_topic": {
      const r = await getRow(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      if (!notMade(r) || !(DELETABLE as readonly number[]).includes(r.status))
        return { ok: false, message: `#${a.id}: video allaqachon tayyorlangan yoki tayyorlanmoqda — o'chirib bo'lmaydi.` };
      const n = await sql()`DELETE FROM ytauto.jobs WHERE id = ${a.id} AND claimed_by IS NULL AND youtube_video_id IS NULL AND video_archive_path IS NULL`;
      return n.count ? { ok: true, message: `#${a.id} o'chirildi.` } : { ok: false, message: `#${a.id}: o'chirib bo'lmadi (worker band qilgan).` };
    }
    case "publish_now": {
      const n = await sql()`
        UPDATE ytauto.jobs SET publish_now = TRUE, next_attempt_at_utc = NULL, updated_at_utc = now()
        WHERE id = ${a.id} AND status NOT IN ${sql()(FINAL as unknown as number[])} AND cancel_requested = FALSE`;
      return n.count
        ? { ok: true, message: `#${a.id}: "Hozir chiqarish" yuborildi — internetga ulangan kompyuter ~2 daqiqada boshlaydi.` }
        : { ok: false, message: `#${a.id}: chiqarilgan yoki bekor qilingan — hozir chiqarib bo'lmaydi.` };
    }
    case "cancel_publish_now": {
      await sql()`UPDATE ytauto.jobs SET publish_now = FALSE, updated_at_utc = now() WHERE id = ${a.id}`;
      return { ok: true, message: `#${a.id}: "Hozir chiqarish" bekor qilindi.` };
    }
    case "upload_ready": {
      const r = await getRow(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      if (r.youtube_video_id) return { ok: false, message: `#${a.id}: video allaqachon YouTube'da.` };
      if (!r.video_archive_path) return { ok: false, message: `#${a.id}: video hali tayyor emas — tayyor bo'lgach yuklash mumkin.` };
      const n = await sql()`
        UPDATE ytauto.jobs SET upload_requested = TRUE, updated_at_utc = now()
        WHERE id = ${a.id} AND youtube_video_id IS NULL AND video_archive_path IS NOT NULL AND claimed_by IS NULL
          AND cancel_requested = FALSE AND status NOT IN ${sql()(FINAL as unknown as number[])}`;
      return n.count
        ? { ok: true, message: `#${a.id}: "YouTube'ga yuklash" yuborildi — video qayta tayyorlanmaydi, kompyuter ~2 daqiqada yuklaydi.` }
        : { ok: false, message: `#${a.id}: kompyuter shu payt band — keyinroq urinib ko'ring.` };
    }
    case "cancel_upload_ready": {
      await sql()`UPDATE ytauto.jobs SET upload_requested = FALSE, updated_at_utc = now() WHERE id = ${a.id}`;
      return { ok: true, message: `#${a.id}: "YouTube'ga yuklash" bekor qilindi.` };
    }
    case "update_prompt": {
      const res = await previewPrompt(a);
      if ("error" in res) return { ok: false, message: res.error };
      await sql()`
        INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${PROMPT_KEYS[a.key]}, ${res.text}, now())
        ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at_utc = excluded.updated_at_utc`;
      return { ok: true, message: `${a.key === "script" ? "Ssenariy (Gemini)" : "Montaj (Claude)"} prompti yangilandi — keyingi yangi vazifalardan boshlab ishlaydi.` };
    }
    default:
      return { ok: false, message: "Noma'lum amal." };
  }
}

export async function getPrompts(): Promise<Prompts> {
  if (isDemo()) return demoPrompts();
  const rows = await sql()`SELECT key, value FROM ytauto.shared_settings WHERE key IN ${sql()(Object.values(PROMPT_KEYS))}`;
  const by = new Map(rows.map((r) => [r.key as string, r.value as string]));
  return { script: by.get(PROMPT_KEYS.script) ?? "", montage: by.get(PROMPT_KEYS.montage) ?? "" };
}

/** Prompt o'zgarishini qo'llamasdan hisoblaydi (AI taklifini ko'rsatish va tekshirish uchun). */
export async function previewPrompt(a: Extract<Action, { type: "update_prompt" }>): Promise<{ text: string; before: string } | { error: string }> {
  if (!(a.key in PROMPT_KEYS)) return { error: "Noma'lum prompt." };
  const before = (await getPrompts())[a.key];
  let text = before;
  if (typeof a.text === "string" && a.text.trim()) text = a.text;
  else if (a.edits?.length) {
    for (const e of a.edits) {
      const count = e.find ? text.split(e.find).length - 1 : 0;
      if (count !== 1) return { error: `Promptda "${e.find.slice(0, 60)}" ${count === 0 ? "topilmadi" : `${count} marta uchradi`} — o'zgarish aniq emas.` };
      text = text.replace(e.find, () => e.replace);
    }
  } else return { error: "Prompt o'zgarishi berilmagan." };
  if (a.key === "script" && !text.includes("{topic}")) return { error: "Ssenariy promptida {topic} bo'lishi shart (mavzu shu joyga qo'yiladi)." };
  if (text.length > 40000) return { error: "Prompt juda uzun." };
  return { text, before };
}

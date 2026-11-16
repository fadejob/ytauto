import "server-only";
import { sql } from "./db";
import { queueForAll } from "./commands";
import { getConfig } from "./config";
import { templateCatalog } from "./library";
import { demoApply, demoCatalog, demoPrompts, demoTopics, isDemo } from "./demo";
import { addDays, parseLocal, utcToZoned, zonedToUtc } from "./time";
import { Status, type Action, type ActionResult, type Catalog, type NewTopic, type PromptKey, type Prompts, type Topic } from "./types";

/** Shablon va format: berilmasa bot sozlamasidagi (yoki katalogdagi) standart; berilsa katalogda borligi tekshiriladi. */
async function pickTemplateFormat(cat: Catalog, template?: string, format?: number, voice?: string): Promise<{ template: string; format: number; voice: string } | { error: string }> {
  const cfg = await getConfig();
  const tpl = template !== undefined ? template : cfg.defaults.template || cat.defaults.template;
  const fmt = format !== undefined ? format : cfg.defaults.format ?? cat.defaults.format;
  const vc = voice !== undefined && voice !== "" ? voice : cfg.defaults.voiceProfile || cat.defaults.voiceProfile || cat.voiceProfiles[0] || "";
  if (![0, 1, 2].includes(fmt)) return { error: "Format noto'g'ri (0 = 16:9, 1 = Shorts 9:16, 2 = 1:1)." };
  if (tpl && cat.known && cat.templates.length > 0 && !cat.templates.some((t) => t.name === tpl)) return { error: `Shablon topilmadi: "${tpl}".` };
  if (vc && cat.known && cat.voiceProfiles.length > 0 && !cat.voiceProfiles.includes(vc)) return { error: `Ovoz profili topilmadi: "${vc}".` };
  return { template: tpl, format: fmt, voice: vc };
}

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
  template_name: string; format: number; voice_profile: string;
};

const cols = () => sql()`id::int AS id, topic, notes, title, status, publish_at_utc, youtube_video_id, video_archive_path, publish_now, upload_requested, claimed_by, cancel_requested, last_error_message, attention_reason, template_name, format, voice_profile`;

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
    template: r.template_name, format: r.format, voice: r.voice_profile,
    canRetry: !r.claimed_by && (r.status === Status.Failed || r.status === Status.NeedsAttention),
    canCancel: !(FINAL as readonly number[]).includes(r.status),
    canReschedule: made && !r.claimed_by && !r.cancel_requested && !(FINAL as readonly number[]).includes(r.status),
    canForceUpload: r.status === Status.NeedsAttention && !r.youtube_video_id && !!r.video_archive_path && !r.claimed_by,
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
    templates: (await templateCatalog()) ?? c?.templates ?? [],
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
        const tf = await pickTemplateFormat(cat, t.template, t.format, t.voice);
        if ("error" in tf) return { ok: false, message: tf.error };
        const now = new Date();
        rows.push({
          topic: t.topic.slice(0, 500), notes: (t.notes ?? "").slice(0, 4000),
          language: "en", // video, subtitr va title har doim ingliz tilida
          target_duration_sec: 0, format: tf.format, template_name: tf.template, voice_profile: tf.voice,
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
      const tf = await pickTemplateFormat(cat, a.template ?? r.template_name, a.format ?? r.format, a.voice ?? r.voice_profile);
      if ("error" in tf) return { ok: false, message: tf.error };
      const contentChanged = topic !== r.topic || notes !== r.notes || tf.template !== r.template_name || tf.format !== r.format || tf.voice !== r.voice_profile;
      // Mazmun o'zgarsa: desktop ilovadagi kabi snapshot va title tozalanadi, vazifa navbatga qaytadi.
      const n = await sql()`
        UPDATE ytauto.jobs SET topic = ${topic}, notes = ${notes}, language = 'en', template_name = ${tf.template}, format = ${tf.format}, voice_profile = ${tf.voice}, publish_at_utc = ${at}, updated_at_utc = now(),
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
    case "retry":
    case "retry_settings": {
      const r = await getRow(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      if (r.claimed_by) return { ok: false, message: `#${a.id}: worker hozir shu vazifa ustida ishlamoqda.` };
      if (r.status !== Status.Failed && r.status !== Status.NeedsAttention) return { ok: false, message: `#${a.id}: faqat xato yoki "e'tibor kerak" holatidagi vazifani qayta urinish mumkin.` };
      const fresh = a.type === "retry_settings"; // joriy sozlama/promptlar bilan qayta snapshot
      const n = await sql()`
        UPDATE ytauto.jobs SET status = ${Status.Queued}, cancel_requested = FALSE, next_attempt_at_utc = NULL, attention_reason = NULL, run_attempts = 0,
          settings_snapshot = CASE WHEN ${fresh} THEN NULL ELSE settings_snapshot END, updated_at_utc = now()
        WHERE id = ${a.id} AND claimed_by IS NULL`;
      return n.count
        ? { ok: true, message: `#${a.id}: navbatga qaytarildi${fresh ? " (joriy sozlamalar bilan)" : ""} — tayyor bosqichlar saqlanadi, keyingi ishga tushishda davom etadi.` }
        : { ok: false, message: `#${a.id}: worker shu payt band qildi.` };
    }
    case "cancel": {
      const r = await getRow(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      if ((FINAL as readonly number[]).includes(r.status)) return { ok: false, message: `#${a.id}: allaqachon chiqarilgan yoki bekor qilingan.` };
      const n = await sql()`UPDATE ytauto.jobs SET status = ${Status.Cancelled}, cancel_requested = TRUE, updated_at_utc = now()
                            WHERE id = ${a.id} AND claimed_by IS NULL AND status <> ${Status.Published}`;
      if (n.count) return { ok: true, message: `#${a.id}: bekor qilindi (YouTube'ga yuklangan video o'chirilmaydi).` };
      await sql()`UPDATE ytauto.jobs SET cancel_requested = TRUE, updated_at_utc = now() WHERE id = ${a.id}`;
      return { ok: true, message: `#${a.id}: worker ishlamoqda — bekor qilish so'rovi yuborildi (u o'zi ochgan jarayonlarni yopib to'xtaydi).` };
    }
    case "reschedule": {
      const at = parsePublishAt(a.publishAt, tz);
      if (at === "invalid" || at === null) return { ok: false, message: "Sana formati noto'g'ri (YYYY-MM-DD HH:mm)." };
      if (at.getTime() <= Date.now() + MIN_LEAD_MS) return { ok: false, message: "Yangi vaqt kamida 30 daqiqa kelajakda bo'lsin." };
      const r = await getRow(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      if (r.claimed_by) return { ok: false, message: `#${a.id}: worker hozir shu vazifa ustida ishlamoqda; keyinroq o'zgartiring.` };
      if ((FINAL as readonly number[]).includes(r.status)) return { ok: false, message: `#${a.id}: chiqarilgan yoki bekor qilingan.` };
      const n = await sql()`
        UPDATE ytauto.jobs SET publish_at_utc = ${at}, next_attempt_at_utc = NULL, updated_at_utc = now(),
          status = CASE WHEN status = ${Status.NeedsAttention} THEN ${Status.Queued} ELSE status END,
          run_attempts = CASE WHEN status = ${Status.NeedsAttention} THEN 0 ELSE run_attempts END,
          attention_reason = CASE WHEN status = ${Status.NeedsAttention} THEN NULL ELSE attention_reason END
        WHERE id = ${a.id} AND claimed_by IS NULL`;
      return n.count
        ? { ok: true, message: `#${a.id}: yangi chiqish vaqti saqlandi.${r.status === Status.NeedsAttention ? " Vazifa navbatga qaytdi (arxivdagi video yuklanadi, qayta yaratilmaydi)." : ""}` }
        : { ok: false, message: `#${a.id}: worker shu payt band qildi.` };
    }
    case "force_new_upload": {
      const r = await getRow(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      if (r.claimed_by) return { ok: false, message: `#${a.id}: worker hozir shu vazifa ustida ishlamoqda.` };
      if (r.youtube_video_id) return { ok: false, message: `#${a.id}: video allaqachon YouTube'da — yangi upload kerak emas.` };
      await sql()`UPDATE ytauto.job_stages SET state = 0, outputs_json = NULL WHERE job_id = ${a.id} AND stage = 8`; // 8 = Upload bosqichi
      await sql()`UPDATE ytauto.jobs SET status = ${Status.Queued}, cancel_requested = FALSE, next_attempt_at_utc = NULL, attention_reason = NULL, run_attempts = 0, updated_at_utc = now()
                  WHERE id = ${a.id} AND claimed_by IS NULL`;
      await queueForAll("clear_upload_session", String(a.id)); // eski upload sessiyasi (lokal sir) kompyuterda tozalanadi
      return { ok: true, message: `#${a.id}: upload sessiyasi tozalanadi — keyingi ishga tushishda yangi upload boshlanadi.` };
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
  if (a.reset) {
    // Standart prompt desktop ilovadan keladi (shared_settings: prompt.default.script / prompt.default.montage).
    const [row] = isDemo() ? [] : await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${"prompt.default." + a.key}`;
    if (!row && !isDemo()) return { error: "Standart prompt hali bazada yo'q — kompyuterdagi YtAuto ilovasini bir marta oching." };
    text = row ? (row.value as string) : before;
  } else if (typeof a.text === "string" && a.text.trim()) text = a.text;
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

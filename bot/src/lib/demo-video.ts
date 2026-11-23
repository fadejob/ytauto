import "server-only";
import crypto from "node:crypto";
import { currentAccountId, isGuest, sql } from "./db";
import { geminiReady, GEMINI_REQUIRED } from "./config";
import { getCatalog, pickTemplateFormat } from "./topics";
import { getMachines } from "./machines";
import { objectInfo, signDownload, signUpload } from "./storage";
import { activeChannelId, youtubeCredentials } from "./youtube";
import { Status, StatusText } from "./types";

/**
 * Claude Code (MCP) orqali loyiha demo videosi. Claude loyihani o'rganadi, screenshot va qisqa action videolarni Storage'ga
 * (imzolangan havola bilan, to'g'ridan-to'g'ri) yuklaydi va so'rov yuboradi. Ssenariy (Gemini), ovoz (VoiceStudio), montaj (OpenFilm)
 * va render foydalanuvchi kompyuteridagi YtAuto worker'ida - oddiy mavzular bilan bir xil pipeline (ytauto.jobs, kind = 'demo').
 *
 * Hammasi hisob kontekstida (withAccount + RLS): boshqa hisobning vazifasi, materiali yoki natijasi ko'rinmaydi.
 * Storage yo'llari faqat joriy hisob papkasida quriladi (acc/<hisob>/...), mijoz bergan yo'l ishlatilmaydi.
 */

export class McpToolError extends Error {}
const fail = (m: string): never => { throw new McpToolError(m); };

export const MEDIA_LIMIT = 30;
const ACTIVE_LIMIT = 5;
const IMAGE_MAX = 25 * 1024 * 1024; // kompyuterdagi DemoMediaFetcher.MaxImageBytes bilan bir xil
const VIDEO_MAX = 500 * 1024 * 1024; // DemoMediaFetcher.MaxVideoBytes
const DOWNLOAD_TTL_SEC = 30 * 24 * 3600; // vazifa navbatda bir necha kun turishi mumkin
const RESULT_TTL_SEC = 24 * 3600;
const IMAGE_EXT = ["png", "jpg", "jpeg", "webp", "gif"];
const VIDEO_EXT = ["mp4", "webm", "mov", "mkv"];
const TYPES: Record<string, string> = {
  png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", webp: "image/webp", gif: "image/gif",
  mp4: "video/mp4", webm: "video/webm", mov: "video/quicktime", mkv: "video/x-matroska",
};
const MEDIA_ID = /^[0-9a-f]{16}\/\d{2}\.(png|jpe?g|webp|gif|mp4|webm|mov|mkv)$/;
const FINAL = [Status.Published, Status.Cancelled];
const STAGES = ["", "script", "voice", "align", "edit", "render", "validate", "archive", "upload"];
const STAGE_DONE_PCT = [0, 15, 30, 40, 65, 82, 92, 97, 100];

const mediaPath = (id: string) => `acc/${currentAccountId()}/demo-media/${id}`;
const resultPrefix = () => `acc/${currentAccountId()}/demo-result/`;
const str = (v: unknown, max: number) => (typeof v === "string" ? v.replace(/\s+\n/g, "\n").trim().slice(0, max) : "");
const opt = (v: unknown, max: number) => str(v, max) || undefined;

// ------------------------------------------------------------------ 1. materiallarni yuklash
export type UploadSlot = { media_id: string; file: string; kind: "screenshot" | "video"; upload_url: string; method: "PUT"; content_type: string; curl: string };

export async function requestMediaUpload(args: Record<string, unknown>): Promise<{ uploads: UploadSlot[]; expires_in_sec: number; note: string }> {
  const files = Array.isArray(args.files) ? (args.files as Record<string, unknown>[]) : [];
  if (files.length === 0) fail("files bo'sh: har screenshot/video uchun {name, size_bytes, kind} bering.");
  if (files.length > MEDIA_LIMIT) fail(`Bir videoga ko'pi bilan ${MEDIA_LIMIT} ta material.`);
  const batch = crypto.randomBytes(8).toString("hex");
  const uploads: UploadSlot[] = [];
  for (let i = 0; i < files.length; i++) {
    const f = files[i] ?? {};
    const name = str(f.name, 200);
    const ext = (name.match(/\.([A-Za-z0-9]+)$/)?.[1] ?? "").toLowerCase();
    const kind = f.kind === "video" || VIDEO_EXT.includes(ext) ? "video" : "screenshot";
    const allowed = kind === "video" ? VIDEO_EXT : IMAGE_EXT;
    if (!allowed.includes(ext)) fail(`"${name}": ${kind === "video" ? "video" : "screenshot"} formati ${allowed.join(", ")} bo'lishi kerak.`);
    const size = Number(f.size_bytes);
    const max = kind === "video" ? VIDEO_MAX : IMAGE_MAX;
    if (!Number.isFinite(size) || size <= 0 || size > max) fail(`"${name}": hajm 1 baytdan ${max / 1048576} MB gacha bo'lsin.`);
    const id = `${batch}/${String(i + 1).padStart(2, "0")}.${ext}`;
    const url = await signUpload(mediaPath(id));
    const type = TYPES[ext];
    uploads.push({
      media_id: id, file: name, kind, upload_url: url, method: "PUT", content_type: type,
      curl: `curl -sS -X PUT -H "Content-Type: ${type}" --data-binary @"${name.replace(/"/g, "")}" "${url}"`,
    });
  }
  return {
    uploads, expires_in_sec: 7200,
    note: "Upload each file with an HTTP PUT of the raw bytes to its upload_url (the curl line does exactly that), then pass the media_id values to create_demo_video. Links are single-use and expire in 2 hours.",
  };
}

// ------------------------------------------------------------------ 2. video yaratish
type SpecMedia = { id: string; kind: string; title: string; description: string; feature?: string; highlight?: string; url: string; size_bytes: number; content_type?: string };

/** Montaj effektlari (worker'dagi YtAuto.Core/Demo/DemoEffects.cs bilan bir xil kalitlar). */
export const DEMO_EFFECTS = [
  "zoom_in", "zoom_out", "pan", "ken_burns", "scroll_through", "parallax_3d", "split_screen", "picture_in_picture",
  "crossfade", "slide_push", "whip_pan", "zoom_through", "wipe", "blur_dissolve", "glitch",
  "spotlight", "outline_box", "cursor_click", "magnifier", "callout",
  "title_reveal", "kinetic_text",
  "cut_ins", "speed_ramp", "freeze_frame", "impact_shake",
] as const;

function demoEffects(v: unknown): string[] | undefined {
  if (v === undefined || v === null) return undefined;
  if (!Array.isArray(v)) fail("requirements.effects ro'yxat bo'lsin.");
  const list = [...new Set((v as unknown[]).map((x) => String(x).trim().toLowerCase()).filter(Boolean))];
  const bad = list.filter((x) => !(DEMO_EFFECTS as readonly string[]).includes(x));
  if (bad.length) fail(`Noma'lum effekt: ${bad.join(", ")}. Mumkinlari: ${DEMO_EFFECTS.join(", ")}.`);
  return list.length ? list : undefined;
}

export async function createDemoVideo(args: Record<string, unknown>) {
  if (!(await geminiReady())) fail(GEMINI_REQUIRED);
  const p = (args.project ?? {}) as Record<string, unknown>;
  const q = (args.requirements ?? {}) as Record<string, unknown>;
  const pub = (args.publish ?? {}) as Record<string, unknown>;
  const name = str(p.name, 120), summary = str(p.summary, 3000);
  if (!name) fail("project.name majburiy.");
  if (summary.length < 20) fail("project.summary majburiy: loyiha nima qiladi va kim uchun (kamida bir-ikki gap).");
  const features = (Array.isArray(p.features) ? p.features : []).slice(0, 20).map((f) => {
    const o = (f ?? {}) as Record<string, unknown>;
    return { name: str(o.name, 120), description: str(o.description, 600) };
  }).filter((f) => f.name);

  const list = Array.isArray(args.media) ? (args.media as Record<string, unknown>[]) : [];
  if (list.length === 0) fail("media bo'sh: avval request_media_upload bilan screenshot/videolarni yuklang.");
  if (list.length > MEDIA_LIMIT) fail(`media ${MEDIA_LIMIT} tadan oshmasin.`);
  const seen = new Set<string>();
  const media: SpecMedia[] = [];
  for (const m of list) {
    const id = str(m.media_id, 80);
    if (!MEDIA_ID.test(id)) fail(`media_id noto'g'ri: "${id}" (request_media_upload qaytargan qiymatni bering).`);
    if (seen.has(id)) fail(`media_id takrorlangan: ${id}`);
    seen.add(id);
    const ext = id.split(".").pop()!;
    const kind = VIDEO_EXT.includes(ext) ? "video" : "screenshot";
    const title = str(m.title, 160), description = str(m.description, 1000);
    if (!title || description.length < 10) fail(`${id}: title va description (kadrda nima ko'rinadi / qanday amal bajariladi) majburiy.`);
    const info = await objectInfo(mediaPath(id));
    if (!info) fail(`${id} Storage'da topilmadi — fayl yuklanmagan yoki yuklash tugamagan.`);
    const max = kind === "video" ? VIDEO_MAX : IMAGE_MAX;
    if (info!.size > max) fail(`${id}: fayl juda katta.`);
    media.push({
      id, kind, title, description, feature: opt(m.feature, 120), highlight: opt(m.highlight, 200),
      url: await signDownload(mediaPath(id), DOWNLOAD_TTL_SEC), size_bytes: info!.size, content_type: info!.contentType || TYPES[ext],
    });
  }

  const language = (str(q.language, 5) || "en").toLowerCase();
  if (!/^[a-z]{2}$/.test(language)) fail("requirements.language ikki harfli kod bo'lsin (masalan en, uz, ru).");
  const duration = Number(q.duration_sec ?? 0);
  if (!Number.isFinite(duration) || (duration !== 0 && (duration < 20 || duration > 600))) fail("requirements.duration_sec 0 (avtomatik) yoki 20–600 bo'lsin.");
  const fmtArg = str(q.format, 8) || "16:9";
  const format = ({ "16:9": 0, "9:16": 1, "1:1": 2 } as Record<string, number>)[fmtArg];
  if (format === undefined) fail('requirements.format "16:9", "9:16" yoki "1:1" bo\'lsin.');
  const cat = await getCatalog();
  // Demo videoda kanal shabloni ishlatilmaydi (faqat loyiha materiallari): bu yerda faqat format va ovoz tanlanadi.
  const tf = await pickTemplateFormat(cat, undefined, format, q.voice_profile === undefined ? undefined : str(q.voice_profile, 120));
  if ("error" in tf) fail(tf.error);
  const t = tf as { template: string; format: number; voice: string };

  // Chiqarish: alohida amal; shu yerda faqat foydalanuvchi aniq buyurgan bo'lsa va faqat asosiy hisob kanaliga.
  const publish = await publishIntent(pub);

  const [{ n }] = await sql()`SELECT count(*)::int AS n FROM ytauto.jobs WHERE kind = 'demo'
    AND status NOT IN ${sql()([Status.Published, Status.Cancelled, Status.Ready, Status.NeedsAttention])}`;
  if ((n as number) >= ACTIVE_LIMIT) fail(`Navbatda allaqachon ${n} ta demo video bor (chegara ${ACTIVE_LIMIT}). Ular tugashini kuting.`);

  const spec = {
    version: 1,
    project: { name, summary, url: opt(p.url, 300), repository: opt(p.repository, 300), tech_stack: opt(p.tech_stack, 300), features },
    media,
    requirements: { language, duration_sec: duration, audience: opt(q.audience, 300), tone: opt(q.tone, 200), notes: opt(q.notes, 2000), effects: demoEffects(q.effects) },
    publish,
  };
  const now = new Date();
  const [row] = await sql()`INSERT INTO ytauto.jobs ${sql()({
    topic: name, notes: summary.slice(0, 4000), language, target_duration_sec: duration, format: t.format, template_name: "",
    voice_profile: t.voice, publish_at_utc: publish.at ?? now, status: Status.Queued, created_at_utc: now, updated_at_utc: now,
    channel_id: await activeChannelId(), // chiqarilsa - so'rov paytidagi faol kanalga
    kind: "demo", demo_spec: JSON.stringify({ ...spec, publish: { requested: publish.requested, when: publish.when } }),
  })} RETURNING id::int AS id`;
  const machines = await machineSummary();
  return {
    job_id: row.id as number, status: "queued",
    voice_profile: t.voice || "(kompyuterdagi standart ovoz)", effects: spec.requirements.effects ?? "auto (varied mix)", format: fmtArg, language,
    publish: publish.requested ? { requested: true, when: publish.when } : { requested: false },
    machines,
    next: "The YtAuto worker on the user's PC picks the job up within ~10 minutes (when the PC is on and online) and runs script → voice → montage → render. Poll get_video_status every few minutes; when status is \"ready\" call get_video_result.",
  };
}

type Intent = { requested: boolean; when?: string; at?: Date };
async function publishIntent(pub: Record<string, unknown>): Promise<Intent> {
  if (pub.requested !== true) return { requested: false };
  if (isGuest()) fail("Bu qo'shimcha (mehmon) token: MCP orqali YouTube'ga avtomatik chiqarish faqat asosiy hisob kanaliga ruxsat etilgan. Videoni chiqarishsiz yarating.");
  if (!(await youtubeCredentials())) fail("YouTube kanali ulanmagan (botda Sozlamalar → YouTube). Videoni chiqarishsiz yarating yoki avval kanalni ulang.");
  const when = str(pub.when, 40) || "now";
  if (when === "now") return { requested: true, when: "now" };
  const at = new Date(when);
  if (Number.isNaN(at.getTime())) fail('publish.when "now" yoki ISO vaqt bo\'lsin (masalan 2026-12-01T15:00:00Z).');
  if (at.getTime() < Date.now() + 30 * 60 * 1000) fail("publish.when kamida 30 daqiqa kelajakda bo'lsin.");
  return { requested: true, when: at.toISOString(), at };
}

async function machineSummary() {
  try {
    const ms = await getMachines();
    return ms.map((m) => ({ name: m.name, online: m.online, worker: m.worker.state, working_on: m.worker.jobId }));
  } catch { return []; }
}

// ------------------------------------------------------------------ 3. holat
type JobRow = {
  id: number; topic: string; status: number; title: string | null; description: string | null; claimed_by: string | null;
  last_error_message: string | null; attention_reason: string | null; video_archive_path: string | null; youtube_video_id: string | null;
  publish_now: boolean; upload_requested: boolean; publish_at_utc: Date; created_at_utc: Date; audio_duration_sec: number | null;
  demo_spec: string | null; demo_result: string | null; cancel_requested: boolean; next_attempt_at_utc: Date | null;
};

async function getDemoJob(args: Record<string, unknown>): Promise<JobRow> {
  const id = Number(args.job_id);
  if (!Number.isInteger(id) || id <= 0) fail("job_id butun son bo'lsin.");
  const [r] = await sql()<JobRow[]>`SELECT id::int AS id, topic, status, title, description, claimed_by, last_error_message, attention_reason, video_archive_path,
    youtube_video_id, publish_now, upload_requested, publish_at_utc, created_at_utc, audio_duration_sec, demo_spec, demo_result, cancel_requested, next_attempt_at_utc
    FROM ytauto.jobs WHERE id = ${id} AND kind = 'demo'`;
  if (!r) fail(`Demo video #${id} topilmadi (yoki boshqa hisobniki).`);
  return r!;
}

function phase(r: JobRow): string {
  if (r.status === Status.Published) return "published";
  if (r.status === Status.Cancelled) return "cancelled";
  if (r.youtube_video_id) return "publishing";
  if (r.status === Status.Ready) return r.publish_now || r.upload_requested ? "publishing" : "ready";
  if (r.status === Status.NeedsAttention) return r.video_archive_path ? "ready_with_problem" : "failed";
  if (r.status === Status.Failed) return "retrying";
  if (r.status === Status.Queued && !r.claimed_by) return "queued";
  return "in_progress";
}

export async function videoStatus(args: Record<string, unknown>) {
  const r = await getDemoJob(args);
  const stages = await sql()`SELECT stage, state, attempts, started_at_utc, finished_at_utc, error_message FROM ytauto.job_stages WHERE job_id = ${r.id} ORDER BY stage`;
  const stateName = ["pending", "running", "done", "failed", "skipped"];
  let pct = 0;
  for (const s of stages) if ((s.state as number) === 2) pct = Math.max(pct, STAGE_DONE_PCT[s.stage as number] ?? 0);
  const p = phase(r);
  if (p === "ready" || p === "published" || p === "publishing") pct = 100;
  const events = await sql()`SELECT at_utc, level, stage, message FROM ytauto.job_events WHERE job_id = ${r.id} ORDER BY id DESC LIMIT 8`;
  const [{ ahead }] = await sql()`SELECT count(*)::int AS ahead FROM ytauto.jobs WHERE kind = 'demo' AND status = ${Status.Queued} AND claimed_by IS NULL
    AND cancel_requested = FALSE AND (created_at_utc, id) < (${r.created_at_utc}, ${r.id})`;
  const error = r.status === Status.NeedsAttention ? r.attention_reason ?? r.last_error_message : r.status === Status.Failed ? r.last_error_message : null;
  return {
    job_id: r.id, project: r.topic, status: p, status_text: StatusText[r.status] ?? String(r.status), progress_percent: pct,
    stages: stages.filter((s) => (s.stage as number) <= 7).map((s) => ({
      stage: STAGES[s.stage as number] ?? String(s.stage), state: stateName[s.state as number] ?? String(s.state), attempts: s.attempts as number,
      error: (s.state as number) === 3 ? (s.error_message as string | null) : undefined,
    })),
    queue_position: p === "queued" ? (ahead as number) + 1 : null,
    next_retry_at: p === "retrying" && r.next_attempt_at_utc ? r.next_attempt_at_utc.toISOString() : null,
    error,
    machines: await machineSummary(),
    recent_events: events.reverse().map((e) => `${(e.at_utc as Date).toISOString().slice(11, 19)} ${e.level}${e.stage ? ` [${e.stage}]` : ""}: ${e.message as string}`),
    hint: p === "ready" ? "Video is ready: call get_video_result. Publish only if the user explicitly asks (publish_video)."
      : p === "failed" ? "The job needs attention; show the error to the user. It can be retried from the Telegram bot after fixing the cause."
      : "Still working; check again in a few minutes.",
  };
}

// ------------------------------------------------------------------ 4. natija
type Result = { video_path?: string; cover_path?: string; subtitles_path?: string; size_bytes?: number; duration_sec?: number; local_path?: string; machine?: string; error?: string; sha256?: string };

export async function videoResult(args: Record<string, unknown>) {
  const r = await getDemoJob(args);
  const p = phase(r);
  if (!r.video_archive_path && !r.youtube_video_id) {
    return { job_id: r.id, ready: false, status: p, message: "Video hali tayyor emas — get_video_status bilan kuzating." };
  }
  let res: Result = {};
  try { res = r.demo_result ? (JSON.parse(r.demo_result) as Result) : {}; } catch { /* buzilgan */ }
  const own = (path?: string) => (path && path.startsWith(resultPrefix()) && !path.includes("..") ? path : null);
  const sign = async (path?: string) => { const ok = own(path); return ok ? signDownload(ok, RESULT_TTL_SEC) : null; };
  return {
    job_id: r.id, ready: true, status: p,
    title: r.title, description: r.description,
    duration_sec: res.duration_sec ?? r.audio_duration_sec,
    video: own(res.video_path) ? { url: await sign(res.video_path), expires_in_sec: RESULT_TTL_SEC, size_bytes: res.size_bytes ?? null, sha256: res.sha256 ?? null } : null,
    cover_url: await sign(res.cover_path),
    subtitles_url: await sign(res.subtitles_path),
    local_archive: { path: res.local_path ?? r.video_archive_path, machine: res.machine ?? null },
    upload_error: res.error ?? null,
    youtube: r.youtube_video_id ? { url: `https://youtu.be/${r.youtube_video_id}`, published: r.status === Status.Published } : null,
    download_hint: "Download with: curl -L -o demo.mp4 \"<video.url>\" (the link expires in 24 h; call get_video_result again for a fresh one).",
  };
}

// ------------------------------------------------------------------ 5. YouTube'ga chiqarish
export async function publishVideo(args: Record<string, unknown>) {
  if (args.confirm !== true) fail("confirm: true kerak — faqat foydalanuvchi videoni YouTube'ga chiqarishni aniq buyurgan bo'lsa chaqiring.");
  if (isGuest()) fail("Bu qo'shimcha (mehmon) token: MCP orqali YouTube'ga chiqarish faqat asosiy hisob kanaliga ruxsat etilgan.");
  if (!(await youtubeCredentials())) fail("YouTube kanali ulanmagan (botda Sozlamalar → YouTube).");
  const r = await getDemoJob(args);
  if (r.youtube_video_id) return { job_id: r.id, ok: true, message: "Video allaqachon YouTube'da.", youtube_url: `https://youtu.be/${r.youtube_video_id}` };
  if ((FINAL as number[]).includes(r.status) || r.cancel_requested) fail(`#${r.id}: bekor qilingan yoki chiqarilgan.`);
  const intent = await publishIntent({ requested: true, when: args.when ?? "now" });

  if (!r.video_archive_path) {
    // Hali tayyorlanmoqda: navbatda turgan (band qilinmagan) bo'lsa niyat so'rovga yoziladi - tayyor bo'lgach chiqadi.
    if (r.claimed_by) fail(`#${r.id}: video hozir tayyorlanmoqda — status "ready" bo'lgach publish_video'ni qayta chaqiring.`);
    const spec = JSON.parse(r.demo_spec ?? "{}") as Record<string, unknown>;
    spec.publish = { requested: true, when: intent.when };
    const n = await sql()`UPDATE ytauto.jobs SET demo_spec = ${JSON.stringify(spec)}, publish_at_utc = ${intent.at ?? new Date()}, updated_at_utc = now()
      WHERE id = ${r.id} AND claimed_by IS NULL AND video_archive_path IS NULL`;
    if (!n.count) fail(`#${r.id}: worker shu payt ishni boshladi — status "ready" bo'lgach qayta urinib ko'ring.`);
    return { job_id: r.id, ok: true, message: "Video hali tayyor emas: tayyor bo'lishi bilan asosiy YouTube kanaliga " + (intent.when === "now" ? "darhol public chiqariladi." : `${intent.when} ga rejalashtiriladi.`) };
  }
  if (r.claimed_by) fail(`#${r.id}: kompyuter hozir shu vazifa ustida ishlamoqda — birozdan keyin qayta urinib ko'ring.`);
  const n = intent.when === "now"
    ? await sql()`UPDATE ytauto.jobs SET publish_now = TRUE, upload_requested = FALSE, next_attempt_at_utc = NULL, updated_at_utc = now()
        WHERE id = ${r.id} AND claimed_by IS NULL AND youtube_video_id IS NULL AND cancel_requested = FALSE`
    : await sql()`UPDATE ytauto.jobs SET publish_at_utc = ${intent.at!}, upload_requested = TRUE, publish_now = FALSE, updated_at_utc = now()
        WHERE id = ${r.id} AND claimed_by IS NULL AND youtube_video_id IS NULL AND cancel_requested = FALSE`;
  if (!n.count) fail(`#${r.id}: holat o'zgardi — qayta urinib ko'ring.`);
  return {
    job_id: r.id, ok: true,
    message: intent.when === "now"
      ? "Chiqarish so'rovi yuborildi: kompyuter ~2 daqiqada tayyor videoni asosiy YouTube kanaliga public yuklaydi (qayta tayyorlanmaydi)."
      : `Chiqarish so'rovi yuborildi: video asosiy YouTube kanaliga yuklanadi va ${intent.when} da public bo'ladi.`,
    next: "Use get_video_status / get_video_result to see the YouTube link once the upload finishes.",
  };
}

// ------------------------------------------------------------------ worker: natijani Storage'ga yuklash havolalari
const RESULT_FILES: Record<string, number> = { "video.mp4": 2 * 1024 * 1024 * 1024, "cover.jpg": 10 * 1024 * 1024, "subtitles.srt": 2 * 1024 * 1024 };

/** Joriy hisob (vazifa egasi) kontekstida: vazifa demo ekanini tekshirib, natija fayllari uchun bir martalik yuklash havolalari. */
export async function signResultUploads(jobId: number, files: { name: string; size: number }[]) {
  if (!Number.isInteger(jobId) || jobId <= 0) fail("jobId noto'g'ri.");
  const [j] = await sql()`SELECT id FROM ytauto.jobs WHERE id = ${jobId} AND kind = 'demo'`;
  if (!j) fail(`Demo vazifa #${jobId} topilmadi.`);
  if (!Array.isArray(files) || files.length === 0 || files.length > 3) fail("files noto'g'ri.");
  const uploads = [];
  for (const f of files) {
    const max = RESULT_FILES[f.name];
    if (!max) fail(`Ruxsat etilmagan fayl: ${f.name}`);
    if (!(Number(f.size) > 0 && Number(f.size) <= max)) fail(`${f.name}: hajm noto'g'ri.`);
    const path = `${resultPrefix()}job-${jobId}/${f.name}`;
    uploads.push({ name: f.name, path, uploadUrl: await signUpload(path, { upsert: true }) });
  }
  return { uploads };
}

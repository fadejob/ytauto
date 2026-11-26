import "server-only";
import { applyAction, getCatalog, listTopics, MCP_MAX_TOPICS } from "./topics";
import { McpToolError } from "./demo-video";
import { FormatText, StatusText, type NewTopic, type Topic } from "./types";

/**
 * Claude Code (MCP) orqali mavzular: ko'rish, bir chaqiruvda 40 tagacha qo'shish, yangilash va o'chirish.
 * Hammasi bot UI ishlatadigan applyAction orqali (bir xil tekshiruvlar: sana, shablon, ovoz, musiqa, qo'lda matn) va hisob kontekstida (RLS).
 */

type Json = Record<string, unknown>;
const fail = (m: string): never => { throw new McpToolError(m); };

const str = (v: unknown, max: number): string | undefined => (typeof v === "string" ? v.slice(0, max) : undefined);
const FORMATS: Record<string, number> = { "16:9": 0, "9:16": 1, "1:1": 2 };
const FORMAT_NAMES = ["16:9", "9:16", "1:1"];

function format(v: unknown): number | undefined {
  if (v === undefined || v === null || v === "") return undefined;
  const f = typeof v === "number" ? v : FORMATS[String(v)];
  if (f === undefined || ![0, 1, 2].includes(f)) fail(`format "16:9", "9:16" yoki "1:1" bo'lsin (berildi: ${String(v)}).`);
  return f;
}

/** MCP kirishidagi mavzu → botdagi NewTopic (maydon nomlari bot UI bilan bir xil). */
function toNewTopic(t: Json, i: number): NewTopic {
  if (!t || typeof t !== "object") fail(`topics[${i}] obyekt bo'lsin.`);
  return {
    topic: str(t.topic, 500) ?? "",
    notes: str(t.notes, 4000),
    title: str(t.title, 200),
    script: str(t.script, 30000),
    publishAt: str(t.publish_at, 40),
    template: str(t.template, 200),
    format: format(t.format),
    voice: str(t.voice, 200),
    music: str(t.music, 200),
  };
}

function view(t: Topic) {
  return {
    id: t.id, topic: t.topic, notes: t.notes || undefined,
    status: StatusText[t.status] ?? String(t.status),
    publish_at: `${t.publishLocal.date} ${t.publishLocal.time}`, publish_at_utc: t.publishAt,
    title: t.title ?? t.manualTitle ?? undefined,
    script_source: t.hasAudio ? "manual+audio" : t.manualScript ? "manual" : "gemini",
    template: t.template, format: FORMAT_NAMES[t.format] ?? String(t.format), voice: t.voice, music: t.music || "auto",
    youtube_url: t.youtubeUrl ?? undefined, error: t.error ?? undefined,
    can_edit: t.editable, can_delete: t.deletable,
  };
}

export async function topicOptions() {
  const cat = await getCatalog();
  return {
    time_zone: cat.youtube.timeZone,
    default_publish_time: cat.youtube.publishTime,
    templates: cat.templates.map((t) => t.name),
    voices: cat.voiceProfiles,
    formats: FORMAT_NAMES.map((f, i) => ({ value: f, name: FormatText[i] })),
    music: { enabled: cat.music.enabled, values: ["auto (omit)", "none", ...cat.music.tracks.map((t) => `${t.id} — ${t.title}`)] },
    defaults: { template: cat.defaults.template, voice: cat.defaults.voiceProfile, format: FORMAT_NAMES[cat.defaults.format] ?? "16:9" },
    max_topics_per_call: MCP_MAX_TOPICS,
    note: "Omitted fields use the defaults. publish_at is local time in time_zone, format \"YYYY-MM-DD HH:mm\"; omitted = next free slot (every 2 days at default_publish_time).",
  };
}

export async function listTopicsTool(args: Json) {
  const cat = await getCatalog();
  const all = await listTopics(cat.youtube.timeZone);
  const filter = String(args.filter ?? "all");
  const pick = filter === "pending" ? all.filter((t) => t.editable)
    : filter === "published" ? all.filter((t) => t.youtubeUrl)
    : filter === "problems" ? all.filter((t) => t.error)
    : all;
  return { time_zone: cat.youtube.timeZone, count: pick.length, topics: pick.map(view) };
}

export async function addTopics(args: Json) {
  const raw = args.topics;
  if (!Array.isArray(raw) || raw.length === 0) fail("topics bo'sh bo'lmasin.");
  if ((raw as unknown[]).length > MCP_MAX_TOPICS) fail(`Bir chaqiruvda eng ko'pi ${MCP_MAX_TOPICS} ta mavzu.`);
  const topics = (raw as Json[]).map(toNewTopic);
  const r = await applyAction({ type: "create_topics", topics }, { maxTopics: MCP_MAX_TOPICS });
  if (!r.ok) fail(`${r.message} Hech qaysi mavzu qo'shilmadi — xatoni tuzatib, hammasini qayta yuboring.`);
  return { ok: true, message: r.message };
}

export async function updateTopics(args: Json) {
  const raw = args.updates;
  if (!Array.isArray(raw) || raw.length === 0) fail("updates bo'sh bo'lmasin.");
  if ((raw as unknown[]).length > MCP_MAX_TOPICS) fail(`Bir chaqiruvda eng ko'pi ${MCP_MAX_TOPICS} ta mavzu.`);
  const results = [];
  for (const u of raw as Json[]) {
    const id = Number(u?.id);
    if (!Number.isInteger(id) || id <= 0) { results.push({ id: u?.id, ok: false, message: "id butun son bo'lsin." }); continue; }
    const t = toNewTopic(u, 0);
    // Faqat berilgan maydonlar o'zgaradi (topic bo'sh satr bo'lsa ham "berilgan" hisoblanadi - applyAction rad etadi).
    const r = await applyAction({
      type: "update_topic", id,
      topic: u.topic !== undefined ? t.topic : undefined, notes: t.notes, title: t.title, script: t.script, publishAt: t.publishAt,
      template: t.template, format: t.format, voice: t.voice, music: t.music,
    });
    results.push({ id, ...r });
  }
  return { updated: results.filter((r) => r.ok).length, failed: results.filter((r) => !r.ok).length, results };
}

export async function deleteTopics(args: Json) {
  if (args.confirm !== true) fail("confirm: true kerak — faqat foydalanuvchi o'chirishni so'ragan bo'lsa.");
  const ids = args.ids;
  if (!Array.isArray(ids) || ids.length === 0) fail("ids bo'sh bo'lmasin.");
  if ((ids as unknown[]).length > MCP_MAX_TOPICS) fail(`Bir chaqiruvda eng ko'pi ${MCP_MAX_TOPICS} ta mavzu.`);
  const results = [];
  for (const v of ids as unknown[]) {
    const id = Number(v);
    if (!Number.isInteger(id) || id <= 0) { results.push({ id: v, ok: false, message: "id butun son bo'lsin." }); continue; }
    results.push({ id, ...(await applyAction({ type: "delete_topic", id })) });
  }
  return {
    deleted: results.filter((r) => r.ok).length, failed: results.filter((r) => !r.ok).length, results,
    hint: "Topics whose video is already made or being made cannot be deleted; a YouTube video is removed separately with delete_youtube_video.",
  };
}

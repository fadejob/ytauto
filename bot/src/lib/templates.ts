import "server-only";
import crypto from "node:crypto";
import { currentAccountId, sql, wakeAgents } from "./db";
import { isDemo } from "./demo";
import { BUCKET, storageCfg } from "./storage";
import type { PendingTemplate, PreparedUpload, TemplateUpload } from "./types";

/**
 * Shablon yuklash: brauzer faylni Supabase Storage'ga (private "templates" bucket) imzolangan havola bilan to'g'ridan-to'g'ri yuboradi
 * (Vercel'ning ~4.5 MB so'rov chegarasini chetlab o'tish uchun). So'ng har tanlangan kompyuter uchun bazada "pending" yozuv qo'shiladi;
 * kompyuter agenti darhol uyg'otiladi (ilova yopiq bo'lsa ham) - uni yuklab, o'rnatadi va holatni (progress, tayyor/xato) yozadi. Hamma yozuv tugagach fayl Storage'dan o'chiriladi.
 */
const MAX_BYTES = 600 * 1024 * 1024;
const VIDEO_EXT = [".mp4", ".mov", ".webm", ".mkv", ".m4v"];
const KINDS = ["Mp4Reference", "OpenFilmProject"] as const;

const cfg = storageCfg;

export async function prepareUpload(i: { name: string; kind: string; filename: string; size: number }): Promise<PreparedUpload> {
  const name = i.name?.trim();
  if (!name || name.length > 60) throw new Error("Shablon nomi 1–60 belgi bo'lsin.");
  if (!KINDS.includes(i.kind as (typeof KINDS)[number])) throw new Error("Shablon turi noto'g'ri.");
  const ext = (i.filename.match(/\.[A-Za-z0-9]+$/)?.[0] ?? "").toLowerCase();
  if (i.kind === "OpenFilmProject" ? ext !== ".zip" : !VIDEO_EXT.includes(ext))
    throw new Error(i.kind === "OpenFilmProject" ? "OpenFilm loyihasi .zip bo'lsin (ichida film.html)." : "Video MP4/MOV/WEBM/MKV bo'lsin.");
  if (!Number.isFinite(i.size) || i.size <= 0 || i.size > MAX_BYTES) throw new Error(`Fayl hajmi 1 baytdan ${MAX_BYTES / 1048576} MB gacha bo'lsin.`);
  if (isDemo()) return { groupId: "demo", path: "demo/template" + ext, uploadUrl: "" };
  const { url, headers } = cfg();
  const groupId = crypto.randomBytes(8).toString("hex");
  const path = `acc/${currentAccountId()}/${groupId}/template${ext}`; // har hisob o'z papkasida
  const r = await fetch(`${url}/storage/v1/object/upload/sign/${BUCKET}/${path}`, { method: "POST", headers, body: "{}" });
  const j = (await r.json().catch(() => ({}))) as { url?: string; message?: string };
  if (!r.ok || !j.url) throw new Error(`Yuklash havolasi olinmadi: ${j.message ?? r.status}`);
  return { groupId, path, uploadUrl: `${url}/storage/v1${j.url}` };
}

export async function commitUpload(i: { groupId: string; path: string; name: string; kind: string; size: number; machines: string[] }) {
  if (!i.machines?.length) throw new Error("Kamida bitta kompyuter tanlang.");
  if (!KINDS.includes(i.kind as (typeof KINDS)[number])) throw new Error("Shablon turi noto'g'ri.");
  if (!isDemo() && (!/^[0-9a-f]{16}$/.test(i.groupId) || !i.path.startsWith(`acc/${currentAccountId()}/${i.groupId}/`) || i.path.includes(".."))) throw new Error("Noto'g'ri fayl yo'li.");
  if (isDemo()) {
    for (const m of i.machines) demoRows.push({ id: demoId++, name: i.name, kind: i.kind as TemplateUpload["kind"], machine: m, status: "pending", progress: 0, message: null, createdAt: new Date().toISOString(), size: i.size, t0: Date.now() });
    return { ok: true };
  }
  const { url, headers } = cfg();
  const info = await fetch(`${url}/storage/v1/object/info/${BUCKET}/${i.path}`, { headers });
  if (!info.ok) throw new Error("Fayl Storage'da topilmadi — yuklash tugamagan bo'lishi mumkin.");
  const s = await fetch(`${url}/storage/v1/object/sign/${BUCKET}/${i.path}`, { method: "POST", headers, body: JSON.stringify({ expiresIn: 86400 }) });
  const sj = (await s.json().catch(() => ({}))) as { signedURL?: string };
  if (!s.ok || !sj.signedURL) throw new Error("Yuklab olish havolasi yaratilmadi.");
  const downloadUrl = `${url}/storage/v1${sj.signedURL}`;
  for (const m of i.machines) {
    await sql()`INSERT INTO ytauto.template_uploads(group_id, name, kind, storage_path, size_bytes, machine, download_url)
                VALUES (${i.groupId}, ${i.name.trim().slice(0, 60)}, ${i.kind}, ${i.path}, ${Math.round(i.size)}, ${m}, ${downloadUrl})`;
  }
  if (i.machines.length) wakeAgents("template");
  return { ok: true };
}

/** Tozalash (muddati o'tganlar + Storage'dan o'chirish) hisob bo'yicha minutiga ko'pi bilan bir marta - ro'yxat har 4 s so'raladi. */
const lastCleanup = new Map<string, number>();

export async function listUploads(): Promise<TemplateUpload[]> {
  if (isDemo()) return demoList();
  const acc = currentAccountId();
  if (Date.now() - (lastCleanup.get(acc) ?? 0) > 60_000) { lastCleanup.set(acc, Date.now()); await cleanupUploads(); }
  const rows = await sql()`SELECT id::int AS id, name, kind, machine, status, progress, message, created_at_utc, size_bytes::float8 AS size
                           FROM ytauto.template_uploads ORDER BY id DESC LIMIT 30`;
  return mapUploads(rows);
}

async function cleanupUploads() {
  // Muddati o'tgan (imzolangan havola 24 soat) kutayotganlar xato deb belgilanadi.
  await sql()`UPDATE ytauto.template_uploads SET status = 'error', message = 'Muddati o''tdi (kompyuter 24 soat ichida olmadi)', updated_at_utc = now()
              WHERE status = 'pending' AND created_at_utc < now() - interval '23 hours'`;
  // Hamma kompyuter tugatgan guruhlarning fayli Storage'dan o'chiriladi.
  const done = await sql()`
    SELECT group_id, min(storage_path) AS path FROM ytauto.template_uploads
    GROUP BY group_id HAVING bool_and(status IN ('done', 'error')) AND bool_or(NOT cleaned)`;
  if (done.length) {
    const { url, headers } = cfg();
    // Guruhlar parallel (har biri Storage'ga alohida HTTP so'rovi).
    await Promise.all(done.map(async (g) => {
      const r = await fetch(`${url}/storage/v1/object/${BUCKET}`, { method: "DELETE", headers, body: JSON.stringify({ prefixes: [g.path] }) }).catch(() => null);
      if (r?.ok) await sql()`UPDATE ytauto.template_uploads SET cleaned = TRUE WHERE group_id = ${g.group_id}`;
    }));
  }
}

function mapUploads(rows: Record<string, unknown>[]): TemplateUpload[] {
  return rows.map((r) => ({
    id: r.id as number, name: r.name as string, kind: r.kind as TemplateUpload["kind"], machine: r.machine as string,
    status: r.status as TemplateUpload["status"], progress: r.progress as number, message: (r.message as string | null) ?? null,
    createdAt: (r.created_at_utc as Date).toISOString(), size: r.size as number,
  }));
}

/**
 * Hali kompyuterga tushmagan yuklashlar (nom bo'yicha, eng ilg'or kompyuter progressi bilan).
 * Shablon faqat kompyuter uni yuklab olib o'rnatgach (ytauto.templates'ga yozilgach) mavzuga tanlanadi.
 */
export async function pendingTemplates(): Promise<PendingTemplate[]> {
  if (isDemo()) return demoList().filter((u) => u.status !== "done" && u.status !== "error").map((u) => ({ name: u.name, progress: u.progress }));
  const rows = await sql()`SELECT name, max(progress)::int AS progress FROM ytauto.template_uploads
                           WHERE status IN ('pending', 'downloading', 'importing') AND created_at_utc > now() - interval '23 hours'
                           GROUP BY name ORDER BY min(id)`;
  return rows.map((r) => ({ name: r.name as string, progress: r.progress as number }));
}

// ------------------------------------------------------------------ demo
type DemoRow = TemplateUpload & { t0: number };
const demoRows: DemoRow[] = [];
let demoId = 1;
function demoList(): TemplateUpload[] {
  const now = Date.now();
  return demoRows.map((r) => {
    const s = (now - r.t0) / 1000;
    const status: TemplateUpload["status"] = s < 3 ? "pending" : s < 9 ? "downloading" : s < 11 ? "importing" : "done";
    return {
      ...r, status,
      progress: status === "pending" ? 0 : status === "downloading" ? Math.min(90, Math.round(((s - 3) / 6) * 90)) : status === "importing" ? 92 : 100,
      message: status === "done" ? `O'rnatildi: '${r.name}'` : null,
    };
  }).reverse();
}

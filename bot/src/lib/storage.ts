import "server-only";

/**
 * Supabase Storage (private "templates" bucket): shablonlar va botdan yuklangan tayyor ovozlar ("audio/" papkasi) shu yerda.
 * Brauzer faylni imzolangan havola bilan to'g'ridan-to'g'ri yuboradi (Vercel'ning ~4.5 MB so'rov chegarasini chetlab o'tish uchun).
 */
export const BUCKET = "templates";

export function storageCfg() {
  const url = process.env.SUPABASE_URL?.replace(/\/+$/, "") ?? "";
  const key = process.env.SUPABASE_SERVICE_ROLE_KEY ?? "";
  if (!url || !key) throw new Error("SUPABASE_URL va SUPABASE_SERVICE_ROLE_KEY sozlanmagan (bot/.env.local).");
  return { url, key, headers: { apikey: key, "Content-Type": "application/json" } };
}

/** Brauzer PUT qiladigan bir martalik yuklash havolasi (2 soat amal qiladi). upsert - bor faylni almashtirishga ruxsat. */
export async function signUpload(path: string, opts?: { upsert?: boolean }): Promise<string> {
  const { url, headers } = storageCfg();
  const h = opts?.upsert ? { ...headers, "x-upsert": "true" } : headers;
  const r = await fetch(`${url}/storage/v1/object/upload/sign/${BUCKET}/${path}`, { method: "POST", headers: h, body: "{}" });
  const j = (await r.json().catch(() => ({}))) as { url?: string; message?: string };
  if (!r.ok || !j.url) throw new Error(`Yuklash havolasi olinmadi: ${j.message ?? r.status}`);
  return `${url}/storage/v1${j.url}`;
}

export async function objectExists(path: string): Promise<boolean> {
  const { url, headers } = storageCfg();
  const r = await fetch(`${url}/storage/v1/object/info/${BUCKET}/${path}`, { headers });
  return r.ok;
}

/** Fayl hajmi va turi (yo'q bo'lsa null). Storage versiyasiga qarab maydonlar ildizda yoki metadata ichida. */
export async function objectInfo(path: string): Promise<{ size: number; contentType: string } | null> {
  const { url, headers } = storageCfg();
  const r = await fetch(`${url}/storage/v1/object/info/${BUCKET}/${path}`, { headers });
  if (!r.ok) return null;
  const j = (await r.json().catch(() => ({}))) as { size?: number; content_type?: string; metadata?: { size?: number; mimetype?: string; contentLength?: number } };
  return { size: Number(j.size ?? j.metadata?.size ?? j.metadata?.contentLength ?? 0), contentType: String(j.content_type ?? j.metadata?.mimetype ?? "") };
}

/** Kompyuter yuklab oladigan imzolangan havola. */
export async function signDownload(path: string, expiresInSec: number): Promise<string> {
  const { url, headers } = storageCfg();
  const s = await fetch(`${url}/storage/v1/object/sign/${BUCKET}/${path}`, { method: "POST", headers, body: JSON.stringify({ expiresIn: expiresInSec }) });
  const sj = (await s.json().catch(() => ({}))) as { signedURL?: string };
  if (!s.ok || !sj.signedURL) throw new Error("Yuklab olish havolasi yaratilmadi.");
  return `${url}/storage/v1${sj.signedURL}`;
}

export async function removeObjects(paths: string[]): Promise<boolean> {
  if (!paths.length) return true;
  const { url, headers } = storageCfg();
  const r = await fetch(`${url}/storage/v1/object/${BUCKET}`, { method: "DELETE", headers, body: JSON.stringify({ prefixes: paths }) });
  return r.ok;
}

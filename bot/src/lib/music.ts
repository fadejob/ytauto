import "server-only";
import { sql } from "./db";
import { isDemo } from "./demo";
import { BUCKET, removeObjects, signDownload, storageCfg } from "./storage";
import type { MusicLibrary, MusicTrack } from "./types";

/**
 * Fon musiqasi kutubxonasi (har hisobniki, shared_settings['music.library'] - bitta JSON, bazaga yangi jadval kerak emas).
 *
 * Oqim: bot Openverse'dan (CC0 / CC BY musiqa, asosan Jamendo) ro'yxat ko'rsatadi - qidiruv brauzerdan to'g'ridan-to'g'ri (Openverse CORS'ga
 * ruxsat beradi, cheklov har foydalanuvchiga alohida). «Ishlatish» bosilganda server trekni Openverse'dan id bo'yicha QAYTA tekshiradi
 * (litsenziya brauzerdan kelganiga ishonilmaydi), faylni yuklab Supabase Storage'ga qo'yadi va kutubxonaga yozadi.
 * Desktop ilova sozlamalarni o'qiganda kutubxonani ham oladi, faylni bir marta yuklab oladi va har videoga navbat bilan bitta trek qo'yadi;
 * YouTube tavsifiga muallif (CC BY talabi) avtomatik yoziladi.
 *
 * Faqat CC0 va CC BY: BY-SA / BY-ND musiqani videoga qo'shish (sinxronlash) litsenziya bo'yicha "o'zgartirilgan asar" hisoblanadi
 * (BY-ND taqiqlaydi, BY-SA butun videoni shu litsenziyaga majburlaydi), NC - monetizatsiyali kanalga mos emas.
 */
const KEY = "music.library";
export const MAX_TRACKS = 30;
const MAX_BYTES = 40 * 1024 * 1024;
const MAX_DURATION_MS = 15 * 60 * 1000;
const URL_DAYS = 365; // desktop yuklab oladigan imzolangan havola; 30 kundan kam qolsa yangilanadi
const OK_LICENSES = new Set(["cc0", "by"]);
/** Openverse audio fayllari shu manbalardan keladi (SSRF'dan himoya: boshqa manzilga so'rov yuborilmaydi). */
const AUDIO_HOSTS = [/^prod-\d+\.storage\.jamendo\.com$/i, /^(.+\.)?jamendo\.com$/i, /^upload\.wikimedia\.org$/i, /^cdn\.freesound\.org$/i];

const DEFAULT: MusicLibrary = { enabled: true, volume: 0.12, tracks: [] };
// Demo: barcha API marshrutlari bitta holatni ko'rishi uchun globalThis'da (har marshrut alohida bundle bo'ladi).
const g = globalThis as unknown as { __ytautoDemoMusic?: MusicLibrary };
const demo = () => (g.__ytautoDemoMusic ??= { ...DEFAULT, tracks: [], topics: {} });

async function read(lock = false): Promise<MusicLibrary> {
  if (isDemo()) return structuredClone(demo());
  const [row] = lock
    ? await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY} FOR UPDATE`
    : await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  if (!row) return { ...DEFAULT, tracks: [] };
  try {
    const v = JSON.parse(row.value as string) as Partial<MusicLibrary>;
    const topics: Record<string, string> = {};
    if (v.topics && typeof v.topics === "object")
      for (const [k, val] of Object.entries(v.topics)) if (/^\d{1,12}$/.test(k) && typeof val === "string" && val) topics[k] = val;
    return { enabled: v.enabled !== false, volume: clampVolume(v.volume), tracks: Array.isArray(v.tracks) ? v.tracks : [], topics };
  } catch { return { ...DEFAULT, tracks: [] }; }
}

async function write(lib: MusicLibrary) {
  if (isDemo()) { g.__ytautoDemoMusic = structuredClone(lib); return; }
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${JSON.stringify(lib)}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

const clampVolume = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? Math.min(0.2, Math.max(0.03, Math.round(v * 100) / 100)) : DEFAULT.volume);

/** Kutubxona; muddati yaqinlashgan yuklab olish havolalari shu yerda yangilanadi (desktop doim ishlaydigan havola oladi). */
export async function getLibrary(): Promise<MusicLibrary> {
  const lib = await read();
  if (isDemo() || lib.tracks.length === 0) return lib;
  const soon = Date.now() + 30 * 86400_000;
  const stale = lib.tracks.filter((t) => t.storagePath && Date.parse(t.urlExpiresAt) < soon);
  if (stale.length === 0) return lib;
  const locked = await read(true);
  for (const t of locked.tracks) {
    if (!t.storagePath || Date.parse(t.urlExpiresAt) >= soon) continue;
    try { t.url = await signDownload(t.storagePath, URL_DAYS * 86400); t.urlExpiresAt = new Date(Date.now() + URL_DAYS * 86400_000).toISOString(); }
    catch { /* keyingi ochilishda qayta urinadi */ }
  }
  await write(locked);
  return locked;
}

export async function setMusicSettings(i: { enabled?: unknown; volume?: unknown }): Promise<MusicLibrary> {
  const lib = await read(true);
  if (typeof i.enabled === "boolean") lib.enabled = i.enabled;
  if (i.volume !== undefined) lib.volume = clampVolume(Number(i.volume));
  await write(lib);
  return lib;
}

type OvTrack = {
  id: string; title?: string; creator?: string; license?: string; license_version?: string; license_url?: string; foreign_landing_url?: string;
  url?: string; duration?: number | null; genres?: string[] | null; attribution?: string; category?: string | null;
};

/** Trek Openverse'dan id bo'yicha olinadi va tekshiriladi: faqat CC0/CC BY musiqa, ma'lum manbadan, 15 daqiqagacha. */
async function fetchOpenverse(id: string): Promise<OvTrack & { url: string }> {
  const r = await fetch(`https://api.openverse.org/v1/audio/${id}/`, { headers: { "User-Agent": "YtAuto/1.0 (background music picker)" }, cache: "no-store" });
  if (r.status === 404) throw new Error("Trek Openverse'da topilmadi (o'chirilgan bo'lishi mumkin).");
  if (!r.ok) throw new Error(`Openverse javob bermadi (HTTP ${r.status}). Birozdan keyin qayta urinib ko'ring.`);
  const t = (await r.json()) as OvTrack;
  if (!OK_LICENSES.has((t.license ?? "").toLowerCase())) throw new Error("Bu trek litsenziyasi videoga mos emas: faqat CC0 yoki CC BY ishlatiladi.");
  if (t.category && t.category !== "music") throw new Error("Bu musiqa emas (ovoz effekti yoki yozuv).");
  if ((t.duration ?? 0) > MAX_DURATION_MS) throw new Error("Trek juda uzun (15 daqiqadan ortiq).");
  let u: URL;
  try { u = new URL(t.url ?? ""); } catch { throw new Error("Trek fayli manzili yo'q."); }
  if (u.protocol !== "https:" || !AUDIO_HOSTS.some((h) => h.test(u.hostname))) throw new Error("Trek fayli ishonchsiz manzilda.");
  return { ...t, url: u.toString() };
}

/** Faylni hajm chegarasi bilan yuklab oladi (oqim bo'yicha - katta faylni oxirigacha o'qimaydi). */
async function download(url: string): Promise<{ bytes: Uint8Array; type: string }> {
  const r = await fetch(url, { headers: { "User-Agent": "YtAuto/1.0" }, redirect: "follow", cache: "no-store" });
  if (!r.ok || !r.body) throw new Error(`Trek faylini yuklab bo'lmadi (HTTP ${r.status}).`);
  const type = (r.headers.get("content-type") ?? "").split(";")[0].trim().toLowerCase();
  if (!type.startsWith("audio/")) throw new Error("Manba audio fayl qaytarmadi.");
  const len = Number(r.headers.get("content-length") ?? 0);
  if (len > MAX_BYTES) throw new Error("Trek fayli juda katta (40 MB dan ortiq).");
  const reader = r.body.getReader();
  const parts: Uint8Array[] = []; let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_BYTES) { await reader.cancel(); throw new Error("Trek fayli juda katta (40 MB dan ortiq)."); }
    parts.push(value);
  }
  if (total < 10_000) throw new Error("Trek fayli bo'sh yoki buzilgan.");
  const bytes = new Uint8Array(total); let o = 0;
  for (const p of parts) { bytes.set(p, o); o += p.byteLength; }
  return { bytes, type };
}

const extOf = (type: string) => (type === "audio/mpeg" || type === "audio/mp3" ? ".mp3" : type === "audio/ogg" ? ".ogg" : type === "audio/wav" || type === "audio/x-wav" ? ".wav" : type === "audio/flac" ? ".flac" : ".mp3");

/**
 * «Ishlatish»: trek serverga (Storage) yuklanadi va kutubxonaga qo'shiladi.
 * Openverse so'rovi, faylni yuklab olish (40 MB gacha) va Storage'ga yuklash baza tranzaksiyasidan TASHQARIDA (1–10 s) - ulanish
 * band turmaydi; bazaga faqat boshida (tekshiruv) va oxirida (yozish) qisqa murojaat (run).
 */
export async function addTrack(idRaw: string, run: <T>(fn: () => Promise<T>) => Promise<T>, accountId: string): Promise<{ library: MusicLibrary; message: string }> {
  const id = String(idRaw ?? "").trim().toLowerCase();
  if (!/^[0-9a-f-]{36}$/.test(id)) throw new Error("Trek identifikatori noto'g'ri.");
  const before = await run(() => read());
  if (before.tracks.some((t) => t.id === id)) return { library: before, message: "Bu trek allaqachon kutubxonada." };
  if (before.tracks.length >= MAX_TRACKS) throw new Error(`Kutubxonada ko'pi bilan ${MAX_TRACKS} ta trek bo'ladi — avval keraksizini o'chiring.`);

  const t = await fetchOpenverse(id);
  const base: Omit<MusicTrack, "storagePath" | "url" | "urlExpiresAt"> = {
    id, title: (t.title ?? "").trim().slice(0, 200) || "Nomsiz trek", creator: (t.creator ?? "").trim().slice(0, 200),
    license: (t.license ?? "").toLowerCase(), licenseVersion: t.license_version ?? "", licenseUrl: t.license_url ?? "",
    landingUrl: t.foreign_landing_url ?? "", durationSec: Math.round((t.duration ?? 0) / 1000), genres: (t.genres ?? []).slice(0, 6),
    attribution: (t.attribution ?? "").slice(0, 500), previewUrl: t.url, addedAt: new Date().toISOString(),
  };
  let track: MusicTrack;
  if (isDemo()) {
    track = { ...base, storagePath: "", url: t.url, urlExpiresAt: new Date(Date.now() + URL_DAYS * 86400_000).toISOString() };
  } else {
    const file = await download(t.url);
    const path = `acc/${accountId}/music/${id}${extOf(file.type)}`;
    const { url, headers } = storageCfg();
    const up = await fetch(`${url}/storage/v1/object/${BUCKET}/${path}`, {
      method: "POST", headers: { apikey: headers.apikey, "Content-Type": file.type, "x-upsert": "true" }, body: Buffer.from(file.bytes),
    });
    if (!up.ok) throw new Error(`Serverga yuklab bo'lmadi (Storage HTTP ${up.status}).`);
    track = { ...base, storagePath: path, url: await signDownload(path, URL_DAYS * 86400), urlExpiresAt: new Date(Date.now() + URL_DAYS * 86400_000).toISOString() };
  }

  const lib = await run(async () => {
    const l = await read(true); // yuklash vaqtida boshqa qurilmadan o'zgargan bo'lishi mumkin
    if (!l.tracks.some((x) => x.id === id)) {
      if (l.tracks.length >= MAX_TRACKS) { if (track.storagePath) await removeObjects([track.storagePath]); throw new Error(`Kutubxona to'lgan (${MAX_TRACKS} ta).`); }
      l.tracks.push(track);
      await write(l);
    }
    return l;
  });
  return { library: lib, message: `Qo'shildi: «${track.title}». Kompyuter keyingi videolarda fon musiqasi sifatida ishlatadi.` };
}

export async function removeTrack(idRaw: string): Promise<MusicLibrary> {
  const id = String(idRaw ?? "").trim().toLowerCase();
  const lib = await read(true);
  const t = lib.tracks.find((x) => x.id === id);
  if (!t) return lib;
  lib.tracks = lib.tracks.filter((x) => x.id !== id);
  // Shu trek tanlangan mavzular avtomatik rejimga qaytadi.
  for (const [k, v] of Object.entries(lib.topics ?? {})) if (v === id) delete lib.topics![k];
  await write(lib);
  if (t.storagePath && !isDemo()) { try { await removeObjects([t.storagePath]); } catch { /* fayl qolsa zarari yo'q */ } }
  return lib;
}

// ------------------------------------------------------------------ har mavzuning o'z musiqasi

/** Mavzu oynasi uchun: kutubxona qisqacha (imzolangan havolalarsiz). */
export async function musicCatalog(): Promise<{ enabled: boolean; tracks: { id: string; title: string; creator: string }[] }> {
  const lib = await read();
  return { enabled: lib.enabled, tracks: lib.tracks.map((t) => ({ id: t.id, title: t.title, creator: t.creator })) };
}

/** Mavzu id → tanlov ("none" yoki trek id). Yo'q - avtomatik. */
export async function topicMusicMap(): Promise<Record<string, string>> {
  return (await read()).topics ?? {};
}

/** Tanlovni tekshiradi: "" (avtomatik), "none" yoki kutubxonadagi trek. Xato bo'lsa matn. */
export async function checkTopicMusic(values: (string | undefined)[]): Promise<string | null> {
  const want = values.map((v) => (v ?? "").trim().toLowerCase()).filter((v) => v && v !== "none");
  if (want.length === 0) return null;
  const ids = new Set((await read()).tracks.map((t) => t.id));
  return want.every((v) => ids.has(v)) ? null : "Tanlangan fon musiqasi kutubxonada yo'q (o'chirilgan bo'lishi mumkin) — boshqasini tanlang.";
}

/**
 * Mavzular tanlovini yozadi ("" - avtomatik, ya'ni yozuv o'chiriladi). Mavzu amali bilan bitta tranzaksiyada.
 * Bazada endi yo'q mavzularning yozuvlari shu yerda tozalanadi (JSON o'smasin).
 */
export async function setTopicMusic(entries: { id: number; music: string | undefined }[]) {
  const todo = entries.filter((e) => e.music !== undefined);
  if (todo.length === 0) return;
  const lib = await read(true);
  const topics = { ...(lib.topics ?? {}) };
  for (const e of todo) {
    const v = (e.music ?? "").trim().toLowerCase();
    if (!v) delete topics[String(e.id)];
    else topics[String(e.id)] = v;
  }
  const keys = Object.keys(topics).map(Number);
  if (!isDemo() && keys.length > 0) {
    const alive = new Set((await sql()`SELECT id::int AS id FROM ytauto.jobs WHERE id = ANY(${keys})`).map((r) => r.id as number));
    for (const k of keys) if (!alive.has(k)) delete topics[String(k)];
  }
  lib.topics = topics;
  await write(lib);
}

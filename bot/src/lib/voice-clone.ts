import "server-only";
import crypto from "node:crypto";
import { setCloneProfile } from "./config";
import { currentAccountId, sql, wakeAgents } from "./db";
import { isDemo } from "./demo";
import { objectExists, removeObjects, signDownload, signUpload } from "./storage";
import type { VoiceClone } from "./types";
import { VOICE_PASSAGES } from "./voice-passages";

/**
 * «Mening ovozim»: foydalanuvchi botdagi oson inglizcha matnni (15–20 s) o'qiydi, bot yozib oladi va shu yerga yuboradi.
 * Model (MOSS-TTS-Nano) kompyuterda bitta; namuna va o'qilgan matn (ref_text) shu hisob vazifalari uchun beriladi
 * (zero-shot klonlash, o'qitish yo'q).
 *
 * Saqlash: Storage `acc/<hisob>/voice/<id>/sample.<ext>` (hisobning o'z papkasi), holat shared_settings['voice.clone'] da (RLS).
 * Worker vazifa egasining namunasini `url` dan yuklab, VoiceStudio'da `profileName` nomli ovoz profilini yaratadi/yangilaydi
 * (`version` o'zgarganda) va natijani shu kalitga yozadi (status/message). Bot ro'yxatiga «Mening ovozim» profili qo'shiladi.
 */
export const KEY = "voice.clone";
export const MODEL = "moss-tts-nano";
export const PROFILE_LABEL = "Mening ovozim";
export const SAMPLE_EXT = [".wav", ".mp3", ".m4a", ".ogg", ".opus", ".webm", ".flac", ".aac"];
const MAX_BYTES = 15 * 1024 * 1024;
const MIN_SEC = 10, MAX_SEC = 45; // botdagi matn ~15–20 s o'qiladi (yozish 40 s da to'xtaydi)
/** Kompyuter yuklab oladigan havola: namuna almashtirilmaguncha amal qiladi. */
const DOWNLOAD_TTL_SEC = 365 * 24 * 3600;

type Stored = VoiceClone & { path: string; url: string };
/** O'chirilgandan keyin: kompyuter VoiceStudio'dagi profil va keshdagi namunani o'chirib, kalitni olib tashlaydi. */
type Tombstone = { deleted: true; profileName: string; deletedAt: string };
const isTomb = (v: unknown): v is Tombstone => !!v && (v as Tombstone).deleted === true;

/** VoiceStudio'dagi profil nomi: hisobga xos, boshqa hisob bilan to'qnashmaydi. */
export const profileNameFor = (accountId: string) => `YtAuto u-${accountId.replace(/-/g, "").slice(0, 8)}`;

let demo: Stored | null = null;

async function read(): Promise<Stored | null> {
  if (isDemo()) return demo;
  const [r] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  try { const v = r ? JSON.parse(r.value as string) : null; return v && !isTomb(v) ? (v as Stored) : null; } catch { return null; }
}

async function write(s: Stored | Tombstone) {
  if (isDemo()) { demo = isTomb(s) ? null : s; return; }
  const json = JSON.stringify(s);
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${json}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
  wakeAgents("voice"); // kompyuter yangi namunani darhol oladi (yoki o'chirilganini tozalaydi)
}

/** Brauzerga: holat + namunani tinglash uchun qisqa muddatli havola (sirli yo'l va uzoq havola qaytarilmaydi). */
export async function getVoiceClone(): Promise<VoiceClone & { listenUrl: string | null } | null> {
  const s = await read();
  if (!s) return null;
  const { path, url: _url, ...pub } = s;
  void _url;
  let listenUrl: string | null = null;
  if (!isDemo()) { try { listenUrl = await listenLink(path); } catch { /* Storage vaqtincha ishlamasa ham holat ko'rinadi */ } }
  return { ...pub, listenUrl };
}

/**
 * Tinglash havolasi (1 soat) muddati tugashiga 10 daqiqa qolguncha qayta ishlatiladi: holat har 15 s so'ralganda Storage'ga
 * HTTP so'rovi ketmaydi va havola o'zgarmagani uchun pleer qayta boshlanib ketmaydi.
 */
const links = new Map<string, { url: string; until: number }>();
async function listenLink(path: string): Promise<string> {
  const hit = links.get(path);
  if (hit && hit.until > Date.now()) return hit.url;
  const url = await signDownload(path, 3600);
  if (links.size > 2000) links.clear();
  links.set(path, { url, until: Date.now() + 50 * 60_000 });
  return url;
}

const samplePathRe = () => new RegExp(`^acc/${currentAccountId()}/voice/[0-9a-f]{16}/sample\\.[a-z0-9]{2,5}$`);

export async function prepareSampleUpload(i: { filename: string; size: number }): Promise<{ path: string; uploadUrl: string }> {
  const ext = (i.filename.match(/\.[A-Za-z0-9]+$/)?.[0] ?? "").toLowerCase();
  if (!SAMPLE_EXT.includes(ext)) throw new Error(`Ovoz namunasi formati: ${SAMPLE_EXT.join(", ")}.`);
  if (!Number.isFinite(i.size) || i.size <= 0 || i.size > MAX_BYTES) throw new Error(`Namuna hajmi ${MAX_BYTES / 1048576} MB dan oshmasin (10–30 soniya yetadi).`);
  const id = crypto.randomBytes(8).toString("hex");
  if (isDemo()) return { path: `acc/demo/voice/${id}/sample${ext}`, uploadUrl: "" };
  const path = `acc/${currentAccountId()}/voice/${id}/sample${ext}`;
  return { path, uploadUrl: await signUpload(path) };
}

/**
 * Yuklangan namunani saqlaydi: rozilik majburiy, davomiylik 10–45 s, o'qilgan matn botdagi ro'yxatdan. Eski namuna Storage'dan o'chiriladi.
 * «Mening ovozim» profili bot ro'yxatiga qo'shiladi (xohlasa standart ovoz bo'ladi).
 */
export async function saveSample(i: { path: string; durationSec: number; passage: number; consent: boolean; makeDefault: boolean; by: string }): Promise<VoiceClone> {
  if (!i.consent) throw new Error("Rozilikni belgilang: namuna sizning ovozingiz yoki egasi ruxsat bergan.");
  // O'qilgan matn - botdagi ro'yxatdan (ixtiyoriy matn qabul qilinmaydi); VoiceStudio'ga ref_text bo'lib beriladi.
  const refText = VOICE_PASSAGES[Number(i.passage)];
  if (!refText) throw new Error("O'qilgan matn topilmadi — sahifani yangilab qayta yozing.");
  const dur = Number(i.durationSec);
  if (!Number.isFinite(dur) || dur < MIN_SEC || dur > MAX_SEC) throw new Error(`Namuna ${MIN_SEC}–${MAX_SEC} soniya bo'lsin (eng yaxshisi 10–30 s toza nutq).`);
  const accountId = isDemo() ? "00000000-0000-0000-0000-000000000000" : currentAccountId();
  if (!isDemo()) {
    if (!samplePathRe().test(i.path)) throw new Error("Namuna yo'li noto'g'ri.");
    if (!(await objectExists(i.path))) throw new Error("Namuna Storage'da topilmadi — yuklash tugamagan bo'lishi mumkin.");
  }
  const prev = await read();
  const now = new Date().toISOString();
  const s: Stored = {
    path: i.path, url: isDemo() ? `https://demo.supabase.co/${i.path}` : await signDownload(i.path, DOWNLOAD_TTL_SEC),
    version: crypto.randomBytes(6).toString("hex"), profileName: profileNameFor(accountId), model: MODEL,
    durationSec: Math.round(dur * 10) / 10, refText, uploadedAt: now, consentAt: now, consentBy: i.by.slice(0, 80),
    status: "pending", message: "Kompyuter namunani VoiceStudio'ga darhol qo'shadi (ilova yopiq bo'lsa ham).", machine: null, readyAt: null,
  };
  await write(s);
  await setCloneProfile({ name: PROFILE_LABEL, model: MODEL, voice: s.profileName }, i.makeDefault);
  if (prev?.path && prev.path !== i.path && !isDemo()) await removeObjects([prev.path]).catch(() => false);
  const { path: _p, url: _u, ...pub } = s;
  void _p; void _u;
  return pub;
}

/**
 * Namunani o'chiradi: Storage'dan darhol, bot profili olib tashlanadi. Kompyuterdagi nusxalar (VoiceStudio profili va kesh)
 * uchun belgi qoldiriladi: worker keyingi tekshiruvda ularni o'chirib, kalitni olib tashlaydi.
 */
export async function deleteSample(): Promise<void> {
  const s = await read();
  if (s?.path && !isDemo()) await removeObjects([s.path]).catch(() => false);
  const accountId = isDemo() ? "00000000-0000-0000-0000-000000000000" : currentAccountId();
  await write({ deleted: true, profileName: s?.profileName ?? profileNameFor(accountId), deletedAt: new Date().toISOString() });
  await setCloneProfile(null, false);
}

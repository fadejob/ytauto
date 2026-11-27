import "server-only";
import crypto from "node:crypto";
import { setCloneProfile } from "./config";
import { currentAccountId, sql, wakeAgents } from "./db";
import { isDemo } from "./demo";
import { objectExists, removeObjects, signDownload, signUpload } from "./storage";
import type { VoiceClone, VoiceCloneOverview } from "./types";
import { VOICE_PASSAGES } from "./voice-passages";
import { activeChannelId, channelList } from "./youtube";

/**
 * «Mening ovozim»: foydalanuvchi botdagi oson inglizcha matnni (15–20 s) o'qiydi yoki tayyor audio faylni yuklaydi.
 * Model (MOSS-TTS-Nano) kompyuterda bitta; namuna va o'qilgan matn (ref_text) zero-shot klonlash uchun beriladi (o'qitish yo'q).
 *
 * Har YouTube kanalining o'z namunasi bo'lishi mumkin:
 *  - kanal namunasi: shared_settings['voice.clone:<kanal id>'], VoiceStudio profili "YtAuto u-<hisob8>-<kanal8>",
 *    bot profili «Mening ovozim · <kanal nomi>» - faqat shu kanalga biriktiriladi (channel-assets), mavzularda faqat shu kanalda tanlanadi;
 *  - umumiy namuna (kanal ulanmaganda yoki eski): shared_settings['voice.clone'], profil "YtAuto u-<hisob8>", «Mening ovozim» -
 *    hamma kanalda (o'z namunasi bo'lmagan kanallar uchun).
 * Saqlash: Storage `acc/<hisob>/voice/<id>/sample.<ext>` (hisobning o'z papkasi), holat kalitda (RLS).
 * Worker har namunani `url` dan yuklab VoiceStudio'da `profileName` nomli klon profilini yaratadi/yangilaydi (`version` o'zgarganda)
 * va natijani o'sha kalitga yozadi (status/message). Vazifa ovoz profilining `voice` maydoni bo'yicha mos namunani tanlaydi.
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
/** O'chirilgandan keyin: kompyuter VoiceStudio'dagi profil va keshdagi namunani o'chiradi. */
type Tombstone = { deleted: true; profileName: string; deletedAt: string };
const isTomb = (v: unknown): v is Tombstone => !!v && (v as Tombstone).deleted === true;

const keyFor = (channel: string) => (channel ? `${KEY}:${channel}` : KEY);
const accountId = () => (isDemo() ? "00000000-0000-0000-0000-000000000000" : currentAccountId());

/** VoiceStudio'dagi profil nomi: hisobga (va kanalga) xos, boshqa hisob yoki kanal bilan to'qnashmaydi. */
export const profileNameFor = (accountId: string, channel = "") =>
  `YtAuto u-${accountId.replace(/-/g, "").slice(0, 8)}${channel ? "-" + crypto.createHash("sha1").update(channel).digest("hex").slice(0, 8) : ""}`;

/** Bot ro'yxatidagi profil nomi (≤60 belgi, kanal nomi bilan - ro'yxatda va mavzu oynasida qaysi kanalniki ekani ko'rinadi). */
const labelFor = (title: string) => (title ? `${PROFILE_LABEL} · ${title}`.slice(0, 60).trim() : PROFILE_LABEL);

const demo = new Map<string, Stored>();

async function read(channel: string): Promise<Stored | null> {
  if (isDemo()) return demo.get(channel) ?? null;
  const [r] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${keyFor(channel)}`;
  try { const v = r ? JSON.parse(r.value as string) : null; return v && !isTomb(v) ? (v as Stored) : null; } catch { return null; }
}

async function write(channel: string, s: Stored | Tombstone) {
  if (isDemo()) { if (isTomb(s)) demo.delete(channel); else demo.set(channel, s); return; }
  const json = JSON.stringify(s);
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${keyFor(channel)}, ${json}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
  wakeAgents("voice"); // kompyuter yangi namunani darhol oladi (yoki o'chirilganini tozalaydi)
}

/**
 * So'ralgan kanal: "" - umumiy namuna. Berilmasa - faol kanal (kanal ulanmagan bo'lsa umumiy).
 * Berilgan kanal shu hisobga ulangan bo'lishi kerak.
 */
async function resolveChannel(requested?: string | null): Promise<{ channel: string; title: string; channels: { id: string; title: string }[] }> {
  const l = await channelList();
  const channels = l.channels.map((c) => ({ id: c.id, title: c.title }));
  const id = requested === undefined || requested === null ? await activeChannelId() : requested;
  if (!id) return { channel: "", title: "", channels };
  const ch = channels.find((c) => c.id === id);
  if (!ch) {
    if (requested) throw new Error("Kanal topilmadi — ro'yxatni yangilang.");
    return { channel: "", title: "", channels }; // faol kanal uzilgan - umumiy
  }
  return { channel: ch.id, title: ch.title, channels };
}

async function publicView(s: Stored | null): Promise<(VoiceClone & { listenUrl: string | null }) | null> {
  if (!s) return null;
  const { path, url: _url, ...pub } = s;
  void _url;
  let listenUrl: string | null = null;
  if (!isDemo()) { try { listenUrl = await listenLink(path); } catch { /* Storage vaqtincha ishlamasa ham holat ko'rinadi */ } }
  return { ...pub, listenUrl };
}

/**
 * Brauzerga: tanlangan kanal namunasi (+ tinglash havolasi), umumiy namuna va har kanal namunasining qisqa holati
 * (sirli yo'l va uzoq havola qaytarilmaydi).
 */
export async function getVoiceClone(requested?: string | null): Promise<VoiceCloneOverview> {
  const { channel, channels } = await resolveChannel(requested);
  const [own, shared, perChannel] = await Promise.all([
    read(channel),
    channel ? read("") : Promise.resolve(null),
    Promise.all(channels.map(async (c) => ({ c, s: await read(c.id) }))),
  ]);
  return {
    channel,
    clone: await publicView(own),
    shared: channel ? await publicView(shared) : null,
    channels: perChannel.map(({ c, s }) => ({ id: c.id, title: c.title, status: s?.status ?? null, durationSec: s?.durationSec ?? null, profileName: s?.profileName ?? null })),
    sharedProfileName: (channel ? shared : own)?.profileName ?? null,
  };
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
 * Yuklangan namunani kanalga saqlaydi: rozilik majburiy, davomiylik 10–45 s, o'qilgan matn botdagi ro'yxatdan (tayyor audio faylda - bo'sh).
 * Shu kanalning eski namunasi Storage'dan o'chiriladi. Kanal profili bot ro'yxatiga qo'shiladi (xohlasa shu kanal standarti bo'ladi).
 */
export async function saveSample(i: { channel?: string | null; path: string; durationSec: number; passage: number; consent: boolean; makeDefault: boolean; by: string }): Promise<VoiceCloneOverview> {
  if (!i.consent) throw new Error("Rozilikni belgilang: namuna sizning ovozingiz yoki egasi ruxsat bergan.");
  // O'qilgan matn - botdagi ro'yxatdan (ixtiyoriy matn qabul qilinmaydi); VoiceStudio'ga ref_text bo'lib beriladi.
  // Tayyor audio fayl yuklansa (passage < 0 yoki yo'q) matn noma'lum: ref_text bo'sh, model ovozni faqat audiodan ko'chiradi.
  const p = Number(i.passage);
  const uploaded = !Number.isInteger(p) || p < 0;
  const refText = uploaded ? "" : VOICE_PASSAGES[p];
  if (refText === undefined) throw new Error("O'qilgan matn topilmadi — sahifani yangilab qayta yozing.");
  const dur = Number(i.durationSec);
  if (!Number.isFinite(dur) || dur < MIN_SEC || dur > MAX_SEC) throw new Error(`Namuna ${MIN_SEC}–${MAX_SEC} soniya bo'lsin (eng yaxshisi 10–30 s toza nutq).`);
  const { channel, title } = await resolveChannel(i.channel);
  if (!isDemo()) {
    if (!samplePathRe().test(i.path)) throw new Error("Namuna yo'li noto'g'ri.");
    if (!(await objectExists(i.path))) throw new Error("Namuna Storage'da topilmadi — yuklash tugamagan bo'lishi mumkin.");
  }
  const prev = await read(channel);
  const now = new Date().toISOString();
  const s: Stored = {
    path: i.path, url: isDemo() ? `https://demo.supabase.co/${i.path}` : await signDownload(i.path, DOWNLOAD_TTL_SEC),
    version: crypto.randomBytes(6).toString("hex"), profileName: profileNameFor(accountId(), channel), model: MODEL,
    durationSec: Math.round(dur * 10) / 10, refText, uploadedAt: now, consentAt: now, consentBy: i.by.slice(0, 80),
    status: "pending", message: "Kompyuter namunani VoiceStudio'ga darhol qo'shadi (ilova yopiq bo'lsa ham).", machine: null, readyAt: null,
  };
  await write(channel, s);
  await setCloneProfile({ name: labelFor(title), model: MODEL, voice: s.profileName }, i.makeDefault, channel);
  if (prev?.path && prev.path !== i.path && !isDemo()) await removeObjects([prev.path]).catch(() => false);
  return getVoiceClone(channel);
}

/**
 * Kanal (yoki umumiy) namunasini o'chiradi: Storage'dan darhol, bot profili olib tashlanadi. Kompyuterdagi nusxalar (VoiceStudio
 * profili va kesh) uchun belgi qoldiriladi: worker keyingi tekshiruvda ularni o'chiradi.
 */
export async function deleteSample(requested?: string | null): Promise<VoiceCloneOverview> {
  const { channel } = await resolveChannel(requested);
  const s = await read(channel);
  if (s?.path && !isDemo()) await removeObjects([s.path]).catch(() => false);
  const profileName = s?.profileName ?? profileNameFor(accountId(), channel);
  await write(channel, { deleted: true, profileName, deletedAt: new Date().toISOString() });
  await setCloneProfile({ name: "", model: MODEL, voice: profileName }, false, channel, true);
  return getVoiceClone(channel);
}

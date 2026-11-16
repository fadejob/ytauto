import "server-only";
import crypto from "node:crypto";
import { currentAccountId } from "./db";
import { isDemo } from "./demo";
import { objectExists, signDownload, signUpload } from "./storage";
import type { PreparedAudio } from "./types";

/**
 * Tayyor ovoz: brauzer audio faylni Storage'ga ("audio/<id>/voice.<ext>") yuboradi, mavzu yaratilganda yo'l tekshirilib,
 * kompyuter uchun uzoq muddatli imzolangan havola bazaga yoziladi. Video YouTube'ga yuklangach yoki mavzu o'chirilganda fayl o'chiriladi.
 */
export const AUDIO_EXT = [".mp3", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".webm"];
export const AUDIO_MAX_BYTES = 200 * 1024 * 1024; // kompyuterdagi RemoteAudioImporter.MaxBytes bilan bir xil
/** Mavzu bir necha hafta navbatda turishi mumkin: havola shuncha amal qiladi. */
const DOWNLOAD_TTL_SEC = 120 * 24 * 3600;

export async function prepareAudioUpload(i: { filename: string; size: number }): Promise<PreparedAudio> {
  const ext = (i.filename.match(/\.[A-Za-z0-9]+$/)?.[0] ?? "").toLowerCase();
  if (!AUDIO_EXT.includes(ext)) throw new Error(`Audio formati qo'llab-quvvatlanmaydi (${AUDIO_EXT.join(", ")}).`);
  if (!Number.isFinite(i.size) || i.size <= 0 || i.size > AUDIO_MAX_BYTES) throw new Error(`Audio hajmi 1 baytdan ${AUDIO_MAX_BYTES / 1048576} MB gacha bo'lsin.`);
  if (isDemo()) return { path: `audio/${crypto.randomBytes(8).toString("hex")}/voice${ext}`, uploadUrl: "" };
  const path = `acc/${currentAccountId()}/audio/${crypto.randomBytes(8).toString("hex")}/voice${ext}`; // har hisob o'z papkasida
  return { path, uploadUrl: await signUpload(path) };
}

/** Faqat shu hisob papkasidagi audio (boshqa foydalanuvchi faylini biriktirib bo'lmaydi). */
export const isAudioPath = (p: string) =>
  isDemo() ? /^audio\/[0-9a-f]{16}\/voice\.[a-z0-9]{2,5}$/.test(p)
    : p.startsWith(`acc/${currentAccountId()}/`) && /^acc\/[0-9a-f-]{36}\/audio\/[0-9a-f]{16}\/voice\.[a-z0-9]{2,5}$/.test(p);

/** Yuklangan fayl joyida ekanini tekshiradi va kompyuter yuklab oladigan havolani qaytaradi. */
export async function attachAudio(path: string): Promise<string> {
  if (!isAudioPath(path)) throw new Error("Audio fayl yo'li noto'g'ri.");
  if (isDemo()) return `https://demo.supabase.co/${path}`;
  if (!(await objectExists(path))) throw new Error("Audio Storage'da topilmadi — yuklash tugamagan bo'lishi mumkin.");
  return signDownload(path, DOWNLOAD_TTL_SEC);
}

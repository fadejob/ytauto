import "server-only";
import crypto from "node:crypto";
import { currentAccountId } from "./db";
import { isDemo } from "./demo";
import { objectExists, signDownload, signUpload } from "./storage";
import type { PreparedAudio } from "./types";

/**
 * Mavzu muqovasi (YouTube thumbnail): brauzer rasmni Storage'ga ("acc/<hisob>/cover/<id>/cover.<ext>") yuboradi, mavzu saqlanganda
 * yo'l tekshirilib, kompyuter uchun uzoq muddatli havola bazaga yoziladi. Kompyuter rasmni 1280 px JPEG'ga aylantirib YouTube'ga qo'yadi.
 * YouTube maxsus thumbnail'ni faqat telefon bilan tasdiqlangan kanalda qabul qiladi - bot shuni tekshiradi (youtube.ts customThumbnails).
 */
export const COVER_EXT = [".jpg", ".jpeg", ".png", ".webp"];
export const COVER_MAX_BYTES = 10 * 1024 * 1024; // kompyuterdagi RemoteCoverImporter.MaxBytes (20 MB) dan kichik
const DOWNLOAD_TTL_SEC = 120 * 24 * 3600;

export async function prepareCoverUpload(i: { filename: string; size: number }): Promise<PreparedAudio> {
  const ext = (i.filename.match(/\.[A-Za-z0-9]+$/)?.[0] ?? "").toLowerCase();
  if (!COVER_EXT.includes(ext)) throw new Error(`Muqova formati: ${COVER_EXT.join(", ")}.`);
  if (!Number.isFinite(i.size) || i.size <= 0 || i.size > COVER_MAX_BYTES) throw new Error(`Muqova rasmi 1 baytdan ${COVER_MAX_BYTES / 1048576} MB gacha bo'lsin.`);
  const id = crypto.randomBytes(8).toString("hex");
  if (isDemo()) return { path: `cover/${id}/cover${ext}`, uploadUrl: "" };
  const path = `acc/${currentAccountId()}/cover/${id}/cover${ext}`;
  return { path, uploadUrl: await signUpload(path) };
}

/** Faqat shu hisob papkasidagi muqova (boshqa foydalanuvchi faylini biriktirib bo'lmaydi). */
export const isCoverPath = (p: string) =>
  isDemo() ? /^cover\/[0-9a-f]{16}\/cover\.(jpe?g|png|webp)$/.test(p)
    : p.startsWith(`acc/${currentAccountId()}/`) && /^acc\/[0-9a-f-]{36}\/cover\/[0-9a-f]{16}\/cover\.(jpe?g|png|webp)$/.test(p);

/** Yuklangan rasm joyida ekanini tekshiradi va kompyuter (va botdagi ko'rinish) uchun havolani qaytaradi. */
export async function attachCover(path: string): Promise<string> {
  if (!isCoverPath(path)) throw new Error("Muqova fayl yo'li noto'g'ri.");
  if (isDemo()) return `https://picsum.photos/seed/${path.split("/")[1]}/1280/720`;
  if (!(await objectExists(path))) throw new Error("Muqova Storage'da topilmadi — yuklash tugamagan bo'lishi mumkin.");
  return signDownload(path, DOWNLOAD_TTL_SEC);
}

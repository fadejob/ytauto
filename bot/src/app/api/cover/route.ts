import { handler } from "@/lib/api";
import { prepareCoverUpload } from "@/lib/cover";
import { customThumbnails } from "@/lib/youtube";

/** Muqova rasmi uchun bir martalik yuklash havolasi (faqat telefon bilan tasdiqlangan kanalda). Fayl brauzerdan to'g'ridan-to'g'ri Storage'ga. */
export const POST = handler(async (req) => {
  if (!(await customThumbnails())) throw new Error("Kanal telefon raqami bilan tasdiqlanmagan: YouTube maxsus muqovani qabul qilmaydi (youtube.com/verify).");
  const b = (await req.json()) as Record<string, unknown>;
  return prepareCoverUpload({ filename: String(b.filename ?? ""), size: Number(b.size) });
});

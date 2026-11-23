/**
 * Video boshlanishi (hook) va uzunligi qoidalari - server va mijoz uchun umumiy. Desktop worker ham xuddi shu qoidani bajaradi
 * (YtAuto.Core VideoLength / HookRule): bu yerdagi raqamlar o'sha bilan bir xil bo'lishi kerak.
 *
 *  - 16:9 (keng ekran) - 2-3 daqiqa; Shorts 9:16 va kvadrat 1:1 - 55 soniyadan oshmaydi.
 *  - Ssenariy foydalanuvchi kiritgan boshlang'ich savol (hook) bilan, aynan shu so'zlar bilan boshlanadi; montaj ham shu savoldan
 *    boshlanadi.
 */
export const FormatLength: Record<number, { minSec: number; maxSec: number; label: string }> = {
  0: { minSec: 120, maxSec: 180, label: "2–3 daqiqa" },
  1: { minSec: 30, maxSec: 55, label: "55 soniyagacha" },
  2: { minSec: 30, maxSec: 55, label: "55 soniyagacha" },
};

/** TTS ovozining taxminiy tezligi (so'z/soniya, ingliz tili, bo'laklar orasidagi pauzalar bilan) - matn uzunligini baholash uchun. */
export const TTS_WORDS_PER_SEC = 2.0;

export const HOOK_MAX_CHARS = 200;
export const HOOK_MAX_WORDS = 30;

export const countWords = (s: string) => (s.match(/[\p{L}\p{N}][\p{L}\p{N}'’-]*/gu) ?? []).length;

/**
 * Hook (boshlang'ich savol) tekshiruvi: bo'sh joylar yig'iladi; savol bo'lishi (? belgisi), qisqa bo'lishi va ovozga o'qiladigan
 * oddiy matn bo'lishi kerak (havola, belgi, qavs ichidagi ko'rsatma yo'q).
 */
export function checkHook(raw: string | null | undefined): { hook: string } | { error: string } {
  const hook = (raw ?? "").replace(/\s+/g, " ").trim();
  if (!hook) return { error: "Boshlang'ich savol kiritilmagan: video shu savol bilan boshlanadi." };
  if (hook.length > HOOK_MAX_CHARS) return { error: `Boshlang'ich savol juda uzun (${hook.length} > ${HOOK_MAX_CHARS} belgi): bir-ikki qisqa gap bo'lsin.` };
  if (countWords(hook) > HOOK_MAX_WORDS) return { error: `Boshlang'ich savol juda uzun (${countWords(hook)} so'z, eng ko'pi ${HOOK_MAX_WORDS}): videoning birinchi soniyalari uchun qisqa savol yozing.` };
  if (countWords(hook) < 3) return { error: "Boshlang'ich savol juda qisqa: to'liq savol yozing." };
  if (!hook.includes("?")) return { error: "Boshlang'ich savol «?» bilan tugashi kerak: video shu savol bilan boshlanadi." };
  if (/https?:\/\/|www\.|[<>[\]{}#*]/i.test(hook)) return { error: "Boshlang'ich savol ovozga o'qiladigan oddiy matn bo'lsin: havola, [ ], < >, # va * belgilarisiz." };
  return { hook };
}

/** Qo'lda kiritilgan matn shu formatga sig'adimi (ovozda taxminan necha soniya). */
export function manualLengthProblem(script: string, format: number): string | null {
  const rule = FormatLength[format] ?? FormatLength[0];
  const sec = Math.round(countWords(script) / TTS_WORDS_PER_SEC);
  // Ovoz tezligi taxminiy: chegaradan 10% gacha oshsa worker ovozni biroz tezlashtirib sig'diradi.
  if (sec > rule.maxSec * 1.1) return `Matn ${FormatTextShort(format)} uchun juda uzun: ovozda ~${sec} soniya, eng ko'pi ${rule.maxSec} soniya (${rule.label}). Qisqartiring.`;
  return null;
}

const FormatTextShort = (f: number) => (f === 1 ? "Shorts" : f === 2 ? "kvadrat video" : "16:9 video");

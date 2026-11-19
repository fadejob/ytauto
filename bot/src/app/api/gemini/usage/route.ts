import { handler } from "@/lib/api";
import { getConfig } from "@/lib/config";
import { isDemo } from "@/lib/demo";
import { buildUsageView, readUsageDocs, type GeminiUsageView } from "@/lib/gemini-usage";

/**
 * Har Gemini modeli bo'yicha bugungi sarf va limit holati (worker + bot). Faqat asosiy hisob egasi ko'radi: qo'shimcha foydalanuvchilar
 * o'z kalitlari bilan ishlaydi, ularning sarfi bu yerga yozilmaydi va ularga bu jadval ko'rsatilmaydi.
 */
export const GET = handler(async (_req, viewer): Promise<GeminiUsageView> => {
  if (viewer.guest) throw new Error("Model limitlarini faqat asosiy hisob egasi ko'radi.");
  const cfg = await getConfig();
  if (isDemo()) return buildUsageView([], cfg.gemini, cfg.gemini.apiKeys.length);
  return buildUsageView(await readUsageDocs(), cfg.gemini, cfg.gemini.apiKeys.length);
});

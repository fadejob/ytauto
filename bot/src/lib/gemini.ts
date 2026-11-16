import "server-only";
import { getGeminiKeys } from "./config";

const BASE = "https://generativelanguage.googleapis.com/v1beta/";

/**
 * Oxirgi ishlagan kalit tartib raqami: keyingi so'rovlar limiti tugagan kalitni qayta-qayta urmasdan shundan boshlaydi.
 * (Server jarayoni yashagan davomida saqlanadi; yangi jarayon 1-kalitdan boshlaydi.)
 */
let active = 0;

const clip = (s: string, n: number) => (s.length > n ? s.slice(0, n) + "…" : s);

function errorMessage(raw: string): string {
  try { return (JSON.parse(raw) as { error?: { message?: string } }).error?.message || clip(raw, 300); } catch { return clip(raw, 300); }
}

/**
 * Gemini generateContent: botda saqlangan kalitlarni navbat bilan sinaydi. 429 (limit tugadi) yoki kalit yaroqsiz bo'lsa
 * keyingi kalitga o'tadi; hammasi limitda bo'lsa xato bilan to'xtaydi.
 */
export async function geminiGenerate(body: Record<string, unknown>, opts: { model?: string; timeoutMs?: number } = {}): Promise<{ data: GeminiResponse; model: string }> {
  const { keys, model: cfgModel } = await getGeminiKeys();
  if (!keys.length) throw new Error("Gemini API kaliti kiritilmagan (Sozlamalar → Gemini).");
  const model = opts.model || process.env.GEMINI_CHAT_MODEL?.trim() || cfgModel;
  const start = Math.min(active, keys.length - 1);
  let limited = 0;
  let lastError = "";
  for (let i = 0; i < keys.length; i++) {
    const idx = (start + i) % keys.length;
    let res: Response;
    try {
      res = await fetch(`${BASE}models/${encodeURIComponent(model)}:generateContent`, {
        method: "POST",
        headers: { "x-goog-api-key": keys[idx], "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(opts.timeoutMs ?? 60_000),
      });
    } catch (e) {
      throw new Error(`Gemini bilan aloqa yo'q: ${e instanceof Error ? e.message : String(e)}`);
    }
    if (res.ok) {
      active = idx;
      return { data: (await res.json()) as GeminiResponse, model };
    }
    const msg = errorMessage(await res.text());
    const badKey = res.status === 401 || res.status === 403 || (res.status === 400 && /api key/i.test(msg));
    if (res.status === 429) { limited++; lastError = msg; continue; }
    if (badKey) { lastError = msg; continue; }
    if (res.status === 404) throw new Error(`Gemini modeli topilmadi (${model}): ${msg}`);
    throw new Error(`Gemini xatosi (HTTP ${res.status}): ${msg}`);
  }
  if (limited === 0) throw new Error(`Gemini kaliti yaroqsiz: ${lastError}`);
  throw new Error(keys.length === 1
    ? `Gemini kalitining limiti tugadi (429). Sozlamalarda yana kalit qo'shing yoki limit tiklanishini kuting.`
    : `Barcha ${keys.length} ta Gemini kalitining limiti tugadi (429). Sozlamalarda yangi kalit qo'shing yoki limit tiklanishini kuting.`);
}

export type GeminiPart = {
  text?: string;
  thought?: boolean;
  functionCall?: { id?: string; name?: string; args?: Record<string, unknown> };
  [k: string]: unknown;
};
export type GeminiContent = { role: "user" | "model"; parts: GeminiPart[] };
export type GeminiResponse = {
  candidates?: { content?: GeminiContent; finishReason?: string }[];
  promptFeedback?: { blockReason?: string };
};

import "server-only";
import { DEFAULT_FALLBACK_MODELS, getGeminiKeys } from "./config";

const BASE = "https://generativelanguage.googleapis.com/v1beta/";

/**
 * Model router: asosiy model limitga tushsa (barcha kalitlarda 429), band bo'lsa (5xx), topilmasa (404) yoki so'rovni
 * qabul qilmasa - zaxira modellar tartib bilan sinaladi va ish oxirigacha yetkaziladi. Bepul limitlar har model uchun alohida,
 * shuning uchun o'sha kalit boshqa modelda odatda ishlaydi. Sozlamalar → Gemini → «Zaxira modellar» (desktop bilan umumiy ro'yxat).
 */
export { DEFAULT_FALLBACK_MODELS };

/**
 * Jarayon xotirasi (server yashagan davomida):
 *  - activeKey: oxirgi ishlagan kalit - limiti tugagan kalitni qayta-qayta urmaslik uchun;
 *  - cooldown: limitga tushgan model qachongacha chetlab o'tiladi (Gemini retryDelay yoki 60 s, kunlik limitda 1 soat);
 *  - missing: bu kalitlarda mavjud bo'lmagan model (404) - 6 soat so'ralmaydi.
 */
let activeKey = 0;
const cooldown = new Map<string, number>();
const missing = new Map<string, number>();

const clip = (s: string, n: number) => (s.length > n ? s.slice(0, n) + "…" : s);

type GeminiError = { error?: { message?: string; details?: { "@type"?: string; retryDelay?: string; violations?: { quotaId?: string }[] }[] } };

function parseError(raw: string): { message: string; retryMs: number | null; daily: boolean } {
  try {
    const e = (JSON.parse(raw) as GeminiError).error;
    const details = e?.details ?? [];
    const delay = details.find((d) => d.retryDelay)?.retryDelay ?? "";
    const sec = /^(\d+(?:\.\d+)?)s$/.exec(delay)?.[1];
    const daily = details.some((d) => d.violations?.some((v) => /PerDay/i.test(v.quotaId ?? "")));
    return { message: e?.message || clip(raw, 300), retryMs: sec ? Math.ceil(Number(sec) * 1000) : null, daily };
  } catch { return { message: clip(raw, 300), retryMs: null, daily: false }; }
}

/** Router tartibi: asosiy model, so'ng zaxiralar (takrorlarsiz). Hozir limitda turganlar oxiriga suriladi, mavjud emaslar tashlanadi. */
export function routeModels(primary: string, fallbacks: string[], now = Date.now()): string[] {
  const all = [...new Set([primary, ...fallbacks].map((m) => m.trim()).filter(Boolean))];
  const available = all.filter((m) => !((missing.get(m) ?? 0) > now));
  const list = available.length ? available : all;
  const ready = list.filter((m) => !((cooldown.get(m) ?? 0) > now));
  const cooling = list.filter((m) => (cooldown.get(m) ?? 0) > now).sort((a, b) => (cooldown.get(a) ?? 0) - (cooldown.get(b) ?? 0));
  return [...ready, ...cooling];
}

/** Zaxira modelda ishlamasligi mumkin bo'lgan sozlamalar (masalan thinking'siz modelda thinkingConfig) olib tashlangan nusxa. */
function relaxed(body: Record<string, unknown>): Record<string, unknown> | null {
  const gc = body.generationConfig as Record<string, unknown> | undefined;
  if (!gc || !("thinkingConfig" in gc)) return null;
  const { thinkingConfig: _drop, ...rest } = gc;
  void _drop;
  return { ...body, generationConfig: rest };
}

type Attempt =
  | { kind: "ok"; data: GeminiResponse }
  | { kind: "limited"; message: string; retryMs: number | null; daily: boolean }
  | { kind: "badkeys"; message: string }
  | { kind: "missing"; message: string }
  | { kind: "busy"; message: string }
  | { kind: "rejected"; status: number; message: string };

/** Bitta model: kalitlarni navbat bilan sinaydi (429 yoki yaroqsiz kalit - keyingisi). */
async function tryModel(model: string, keys: string[], body: Record<string, unknown>, timeoutMs: number): Promise<Attempt> {
  const start = Math.min(activeKey, keys.length - 1);
  let limited: Extract<Attempt, { kind: "limited" }> | null = null;
  let lastBad = "";
  for (let i = 0; i < keys.length; i++) {
    const idx = (start + i) % keys.length;
    let res: Response;
    try {
      res = await fetch(`${BASE}models/${encodeURIComponent(model)}:generateContent`, {
        method: "POST",
        headers: { "x-goog-api-key": keys[idx], "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch (e) {
      const timeout = e instanceof Error && (e.name === "TimeoutError" || e.name === "AbortError");
      // Vaqt tugashi - model band/sekin: keyingi modelga o'tiladi. Tarmoq yo'qligi - router yordam bermaydi.
      if (timeout) return { kind: "busy", message: `${Math.round(timeoutMs / 1000)} s ichida javob bermadi` };
      throw new Error(`Gemini bilan aloqa yo'q: ${e instanceof Error ? e.message : String(e)}`);
    }
    if (res.ok) { activeKey = idx; return { kind: "ok", data: (await res.json()) as GeminiResponse }; }
    const err = parseError(await res.text());
    if (res.status === 429) {
      if (!limited || (err.retryMs ?? 0) < (limited.retryMs ?? Infinity)) limited = { kind: "limited", ...err };
      continue;
    }
    if (res.status === 401 || res.status === 403 || (res.status === 400 && /api key/i.test(err.message))) { lastBad = err.message; continue; }
    if (res.status === 404) return { kind: "missing", message: err.message };
    if (res.status >= 500) return { kind: "busy", message: `HTTP ${res.status}: ${err.message}` };
    return { kind: "rejected", status: res.status, message: err.message };
  }
  return limited ?? { kind: "badkeys", message: lastBad };
}

/**
 * Gemini generateContent model router orqali. Botdagi barcha Gemini chaqiruvlari (AI yordamchi, kanal tahlili, mavzu tavsiyalari)
 * shu yerdan o'tadi. Natijada qaysi model javob bergani qaytadi.
 * onSwitch - model almashganda so'rovni moslash uchun (masalan, boshqa model yozgan thought signature'larni olib tashlash).
 */
export async function geminiGenerate(
  body: Record<string, unknown>,
  opts: {
    model?: string; timeoutMs?: number;
    /** Tranzaksiyadan tashqarida (fon ishi): kalitlar oldindan o'qilgan. */
    keys?: { keys: string[]; model: string; fallbackModels?: string[] };
    onSwitch?: (body: Record<string, unknown>, from: string, to: string) => Record<string, unknown>;
  } = {},
): Promise<{ data: GeminiResponse; model: string; switched: boolean }> {
  const cfg = opts.keys ?? (await getGeminiKeys());
  const keys = cfg.keys;
  if (!keys.length) throw new Error("Gemini API kaliti kiritilmagan (Sozlamalar → Gemini).");
  const primary = opts.model || (opts.keys ? cfg.model : process.env.GEMINI_CHAT_MODEL?.trim() || cfg.model);
  const chain = routeModels(primary, cfg.fallbackModels ?? DEFAULT_FALLBACK_MODELS);
  const timeoutMs = opts.timeoutMs ?? 60_000;

  const problems: string[] = [];
  let sawLimit = false, sawBadKeys = false;
  let prev = primary;
  for (const model of chain) {
    let req = model === prev || !opts.onSwitch ? body : opts.onSwitch(body, prev, model);
    let r = await tryModel(model, keys, req, timeoutMs);
    // Zaxira model ba'zi sozlamani qo'llamasa (400) - o'sha sozlamasiz bir marta.
    if (r.kind === "rejected" && model !== primary) {
      const lighter = relaxed(req);
      if (lighter) { req = lighter; r = await tryModel(model, keys, req, timeoutMs); }
    }
    if (r.kind === "ok") {
      cooldown.delete(model);
      if (model !== primary) console.warn(`[gemini] ${primary} ishlamadi (${problems.join("; ")}) — javob ${model} modelidan.`);
      return { data: r.data, model, switched: model !== primary };
    }
    if (r.kind === "limited") {
      sawLimit = true;
      cooldown.set(model, Date.now() + (r.daily ? 3_600_000 : Math.min(Math.max(r.retryMs ?? 60_000, 5_000), 600_000)));
      problems.push(`${model}: limit (429)`);
    } else if (r.kind === "missing") {
      missing.set(model, Date.now() + 6 * 3_600_000);
      problems.push(`${model}: mavjud emas`);
    } else if (r.kind === "busy") {
      problems.push(`${model}: ${r.message}`);
    } else if (r.kind === "badkeys") {
      sawBadKeys = true;
      break; // kalitlar yaroqsiz - boshqa model ham yordam bermaydi
    } else if (r.kind === "rejected") {
      // Asosiy model so'rovni rad etdi (400) - so'rovning o'zida xato, boshqa model ham rad etadi.
      if (model === primary) throw new Error(`Gemini xatosi (HTTP ${r.status}): ${r.message}`);
      problems.push(`${model}: rad etdi (${r.status})`);
    }
    prev = model;
  }
  if (sawBadKeys && !sawLimit) throw new Error("Gemini kaliti yaroqsiz yoki ruxsat yo'q. Sozlamalar → Gemini'da kalitni tekshiring.");
  if (sawLimit)
    throw new Error(`Barcha ${keys.length} ta Gemini kaliti va ${chain.length} ta model limitda (429): ${problems.join("; ")}. Yangi kalit qo'shing yoki limit tiklanishini kuting.`);
  throw new Error(`Gemini javob bermadi — hamma model ishlamadi: ${problems.join("; ")}.`);
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

/** Javobning matn qismlari (fikrlash qismlarisiz). */
export const responseText = (d: GeminiResponse) =>
  (d.candidates?.[0]?.content?.parts ?? []).filter((p) => !p.thought && typeof p.text === "string").map((p) => p.text).join("").trim();

/** responseMimeType=application/json javobini o'qiydi (ba'zan ```json bilan o'ralgan bo'ladi). */
export function responseJson<T>(d: GeminiResponse): T {
  if (d.promptFeedback?.blockReason) throw new Error(`Gemini so'rovni blokladi: ${d.promptFeedback.blockReason}`);
  const raw = responseText(d).replace(/^```(?:json)?\s*/i, "").replace(/```\s*$/, "");
  if (!raw) throw new Error(`Gemini bo'sh javob qaytardi (${d.candidates?.[0]?.finishReason ?? "sabab noma'lum"}).`);
  try { return JSON.parse(raw) as T; } catch { throw new Error("Gemini JSON o'rniga boshqa matn qaytardi."); }
}

import "server-only";
import { createHash } from "node:crypto";
import { getGeminiKeys, type GeminiKeys } from "./config";
import { matchModel, nextPacificMidnight, type CatalogEntry } from "./gemini-models";
import { recordBotUsage } from "./gemini-usage";

const BASE = "https://generativelanguage.googleapis.com/v1beta/";

/**
 * Oxirgi ishlagan kalit tartib raqami: keyingi so'rovlar limiti tugagan kalitni qayta-qayta urmasdan shundan boshlaydi.
 * (Server jarayoni yashagan davomida saqlanadi; yangi jarayon 1-kalitdan boshlaydi.)
 */
let active = 0;
/** kalit+model → shu vaqtgacha limitda (ms). Jarayon ichida; kunlik limitlar sarf yozuvidan ham o'qiladi (limited). */
const cooldown = new Map<string, number>();
/** Google models ro'yxati (nom → API id), 6 soat keshlanadi. */
let catalog: { at: number; list: CatalogEntry[] } | null = null;

const clip = (s: string, n: number) => (s.length > n ? s.slice(0, n) + "…" : s);
const keyId = (k: string) => createHash("sha256").update(k).digest("hex").slice(0, 12);

type ApiError = { error?: { message?: string; details?: { violations?: { quotaId?: string; quotaMetric?: string }[]; retryDelay?: string }[] } };

function errorMessage(raw: string): string {
  try { return (JSON.parse(raw) as ApiError).error?.message || clip(raw, 300); } catch { return clip(raw, 300); }
}

/** 429 tanasidan: kunlik limitmi (quotaId ...PerDay...) va qancha kutish kerak (retryDelay). */
export function parseRateLimit(raw: string, now = Date.now()): { perDay: boolean; until: number } {
  let perDay = false, retry = 0;
  try {
    for (const d of (JSON.parse(raw) as ApiError).error?.details ?? []) {
      for (const v of d.violations ?? []) if (/per_?day/i.test(`${v.quotaId ?? ""} ${v.quotaMetric ?? ""}`)) perDay = true;
      if (d.retryDelay) retry = parseFloat(d.retryDelay) || retry;
    }
  } catch { /* tana JSON emas */ }
  if (!perDay && /per[ _-]?day|daily|RPD/i.test(raw)) perDay = true;
  return { perDay, until: perDay ? nextPacificMidnight(now) : now + (retry > 0 ? retry * 1000 : 60_000) };
}

async function loadCatalog(keys: string[]): Promise<void> {
  for (const key of keys) {
    try {
      const res = await fetch(`${BASE}models?pageSize=1000`, { headers: { "x-goog-api-key": key }, signal: AbortSignal.timeout(20_000) });
      if (!res.ok) continue;
      const j = (await res.json()) as { models?: { name?: string; displayName?: string; supportedGenerationMethods?: string[] }[] };
      const list = (j.models ?? []).filter((m) => m.supportedGenerationMethods?.includes("generateContent"))
        .map((m) => ({ id: (m.name ?? "").replace(/^models\//, ""), display: m.displayName ?? "" })).filter((m) => m.id);
      if (list.length) { catalog = { at: Date.now(), list }; return; }
    } catch { /* keyingi kalit */ }
  }
}

/**
 * Gemini generateContent, model router bilan: zanjirdagi modellarni (asosiy, keyin zaxiralar) navbat bilan, har birini barcha kalitlar
 * bilan sinaydi. 429 - kalit+model juftligi sovutiladi (kunlik limit - Tinch okeani yarim tunigacha) va keyingisiga o'tiladi;
 * 404 (model yo'q), 400 (bu so'rovni qila olmaydi), 5xx va tarmoq xatosi - keyingi model. Hammasi limitda bo'lsa xato bilan to'xtaydi.
 */
export async function geminiGenerate(
  body: Record<string, unknown>,
  opts: { model?: string; timeoutMs?: number; /** Tranzaksiyadan tashqarida (fon ishi): kalitlar oldindan o'qilgan. */ keys?: GeminiKeys } = {},
): Promise<{ data: GeminiResponse; model: string }> {
  const cfg = opts.keys ?? (await getGeminiKeys());
  const { keys } = cfg;
  if (!keys.length) throw new Error("Gemini API kaliti kiritilmagan (Sozlamalar → Gemini).");
  const envModel = opts.keys ? "" : process.env.GEMINI_CHAT_MODEL?.trim() ?? "";
  const names = opts.model ? [opts.model] : envModel ? [envModel, ...cfg.models] : cfg.models;
  if (names.some((n) => /\s/.test(n)) && (!catalog || Date.now() - catalog.at > 6 * 3600_000)) await loadCatalog(keys);

  const models: { id: string; display: string }[] = [];
  for (const n of names) {
    const id = matchModel(n, catalog?.list ?? null);
    if (!id || models.some((m) => m.id.toLowerCase() === id.toLowerCase())) continue;
    models.push({ id, display: catalog?.list.find((c) => c.id === id)?.display || n });
  }
  if (!models.length) throw new Error(`Sozlangan Gemini modellaridan birortasi bu kalitda mavjud emas: ${names.join(", ")}.`);

  const ids = keys.map(keyId);
  const chain = models.map((m) => m.id);
  const record = (ev: Parameters<typeof recordBotUsage>[3]) => recordBotUsage(cfg.account, keys.length, chain, ev);
  const start = Math.min(active, keys.length - 1);
  let limited = 0, lastError = "", authFailed = 0;
  let first: string | null = null;
  for (const m of models) {
    const sharedUntil = cfg.limited.get(m.id.toLowerCase()) ?? 0; // worker yoki bot bugun shu modelni limitga urgan
    if (sharedUntil > Date.now()) { limited++; continue; }
    first ??= m.display;
    let nextModel = false;
    for (let i = 0; i < keys.length && !nextModel; i++) {
      const idx = (start + i) % keys.length;
      const pair = `${ids[idx]}|${m.id}`;
      if ((cooldown.get(pair) ?? 0) > Date.now()) { limited++; continue; }
      let res: Response;
      try {
        res = await fetch(`${BASE}models/${encodeURIComponent(m.id)}:generateContent`, {
          method: "POST",
          headers: { "x-goog-api-key": keys[idx], "Content-Type": "application/json" },
          body: JSON.stringify(body),
          signal: AbortSignal.timeout(opts.timeoutMs ?? 60_000),
        });
      } catch (e) {
        lastError = `Gemini bilan aloqa yo'q (${m.display}): ${e instanceof Error ? e.message : String(e)}`;
        nextModel = true; continue;
      }
      if (res.ok) {
        active = idx;
        const data = (await res.json()) as GeminiResponse;
        if (first !== m.display) console.info(`[gemini] ${first} ishlatib bo'lmadi, so'rov ${m.display} bilan bajarildi.`);
        await record({ model: m.id, display: m.display, ok: true, tokens: data.usageMetadata?.totalTokenCount ?? 0 });
        return { data, model: m.id };
      }
      const raw = await res.text();
      const msg = errorMessage(raw);
      const badKey = res.status === 401 || res.status === 403 || (res.status === 400 && /api key/i.test(msg));
      if (res.status === 429) {
        limited++; lastError = msg;
        const rl = parseRateLimit(raw);
        cooldown.set(pair, rl.until);
        const all = ids.every((k) => (cooldown.get(`${k}|${m.id}`) ?? 0) > Date.now());
        await record({ model: m.id, display: m.display, ok: false, limitedUntil: rl.until, perDay: rl.perDay, allKeysLimited: all });
        continue;
      }
      if (badKey) { lastError = msg; authFailed++; continue; }
      lastError = `Gemini xatosi (${m.display}, HTTP ${res.status}): ${msg}`;
      if (res.status === 404) await record({ model: m.id, display: m.display, ok: false, unavailable: "bu kalitda mavjud emas" });
      nextModel = true; // 404, 400, 5xx - keyingi model
    }
  }
  if (authFailed === keys.length && limited === 0) throw new Error(`Gemini kaliti yaroqsiz: ${lastError}`);
  if (limited > 0)
    throw new Error(keys.length === 1
      ? `Barcha Gemini modellarining (${models.length} ta) limiti tugadi (429). Sozlamalarda yana kalit qo'shing yoki limit tiklanishini kuting.`
      : `Barcha Gemini modellari (${models.length} ta) va ${keys.length} ta kalitning limiti tugadi (429). Sozlamalarda yangi kalit qo'shing yoki limit tiklanishini kuting.`);
  throw new Error(lastError || "Ishlaydigan Gemini modeli qolmadi.");
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
  usageMetadata?: { totalTokenCount?: number };
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

/**
 * Gemini model zanjiri va bepul tarif limitlari (Google AI Studio → Rate limits jadvali, 2026-11).
 * Worker bilan bir xil standart zanjir: app/src/YtAuto.Infrastructure/Gemini/GeminiModelRouter.cs (DefaultChain).
 */

/** Bepul kalitda matn yozadigan, limiti 0 bo'lmagan barcha modellar, sifat tartibida: birinchisi asosiy, qolganlari zaxira. */
export const DEFAULT_GEMINI_CHAIN = [
  "Gemini 3.8 Flash", "Gemini 3 Flash", "Gemini 3.5 Flash Lite", "Gemini 3.6 Flash", "Gemini 4 Hydrogen", "Gemini 2.5 Flash",
  "Gemini 3.1 Flash Lite", "Gemini 2.5 Flash Lite", "Gemma 4 31B", "Gemma 4 26B", "Gemini Robotics ER 2 Preview",
];

export type ModelLimits = { rpm: number; tpm: number; rpd: number };

/** Bitta kalit (loyiha) uchun bepul tarif limitlari: daqiqada so'rov, daqiqada token, kuniga so'rov. */
export const FREE_TIER_LIMITS: Record<string, ModelLimits> = {
  "Gemini 3.8 Flash": { rpm: 5, tpm: 250_000, rpd: 20 },
  "Gemini 3 Flash": { rpm: 5, tpm: 250_000, rpd: 20 },
  "Gemini 3.5 Flash Lite": { rpm: 15, tpm: 250_000, rpd: 500 },
  "Gemini 3.6 Flash": { rpm: 5, tpm: 250_000, rpd: 20 },
  "Gemini 4 Hydrogen": { rpm: 5, tpm: 250_000, rpd: 20 },
  "Gemini 2.5 Flash": { rpm: 5, tpm: 250_000, rpd: 20 },
  "Gemini 3.1 Flash Lite": { rpm: 15, tpm: 250_000, rpd: 500 },
  "Gemini 2.5 Flash Lite": { rpm: 10, tpm: 250_000, rpd: 20 },
  "Gemma 4 31B": { rpm: 30, tpm: 16_000, rpd: 14_400 },
  "Gemma 4 26B": { rpm: 30, tpm: 16_000, rpd: 14_400 },
  "Gemini Robotics ER 2 Preview": { rpm: 5, tpm: 250_000, rpd: 20 },
};

const norm = (s: string) => s.trim().toLowerCase().replace(/^models\//, "").replace(/\s+/g, "-");

/** Nom yoki API id bo'yicha limit (id "gemini-3.8-flash-preview-…" ham "Gemini 3.8 Flash" ga mos keladi). */
export function limitsFor(nameOrId: string, display?: string): ModelLimits | null {
  for (const n of [display, nameOrId]) {
    if (!n) continue;
    const exact = Object.keys(FREE_TIER_LIMITS).find((k) => k.toLowerCase() === n.trim().toLowerCase());
    if (exact) return FREE_TIER_LIMITS[exact];
  }
  const id = norm(nameOrId);
  const hit = Object.keys(FREE_TIER_LIMITS).find((k) => new RegExp(`^${norm(k).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(-preview|-latest|-exp)?(-[0-9][0-9-]*)?$`).test(id));
  return hit ? FREE_TIER_LIMITS[hit] : null;
}

export type CatalogEntry = { id: string; display: string };

/**
 * Sozlamadagi nom → API id: aniq id, aniq displayName, keyin id + -preview/-latest/-sana (eng qisqasi). Ro'yxat bo'lmasa nomdan id
 * yasaladi. Topilmasa null. (Worker'dagi GeminiModelRouter.Match bilan bir xil qoida.)
 */
export function matchModel(name: string, catalog: CatalogEntry[] | null): string | null {
  const n = name.trim().replace(/^models\//i, "");
  if (!n) return null;
  const slug = norm(n);
  if (!catalog?.length) return slug;
  const exact = catalog.find((c) => c.id.toLowerCase() === n.toLowerCase() || c.id.toLowerCase() === slug);
  if (exact) return exact.id;
  const byName = catalog.filter((c) => c.display.trim().toLowerCase() === n.toLowerCase()).sort((a, b) => a.id.length - b.id.length)[0];
  if (byName) return byName.id;
  const rx = new RegExp(`^${slug.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(-preview|-latest|-exp)?(-[0-9][0-9-]*)?$`, "i");
  return catalog.filter((c) => rx.test(c.id)).sort((a, b) => a.id.length - b.id.length)[0]?.id ?? null;
}

/** Sozlangan zanjir (bo'sh bo'lsa standart) + eski bitta model oxirida (takrorlanmasa). */
export function modelChain(models: string[] | undefined, legacy?: string): string[] {
  const list = (models ?? []).map((m) => m.trim()).filter(Boolean);
  const out = list.length ? list : [...DEFAULT_GEMINI_CHAIN];
  if (legacy?.trim() && !out.some((m) => m.toLowerCase() === legacy.trim().toLowerCase())) out.push(legacy.trim());
  return out.filter((m, i) => out.findIndex((x) => x.toLowerCase() === m.toLowerCase()) === i);
}

const PT = "America/Los_Angeles";
const dayFmt = new Intl.DateTimeFormat("en-CA", { timeZone: PT, year: "numeric", month: "2-digit", day: "2-digit" });
const hourFmt = new Intl.DateTimeFormat("en-US", { timeZone: PT, hour: "numeric", hourCycle: "h23" });

/** Gemini kunlik limiti Tinch okeani vaqti bilan yarim tunda yangilanadi. */
export const pacificDay = (t: Date | number) => dayFmt.format(t);

/** Keyingi Tinch okeani yarim tuni (UTC ms). */
export function nextPacificMidnight(t: number): number {
  const [y, m, d] = pacificDay(t).split("-").map(Number);
  for (const h of [7, 8]) {
    const c = Date.UTC(y, m - 1, d + 1, h);
    if (Number(hourFmt.format(c)) === 0) return c;
  }
  return Date.UTC(y, m - 1, d + 1, 8);
}

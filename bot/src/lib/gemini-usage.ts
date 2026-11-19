import "server-only";
import { sql, withAccount } from "./db";
import { isDemo } from "./demo";
import { limitsFor, modelChain, pacificDay, type ModelLimits } from "./gemini-models";

/**
 * Har model bo'yicha bugungi Gemini sarfi (Tinch okeani sanasi). Ikki yozuv: worker o'z chaqiruvlarini `gemini.usage.worker` ga,
 * bot (AI yordamchi, kanal tahlili) - `gemini.usage.bot` ga yozadi; ikkalasi ham faqat asosiy hisob egasining kalitlari bo'yicha.
 * Google kalit uchun qolgan limitni beradigan API'ni ochmagan, shuning uchun sarf o'zimiz sanaymiz, limitlar - bepul tarif jadvali.
 */
export const USAGE_WORKER = "gemini.usage.worker";
export const USAGE_BOT = "gemini.usage.bot";

export type ModelUsage = {
  model: string; display: string; requests: number; tokens: number; rateLimited: number;
  /** Oxirgi so'rovlar (unix s): daqiqadagi so'rov soni shundan. */
  recent: number[];
  dayLimitedUntil?: string | null; minuteLimitedUntil?: string | null; unavailable?: string; lastUsedUtc?: string | null;
};
export type UsageDoc = { day: string; updatedAtUtc: string; keys: number; chain: string[]; models: Record<string, ModelUsage> };

const parse = (v: unknown): UsageDoc | null => {
  try { const d = JSON.parse(String(v)) as UsageDoc; return d && typeof d === "object" && d.models ? d : null; } catch { return null; }
};

/** Joriy hisob kontekstida ikkala yozuv (bugungi bo'lmasa - bo'sh). */
export async function readUsageDocs(): Promise<UsageDoc[]> {
  const rows = await sql()`SELECT key, value FROM ytauto.shared_settings WHERE key IN (${USAGE_WORKER}, ${USAGE_BOT})`;
  const today = pacificDay(Date.now());
  return rows.map((r) => parse(r.value)).filter((d): d is UsageDoc => !!d && d.day === today);
}

/** Bugun limiti tugagan modellar (id → qachongacha, ms): router ularni urmaydi. Worker va bot bir xil kalitlardan foydalanadi. */
export function limitedModels(docs: UsageDoc[], now = Date.now()): Map<string, number> {
  const m = new Map<string, number>();
  for (const d of docs) for (const u of Object.values(d.models)) {
    for (const t of [u.dayLimitedUntil, u.minuteLimitedUntil]) {
      const ms = t ? Date.parse(t) : NaN;
      if (ms > now) m.set(u.model.toLowerCase(), Math.max(ms, m.get(u.model.toLowerCase()) ?? 0));
    }
  }
  return m;
}

export type UsageEvent = { model: string; display: string; ok: boolean; tokens?: number; limitedUntil?: number; perDay?: boolean; allKeysLimited?: boolean; unavailable?: string };

/** Bot chaqiruvi natijasini yozadi (o'z tranzaksiyasida; fon ishlarida ham ishlaydi). Mehmon hisobi va demo - yozilmaydi. Xato - jim. */
export async function recordBotUsage(account: { id: string; guest: boolean } | null, keys: number, chain: string[], ev: UsageEvent): Promise<void> {
  if (!account || account.guest || isDemo()) return;
  try {
    await withAccount(account.id, async () => {
      const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${USAGE_BOT}`;
      const now = Date.now(), today = pacificDay(now);
      let doc = parse(row?.value);
      if (!doc || doc.day !== today) doc = { day: today, updatedAtUtc: "", keys, chain, models: {} };
      doc.keys = keys; doc.chain = chain; doc.updatedAtUtc = new Date(now).toISOString();
      const u = (doc.models[ev.model] ??= { model: ev.model, display: ev.display, requests: 0, tokens: 0, rateLimited: 0, recent: [] });
      u.display = ev.display || u.display;
      u.recent = (u.recent ?? []).filter((t) => t >= now / 1000 - 60);
      if (ev.ok) {
        u.requests++; u.tokens += ev.tokens ?? 0; u.recent.push(Math.floor(now / 1000)); u.lastUsedUtc = new Date(now).toISOString();
        u.unavailable = ""; u.minuteLimitedUntil = null;
      } else if (ev.limitedUntil) {
        u.rateLimited++;
        if (ev.allKeysLimited) {
          const until = new Date(ev.limitedUntil).toISOString();
          if (ev.perDay || ev.limitedUntil - now > 10 * 60_000) u.dayLimitedUntil = until; else u.minuteLimitedUntil = until;
        }
      } else if (ev.unavailable) u.unavailable = ev.unavailable;
      await sql()`
        INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${USAGE_BOT}, ${JSON.stringify(doc)}, now())
        ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
    }, { guest: false });
  } catch (e) {
    console.warn("[gemini] sarf yozilmadi:", e instanceof Error ? e.message : e);
  }
}

export type ModelLimitRow = {
  model: string; display: string; primary: boolean;
  /** Bitta kalit uchun bepul tarif limiti (jadvalda bo'lmasa null). */
  limits: ModelLimits | null;
  requestsToday: number; tokensToday: number; requestsLastMinute: number; rateLimitedToday: number;
  status: "ok" | "idle" | "minute" | "day" | "unavailable";
  until: string | null; note: string;
};
export type GeminiUsageView = { day: string; keys: number; rows: ModelLimitRow[]; updatedAtUtc: string | null };

/**
 * Egasi uchun jadval: zanjir tartibida har model, worker va bot sarfi qo'shilgan, bepul tarif limiti bilan.
 * Zanjirdagi nom (AI Studio nomi) sarf yozuvidagi API id bilan nomi yoki id'si orqali bog'lanadi.
 */
export function buildUsageView(docs: UsageDoc[], cfg: { models?: string[]; model?: string }, keys: number, now = Date.now()): GeminiUsageView {
  const chain = modelChain(cfg.models, cfg.model);
  const all = new Map<string, ModelUsage[]>();
  for (const d of docs) for (const u of Object.values(d.models)) {
    const k = u.model.toLowerCase();
    all.set(k, [...(all.get(k) ?? []), u]);
  }
  const used = new Set<string>();
  const findFor = (name: string) => {
    const n = name.trim().toLowerCase(), slug = n.replace(/\s+/g, "-");
    for (const [k, us] of all) {
      if (used.has(k)) continue;
      if (k === n || k === slug || us.some((u) => u.display?.trim().toLowerCase() === n) || k.startsWith(slug + "-preview") || k.startsWith(slug + "-0") || k.startsWith(slug + "-latest")) return [k, us] as const;
    }
    return null;
  };
  const row = (name: string, model: string, us: ModelUsage[], primary: boolean): ModelLimitRow => {
    const sum = (f: (u: ModelUsage) => number) => us.reduce((a, u) => a + f(u), 0);
    const display = us.find((u) => u.display)?.display || name;
    const latest = (f: (u: ModelUsage) => string | null | undefined) => us.map((u) => (f(u) ? Date.parse(f(u)!) : 0)).reduce((a, b) => Math.max(a, b), 0);
    const day = latest((u) => u.dayLimitedUntil), minute = latest((u) => u.minuteLimitedUntil);
    const unavailable = us.map((u) => u.unavailable).find((x) => x) ?? "";
    const requests = sum((u) => u.requests);
    const status: ModelLimitRow["status"] = day > now ? "day" : minute > now ? "minute" : unavailable ? "unavailable" : requests > 0 ? "ok" : "idle";
    return {
      model, display, primary, limits: limitsFor(model, display),
      requestsToday: requests, tokensToday: sum((u) => u.tokens),
      requestsLastMinute: sum((u) => (u.recent ?? []).filter((t) => t >= now / 1000 - 60).length),
      rateLimitedToday: sum((u) => u.rateLimited),
      status, until: status === "day" ? new Date(day).toISOString() : status === "minute" ? new Date(minute).toISOString() : null,
      note: status === "unavailable" ? unavailable : "",
    };
  };
  const rows: ModelLimitRow[] = chain.map((name, i) => {
    const hit = findFor(name);
    if (hit) used.add(hit[0]);
    return row(name, hit?.[0] ?? name, hit?.[1] ?? [], i === 0);
  });
  for (const [k, us] of all) if (!used.has(k)) rows.push(row(us[0].display || k, k, us, false));
  const updated = docs.map((d) => d.updatedAtUtc).filter(Boolean).sort().at(-1) ?? null;
  return { day: pacificDay(now), keys, rows, updatedAtUtc: updated };
}

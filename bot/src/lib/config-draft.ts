import type { BotConfig, BotConfigInput } from "./types";

/**
 * Saqlangan config → saqlashga yuboriladigan qoralama (sirlar bo'sh: server eskisini saqlab qoladi).
 * Klient ham, server ham ishlatadi (server: faqat o'zgargan maydonlar kelganda yangi asos shundan quriladi).
 */
export type ConfigDraft = BotConfigInput;

export const toDraft = (c: BotConfig): ConfigDraft => ({
  gemini: { ...c.gemini, apiKeys: c.gemini.apiKeys.map((k) => ({ ...k })) },
  limits: { ...c.limits },
  defaults: { ...c.defaults },
  youtube: { ...c.youtube, clientSecret: "" },
  openFilm: { ...c.openFilm },
  visuals: { ...c.visuals, pexelsApiKey: "", pixabayApiKey: "" },
  claude: { ...c.claude },
  voice: { profiles: c.voice.profiles.map((p) => ({ ...p })), settings: { ...c.voice.settings } },
});

/** Bo'lim → o'zgargan maydonlar (faqat shular yuboriladi). */
export type ConfigPatch = { [K in keyof ConfigDraft]?: Partial<NonNullable<ConfigDraft[K]>> };

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** Ikki qoralama farqi: faqat o'zgargan bo'limlarning o'zgargan maydonlari. */
export function diffDraft(before: ConfigDraft, after: ConfigDraft): ConfigPatch {
  const out: Record<string, Record<string, unknown>> = {};
  for (const k of Object.keys(after) as (keyof ConfigDraft)[]) {
    const a = (after[k] ?? {}) as Record<string, unknown>, b = (before[k] ?? {}) as Record<string, unknown>;
    for (const f of Object.keys(a)) if (!same(a[f], b[f])) (out[k] ??= {})[f] = a[f];
  }
  return out as ConfigPatch;
}

/** Yangi asos (serverdagi eng so'nggi config) ustiga faqat o'zgargan maydonlar. */
export function applyPatch(base: ConfigDraft, patch: ConfigPatch): ConfigDraft {
  const next = { ...base } as Record<string, unknown>;
  for (const [k, v] of Object.entries(patch)) if (v && typeof v === "object") next[k] = { ...((base as Record<string, object>)[k] ?? {}), ...v };
  return next as ConfigDraft;
}

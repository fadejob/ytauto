import "server-only";
import { createHash } from "node:crypto";
import { sql } from "./db";
import { isDemo } from "./demo";
import type { BotConfig, BotConfigInput, GeminiKeyInfo } from "./types";

const KEY = "config";

/**
 * Bot boshqaradigan sozlamalar online bazada bitta JSON sifatida (shared_settings.config) turadi.
 * Desktop ilova va worker uni o'qib, lokal sozlamalar ustiga qo'yadi. Sirlar (Gemini kalitlari, OAuth client secret)
 * shu JSON ichida saqlanadi, lekin API orqali brauzerga hech qachon qaytarilmaydi.
 */
type Stored = {
  /** apiKeys - kalitlar tartib bilan; apiKey - eski (bitta kalit) format, o'qiladi va eski desktoplar uchun birinchi kalit yoziladi. */
  gemini?: Partial<Omit<BotConfig["gemini"], "apiKeys">> & { apiKey?: string; apiKeys?: string[] };
  limits?: Partial<BotConfig["limits"]>;
  defaults?: Partial<BotConfig["defaults"]>;
  youtube?: Partial<BotConfig["youtube"]> & { clientSecret?: string };
  openFilm?: Partial<BotConfig["openFilm"]>;
  claude?: Partial<BotConfig["claude"]>;
  voice?: { profiles?: BotConfig["voice"]["profiles"]; settings?: Partial<BotConfig["voice"]["settings"]> };
  visuals?: Partial<Omit<BotConfig["visuals"], "pexelsKeySet" | "pixabayKeySet">> & { pexelsApiKey?: string; pixabayApiKey?: string };
};

const DEF: BotConfig = {
  gemini: { model: "gemini-2.5-flash", temperature: 0.7, useSearchGrounding: true, timeoutSec: 180, apiKeys: [] },
  claude: { model: "", timeoutMinutes: 45, maxBudgetUsd: 0 },
  voice: {
    profiles: [],
    settings: { baseUrl: "http://127.0.0.1:3900", startupTimeoutSec: 300, requestTimeoutSec: 900, chunkChars: 450, pauseBetweenChunksSec: 0.25, whisperModel: "Systran/faster-whisper-base", alignTimeoutSec: 1200 },
  },
  limits: { maxRunAttempts: 3, maxQaFixCycles: 2, minFreeDiskGb: 5, minFreeRamGbForTts: 1 },
  defaults: { language: "en", format: 0, template: "", voiceProfile: "" },
  youtube: {
    clientId: "", clientSecretSet: false, publishTime: "18:00", timeZoneId: "Asia/Tashkent", categoryId: "27", regionCode: "UZ",
    madeForKids: false, containsSyntheticMedia: true, license: "youtube", embeddable: true, publicStatsViewable: true,
    notifySubscribers: true, setRecordingDate: false, locationDescription: "", defaultTags: "", defaultDescription: "", minLeadMinutes: 30,
  },
  openFilm: { renderWorkers: 1, fps: 30, loudnessLufs: -14, burnSubtitles: true, lookFullAfterRender: true, renderTimeoutMinutes: 90 },
  visuals: { realMedia: true, maxMedia: 16, templateStyleGuide: true, templateMotion: true, styleReview: true, minStyleScore: 6.5, pexelsKeySet: false, pixabayKeySet: false,
    openFilmStock: true, stockImages: 5, stockClips: 3, stockScenes: 8 },
};

let demoStored: Stored = {};

async function readStored(): Promise<Stored> {
  if (isDemo()) return demoStored;
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  try { return row ? (JSON.parse(row.value as string) as Stored) : {}; } catch { return {}; }
}

/** Saqlangan Gemini kalitlari tartib bilan (takrorlarsiz). */
function storedKeys(s: Stored): string[] {
  const all = [...(s.gemini?.apiKeys ?? []), s.gemini?.apiKey ?? ""].map((k) => k.trim()).filter(Boolean);
  return [...new Set(all)];
}
const keyId = (k: string) => createHash("sha256").update(k).digest("hex").slice(0, 12);
const keyInfo = (k: string): GeminiKeyInfo => ({ id: keyId(k), hint: k.length > 12 ? `${k.slice(0, 4)}…${k.slice(-4)}` : "…" });

/**
 * Faqat server ichida: shu hisobning Gemini kalitlari (AI yordamchi uchun). Brauzerga qaytarilmaydi.
 * Har foydalanuvchi o'z kalitini ishlatadi; GEMINI_API_KEY (server kaliti) faqat demo rejimda.
 */
export async function getGeminiKeys(): Promise<{ keys: string[]; model: string }> {
  const s = await readStored();
  const keys = storedKeys(s);
  const env = isDemo() ? process.env.GEMINI_API_KEY?.trim() : undefined;
  return { keys: keys.length ? keys : env ? [env] : [], model: s.gemini?.model?.trim() || DEF.gemini.model };
}

/** Gemini kaliti kiritilganmi: yo'q bo'lsa mavzu qo'shib bo'lmaydi, AI yordamchi o'chiq, mavjud mavzular muzlatilgan. */
export async function geminiReady(): Promise<boolean> {
  return (await getGeminiKeys()).keys.length > 0;
}

export const GEMINI_REQUIRED = "Gemini kaliti kiritilmagan: avval Sozlamalar → Gemini'ga kalit qo'shing (mavzular shungacha muzlatilgan).";

function view(s: Stored): BotConfig {
  const g = { ...(s.gemini ?? {}) };
  delete g.apiKey; delete g.apiKeys; // sirlar brauzerga qaytarilmaydi
  const { clientSecret, ...y } = s.youtube ?? {};
  const { pexelsApiKey, pixabayApiKey, ...vis } = s.visuals ?? {}; // stok foto kalitlari ham brauzerga qaytarilmaydi
  return {
    gemini: { ...DEF.gemini, ...g, apiKeys: storedKeys(s).map(keyInfo) },
    limits: { ...DEF.limits, ...(s.limits ?? {}) },
    defaults: { ...DEF.defaults, ...(s.defaults ?? {}) },
    youtube: { ...DEF.youtube, ...y, clientSecretSet: !!clientSecret },
    openFilm: { ...DEF.openFilm, ...(s.openFilm ?? {}) },
    visuals: { ...DEF.visuals, ...vis, pexelsKeySet: !!pexelsApiKey, pixabayKeySet: !!pixabayApiKey },
    claude: { ...DEF.claude, ...(s.claude ?? {}) },
    voice: { profiles: s.voice?.profiles ?? [], settings: { ...DEF.voice.settings, ...(s.voice?.settings ?? {}) } },
  };
}

export async function getConfig(): Promise<BotConfig> { return view(await readStored()); }

/** «Mening ovozim» klon profili (VoiceStudio profil nomi "YtAuto u-<hisob>"): faqat voice-clone.ts boshqaradi. */
const isCloneProfile = (p: { voice?: string }) => /^YtAuto u-[0-9a-f]{8}$/.test(p.voice ?? "");

async function writeStored(next: Stored) {
  const json = JSON.stringify(next);
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${json}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

/**
 * Klon profilini bot ro'yxatiga qo'yadi (null - olib tashlaydi). makeDefault - yangi mavzular standart ovozi shu bo'ladi.
 * Olib tashlanganda standart ovoz shu profil bo'lsa, standart bo'shatiladi (kompyuter o'z standartini ishlatadi).
 */
export async function setCloneProfile(p: { name: string; model: string; voice: string } | null, makeDefault: boolean): Promise<void> {
  const cur = await readStored();
  const old = (cur.voice?.profiles ?? []).find(isCloneProfile);
  const profiles = (cur.voice?.profiles ?? []).filter((x) => !isCloneProfile(x) && (!p || x.name !== p.name));
  if (p) profiles.push({ name: p.name, model: p.model, voice: p.voice, language: "en", speed: 1 });
  const defaults = { ...(cur.defaults ?? {}) };
  if (p && makeDefault) defaults.voiceProfile = p.name;
  else if (!p && old && defaults.voiceProfile === old.name) defaults.voiceProfile = "";
  const next: Stored = { ...cur, voice: { ...(cur.voice ?? {}), profiles }, defaults };
  if (isDemo()) { demoStored = next; return; }
  await writeStored(next);
}

/** Faqat server ichida: OAuth client (YouTube ulash uchun). Brauzerga qaytarilmaydi. */
export async function getYouTubeClient(): Promise<{ clientId: string; clientSecret: string }> {
  const s = await readStored();
  return { clientId: s.youtube?.clientId?.trim() ?? "", clientSecret: s.youtube?.clientSecret?.trim() ?? "" };
}

const num = (v: unknown, lo: number, hi: number, name: string) => {
  const n = Number(v);
  if (!Number.isFinite(n) || n < lo || n > hi) throw new Error(`${name}: ${lo}–${hi} oralig'ida bo'lsin.`);
  return n;
};
const hhmm = (v: string, name: string) => {
  if (!/^([01]\d|2[0-3]):[0-5]\d$/.test(v)) throw new Error(`${name}: HH:mm formatida bo'lsin.`);
  return v;
};

/**
 * Stok foto kaliti (Pexels/Pixabay): yangisi kiritilgan bo'lsa - u (format tekshiriladi); bo'sh va keep=true - eskisi qoladi;
 * bo'sh va keep=false - o'chiriladi (undefined).
 */
function stockKey(next: string | undefined, keep: boolean | undefined, cur: string | undefined, name: string): string | undefined {
  const k = String(next ?? "").trim();
  if (k) {
    if (!/^[A-Za-z0-9_-]{10,120}$/.test(k)) throw new Error(`${name} kaliti noto'g'ri ko'rinishda (bo'sh joysiz harf/raqam satri bo'lishi kerak).`);
    return k;
  }
  return keep ? cur?.trim() || undefined : undefined;
}

export async function saveConfig(input: BotConfigInput): Promise<BotConfig> {
  const cur = await readStored();
  const g = input.gemini, l = input.limits, d = input.defaults, y = input.youtube;
  if (!g.model.trim()) throw new Error("Gemini modeli bo'sh bo'lmasin.");
  try { new Intl.DateTimeFormat("en", { timeZone: y.timeZoneId }); } catch { throw new Error("YouTube vaqt zonasi noto'g'ri (masalan Asia/Tashkent)."); }
  if (![0, 1, 2].includes(Number(d.format))) throw new Error("Format noto'g'ri.");
  // Kalitlar: qoldirilganlari (id bo'yicha, kelgan tartibda) + yangilari. Hech biri brauzerdan to'liq kelmaydi.
  const byId = new Map(storedKeys(cur).map((k) => [keyId(k), k]));
  const keys: string[] = [];
  for (const k of g.apiKeys ?? []) { const v = byId.get(String(k?.id ?? "")); if (v && !keys.includes(v)) keys.push(v); }
  for (const raw of g.newApiKeys ?? []) {
    const k = String(raw ?? "").trim();
    if (!k) continue;
    if (k.length < 20 || /\s/.test(k)) throw new Error("Gemini kaliti noto'g'ri ko'rinishda (AIza… bilan boshlanadigan uzun satr bo'lishi kerak).");
    if (!keys.includes(k)) keys.push(k);
  }
  if (keys.length > 50) throw new Error("Gemini kalitlari 50 tadan oshmasin.");
  const next: Stored = {
    gemini: {
      model: g.model.trim(), temperature: num(g.temperature, 0, 2, "Temperature"), useSearchGrounding: !!g.useSearchGrounding,
      apiKeys: keys, apiKey: keys[0], // apiKey - faqat eski desktop versiyalari uchun
      timeoutSec: Math.round(num(g.timeoutSec ?? 180, 30, 1800, "Gemini timeout")),
    },
    claude: {
      model: (input.claude?.model ?? "").trim(), timeoutMinutes: Math.round(num(input.claude?.timeoutMinutes ?? 45, 5, 600, "Claude timeout")),
      maxBudgetUsd: num(input.claude?.maxBudgetUsd ?? 0, 0, 1000, "Claude byudjeti"),
    },
    voice: {
      profiles: (input.voice?.profiles ?? []).map((p) => ({
        name: String(p.name).trim().slice(0, 60), model: String(p.model).trim(), voice: String(p.voice ?? "").trim(),
        language: String(p.language || "en").trim(), speed: num(p.speed ?? 1, 0.5, 2, "Ovoz tezligi"),
      })).filter((p) => p.name && p.model && !isCloneProfile(p))
        // «Mening ovozim» faqat namuna bo'limidan boshqariladi: oddiy saqlashda yo'qolib qolmaydi va qo'lda yasab bo'lmaydi.
        .concat((cur.voice?.profiles ?? []).filter(isCloneProfile)),
      settings: (() => {
        const v = input.voice?.settings ?? DEF.voice.settings;
        if (!/^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?\/?$/i.test(v.baseUrl.trim())) throw new Error("VoiceStudio manzili faqat lokal bo'lsin (http://127.0.0.1:3900).");
        return {
          baseUrl: v.baseUrl.trim().replace(/\/+$/, ""), startupTimeoutSec: Math.round(num(v.startupTimeoutSec, 30, 1800, "VoiceStudio kutish")),
          requestTimeoutSec: Math.round(num(v.requestTimeoutSec, 30, 3600, "TTS so'rov vaqti")), chunkChars: Math.round(num(v.chunkChars, 100, 2000, "TTS bo'lagi")),
          pauseBetweenChunksSec: num(v.pauseBetweenChunksSec, 0, 5, "Bo'laklar orasidagi pauza"), whisperModel: v.whisperModel.trim() || DEF.voice.settings.whisperModel,
          alignTimeoutSec: Math.round(num(v.alignTimeoutSec, 60, 7200, "Subtitr vaqti timeouti")),
        };
      })(),
    },
    limits: {
      maxRunAttempts: Math.round(num(l.maxRunAttempts, 1, 10, "Urinishlar")), maxQaFixCycles: Math.round(num(l.maxQaFixCycles, 0, 10, "QA sikllari")),
      minFreeDiskGb: num(l.minFreeDiskGb, 0, 1000, "Min. disk"), minFreeRamGbForTts: num(l.minFreeRamGbForTts, 0, 128, "Min. RAM"),
    },
    defaults: { language: (d.language || "en").trim(), format: Number(d.format), template: d.template ?? "", voiceProfile: d.voiceProfile ?? "" },
    youtube: {
      clientId: y.clientId.trim(), publishTime: hhmm(y.publishTime, "Public soati"), timeZoneId: y.timeZoneId.trim(),
      categoryId: y.categoryId.trim() || "27", regionCode: (y.regionCode || "UZ").trim().toUpperCase(), madeForKids: !!y.madeForKids,
      containsSyntheticMedia: !!y.containsSyntheticMedia, license: y.license === "creativeCommon" ? "creativeCommon" : "youtube",
      embeddable: !!y.embeddable, publicStatsViewable: !!y.publicStatsViewable, notifySubscribers: !!y.notifySubscribers,
      setRecordingDate: !!y.setRecordingDate, locationDescription: y.locationDescription ?? "", defaultTags: y.defaultTags ?? "", defaultDescription: y.defaultDescription ?? "",
      minLeadMinutes: Math.round(num(y.minLeadMinutes, 0, 1440, "Minimal oldindan vaqt")),
      clientSecret: y.clientSecret?.trim() ? y.clientSecret.trim() : cur.youtube?.clientSecret,
    },
    openFilm: {
      renderWorkers: Math.round(num(input.openFilm.renderWorkers, 1, 4, "Parallel renderer")), fps: Math.round(num(input.openFilm.fps, 1, 120, "FPS")),
      loudnessLufs: num(input.openFilm.loudnessLufs, -40, 0, "Loudness (LUFS)"), burnSubtitles: !!input.openFilm.burnSubtitles,
      lookFullAfterRender: !!input.openFilm.lookFullAfterRender, renderTimeoutMinutes: Math.round(num(input.openFilm.renderTimeoutMinutes, 5, 600, "Render timeout")),
    },
    visuals: input.visuals ? {
      realMedia: !!input.visuals.realMedia, maxMedia: Math.round(num(input.visuals.maxMedia, 0, 40, "Rasmlar soni")),
      templateStyleGuide: !!input.visuals.templateStyleGuide, templateMotion: input.visuals.templateMotion !== false, styleReview: !!input.visuals.styleReview,
      minStyleScore: num(input.visuals.minStyleScore, 0, 10, "Shablonga moslik bali"),
      openFilmStock: input.visuals.openFilmStock !== false,
      stockImages: Math.round(num(input.visuals.stockImages ?? DEF.visuals.stockImages, 1, 8, "Sahnaga rasm nomzodlari")),
      stockClips: Math.round(num(input.visuals.stockClips ?? DEF.visuals.stockClips, 0, 4, "Sahnaga video nomzodlari")),
      stockScenes: Math.round(num(input.visuals.stockScenes ?? DEF.visuals.stockScenes, 0, 20, "Stok sahnalar soni")),
      pexelsApiKey: stockKey(input.visuals.pexelsApiKey, input.visuals.pexelsKeySet, cur.visuals?.pexelsApiKey, "Pexels"),
      pixabayApiKey: stockKey(input.visuals.pixabayApiKey, input.visuals.pixabayKeySet, cur.visuals?.pixabayApiKey, "Pixabay"),
    } : cur.visuals,
  };
  if (isDemo()) { demoStored = next; return view(next); }
  await writeStored(next);
  return view(next);
}

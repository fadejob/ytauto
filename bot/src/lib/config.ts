import "server-only";
import { sql } from "./db";
import { isDemo } from "./demo";
import type { BotConfig, BotConfigInput } from "./types";

const KEY = "config";

/**
 * Bot boshqaradigan sozlamalar online bazada bitta JSON sifatida (shared_settings.config) turadi.
 * Desktop ilova va worker uni o'qib, lokal sozlamalar ustiga qo'yadi. Sirlar (Gemini kaliti, OAuth client secret)
 * shu JSON ichida saqlanadi, lekin API orqali brauzerga hech qachon qaytarilmaydi.
 */
type Stored = {
  gemini?: Partial<BotConfig["gemini"]> & { apiKey?: string };
  limits?: Partial<BotConfig["limits"]>;
  defaults?: Partial<BotConfig["defaults"]>;
  youtube?: Partial<BotConfig["youtube"]> & { clientSecret?: string };
  openFilm?: Partial<BotConfig["openFilm"]>;
  claude?: Partial<BotConfig["claude"]>;
  voice?: { profiles?: BotConfig["voice"]["profiles"]; settings?: Partial<BotConfig["voice"]["settings"]> };
};

const DEF: BotConfig = {
  gemini: { model: "gemini-2.5-flash", temperature: 0.7, useSearchGrounding: true, timeoutSec: 180, apiKeySet: false },
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
};

let demoStored: Stored = {};

async function readStored(): Promise<Stored> {
  if (isDemo()) return demoStored;
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  try { return row ? (JSON.parse(row.value as string) as Stored) : {}; } catch { return {}; }
}

function view(s: Stored): BotConfig {
  const { apiKey, ...g } = s.gemini ?? {};
  const { clientSecret, ...y } = s.youtube ?? {};
  return {
    gemini: { ...DEF.gemini, ...g, apiKeySet: !!apiKey },
    limits: { ...DEF.limits, ...(s.limits ?? {}) },
    defaults: { ...DEF.defaults, ...(s.defaults ?? {}) },
    youtube: { ...DEF.youtube, ...y, clientSecretSet: !!clientSecret },
    openFilm: { ...DEF.openFilm, ...(s.openFilm ?? {}) },
    claude: { ...DEF.claude, ...(s.claude ?? {}) },
    voice: { profiles: s.voice?.profiles ?? [], settings: { ...DEF.voice.settings, ...(s.voice?.settings ?? {}) } },
  };
}

export async function getConfig(): Promise<BotConfig> { return view(await readStored()); }

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

export async function saveConfig(input: BotConfigInput): Promise<BotConfig> {
  const cur = await readStored();
  const g = input.gemini, l = input.limits, d = input.defaults, y = input.youtube;
  if (!g.model.trim()) throw new Error("Gemini modeli bo'sh bo'lmasin.");
  try { new Intl.DateTimeFormat("en", { timeZone: y.timeZoneId }); } catch { throw new Error("YouTube vaqt zonasi noto'g'ri (masalan Asia/Tashkent)."); }
  if (![0, 1, 2].includes(Number(d.format))) throw new Error("Format noto'g'ri.");
  const next: Stored = {
    gemini: {
      model: g.model.trim(), temperature: num(g.temperature, 0, 2, "Temperature"), useSearchGrounding: !!g.useSearchGrounding,
      apiKey: g.apiKey?.trim() ? g.apiKey.trim() : cur.gemini?.apiKey,
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
      })).filter((p) => p.name && p.model),
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
  };
  if (isDemo()) { demoStored = next; return view(next); }
  const json = JSON.stringify(next);
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${json}, now())
    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
  return view(next);
}

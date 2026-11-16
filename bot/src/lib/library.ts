import "server-only";
import { sql } from "./db";
import { isDemo } from "./demo";
import type { TemplateItem, VoiceInfo } from "./types";

/** Shablonlar (ytauto.templates) va ovoz ma'lumotlari (shared_settings: voice.catalog.KOMPYUTER): kompyuterlar bazaga yozadi, bot faqat ko'rsatadi. */
export async function getTemplates(): Promise<TemplateItem[]> {
  if (isDemo()) return demoTemplates();
  try {
    const rows = await sql()`SELECT machine, name, kind, size_bytes::float8 AS size, added_at_utc, source, notes
                             FROM ytauto.templates ORDER BY added_at_utc DESC NULLS LAST, name`;
    return rows.map((r) => ({
      machine: r.machine as string, name: r.name as string, kind: r.kind as string, size: r.size as number,
      addedAt: r.added_at_utc ? (r.added_at_utc as Date).toISOString() : null, source: r.source === "video" ? "video" : "upload", notes: (r.notes as string | null) ?? "",
    }));
  } catch { return []; /* jadval hali yaratilmagan: yangi ilova bir marta ochilmagan */ }
}

export async function getVoice(): Promise<VoiceInfo[]> {
  if (isDemo()) return demoVoice();
  const rows = await sql()`SELECT value FROM ytauto.shared_settings WHERE key LIKE 'voice.catalog.%' ORDER BY key`;
  const out: VoiceInfo[] = [];
  for (const r of rows) { try { out.push(JSON.parse(r.value as string) as VoiceInfo); } catch { /* buzilgan */ } }
  return out;
}

/** Mavzu yaratishdagi tanlov uchun: barcha kompyuterlardagi shablon nomlari (takrorlarsiz). */
export async function templateCatalog(): Promise<{ name: string; kind: string }[] | null> {
  if (isDemo()) return null;
  const items = await getTemplates();
  if (items.length === 0) return null;
  const seen = new Map<string, string>();
  for (const t of items) if (!seen.has(t.name)) seen.set(t.name, t.kind);
  return [...seen].map(([name, kind]) => ({ name, kind }));
}

// ------------------------------------------------------------------ demo
function demoTemplates(): TemplateItem[] {
  const d = (days: number) => new Date(Date.now() - days * 86400000).toISOString();
  return [
    { machine: "AIBA", name: "#106 How Your Phone Knows Where You Are", kind: "Mp4Reference", size: 21_400_000, addedAt: d(2), source: "video", notes: "Video #106 — 1920x1080, 58.2s" },
    { machine: "AIBA", name: "#105 Octopuses Are Smarter Than You Think", kind: "Mp4Reference", size: 19_800_000, addedAt: d(3), source: "video", notes: "Video #105 — 1920x1080, 61.0s" },
    { machine: "AIBA", name: "Clean explainer", kind: "OpenFilmProject", size: 3_100_000, addedAt: d(9), source: "upload", notes: "Bot orqali yuklangan" },
  ];
}

function demoVoice(): VoiceInfo[] {
  return [{
    machine: "AIBA", fetchedAtUtc: new Date().toISOString(), voiceStudioVersion: "0.5.8", updatedAtUtc: new Date().toISOString(),
    engines: [
      { id: "kittentts", displayName: "KittenTTS (CPU)", available: true, usable: true, reason: null, voices: ["expr-voice-2-m", "expr-voice-2-f", "expr-voice-3-m", "expr-voice-3-f"], installedModels: ["KittenML/kitten-tts-mini-0.8"], knownModels: ["KittenML/kitten-tts-mini-0.8"], supportsCloning: false },
      { id: "omnivoice", displayName: "OmniVoice (600+ til, klonlash)", available: true, usable: true, reason: null, voices: [], installedModels: ["k2-fsa/OmniVoice"], knownModels: ["k2-fsa/OmniVoice"], supportsCloning: true },
      { id: "mlx-audio", displayName: "MLX-Audio", available: false, usable: false, reason: "Bu dvigatel shu kompyuter platformasida ishlamaydi.", voices: [], installedModels: [], knownModels: [], supportsCloning: false },
    ],
    profiles: [{ name: "Kitten EN ayol", model: "kittentts", voice: "expr-voice-2-f", language: "en", speed: 1 }, { name: "Kitten EN erkak", model: "kittentts", voice: "expr-voice-2-m", language: "en", speed: 1 }],
    defaultProfile: "Kitten EN ayol",
  }];
}

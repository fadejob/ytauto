import "server-only";
import { sql } from "./db";
import { isDemo } from "./demo";
import type { ChannelAssets } from "./types";
import { activeChannelId, channelList } from "./youtube";

/**
 * Har YouTube kanalining o'z shablonlari va ovoz profillari: shared_settings['channel.assets'] =
 *   { templates: {nom: kanal id}, voices: {nom: kanal id}, defaults: {kanal id: {template?, voice?}} }.
 *  - Kanalga biriktirilgan shablon/ovoz faqat shu kanal mavzularida tanlanadi (bot va MCP - katalog shu kanal bo'yicha).
 *  - Biriktirilmagani (eski yoki kompyuterda qo'shilgani) - «umumiy»: hamma kanalda ko'rinadi. Uzilgan kanalniki ham umumiy bo'ladi.
 *  - Yangi yuklangan shablon va yangi ovoz profili yuklangan paytdagi faol kanalga biriktiriladi; tayyor video shabloni
 *    («#<id> ...») - o'sha video kanaliga.
 *  - Standart shablon/ovoz ham kanal bo'yicha (bo'lmasa - hisobning umumiy standarti, agar shu kanalda ruxsat bo'lsa).
 */
const KEY = "channel.assets";
export type AssetKind = "template" | "voice";
type ChannelDefaults = { template?: string; voice?: string };
type Stored = { templates?: Record<string, string>; voices?: Record<string, string>; defaults?: Record<string, ChannelDefaults> };

let demo: Stored = {};

async function read(): Promise<Stored> {
  if (isDemo()) return demo;
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  try { return row ? (JSON.parse(row.value as string) as Stored) : {}; } catch { return {}; }
}

async function write(s: Stored) {
  if (isDemo()) { demo = s; return; }
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${JSON.stringify(s)}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

/** O'qish-o'zgartirish-yozish (bir tranzaksiyada qator qulflanadi - parallel saqlashlar bir-birini o'chirmaydi). */
async function update(fn: (s: Stored) => void) {
  if (!isDemo()) await sql()`SELECT 1 FROM ytauto.shared_settings WHERE key = ${KEY} FOR UPDATE`;
  const s = await read();
  fn(s);
  await write(s);
}

const VIDEO_TEMPLATE = /^#(\d+)\s/;

/**
 * So'rov kanali (kanal MCP'si - bog'langan kanal, aks holda faol kanal) bo'yicha ko'rinish. templateNames berilsa - tayyor video
 * shablonlarining egasi (biriktirilmagan bo'lsa) video kanalidan aniqlanadi.
 */
export type AssetScope = ChannelAssets & {
  allowed: (kind: AssetKind, name: string) => boolean;
  /** Kanalning o'z standarti (undefined - kanalda tanlanmagan, umumiy standart ishlatiladi). */
  defaults: ChannelDefaults;
};

export async function assetScope(templateNames: string[] = []): Promise<AssetScope> {
  const [s, l, active] = await Promise.all([read(), channelList(), activeChannelId()]);
  const ids = new Set(l.channels.map((c) => c.id));
  const live = (m: Record<string, string> | undefined) => Object.fromEntries(Object.entries(m ?? {}).filter(([, ch]) => ids.has(ch)));
  const templates = live(s.templates), voices = live(s.voices);

  // Tayyor video shabloni «#<vazifa id> ...»: biriktirilmagan bo'lsa - vazifaning kanali.
  const jobIds = [...new Set(templateNames.filter((n) => !(n in templates)).map((n) => Number(VIDEO_TEMPLATE.exec(n)?.[1])).filter((n) => n > 0))];
  if (jobIds.length && !isDemo()) {
    const rows = await sql()`SELECT id::int AS id, channel_id FROM ytauto.jobs WHERE id IN ${sql()(jobIds)} AND channel_id <> ''`;
    const chOf = new Map(rows.map((r) => [r.id as number, r.channel_id as string]));
    for (const n of templateNames) {
      const ch = chOf.get(Number(VIDEO_TEMPLATE.exec(n)?.[1]));
      if (!(n in templates) && ch && ids.has(ch)) templates[n] = ch;
    }
  }
  const channel = ids.has(active) ? active : "";
  const owners = { template: templates, voice: voices };
  return {
    channel, channels: l.channels.map((c) => ({ id: c.id, title: c.title })), templates, voices,
    allowed: (kind, name) => !channel || !owners[kind][name] || owners[kind][name] === channel,
    defaults: channel ? { ...(s.defaults?.[channel] ?? {}) } : {},
  };
}

/** Shablon yoki ovozni kanalga biriktiradi ("" - umumiy). Kanal shu hisobga ulangan bo'lishi kerak. */
export async function assignAsset(kind: AssetKind, name: string, channel: string): Promise<void> {
  const n = name.trim();
  if (!n) throw new Error("Nom bo'sh.");
  if (channel && !(await channelList()).channels.some((c) => c.id === channel)) throw new Error("Kanal topilmadi — ro'yxatni yangilang.");
  await update((s) => {
    const key = kind === "template" ? "templates" : "voices";
    const m = { ...(s[key] ?? {}) };
    if (channel) m[n] = channel; else delete m[n];
    s[key] = m;
    // Boshqa kanalga o'tgan shablon/ovoz eski kanalning standarti bo'lib qolmasin.
    const field = kind === "template" ? "template" : "voice";
    for (const [ch, d] of Object.entries(s.defaults ?? {})) if (channel && ch !== channel && d[field] === n) delete d[field];
  });
}

/** Yangi shablon/ovozlarni so'rov kanaliga biriktiradi (kanal yo'q bo'lsa - umumiy qoladi). */
export async function assignToActive(kind: AssetKind, names: string[]): Promise<void> {
  const ch = await activeChannelId();
  if (!ch || names.length === 0) return;
  await update((s) => {
    const key = kind === "template" ? "templates" : "voices";
    s[key] = { ...(s[key] ?? {}), ...Object.fromEntries(names.map((n) => [n.trim(), ch])) };
  });
}

/** O'chirilgan shablon/ovoz biriktirmalardan olib tashlanadi. */
export async function forgetAssets(kind: AssetKind, names: string[]): Promise<void> {
  if (names.length === 0) return;
  await update((s) => {
    const key = kind === "template" ? "templates" : "voices";
    const m = { ...(s[key] ?? {}) };
    for (const n of names) delete m[n];
    s[key] = m;
    const field = kind === "template" ? "template" : "voice";
    for (const d of Object.values(s.defaults ?? {})) if (d[field] !== undefined && names.includes(d[field]!)) d[field] = "";
  });
}

/** So'rov kanalining standart shablon/ovozi (undefined - o'zgarmaydi). Kanal yo'q bo'lsa false (umumiy standart saqlansin). */
export async function setActiveDefaults(d: ChannelDefaults): Promise<boolean> {
  return setChannelDefaults(await activeChannelId(), d);
}

/** Berilgan kanalning standart shablon/ovozi (undefined - o'zgarmaydi). Kanal ulanmagan bo'lsa false. */
export async function setChannelDefaults(ch: string, d: ChannelDefaults): Promise<boolean> {
  if (!ch || !(await channelList()).channels.some((c) => c.id === ch)) return false;
  await update((s) => {
    const cur = { ...(s.defaults?.[ch] ?? {}) };
    if (d.template !== undefined) cur.template = d.template;
    if (d.voice !== undefined) cur.voice = d.voice;
    s.defaults = { ...(s.defaults ?? {}), [ch]: cur };
  });
  return true;
}

/** Kanal ma'lumotlari o'chirilganda: kanal standartlari (biriktirmalar qoladi - shablon/ovozning o'zi o'chmaydi). */
export async function forgetChannelAssetDefaults(channel: string): Promise<void> {
  await update((s) => { if (s.defaults) delete s.defaults[channel]; });
}

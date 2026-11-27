import "server-only";
import crypto from "node:crypto";
import { getConfig, getYouTubeClient } from "./config";
import { currentAccountId, scopedChannel, sql } from "./db";
import { isDemo } from "./demo";
import { BLOCKS_KEY, blocksKey } from "./channel/keys";
import { Status } from "./types";

/**
 * YouTube kanallarini bot orqali ulash (Google OAuth, "Web application" client, authorization code oqimi).
 *
 * Bir hisobga bir nechta kanal ulanadi; bittasi FAOL (Sozlamalar → YouTube'da almashtiriladi). Ro'yxat va refresh tokenlar online
 * bazada (shared_settings['youtube.channels']): desktop ilova va barcha kompyuterlar shu tokenlardan foydalanadi.
 *  - Yangi mavzu yaratilgan paytdagi faol kanalga bog'lanadi (jobs.channel_id): kanal keyin almashtirilsa ham video o'z kanaliga yuklanadi.
 *  - Mavzular ro'yxati, kanal tahlili va tavsiyalar faol kanal bo'yicha ko'rsatiladi.
 *  - shared_settings['youtube.oauth'] va ['channel.context'] - faol kanal nusxasi (eski desktop versiyalari shularni o'qiydi).
 *
 * OAuth client: hisob o'zinikini kiritgan bo'lsa - o'shanisi; aks holda serverning UMUMIY client'i (YOUTUBE_CLIENT_ID/SECRET env),
 * ya'ni har kim Google Cloud loyihasisiz o'z kanalini ulaydi. Har kanal qaysi client bilan ulanganini eslaydi (refresh token faqat
 * o'sha client bilan ishlaydi). Umumiy secret foydalanuvchiga (brauzer yoki kompyuter) berilmaydi: kompyuter access token'ni
 * botdan oladi (/api/device/youtube-token).
 */
const KEY = "youtube.oauth";
const LIST_KEY = "youtube.channels";
// youtube.force-ssl: Claude Code (MCP) orqali video nomi/tavsifi/holatini tahrirlash va videoni o'chirish uchun.
const MANAGE_SCOPE = "https://www.googleapis.com/auth/youtube.force-ssl";
const SCOPES = ["https://www.googleapis.com/auth/youtube.upload", "https://www.googleapis.com/auth/youtube.readonly", MANAGE_SCOPE];
const STATE_TTL_MS = 15 * 60 * 1000;
export const MAX_CHANNELS = 10;
/** Kanal id'si aniqlanmagan eski ulanish (token bekor qilingan bo'lsa): shu kanal qayta ulanganda haqiqiy id bilan almashtiriladi. */
const LEGACY_ID = "legacy";

/** client: qaysi OAuth client bilan ulangan; yo'q (eski yozuv) = "own". */
export type YtChannel = { id: string; title: string; refreshToken: string; scope: string; connectedAt: string; /** Google ruxsatni bekor qilgan/muddati tugagan (tekshiruvda aniqlangan); qayta ulansa tozalanadi. */ expired?: boolean; client?: ClientKind };
type ChannelList = { active: string; channels: YtChannel[] };
type ClientKind = "own" | "global";
type Client = { clientId: string; clientSecret: string; kind: ClientKind };

/** Serverning umumiy OAuth client'i (Vercel env). Yo'q bo'lsa null. */
export function globalClient(): Client | null {
  const clientId = process.env.YOUTUBE_CLIENT_ID?.trim() ?? "", clientSecret = process.env.YOUTUBE_CLIENT_SECRET?.trim() ?? "";
  return clientId && clientSecret ? { clientId, clientSecret, kind: "global" } : null;
}

/** Yangi ulash uchun: hisobning o'z client'i (kiritilgan bo'lsa), aks holda umumiy. */
async function connectClient(): Promise<Client | null> {
  const own = await getYouTubeClient();
  return own.clientId && own.clientSecret ? { ...own, kind: "own" } : globalClient();
}

/** Kanal qaysi client bilan ulangan bo'lsa - o'sha (token faqat u bilan yangilanadi). */
async function clientOf(ch: Pick<YtChannel, "client">): Promise<Client | null> {
  if (ch.client === "global") return globalClient();
  const own = await getYouTubeClient();
  return own.clientId && own.clientSecret ? { ...own, kind: "own" } : null;
}

/** Kanalning standart teglari (vergul bilan) va tavsifi: shu kanal videolariga qo'yiladi; bo'sh bo'lsa Gemini yozadi. */
export type ChannelDefaults = { tags: string; description: string };
export type YouTubeChannelInfo = { id: string; title: string; connectedAt: string; active: boolean; pending: number; expired: boolean; defaults: ChannelDefaults };
export type YouTubeStatus = {
  connected: boolean; channelId: string; channelTitle: string; connectedAt: string | null;
  channels: YouTubeChannelInfo[]; maxChannels: number;
  clientConfigured: boolean; redirectUri: string;
  /** Server umumiy OAuth client'i bor: foydalanuvchi Client ID/Secret kiritmasdan ulay oladi. */
  globalClient: boolean;
};

/** So'rov kelgan manzil (Vercel/proxy ortida ham): redirect_uri shundan quriladi va Google konsolida ro'yxatdan o'tgan bo'lishi kerak. */
export function originOf(req: Request): string {
  const h = req.headers;
  const host = h.get("x-forwarded-host") ?? h.get("host");
  const proto = h.get("x-forwarded-proto") ?? (host?.startsWith("localhost") || host?.startsWith("127.") ? "http" : "https");
  return host ? `${proto}://${host}` : new URL(req.url).origin;
}
const redirectOf = (origin: string) => `${origin}/api/youtube/callback`;

const hmac = (p: string) => crypto.createHmac("sha256", process.env.TELEGRAM_BOT_TOKEN || "ytauto-dev").update(p).digest("base64url");

/** OAuth state: vaqt, tasodifiy qiymat va HISOB (Google qaytarganda token aynan shu foydalanuvchi hisobiga yoziladi), imzolangan. */
export function makeState(accountId: string): string {
  const p = `${Date.now()}.${crypto.randomBytes(8).toString("hex")}.${accountId}`;
  return `${p}.${hmac(p)}`;
}

/** To'g'ri va eskirmagan bo'lsa hisob id'sini qaytaradi. */
export function checkState(state: string | null): string | null {
  if (!state) return null;
  const i = state.lastIndexOf(".");
  if (i < 0) return null;
  const p = state.slice(0, i), sig = state.slice(i + 1);
  const a = Buffer.from(sig), b = Buffer.from(hmac(p));
  if (a.length !== b.length || !crypto.timingSafeEqual(a, b)) return null;
  const [tsRaw, , acc] = p.split(".");
  const ts = Number(tsRaw);
  return Number.isFinite(ts) && Date.now() - ts >= 0 && Date.now() - ts < STATE_TTL_MS && acc ? acc : null;
}

// ------------------------------------------------------------------ kanallar ro'yxati
/** DEMO_MODE: kanal almashtirish dizaynini bazasiz ko'rish uchun ikkita namunaviy kanal (tokenlar soxta). */
let demoList: ChannelList = {
  active: "demo-tech",
  channels: [
    { id: "demo-tech", title: "Tech Explained Simply", refreshToken: "demo", scope: "", connectedAt: "2026-11-01T10:00:00.000Z" },
    { id: "demo-history", title: "History in 60 Seconds", refreshToken: "demo", scope: "", connectedAt: "2026-11-15T10:00:00.000Z" },
  ],
};

async function getSetting(key: string): Promise<string | null> {
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${key}`;
  return row ? (row.value as string) : null;
}
async function putSetting(key: string, value: string | null) {
  if (value === null) { await sql()`DELETE FROM ytauto.shared_settings WHERE key = ${key}`; return; }
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${key}, ${value}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

function parseList(raw: string | null): ChannelList | null {
  if (!raw) return null;
  try {
    const j = JSON.parse(raw) as Partial<ChannelList>;
    const channels = (Array.isArray(j.channels) ? j.channels : [])
      .filter((c): c is YtChannel => !!c && typeof c.id === "string" && !!c.id && typeof c.refreshToken === "string" && !!c.refreshToken)
      .map((c) => ({ id: c.id, title: String(c.title ?? ""), refreshToken: c.refreshToken, scope: String(c.scope ?? ""), connectedAt: String(c.connectedAt ?? ""), ...(c.expired ? { expired: true } : {}), ...(c.client === "global" ? { client: "global" as const } : {}) }));
    const active = channels.some((c) => c.id === j.active) ? String(j.active) : channels[0]?.id ?? "";
    return { active, channels };
  } catch { return null; }
}

/** Ro'yxatni o'zgartiruvchi amallar bir hisob ichida navbat bilan (ikki ulanish bir vaqtda kelsa biri yo'qolmaydi). */
async function lockList() {
  await sql()`SELECT pg_advisory_xact_lock(hashtext(${"ytauto.youtube.channels:" + currentAccountId()}))`;
}

/**
 * Ro'yxatni yozadi va faol kanal nusxalarini yangilaydi: youtube.oauth (refresh token) va channel.context (kanal konteksti).
 * Eski desktop versiyalari faqat shu nusxalarni o'qiydi.
 */
async function writeList(l: ChannelList) {
  if (isDemo()) { demoList = l; return; }
  const a = l.channels.find((c) => c.id === l.active);
  // Uchala yozuv bir-biriga bog'liq emas - birga yuboriladi (pipelining, bitta tranzaksiya ichida).
  await Promise.all([
    putSetting(LIST_KEY, JSON.stringify(l)),
    putSetting(KEY, a ? JSON.stringify({ refreshToken: a.refreshToken, channelTitle: a.title, channelId: a.id, scope: a.scope, connectedAt: a.connectedAt, client: a.client ?? "own" }) : null),
    (a ? getSetting(blocksKey(a.id)) : Promise.resolve(null)).then((v) => putSetting(BLOCKS_KEY, v)),
  ]);
}

/** Kanal id'si va nomi (refresh token bilan). Token yaroqsiz bo'lsa null. */
async function lookupChannel(refreshToken: string): Promise<{ id: string; title: string } | null> {
  try {
    const c = await clientOf({}); // eski (v6 dan oldingi) ulanish - hisobning o'z client'i
    if (!c) return null;
    const token = await youtubeAccessToken({ refreshToken, clientId: c.clientId, clientSecret: c.clientSecret });
    const res = await fetch("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", { headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(15_000) });
    const j = (await res.json().catch(() => ({}))) as { items?: { id?: string; snippet?: { title?: string } }[] };
    const it = j.items?.[0];
    return it?.id ? { id: it.id, title: it.snippet?.title ?? "" } : null;
  } catch { return null; }
}

/** Shu hisob vazifalari, tavsiyalari va kanal tahlilini (kanal tanlanmagan, '') berilgan kanalga bog'laydi. */
async function bindUnassigned(id: string, from = "") {
  await sql()`UPDATE ytauto.jobs SET channel_id = ${id} WHERE channel_id = ${from}`;
  await sql()`UPDATE ytauto.topic_suggestions SET channel_id = ${id} WHERE channel_id = ${from}`;
  const [taken] = await sql()`SELECT 1 FROM ytauto.channel_profile WHERE channel_id = ${id}`;
  if (taken) await sql()`DELETE FROM ytauto.channel_profile WHERE channel_id = ${from}`;
  else await sql()`UPDATE ytauto.channel_profile SET channel_id = ${id} WHERE channel_id = ${from}`;
}

/**
 * Bitta ulanishli (v6 dan oldingi) hisobni ro'yxatga o'tkazadi: youtube.oauth dagi kanal birinchi (faol) kanal bo'ladi, mavjud
 * vazifalar, tavsiyalar va kanal konteksti unga bog'lanadi. Bir marta.
 */
async function migrateLegacy(): Promise<ChannelList> {
  await lockList();
  const again = parseList(await getSetting(LIST_KEY));
  if (again) return again;
  let old: { refreshToken?: string; channelTitle?: string; scope?: string; connectedAt?: string } | null = null;
  try { old = JSON.parse((await getSetting(KEY)) ?? "null"); } catch { old = null; }
  if (!old?.refreshToken) return { active: "", channels: [] };
  const [p] = await sql()`SELECT channel_id FROM ytauto.channel_profile WHERE channel_id <> '' ORDER BY updated_at_utc DESC LIMIT 1`;
  const found = p ? { id: p.channel_id as string, title: "" } : await lookupChannel(old.refreshToken);
  const id = found?.id || LEGACY_ID;
  const l: ChannelList = { active: id, channels: [{ id, title: old.channelTitle || found?.title || "", refreshToken: old.refreshToken, scope: old.scope ?? "", connectedAt: old.connectedAt ?? "" }] };
  await bindUnassigned(id);
  const ctx = await getSetting(BLOCKS_KEY);
  if (ctx && !(await getSetting(blocksKey(id)))) await putSetting(blocksKey(id), ctx);
  await writeList(l);
  return l;
}

/** Ulangan kanallar va faol kanal (hisob konteksti ichida). */
export async function channelList(): Promise<ChannelList> {
  if (isDemo()) return demoList;
  return parseList(await getSetting(LIST_KEY)) ?? (await getSetting(KEY) ? migrateLegacy() : { active: "", channels: [] });
}

/** Faol kanal id'si ('' - kanal ulanmagan). Yangi mavzular shu kanalga bog'lanadi; ro'yxatlar shu kanal bo'yicha. */
export async function activeChannelId(): Promise<string> {
  const ch = scopedChannel(); // kanal MCP'si: so'rov shu kanalga bog'langan
  return ch ?? (await channelList()).active;
}

async function activeChannel(): Promise<YtChannel | null> {
  const l = await channelList();
  const id = scopedChannel() ?? l.active;
  return l.channels.find((c) => c.id === id) ?? null;
}

/** Kanal MCP'si uchun: kanal shu hisobga ulanganmi (id va nomi) - yo'q bo'lsa null. */
export async function findChannel(id: string): Promise<{ id: string; title: string } | null> {
  const c = (await channelList()).channels.find((x) => x.id === id);
  return c ? { id: c.id, title: c.title } : null;
}

// ------------------------------------------------------------------ kanal standart teglari va tavsifi
/**
 * Har kanalning o'z standart teglari va tavsifi: shared_settings['youtube.defaults:<kanal id>'] = {tags, description}.
 * Desktop worker vazifa qaysi kanalniki bo'lsa, o'shaning qiymatini oladi (yo'q bo'lsa - hisob sozlamasidagi umumiy qiymat).
 */
const defaultsKey = (id: string) => `youtube.defaults:${id}`;
const DEFAULTS_MIGRATED = "youtube.defaults.migrated";
export const CHANNEL_TAGS_MAX = 500;
export const CHANNEL_DESC_MAX = 2000;
const EMPTY_DEFAULTS: ChannelDefaults = { tags: "", description: "" };
const demoDefaults = new Map<string, ChannelDefaults>();

function parseDefaults(raw: string | null): ChannelDefaults | null {
  if (!raw) return null;
  try {
    const j = JSON.parse(raw) as Partial<ChannelDefaults>;
    return { tags: String(j.tags ?? ""), description: String(j.description ?? "") };
  } catch { return null; }
}

async function readDefaults(id: string): Promise<ChannelDefaults | null> {
  if (isDemo()) return demoDefaults.get(id) ?? null;
  return parseDefaults(await getSetting(defaultsKey(id)));
}

async function writeDefaults(id: string, d: ChannelDefaults) {
  if (isDemo()) { demoDefaults.set(id, d); return; }
  await putSetting(defaultsKey(id), JSON.stringify(d));
}

/**
 * Bir martalik: kanal bo'yicha qiymatlar paydo bo'lishidan oldingi umumiy (hisob) teglar va tavsif hozir ulangan kanallarning har
 * biriga ko'chiriladi - mavjud xatti-harakat o'zgarmaydi. Shundan keyin ulangan yangi kanal bo'sh boshlanadi (boshqa kanal
 * teglari unga o'tmaydi).
 */
async function migrateDefaults(l: ChannelList) {
  if (isDemo() || (await getSetting(DEFAULTS_MIGRATED))) return;
  const y = (await getConfig()).youtube;
  for (const c of l.channels) if (!(await readDefaults(c.id))) await writeDefaults(c.id, { tags: y.defaultTags ?? "", description: y.defaultDescription ?? "" });
  await putSetting(DEFAULTS_MIGRATED, new Date().toISOString());
}

/** Teglar: vergul bilan, bo'sh va takrorlar (katta-kichik harf farqisiz) olib tashlanadi; YouTube cheklovi - jami 500 belgi. */
function cleanTags(v: string): string {
  const seen = new Set<string>(), out: string[] = [];
  for (const t of v.split(",")) {
    const x = t.replace(/\s+/g, " ").trim().slice(0, 100);
    if (x && !seen.has(x.toLowerCase())) { seen.add(x.toLowerCase()); out.push(x); }
  }
  return out.join(", ");
}

/** Kanalning standart teglari va tavsifini saqlaydi (faqat shu hisobga ulangan kanal). */
export async function setChannelDefaults(id: string, d: Partial<ChannelDefaults>): Promise<ChannelDefaults> {
  const l = await channelList();
  if (!l.channels.some((x) => x.id === id)) throw new Error("Kanal topilmadi — ro'yxatni yangilang.");
  await migrateDefaults(l);
  const cur = (await readDefaults(id)) ?? EMPTY_DEFAULTS;
  const tags = d.tags !== undefined ? cleanTags(String(d.tags)) : cur.tags;
  const description = d.description !== undefined ? String(d.description).replace(/\r\n/g, "\n").trim() : cur.description;
  if (tags.length > CHANNEL_TAGS_MAX) throw new Error(`Teglar birga ${CHANNEL_TAGS_MAX} belgidan oshmasin (YouTube cheklovi).`);
  if (description.length > CHANNEL_DESC_MAX) throw new Error(`Tavsif ${CHANNEL_DESC_MAX} belgidan oshmasin.`);
  if (/[<>]/.test(tags) || /[<>]/.test(description)) throw new Error("Teglar va tavsifda < yoki > bo'lmasligi kerak (YouTube rad etadi).");
  const next = { tags, description };
  await writeDefaults(id, next);
  return next;
}

/** Kanal (id; bo'sh - faol/bog'langan kanal) standart teglari va tavsifi. Hali yozilmagan bo'lsa - hisobning umumiy qiymati. */
export async function channelDefaults(id = ""): Promise<ChannelDefaults> {
  const ch = id || (await activeChannelId());
  const d = ch ? await readDefaults(ch) : null;
  if (d) return d;
  const y = (await getConfig()).youtube;
  return { tags: y.defaultTags ?? "", description: y.defaultDescription ?? "" };
}

/** Hamma kanallarning teg/tavsifi bitta so'rovda (kanal soniga qarab N ta so'rov emas). Ko'chirish kerak bo'lsa - bir marta. */
async function defaultsFor(l: ChannelList): Promise<Map<string, ChannelDefaults>> {
  const out = new Map<string, ChannelDefaults>();
  if (!l.channels.length) return out;
  if (isDemo()) { for (const c of l.channels) out.set(c.id, await channelDefaults(c.id)); return out; }
  const read = async () => new Map((await sql()`SELECT key, value FROM ytauto.shared_settings
    WHERE key IN ${sql()([DEFAULTS_MIGRATED, ...l.channels.map((c) => defaultsKey(c.id))])}`).map((r) => [r.key as string, r.value as string]));
  let kv = await read();
  if (!kv.has(DEFAULTS_MIGRATED)) { await migrateDefaults(l); kv = await read(); }
  let account: ChannelDefaults | null = null;
  for (const c of l.channels) {
    let d = parseDefaults(kv.get(defaultsKey(c.id)) ?? null);
    if (!d) { account ??= await channelDefaults("__none__"); d = account; }
    out.set(c.id, d);
  }
  return out;
}

export async function getYouTubeStatus(origin: string): Promise<YouTubeStatus> {
  const [l, c] = await Promise.all([channelList(), connectClient()]);
  // Teg/tavsif va navbatdagi videolar soni birga (pipelining).
  const [defaults, rows] = await Promise.all([
    defaultsFor(l),
    !isDemo() && l.channels.length ? sql()`SELECT channel_id, count(*)::int AS n FROM ytauto.jobs
      WHERE youtube_video_id IS NULL AND status NOT IN ${sql()([Status.Published, Status.Cancelled])} GROUP BY channel_id` : Promise.resolve([]),
  ]);
  const pending = new Map<string, number>();
  for (const r of rows) pending.set(r.channel_id as string, r.n as number);
  const a = l.channels.find((x) => x.id === l.active);
  return {
    connected: !!a, channelId: a?.id ?? "", channelTitle: a?.title ?? "", connectedAt: a?.connectedAt || null,
    channels: l.channels.map((x) => ({ id: x.id, title: x.title, connectedAt: x.connectedAt, active: x.id === l.active, pending: pending.get(x.id) ?? 0, expired: x.expired === true, defaults: defaults.get(x.id) ?? EMPTY_DEFAULTS })),
    maxChannels: MAX_CHANNELS,
    clientConfigured: !!c, redirectUri: redirectOf(origin), globalClient: !!globalClient(),
  };
}

export async function startYouTube(origin: string): Promise<{ url: string }> {
  const c = await connectClient();
  if (!c) throw new Error("YouTube ulash hali sozlanmagan: server administratori YOUTUBE_CLIENT_ID/SECRET ni kiritishi kerak (yoki Sozlamalar → YouTube'da o'z Client ID va Secret'ingizni saqlang).");
  if (isDemo()) throw new Error("Demo rejimda YouTube ulanmaydi.");
  // select_account: boshqa Google hisobi yoki brend kanalini tanlash oynasi har doim chiqadi (ikkinchi kanalni ulash uchun).
  const q = new URLSearchParams({
    response_type: "code", client_id: c.clientId, redirect_uri: redirectOf(origin), scope: SCOPES.join(" "),
    access_type: "offline", prompt: "consent select_account", include_granted_scopes: "true", state: makeState(currentAccountId()),
  });
  return { url: `https://accounts.google.com/o/oauth2/v2/auth?${q}` };
}

/**
 * Google qaytargan code'ni tokenga almashtiradi, kanal id/nomini oladi va ro'yxatga qo'shadi (yoki tokenini yangilaydi).
 * Yangi ulangan kanal faol bo'ladi.
 */
export async function finishYouTube(code: string, origin: string): Promise<string> {
  const c = await connectClient();
  if (!c) throw new Error("YouTube OAuth client sozlanmagan.");
  const res = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ code, client_id: c.clientId, client_secret: c.clientSecret, redirect_uri: redirectOf(origin), grant_type: "authorization_code" }),
  });
  const j = (await res.json().catch(() => ({}))) as { access_token?: string; refresh_token?: string; scope?: string; error?: string; error_description?: string };
  if (!res.ok || !j.access_token) throw new Error(`Google token bermadi: ${j.error_description ?? j.error ?? res.status}`);
  if (!j.refresh_token) throw new Error("Google refresh_token bermadi. Google hisobida YtAuto ruxsatini olib tashlab, qayta ulang.");
  const ch = await fetch("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", { headers: { Authorization: `Bearer ${j.access_token}` } });
  const cj = (await ch.json().catch(() => ({}))) as { items?: { id?: string; snippet?: { title?: string } }[] };
  const id = cj.items?.[0]?.id, title = cj.items?.[0]?.snippet?.title;
  if (!id || !title) throw new Error("Bu Google hisobida YouTube kanali topilmadi.");
  const entry: YtChannel = { id, title, refreshToken: j.refresh_token, scope: j.scope ?? "", connectedAt: new Date().toISOString(), ...(c.kind === "global" ? { client: "global" as const } : {}) };

  await channelList(); // eski bitta ulanish bo'lsa avval ro'yxatga o'tkaziladi
  await lockList();
  const l = parseList(await getSetting(LIST_KEY)) ?? { active: "", channels: [] };
  await migrateDefaults(l); // mavjud kanallar umumiy teg/tavsifni oladi - yangi kanal esa bo'sh boshlanadi (pastda)
  // Id'si aniqlanmagan eski ulanish shu kanal bo'lsa (nomi bir xil) - uning vazifalari shu kanalga o'tadi.
  const legacy = l.channels.find((x) => x.id === LEGACY_ID && x.title === title);
  if (legacy) {
    await bindUnassigned(id, LEGACY_ID); l.channels = l.channels.filter((x) => x !== legacy);
    const ld = await readDefaults(LEGACY_ID);
    if (ld && !(await readDefaults(id))) await writeDefaults(id, ld);
  }
  if (!(await readDefaults(id))) await writeDefaults(id, EMPTY_DEFAULTS);
  const i = l.channels.findIndex((x) => x.id === id);
  if (i >= 0) l.channels[i] = entry;
  else {
    if (l.channels.length >= MAX_CHANNELS) throw new Error(`Eng ko'pi ${MAX_CHANNELS} ta kanal ulanadi — avval keraksizini uzing.`);
    l.channels.push(entry);
  }
  // Hali hech qaysi kanalga bog'lanmagan vazifalar (kanal ulanmasdan qo'shilgan) birinchi ulangan kanalga.
  if (l.channels.length === 1) await bindUnassigned(id);
  await writeList({ active: id, channels: l.channels });
  return title;
}

/** Faol kanalni almashtiradi: mavzular, kanal tahlili va yangi vazifalar endi shu kanal bo'yicha. */
export async function switchChannel(id: string): Promise<string> {
  if (isDemo()) {
    const c = demoList.channels.find((x) => x.id === id);
    if (!c) throw new Error("Kanal topilmadi.");
    demoList = { ...demoList, active: id };
    return c.title;
  }
  await channelList();
  await lockList();
  const l = parseList(await getSetting(LIST_KEY));
  const c = l?.channels.find((x) => x.id === id);
  if (!l || !c) throw new Error("Kanal topilmadi — ro'yxatni yangilang.");
  if (l.active !== id) await writeList({ ...l, active: id });
  return c.title;
}

/**
 * Kanalni uzadi. Shu kanalga bog'langan, hali yuklanmagan videolar yuklanmaydi ("E'tibor kerak") - kanal qayta ulansa davom etadi.
 * Faol kanal uzilsa ro'yxatdagi keyingisi faol bo'ladi.
 */
export async function removeChannel(id: string): Promise<void> {
  if (isDemo()) {
    const channels = demoList.channels.filter((x) => x.id !== id);
    demoList = { active: demoList.active === id ? channels[0]?.id ?? "" : demoList.active, channels };
    return;
  }
  await channelList();
  await lockList();
  const l = parseList(await getSetting(LIST_KEY));
  if (!l || !l.channels.some((x) => x.id === id)) return;
  const channels = l.channels.filter((x) => x.id !== id);
  await writeList({ active: l.active === id ? channels[0]?.id ?? "" : l.active, channels });
}

/** Faol kanalni uzadi (eski API). */
export async function disconnectYouTube() {
  const a = await activeChannelId();
  if (a) await removeChannel(a);
}

type Creds = { refreshToken: string; clientId: string; clientSecret: string; channelTitle: string; channelId: string };

/** Faqat server ichida (hisob konteksti): fon ishi tranzaksiyadan tashqarida token olishi uchun. Faol kanal ulanmagan bo'lsa null. */
export async function youtubeCredentials(): Promise<Creds | null> {
  return credsOf(await activeChannel());
}

/** Berilgan kanal (id; bo'sh - faol kanal) tokeni uchun: kompyuter vazifa qaysi kanalniki bo'lsa, o'shaning tokenini oladi. */
export async function youtubeCredentialsFor(channelId: string): Promise<Creds | null> {
  if (!channelId) return youtubeCredentials();
  const l = await channelList();
  return credsOf(l.channels.find((x) => x.id === channelId) ?? null);
}

async function credsOf(a: YtChannel | null): Promise<Creds | null> {
  const c = a ? await clientOf(a) : null;
  if (!a || !c) return null;
  return { refreshToken: a.refreshToken, clientId: c.clientId, clientSecret: c.clientSecret, channelTitle: a.title, channelId: a.id };
}

/** Faol kanal ruxsati videolarni tahrirlash/o'chirishga yetadimi (eski ulanishlarda faqat upload+readonly bo'ladi). */
export async function youtubeCanManage(): Promise<boolean> {
  const a = await activeChannel();
  return !!a && a.scope.split(/\s+/).some((x) => x === MANAGE_SCOPE || x === "https://www.googleapis.com/auth/youtube");
}

/**
 * Faol kanal telefon raqami bilan tasdiqlanganmi: YouTube maxsus thumbnail'ni (muqova) faqat shunday kanalda qabul qiladi.
 * channels.status.longUploadsStatus = "allowed" - tasdiqlangan kanal belgisi. Natija kanal bo'yicha 6 soat keshlanadi
 * (shared_settings); YouTube javob bermasa oxirgi ma'lum qiymat (yo'q bo'lsa - false).
 */
export async function customThumbnails(): Promise<boolean> {
  if (isDemo()) return true;
  const a = await activeChannel();
  if (!a) return false;
  const key = `youtube.verified.${a.id}`;
  let cached: { v: boolean; at: number } | null = null;
  try { cached = JSON.parse((await getSetting(key)) ?? "null"); } catch { cached = null; }
  if (cached && Date.now() - cached.at < 6 * 3600_000) return cached.v;
  try {
    const c = await youtubeCredentials();
    if (!c) return cached?.v ?? false;
    const token = await youtubeAccessToken(c);
    const r = await fetch("https://www.googleapis.com/youtube/v3/channels?part=status&mine=true", { headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(6000) });
    const j = (await r.json().catch(() => ({}))) as { items?: { status?: { longUploadsStatus?: string } }[] };
    if (!r.ok) return cached?.v ?? false;
    const v = j.items?.[0]?.status?.longUploadsStatus === "allowed";
    await putSetting(key, JSON.stringify({ v, at: Date.now() }));
    return v;
  } catch { return cached?.v ?? false; }
}

/** Refresh token → access token (tarmoq; bazaga tegmaydi). */
export async function youtubeAccessToken(c: { refreshToken: string; clientId: string; clientSecret: string }): Promise<string> {
  const res = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ client_id: c.clientId, client_secret: c.clientSecret, refresh_token: c.refreshToken, grant_type: "refresh_token" }),
    signal: AbortSignal.timeout(20_000),
  });
  const j = (await res.json().catch(() => ({}))) as { access_token?: string; error?: string; error_description?: string };
  if (!res.ok || !j.access_token)
    throw new Error(j.error === "invalid_grant" ? "YouTube ruxsati bekor qilingan yoki muddati tugagan — kanalni qayta ulang." : `YouTube token yangilanmadi: ${j.error_description ?? j.error ?? res.status}`);
  return j.access_token;
}

/** Kanalning «token eskirgan» belgisini yangilaydi (o'zgarmasa yozmaydi). */
async function markExpired(id: string, expired: boolean) {
  await lockList();
  const l = await channelList();
  const c = l.channels.find((x) => x.id === id);
  if (!c || (c.expired === true) === expired) return;
  if (expired) c.expired = true; else delete c.expired;
  await writeList(l);
}

/**
 * Ulanishni HAQIQATAN tekshiradi (faqat bazadagi belgiga ishonmaydi): saqlangan refresh token bilan Google'dan access token oladi va
 * kanal nomini so'raydi. Token bekor qilingan/muddati o'tgan (masalan Google "Testing" rejimida 7 kundan keyin) bo'lsa sababini aytadi.
 * id berilmasa - faol kanal.
 */
export async function verifyYouTube(id?: string): Promise<{ ok: boolean; channelTitle?: string; message: string }> {
  const l = await channelList();
  const s = l.channels.find((x) => x.id === (id || scopedChannel() || l.active));
  if (!s?.refreshToken) return { ok: false, message: "Kanal ulanmagan: «YouTube'ni ulash» ni bosing." };
  const c = await clientOf(s);
  if (!c) return { ok: false, message: s.client === "global" ? "Server umumiy OAuth client'i o'chirilgan — administratorga murojaat qiling." : "OAuth Client ID/Secret saqlanmagan (Sozlamalar → YouTube)." };
  let res: Response;
  try {
    res = await fetch("https://oauth2.googleapis.com/token", {
      method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ client_id: c.clientId, client_secret: c.clientSecret, refresh_token: s.refreshToken, grant_type: "refresh_token" }),
    });
  } catch (e) { return { ok: false, message: `Google bilan aloqa yo'q: ${e instanceof Error ? e.message : String(e)}` }; }
  const j = (await res.json().catch(() => ({}))) as { access_token?: string; error?: string; error_description?: string };
  if (!res.ok || !j.access_token) {
    const why = j.error === "invalid_grant"
      ? "ruxsat bekor qilingan yoki muddati tugagan (Google Cloud'da ilova «Testing» rejimida bo'lsa token 7 kunda tugaydi) — qayta ulang"
      : j.error === "invalid_client" ? "Client ID/Secret noto'g'ri" : (j.error_description ?? j.error ?? `HTTP ${res.status}`);
    if (j.error === "invalid_grant") await markExpired(s.id, true);
    return { ok: false, message: `«${s.title}»: token yangilanmadi: ${why}.` };
  }
  await markExpired(s.id, false);
  const ch = await fetch("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", { headers: { Authorization: `Bearer ${j.access_token}` } });
  const cj = (await ch.json().catch(() => ({}))) as { items?: { id?: string; snippet?: { title?: string } }[]; error?: { message?: string } };
  if (!ch.ok) return { ok: false, message: `YouTube API xatosi: ${cj.error?.message ?? ch.status}` };
  const title = cj.items?.[0]?.snippet?.title;
  return title ? { ok: true, channelTitle: title, message: `Ulangan va ishlayapti: ${title}` } : { ok: false, message: "Token ishlaydi, lekin bu Google hisobida YouTube kanali topilmadi." };
}

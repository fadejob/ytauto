import "server-only";
import crypto from "node:crypto";
import { getYouTubeClient } from "./config";
import { currentAccountId, sql } from "./db";
import { isDemo } from "./demo";
import { readProfile, resetChannelData } from "./channel/store";

/**
 * YouTube kanalini bot orqali ulash (Google OAuth, "Web application" client, authorization code oqimi).
 * Refresh token online bazada (shared_settings.youtube.oauth) saqlanadi: desktop ilova va barcha kompyuterlar
 * o'sha tokendan foydalanadi (alohida kompyuterda qayta ulash shart emas).
 *
 * OAuth client: hisob o'zinikini kiritgan bo'lsa - o'shanisi; aks holda serverning UMUMIY client'i (YOUTUBE_CLIENT_ID/SECRET env),
 * ya'ni har kim Google Cloud loyihasisiz o'z kanalini ulaydi. Token har hisobda alohida (RLS). Umumiy secret foydalanuvchiga
 * (brauzer yoki kompyuter) hech qachon berilmaydi: kompyuter access token'ni botdan oladi (/api/device/youtube-token).
 */
const KEY = "youtube.oauth";
// youtube.force-ssl: Claude Code (MCP) orqali video nomi/tavsifi/holatini tahrirlash va videoni o'chirish uchun.
const MANAGE_SCOPE = "https://www.googleapis.com/auth/youtube.force-ssl";
const SCOPES = ["https://www.googleapis.com/auth/youtube.upload", "https://www.googleapis.com/auth/youtube.readonly", MANAGE_SCOPE];
const STATE_TTL_MS = 15 * 60 * 1000;

export type YouTubeStatus = {
  connected: boolean; channelTitle: string; connectedAt: string | null;
  clientConfigured: boolean; redirectUri: string;
  /** Server umumiy OAuth client'i bor: foydalanuvchi Client ID/Secret kiritmasdan ulay oladi. */
  globalClient: boolean;
};

/** client: qaysi OAuth client bilan ulangan (refresh token faqat o'sha client bilan ishlaydi). Eski yozuvlarda yo'q = "own". */
type Stored = { refreshToken: string; channelTitle: string; channelId?: string; scope: string; connectedAt: string; client?: ClientKind };
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

/** Saqlangan token qaysi client bilan olingan bo'lsa - o'sha. */
async function clientOf(s: Stored): Promise<Client | null> {
  if (s.client === "global") return globalClient();
  const own = await getYouTubeClient();
  return own.clientId && own.clientSecret ? { ...own, kind: "own" } : null;
}

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

let demoStored: Stored | null = null;

async function readStored(): Promise<Stored | null> {
  if (isDemo()) return demoStored;
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  try { return row ? (JSON.parse(row.value as string) as Stored) : null; } catch { return null; }
}

export async function getYouTubeStatus(origin: string): Promise<YouTubeStatus> {
  const [s, c] = await Promise.all([readStored(), connectClient()]);
  return {
    connected: !!s?.refreshToken, channelTitle: s?.channelTitle ?? "", connectedAt: s?.connectedAt ?? null,
    clientConfigured: !!c, redirectUri: redirectOf(origin), globalClient: !!globalClient(),
  };
}

export async function startYouTube(origin: string): Promise<{ url: string }> {
  const c = await connectClient();
  if (!c) throw new Error("YouTube ulash hali sozlanmagan: server administratori YOUTUBE_CLIENT_ID/SECRET ni kiritishi kerak (yoki Sozlamalar → YouTube'da o'z Client ID va Secret'ingizni saqlang).");
  if (isDemo()) throw new Error("Demo rejimda YouTube ulanmaydi.");
  const q = new URLSearchParams({
    response_type: "code", client_id: c.clientId, redirect_uri: redirectOf(origin), scope: SCOPES.join(" "),
    // select_account: Google har safar akkaunt/kanal (Brand Account ham) tanlash oynasini ko'rsatadi - aks holda brauzerda
    // kirilgan akkaunt jimgina olinadi va foydalanuvchi boshqa kanalini ulay olmaydi. consent: refresh_token har safar beriladi.
    access_type: "offline", prompt: "select_account consent", include_granted_scopes: "true", state: makeState(currentAccountId()),
  });
  return { url: `https://accounts.google.com/o/oauth2/v2/auth?${q}` };
}

/** Google qaytargan code'ni tokenga almashtiradi, kanal nomini oladi va bazaga yozadi. */
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
  const title = cj.items?.[0]?.snippet?.title;
  const channelId = cj.items?.[0]?.id ?? "";
  if (!title) throw new Error("Bu Google hisobida YouTube kanali topilmadi.");
  // Boshqa kanal ulandi: eski kanalning tahlili, promptlarga qo'shiladigan konteksti va tavsiyalari yangi kanalga o'tmasin.
  const [prev, prof] = await Promise.all([readStored(), readProfile()]);
  const prevId = prev?.channelId || prof?.channel_id || "";
  const switched = prevId ? prevId !== channelId : !!prev?.channelTitle && prev.channelTitle !== title;
  if (switched) await resetChannelData();
  const stored: Stored = { refreshToken: j.refresh_token, channelTitle: title, channelId, scope: j.scope ?? "", connectedAt: new Date().toISOString(), client: c.kind };
  await writeStored(stored);
  return title;
}

async function writeStored(s: Stored | null) {
  if (isDemo()) { demoStored = s; return; }
  if (!s) { await sql()`DELETE FROM ytauto.shared_settings WHERE key = ${KEY}`; return; }
  const json = JSON.stringify(s);
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${json}, now())
    ON CONFLICT (account_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

/** Uzish: token va shu kanaldan olingan ma'lumotlar (tahlil, kontekst, tavsiyalar) o'chadi - keyingi kanal toza boshlanadi. */
export async function disconnectYouTube() {
  await writeStored(null);
  if (!isDemo()) await resetChannelData();
}

/** Faqat server ichida (hisob konteksti): fon ishi tranzaksiyadan tashqarida token olishi uchun. Ulanmagan bo'lsa null. */
export async function youtubeCredentials(): Promise<{ refreshToken: string; clientId: string; clientSecret: string; channelTitle: string } | null> {
  const s = await readStored();
  const c = s?.refreshToken ? await clientOf(s) : null;
  if (!s?.refreshToken || !c) return null;
  return { refreshToken: s.refreshToken, clientId: c.clientId, clientSecret: c.clientSecret, channelTitle: s.channelTitle };
}

/** Saqlangan ruxsat videolarni tahrirlash/o'chirishga yetadimi (eski ulanishlarda faqat upload+readonly bo'ladi). */
export async function youtubeCanManage(): Promise<boolean> {
  const s = await readStored();
  return !!s?.refreshToken && (s.scope ?? "").split(/\s+/).some((x) => x === MANAGE_SCOPE || x === "https://www.googleapis.com/auth/youtube");
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

/**
 * Ulanishni HAQIQATAN tekshiradi (faqat bazadagi belgiga ishonmaydi): saqlangan refresh token bilan Google'dan access token oladi va
 * kanal nomini so'raydi. Token bekor qilingan/muddati o'tgan (masalan Google "Testing" rejimida 7 kundan keyin) bo'lsa sababini aytadi.
 */
export async function verifyYouTube(): Promise<{ ok: boolean; channelTitle?: string; message: string }> {
  const s = await readStored();
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
    return { ok: false, message: `Token yangilanmadi: ${why}.` };
  }
  const ch = await fetch("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", { headers: { Authorization: `Bearer ${j.access_token}` } });
  const cj = (await ch.json().catch(() => ({}))) as { items?: { snippet?: { title?: string } }[]; error?: { message?: string } };
  if (!ch.ok) return { ok: false, message: `YouTube API xatosi: ${cj.error?.message ?? ch.status}` };
  const title = cj.items?.[0]?.snippet?.title;
  return title ? { ok: true, channelTitle: title, message: `Ulangan va ishlayapti: ${title}` } : { ok: false, message: "Token ishlaydi, lekin bu Google hisobida YouTube kanali topilmadi." };
}

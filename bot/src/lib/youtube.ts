import "server-only";
import crypto from "node:crypto";
import { getYouTubeClient } from "./config";
import { sql } from "./db";
import { isDemo } from "./demo";

/**
 * YouTube kanalini bot orqali ulash (Google OAuth, "Web application" client, authorization code oqimi).
 * Refresh token online bazada (shared_settings.youtube.oauth) saqlanadi: desktop ilova va barcha kompyuterlar
 * o'sha tokendan foydalanadi (alohida kompyuterda qayta ulash shart emas).
 */
const KEY = "youtube.oauth";
const SCOPES = ["https://www.googleapis.com/auth/youtube.upload", "https://www.googleapis.com/auth/youtube.readonly"];
const STATE_TTL_MS = 15 * 60 * 1000;

export type YouTubeStatus = {
  connected: boolean; channelTitle: string; connectedAt: string | null;
  clientConfigured: boolean; redirectUri: string;
};

type Stored = { refreshToken: string; channelTitle: string; scope: string; connectedAt: string };

/** So'rov kelgan manzil (Vercel/proxy ortida ham): redirect_uri shundan quriladi va Google konsolida ro'yxatdan o'tgan bo'lishi kerak. */
export function originOf(req: Request): string {
  const h = req.headers;
  const host = h.get("x-forwarded-host") ?? h.get("host");
  const proto = h.get("x-forwarded-proto") ?? (host?.startsWith("localhost") || host?.startsWith("127.") ? "http" : "https");
  return host ? `${proto}://${host}` : new URL(req.url).origin;
}
const redirectOf = (origin: string) => `${origin}/api/youtube/callback`;

const hmac = (p: string) => crypto.createHmac("sha256", process.env.TELEGRAM_BOT_TOKEN || "ytauto-dev").update(p).digest("base64url");

export function makeState(): string {
  const p = `${Date.now()}.${crypto.randomBytes(8).toString("hex")}`;
  return `${p}.${hmac(p)}`;
}

export function checkState(state: string | null): boolean {
  if (!state) return false;
  const i = state.lastIndexOf(".");
  if (i < 0) return false;
  const p = state.slice(0, i), sig = state.slice(i + 1);
  const a = Buffer.from(sig), b = Buffer.from(hmac(p));
  if (a.length !== b.length || !crypto.timingSafeEqual(a, b)) return false;
  const ts = Number(p.split(".")[0]);
  return Number.isFinite(ts) && Date.now() - ts >= 0 && Date.now() - ts < STATE_TTL_MS;
}

let demoStored: Stored | null = null;

async function readStored(): Promise<Stored | null> {
  if (isDemo()) return demoStored;
  const [row] = await sql()`SELECT value FROM ytauto.shared_settings WHERE key = ${KEY}`;
  try { return row ? (JSON.parse(row.value as string) as Stored) : null; } catch { return null; }
}

export async function getYouTubeStatus(origin: string): Promise<YouTubeStatus> {
  const [s, c] = await Promise.all([readStored(), getYouTubeClient()]);
  return {
    connected: !!s?.refreshToken, channelTitle: s?.channelTitle ?? "", connectedAt: s?.connectedAt ?? null,
    clientConfigured: !!c.clientId && !!c.clientSecret, redirectUri: redirectOf(origin),
  };
}

export async function startYouTube(origin: string): Promise<{ url: string }> {
  const c = await getYouTubeClient();
  if (!c.clientId || !c.clientSecret) throw new Error("Avval Sozlamalar → YouTube'da OAuth Client ID va Secret'ni saqlang.");
  if (isDemo()) throw new Error("Demo rejimda YouTube ulanmaydi.");
  const q = new URLSearchParams({
    response_type: "code", client_id: c.clientId, redirect_uri: redirectOf(origin), scope: SCOPES.join(" "),
    access_type: "offline", prompt: "consent", include_granted_scopes: "true", state: makeState(),
  });
  return { url: `https://accounts.google.com/o/oauth2/v2/auth?${q}` };
}

/** Google qaytargan code'ni tokenga almashtiradi, kanal nomini oladi va bazaga yozadi. */
export async function finishYouTube(code: string, origin: string): Promise<string> {
  const c = await getYouTubeClient();
  const res = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ code, client_id: c.clientId, client_secret: c.clientSecret, redirect_uri: redirectOf(origin), grant_type: "authorization_code" }),
  });
  const j = (await res.json().catch(() => ({}))) as { access_token?: string; refresh_token?: string; scope?: string; error?: string; error_description?: string };
  if (!res.ok || !j.access_token) throw new Error(`Google token bermadi: ${j.error_description ?? j.error ?? res.status}`);
  if (!j.refresh_token) throw new Error("Google refresh_token bermadi. Google hisobida YtAuto ruxsatini olib tashlab, qayta ulang.");
  const ch = await fetch("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", { headers: { Authorization: `Bearer ${j.access_token}` } });
  const cj = (await ch.json().catch(() => ({}))) as { items?: { snippet?: { title?: string } }[] };
  const title = cj.items?.[0]?.snippet?.title;
  if (!title) throw new Error("Bu Google hisobida YouTube kanali topilmadi.");
  const stored: Stored = { refreshToken: j.refresh_token, channelTitle: title, scope: j.scope ?? "", connectedAt: new Date().toISOString() };
  await writeStored(stored);
  return title;
}

async function writeStored(s: Stored | null) {
  if (isDemo()) { demoStored = s; return; }
  if (!s) { await sql()`DELETE FROM ytauto.shared_settings WHERE key = ${KEY}`; return; }
  const json = JSON.stringify(s);
  await sql()`
    INSERT INTO ytauto.shared_settings(key, value, updated_at_utc) VALUES (${KEY}, ${json}, now())
    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at_utc = now()`;
}

export const disconnectYouTube = () => writeStored(null);

/**
 * Ulanishni HAQIQATAN tekshiradi (faqat bazadagi belgiga ishonmaydi): saqlangan refresh token bilan Google'dan access token oladi va
 * kanal nomini so'raydi. Token bekor qilingan/muddati o'tgan (masalan Google "Testing" rejimida 7 kundan keyin) bo'lsa sababini aytadi.
 */
export async function verifyYouTube(): Promise<{ ok: boolean; channelTitle?: string; message: string }> {
  const [s, c] = await Promise.all([readStored(), getYouTubeClient()]);
  if (!s?.refreshToken) return { ok: false, message: "Kanal ulanmagan: «YouTube'ni ulash» ni bosing." };
  if (!c.clientId || !c.clientSecret) return { ok: false, message: "OAuth Client ID/Secret saqlanmagan (Sozlamalar → YouTube)." };
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

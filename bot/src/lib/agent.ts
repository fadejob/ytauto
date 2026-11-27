import "server-only";
import crypto from "node:crypto";
import { agentEndpoints } from "./accounts";

/**
 * Kompyuterdagi doimiy agent bilan aloqa (YtAuto.exe --agent: lokal server + Cloudflare quick tunnel).
 *
 * Agent tunnel manzilini (https://*.trycloudflare.com) va imzo sirini ytauto.machines ga yozadi; kompyuter qayta yonsa yoki tunnel
 * qayta ulansa manzil darhol yangilanadi. Bot har safar manzilni bazadan oladi - doim to'g'ri joyga so'rov yuboradi.
 *  - uyg'otish (wake): bot so'rov yaratganda worker darhol ishga tushadi (avval 2 daqiqagacha kutilardi);
 *  - holat va kutish (long-poll): MCP get_video_status real vaqtda javob beradi;
 *  - WebSocket: bot web app (brauzer) agentga to'g'ridan-to'g'ri ulanib jonli hodisalarni oladi;
 *  - media: tayyor video, muqova va subtitr imzolangan public havola orqali (Range bilan - ko'rish va yuklab olish).
 * Imzo: HMAC-SHA256 (agent sirida). Algoritm desktop'dagi AgentAuth.cs bilan bir xil.
 * Agent javob bermasa hech narsa buzilmaydi: hamma so'rov bazada, kompyuter ularni o'zi topadi.
 */

export type AgentEndpoint = { machine: string; url: string; secret: string };

const TOKEN_TTL_SEC = 10 * 60;
/** Faqat Cloudflare quick tunnel manzillari (bazadagi ixtiyoriy manzilga server so'rov yubormasligi uchun - SSRF). */
const URL_RE = /^https:\/\/[a-z0-9-]+\.trycloudflare\.com$/;
/** Lokal sinov (next dev): agent tunnelsiz, http://localhost:PORT. Production'da hech qachon qabul qilinmaydi. */
const DEV_URL_RE = /^http:\/\/localhost:\d{2,5}$/;
const urlAllowed = (u: string) => URL_RE.test(u) || (process.env.NODE_ENV !== "production" && DEV_URL_RE.test(u));
export const MEDIA_FILES = ["video.mp4", "cover.jpg", "subtitles.srt"] as const;
export type MediaFile = (typeof MEDIA_FILES)[number];

const b64url = (b: Buffer) => b.toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
const mac = (secret: string, data: string) => b64url(crypto.createHmac("sha256", Buffer.from(secret, "utf8")).update(data, "utf8").digest());

/** Agent tekshiradigan qisqa muddatli token: "{exp}.{scope}.{account}.{hmac}". */
export function signAgentToken(secret: string, scope: "api" | "ws", account: string, ttlSec = TOKEN_TTL_SEC): string {
  const body = `${Math.floor(Date.now() / 1000) + ttlSec}.${scope}.${account.toLowerCase()}`;
  return `${body}.${mac(secret, body)}`;
}

export function mediaSig(secret: string, account: string, jobId: number, file: MediaFile): string {
  return mac(secret, `media|${account.toLowerCase()}|${jobId}|${file}`).slice(0, 32);
}

/** Hisobning tirik agentlari (mehmon bo'lsa - mezbon kompyuterlari). Manzil formati noto'g'ri bo'lsa tashlab yuboriladi. */
export async function liveAgents(accountId: string): Promise<AgentEndpoint[]> {
  const list = await agentEndpoints(accountId);
  return list.filter((a) => urlAllowed(a.url) && a.secret.length >= 32);
}

async function call(ep: AgentEndpoint, account: string, path: string, init: RequestInit & { timeoutMs: number }, errors?: string[]): Promise<Response | null> {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), init.timeoutMs);
  try {
    return await fetch(ep.url + path, {
      ...init, signal: init.signal ? AbortSignal.any([ctl.signal, init.signal]) : ctl.signal, cache: "no-store",
      headers: { ...(init.headers ?? {}), Authorization: `Bearer ${signAgentToken(ep.secret, "api", account)}` },
    });
  } catch (e) {
    const c = (e as { cause?: { code?: string; message?: string } }).cause;
    errors?.push(`${ep.machine}: ${ctl.signal.aborted ? "vaqt tugadi" : (c?.code ?? c?.message ?? (e instanceof Error ? e.message : String(e)))}`);
    return null;
  }
  finally { clearTimeout(t); }
}

/** So'rov bazaga yozilgach (COMMIT) - barcha tirik agentlarni uyg'otadi. Qaytaradi: nechta agent qabul qildi. */
export async function pokeAgents(accountId: string, reasons: string[]): Promise<number> {
  const agents = await liveAgents(accountId);
  if (!agents.length) return 0;
  const body = JSON.stringify({ reason: reasons.join(",").slice(0, 60) });
  const res = await Promise.all(agents.map((a) =>
    call(a, accountId, "/v1/wake", { method: "POST", body, headers: { "Content-Type": "application/json" }, timeoutMs: 2500 })));
  return res.filter((r) => r?.ok).length;
}

export type AgentStatus = {
  machine: string; version: string; startedAt: string; tunnel: string | null;
  worker: { state: "busy" | "idle"; jobId: number | null; detail: string | null }; live: number;
};

/** Agentlarning jonli holati (javob bermaganlari tashlanadi). */
export async function agentStatuses(accountId: string): Promise<AgentStatus[]> {
  const agents = await liveAgents(accountId);
  const res = await Promise.all(agents.map(async (a) => {
    const r = await call(a, accountId, "/v1/status", { method: "GET", timeoutMs: 2500 });
    if (!r?.ok) return null;
    return (await r.json().catch(() => null)) as AgentStatus | null;
  }));
  return res.filter((s): s is AgentStatus => !!s);
}

/**
 * Vazifa o'zgarishini kutish (long-poll): agent worker'dan signal kelishi bilan javob beradi (yoki timeout). Bir nechta kompyuter
 * bo'lsa - qaysi biri birinchi signal bersa. null - tirik agent yo'q (chaqiruvchi oddiy kutishga o'tadi).
 */
export async function waitJobChange(accountId: string, jobId: number, seconds: number): Promise<boolean | null> {
  const agents = await liveAgents(accountId);
  if (!agents.length) return null;
  if (seconds <= 0) return false;
  const sec = Math.min(25, Math.max(1, Math.round(seconds)));
  const ctl = new AbortController();
  try {
    return await Promise.any(agents.map(async (a) => {
      const r = await call(a, accountId, `/v1/wait?job=${jobId}&timeout=${sec}`, { method: "GET", timeoutMs: (sec + 5) * 1000, signal: ctl.signal });
      const j = r?.ok ? ((await r.json().catch(() => null)) as { changed?: boolean } | null) : null;
      if (!j?.changed) throw new Error("no change");
      return true;
    })).catch(() => false);
  } finally { ctl.abort(); }
}

export type MediaLinks = { machine: string; video: string; download: string; cover: string | null; subtitles: string | null };

/** Shu kompyuterdagi tayyor video uchun public havolalar (tunnel manzili o'zgarsa - qayta so'rang). Fayl topilmasa null. */
export async function mediaLinks(accountId: string, jobId: number): Promise<MediaLinks | null> {
  return (await mediaLinksWhy(accountId, jobId)).links;
}

/** mediaLinks + havola nima uchun yo'qligi (kompyuter o'chiq / agentga yetib bo'lmadi / fayl yo'q) - MCP javobida ko'rsatiladi. */
export async function mediaLinksWhy(accountId: string, jobId: number): Promise<{ links: MediaLinks | null; reason?: string }> {
  const listed = await agentEndpoints(accountId);
  const agents = listed.filter((a) => urlAllowed(a.url) && a.secret.length >= 32);
  if (!agents.length) return { links: null, reason: listed.length ? "agent manzili yaroqsiz" : "kompyuter agenti online emas (kompyuter o'chiq yoki YtAuto eski versiya)" };
  const errors: string[] = [];
  const has = await Promise.all(agents.map(async (a) => {
    // Qisqa kutish: pleer shu javobni kutadi (oldin 6 s). Yagona kompyuter javob bermasa ham havola beriladi (pastda).
    const r = await call(a, accountId, `/v1/has?job=${jobId}`, { method: "GET", timeoutMs: 2500 }, errors);
    if (r && !r.ok) errors.push(`${a.machine}: HTTP ${r.status}`);
    const j = r?.ok ? ((await r.json().catch(() => null)) as { video?: boolean; cover?: boolean; subtitles?: boolean } | null) : null;
    if (j && !j.video) errors.push(`${a.machine}: video fayli kompyuterda topilmadi`);
    return j?.video ? { a, cover: !!j.cover, subtitles: !!j.subtitles } : null;
  }));
  // Yagona kompyuter va u "fayl yo'q" demagan bo'lsa (tunnel sekin/uzilgan) - havola baribir beriladi: pleer o'zi ochib ko'radi.
  const notFound = errors.some((e) => e.includes("topilmadi"));
  const hit = has.find((x) => !!x) ?? (agents.length === 1 && !notFound ? { a: agents[0], cover: false, subtitles: false } : null);
  if (!hit) return { links: null, reason: errors.join("; ") || "agent javob bermadi" };
  const { a } = hit;
  const acc = accountId.toLowerCase();
  const link = (f: MediaFile, extra = "") => `${a.url}/media/${jobId}/${f}?a=${acc}&s=${mediaSig(a.secret, acc, jobId, f)}${extra}`;
  return {
    links: {
      machine: a.machine, video: link("video.mp4"), download: link("video.mp4", "&download=1"),
      cover: hit.cover ? link("cover.jpg") : null, subtitles: hit.subtitles ? link("subtitles.srt") : null,
    },
  };
}

/** Bot web app uchun WebSocket manzillari (token 10 daqiqa: ulanish uzilsa ilova yangisini so'raydi). */
export async function liveSockets(accountId: string): Promise<{ machine: string; url: string }[]> {
  const agents = await liveAgents(accountId);
  return agents.map((a) => ({ machine: a.machine, url: `${a.url.replace(/^http/, "ws")}/v1/ws?t=${encodeURIComponent(signAgentToken(a.secret, "ws", accountId))}` }));
}

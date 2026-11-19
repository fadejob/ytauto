"use client";

/** Telegram Mini App SDK (telegram-web-app.js) ning biz ishlatadigan qismi. */
type TgWebApp = {
  initData: string;
  colorScheme: "light" | "dark";
  /** "android" | "ios" | "tdesktop" | "macos" | "weba" | "webk" | "unigram" | "unknown" … */
  platform?: string;
  isFullscreen?: boolean;
  ready(): void;
  expand(): void;
  /** Bot API 8.0+: Mini App butun ekranni egallaydi (Telegram sarlavhasi ham yashiriladi). */
  requestFullscreen?(): void;
  onEvent?(e: string, cb: () => void): void;
  disableVerticalSwipes?(): void;
  setHeaderColor?(c: string): void;
  setBackgroundColor?(c: string): void;
  showConfirm(msg: string, cb: (ok: boolean) => void): void;
  HapticFeedback?: { impactOccurred(s: "light" | "medium"): void; notificationOccurred(t: "success" | "error" | "warning"): void };
  isVersionAtLeast?(v: string): boolean;
  openLink?(url: string): void;
};

declare global {
  interface Window { Telegram?: { WebApp?: TgWebApp } }
}

export function tg(): TgWebApp | null {
  const w = typeof window !== "undefined" ? window.Telegram?.WebApp : undefined;
  return w && w.initData ? w : null; // initData bo'sh — oddiy brauzer
}

export function initTelegram() {
  const t = tg();
  if (!t) return;
  document.documentElement.classList.add("tg");
  t.ready();
  t.expand();
  try { t.disableVerticalSwipes?.(); } catch {}
  try { t.setHeaderColor?.("secondary_bg_color"); t.setBackgroundColor?.("secondary_bg_color"); } catch {}
  // Kompyuter yoki planshetda kichik oyna o'rniga darhol to'liq ekran (telefonda odatdagidek - pastdan ochiladigan oyna).
  if (isLargeDevice(t) && t.isVersionAtLeast?.("8.0") && !t.isFullscreen) {
    try { t.onEvent?.("fullscreenFailed", () => {}); t.requestFullscreen?.(); } catch { /* eski Telegram — oddiy oyna qoladi */ }
  }
}

/** Kompyuter (Telegram Desktop, macOS, Web) yoki planshet: ekranning qisqa tomoni 600px dan katta. */
function isLargeDevice(t: TgWebApp): boolean {
  const p = t.platform ?? "";
  if (["tdesktop", "macos", "weba", "webk", "web", "unigram"].includes(p)) return true;
  return Math.min(window.screen.width, window.screen.height) >= 600;
}

export const haptic = {
  tap: () => tg()?.HapticFeedback?.impactOccurred("light"),
  ok: () => tg()?.HapticFeedback?.notificationOccurred("success"),
  fail: () => tg()?.HapticFeedback?.notificationOccurred("error"),
};

/** Tashqi havolani ochadi: Telegram ichida tashqi brauzerda (Google kirishi Telegram ichki oynasida ishlamaydi), aks holda yangi tab. */
export function openExternal(url: string) {
  const t = tg();
  if (t?.openLink) t.openLink(url);
  else window.open(url, "_blank", "noopener");
}

export function confirmBox(message: string): Promise<boolean> {
  const t = tg();
  if (t?.isVersionAtLeast?.("6.2")) return new Promise((res) => t.showConfirm(message, res));
  return Promise.resolve(window.confirm(message));
}

export class ApiError extends Error {
  /** code "token_required" - botga hali YtAuto ilovasidagi token bilan kirilmagan. */
  constructor(message: string, public status: number, public telegramId?: number, public code?: string) { super(message); }
}

export async function api<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    headers: { "x-tg-init": tg()?.initData ?? "", ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store",
  });
  const data = await res.json().catch(() => ({ error: `HTTP ${res.status}` }));
  if (!res.ok) throw new ApiError(data.error ?? `HTTP ${res.status}`, res.status, data.telegramId, data.code);
  return data as T;
}

/** Oxirgi holat keshi: ilova ochilganda ro'yxat darhol ko'rinadi, yangisi fonda olinadi. */
export const cache = {
  get<T>(k: string): T | null { try { const v = localStorage.getItem("ytauto:" + k); return v ? (JSON.parse(v) as T) : null; } catch { return null; } },
  set(k: string, v: unknown) { try { localStorage.setItem("ytauto:" + k, JSON.stringify(v)); } catch {} },
};

/** Faylni Supabase Storage'ga imzolangan havola bilan yuboradi (XHR: yuklash foizi uchun). */
export function putFile(url: string, file: File, onProgress: (p: number) => void): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("PUT", url);
    xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100)); };
    xhr.onload = () => (xhr.status >= 200 && xhr.status < 300 ? resolve() : reject(new Error(`Yuklash xatosi (HTTP ${xhr.status}): ${xhr.responseText.slice(0, 160)}`)));
    xhr.onerror = () => reject(new Error("Tarmoq xatosi yuklash vaqtida."));
    const fd = new FormData();
    fd.append("cacheControl", "3600");
    fd.append("", file);
    xhr.send(fd);
  });
}

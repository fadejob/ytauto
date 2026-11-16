"use client";

/** Telegram Mini App SDK (telegram-web-app.js) ning biz ishlatadigan qismi. */
type TgWebApp = {
  initData: string;
  colorScheme: "light" | "dark";
  ready(): void;
  expand(): void;
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
  constructor(message: string, public status: number, public telegramId?: number) { super(message); }
}

export async function api<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    headers: { "x-tg-init": tg()?.initData ?? "", ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store",
  });
  const data = await res.json().catch(() => ({ error: `HTTP ${res.status}` }));
  if (!res.ok) throw new ApiError(data.error ?? `HTTP ${res.status}`, res.status, data.telegramId);
  return data as T;
}

/** Oxirgi holat keshi: ilova ochilganda ro'yxat darhol ko'rinadi, yangisi fonda olinadi. */
export const cache = {
  get<T>(k: string): T | null { try { const v = localStorage.getItem("ytauto:" + k); return v ? (JSON.parse(v) as T) : null; } catch { return null; } },
  set(k: string, v: unknown) { try { localStorage.setItem("ytauto:" + k, JSON.stringify(v)); } catch {} },
};

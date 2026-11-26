"use client";

import { useSyncExternalStore } from "react";
import App from "./App";

const noop = () => () => {};
const inApp = () => document.documentElement.classList.contains("in-app");

/**
 * "/" da ilovani faqat Telegram ichida (layout skripti qo'ygan html.in-app) yoki lokal dev/demo rejimda yuklaydi. Oddiy brauzerda
 * ilova umuman ishga tushmaydi (bosh sahifa ko'rinadi, API'ga so'rov ketmaydi). Server render va hydration'da - null.
 */
export default function AppGate({ browserApp }: { browserApp: boolean }) {
  const show = useSyncExternalStore(noop, () => browserApp || inApp(), () => false);
  return show ? <App /> : null;
}

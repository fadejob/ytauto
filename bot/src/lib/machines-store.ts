"use client";

import { useEffect, useSyncExternalStore } from "react";
import { api } from "./client";
import { useLiveRefresh } from "./live";
import type { Machine } from "./types";

/**
 * Kompyuterlar holati - butun ilova uchun BITTA manba. Oldin App, Faoliyat, Kompyuter sozlamasi, Hub va shablon yuklash har biri
 * /api/machines ni alohida so'rardi (Faoliyat sahifasida daqiqasiga ~8 marta, sahifa yashirin bo'lsa ham).
 *  - Bitta polling (15 s), faqat sahifa ko'rinib turganda; sahifaga qaytilganda darhol yangilanadi.
 *  - Bir vaqtda faqat bitta so'rov: ustma-ust chaqiruvlar shu so'rovni kutadi.
 *  - Agent "machine" hodisasi yuborsa darhol yangilanadi; POST javobida kelgan ro'yxat to'g'ridan-to'g'ri qo'yiladi (setMachines).
 */
type Snap = { list: Machine[] | null; error: string | null; at: number };

let snap: Snap = { list: null, error: null, at: Date.now() };
const subs = new Set<() => void>();
let inflight: Promise<void> | null = null;
let users = 0;
let timer: number | null = null;

const emit = (next: Partial<Snap>) => { snap = { ...snap, ...next, at: Date.now() }; subs.forEach((f) => f()); };

export function setMachines(list: Machine[]) { emit({ list, error: null }); }

/** Oxirgi ma'lum ro'yxat (bir martalik o'qish uchun). */
export const machinesSnapshot = () => snap.list;

export function reloadMachines(): Promise<void> {
  inflight ??= api<Machine[]>("/api/machines")
    .then((list) => emit({ list, error: null }))
    .catch((e) => emit({ error: e instanceof Error ? e.message : String(e) }))
    .finally(() => { inflight = null; });
  return inflight;
}

const visible = () => typeof document === "undefined" || document.visibilityState === "visible";
const onVisible = () => { if (visible() && (!snap.list || Date.now() - snap.at > 5_000)) void reloadMachines(); };

function start() {
  if (++users > 1) return;
  if (!snap.list || Date.now() - snap.at > 5_000) void reloadMachines();
  timer = window.setInterval(() => { if (visible()) void reloadMachines(); }, 15_000);
  document.addEventListener("visibilitychange", onVisible);
}

function stop() {
  if (--users > 0) return;
  if (timer !== null) window.clearInterval(timer);
  timer = null;
  document.removeEventListener("visibilitychange", onVisible);
}

const subscribe = (f: () => void) => { subs.add(f); return () => { subs.delete(f); }; };
const get = () => snap;

/** enabled=false (qo'shimcha token) bo'lsa so'rov yuborilmaydi: kompyuter asosiy hisob egasiniki. now - oxirgi yangilanish vaqti. */
export function useMachineStore(enabled = true) {
  const s = useSyncExternalStore(subscribe, get, get);
  useEffect(() => {
    if (!enabled) return;
    start();
    return stop;
  }, [enabled]);
  useLiveRefresh((e) => enabled && e.type === "machine", () => void reloadMachines(), 800);
  return { list: s.list, error: s.error, reload: reloadMachines, now: s.at };
}

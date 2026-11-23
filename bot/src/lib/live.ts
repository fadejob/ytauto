"use client";

import { useCallback, useEffect, useRef, useSyncExternalStore } from "react";
import { api } from "./client";

/**
 * Real vaqt: bot web app kompyuterdagi YtAuto agentiga (Cloudflare tunnel) to'g'ridan-to'g'ri WebSocket bilan ulanadi va
 * hodisalarni oladi - video bosqichi o'zgardi, worker band/bo'sh. Komponentlar shu hodisada ma'lumotni qayta yuklaydi (manba -
 * baribir server/baza). Agent yo'q bo'lsa hech narsa buzilmaydi: komponentlarning odatiy davriy yangilanishi ishlayveradi.
 *
 * Tunnel manzili kompyuter qayta yonganda o'zgaradi: ulanish uzilsa yangi manzil va token /api/agent dan qayta olinadi.
 */
export type LiveJob = {
  id: number; status: number; topic: string; kind: string; claimed: boolean; stage: string | null; stageRunning: boolean;
  doneStage: number | null; message: string | null; level: string | null; at: string | null; hasVideo: boolean; youtubeId: string | null;
};
export type LiveMachine = {
  machine: string; tunnel: string | null; worker: { state: "busy" | "idle"; jobId: number | null; detail: string | null };
};
export type LiveEvent =
  | { type: "hello"; machine: LiveMachine }
  | { type: "job"; job: LiveJob }
  | { type: "machine"; machine: LiveMachine };

type Listener = (e: LiveEvent) => void;

class LiveHub {
  private listeners = new Set<Listener>();
  private states = new Set<(on: boolean) => void>();
  private sockets = new Map<string, WebSocket>();
  private timer: number | null = null;
  private pinger: number | null = null;
  private backoff = 2000;
  private starting = false;
  readonly jobs = new Map<number, LiveJob>();
  readonly machines = new Map<string, LiveMachine>();

  get connected() { for (const s of this.sockets.values()) if (s.readyState === WebSocket.OPEN) return true; return false; }

  subscribe(l: Listener) {
    this.listeners.add(l);
    this.ensure();
    return () => { this.listeners.delete(l); };
  }

  onState(l: (on: boolean) => void) {
    this.states.add(l);
    this.ensure();
    return () => { this.states.delete(l); };
  }

  private emitState() { const on = this.connected; for (const s of this.states) s(on); }

  private ensure() {
    if (typeof window === "undefined" || this.starting || this.sockets.size || this.timer !== null) return;
    void this.start();
    if (this.pinger === null) {
      // Cloudflare bo'sh ulanishni ~100 soniyada yopadi: ilova tomonidan ham "ping".
      this.pinger = window.setInterval(() => { for (const s of this.sockets.values()) if (s.readyState === WebSocket.OPEN) s.send("ping"); }, 40_000);
      document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible" && !this.connected) this.retryNow(); });
    }
  }

  private retryNow() {
    if (this.timer !== null) { window.clearTimeout(this.timer); this.timer = null; }
    this.backoff = 2000;
    void this.start();
  }

  private schedule() {
    if (this.timer !== null) return;
    const wait = this.backoff;
    this.backoff = Math.min(this.backoff * 2, 60_000);
    this.timer = window.setTimeout(() => { this.timer = null; void this.start(); }, wait);
  }

  private async start() {
    if (this.starting) return;
    this.starting = true;
    try {
      const r = await api<{ sockets: { machine: string; url: string }[] }>("/api/agent");
      for (const s of r.sockets) if (!this.sockets.has(s.machine)) this.open(s.machine, s.url);
      if (!r.sockets.length) this.schedule(); // agent hali yo'q: keyinroq yana (kompyuter yoqilishi mumkin)
    } catch { this.schedule(); }
    finally { this.starting = false; }
  }

  private open(machine: string, url: string) {
    let ws: WebSocket;
    try { ws = new WebSocket(url); } catch { this.schedule(); return; }
    this.sockets.set(machine, ws);
    ws.onopen = () => { this.backoff = 2000; this.emitState(); };
    ws.onmessage = (m) => {
      let e: LiveEvent;
      try { e = JSON.parse(String(m.data)) as LiveEvent; } catch { return; }
      if (e.type === "job") this.jobs.set(e.job.id, e.job);
      else if (e.type === "machine" || e.type === "hello") this.machines.set(e.machine.machine, e.machine);
      else return;
      for (const l of this.listeners) { try { l(e); } catch { /* komponent xatosi boshqalarga ta'sir qilmasin */ } }
    };
    ws.onclose = () => {
      if (this.sockets.get(machine) === ws) this.sockets.delete(machine);
      this.emitState();
      this.schedule(); // manzil o'zgargan bo'lishi mumkin - yangisi /api/agent dan
    };
    ws.onerror = () => { try { ws.close(); } catch { /* */ } };
  }
}

let hub: LiveHub | null = null;
export function liveHub(): LiveHub {
  hub ??= new LiveHub();
  return hub;
}

/**
 * Jonli hodisa kelganda fn ni chaqiradi (ketma-ket hodisalar - masalan bosqich + log + yangilash - ms ichida bittaga birlashadi).
 * match va fn har renderda yangilanishi mumkin: oxirgisi ishlatiladi.
 */
export function useLiveRefresh(match: (e: LiveEvent) => boolean, fn: () => void, ms = 600) {
  const fnRef = useRef(fn);
  const matchRef = useRef(match);
  useEffect(() => { fnRef.current = fn; matchRef.current = match; });
  useEffect(() => {
    let t: number | null = null;
    const off = liveHub().subscribe((e) => {
      if (!matchRef.current(e)) return;
      if (t !== null) window.clearTimeout(t);
      t = window.setTimeout(() => { t = null; fnRef.current(); }, ms);
    });
    return () => { off(); if (t !== null) window.clearTimeout(t); };
  }, [ms]);
}

/** Kompyuter agenti bilan jonli ulanish bormi. */
export function useLiveConnected(): boolean {
  const sub = useCallback((cb: () => void) => liveHub().onState(cb), []);
  return useSyncExternalStore(sub, () => liveHub().connected, () => false);
}

/** Shu vazifaning oxirgi jonli holati (agentdan). */
export function useLiveJob(id: number | null): LiveJob | null {
  const sub = useCallback((cb: () => void) => liveHub().subscribe((e) => { if (e.type === "job" && e.job.id === id) cb(); }), [id]);
  return useSyncExternalStore(sub, () => (id ? liveHub().jobs.get(id) ?? null : null), () => null);
}

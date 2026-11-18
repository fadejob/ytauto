import "server-only";
import { sql } from "./db";
import { isDemo } from "./demo";
import type { CheckItem, Machine } from "./types";

/** Yurak urishi shu vaqtdan eski bo'lsa kompyuter offline: yengil poll har 10 daqiqada, ilova (ochiq bo'lsa) 30 soniyada yozadi. */
const ONLINE_MS = 25 * 60 * 1000; // poll har 10 daqiqada signal beradi: 2.5 interval
const APP_MS = 90 * 1000;
const BUSY_MAX_MS = 8 * 60 * 60 * 1000; // og'ir ish davomida signal yuborilmaydi: "band" holati shu muddatgacha ishonchli

type Row = {
  machine: string; last_seen_utc: Date; app_open: boolean; app_seen_utc: Date | null; worker_state: string | null; worker_job_id: number | null;
  worker_detail: string | null; worker_seen_utc: Date | null; version: string | null; check_requested: boolean; check_requested_at_utc: Date | null;
  check_result_json: string | null; check_finished_at_utc: Date | null; run_requested: boolean; run_requested_at_utc: Date | null;
};

const ago = (d: Date | null) => (d ? Date.now() - d.getTime() : Infinity);

export function toMachine(r: Row): Machine {
  let results: CheckItem[] = []; let durationSec: number | null = null;
  try {
    if (r.check_result_json) { const j = JSON.parse(r.check_result_json); results = j.results ?? []; durationSec = j.durationSec ?? null; }
  } catch { /* buzilgan JSON */ }
  // Og'ir ish paytida worker signal yubormaydi: "band" belgisi qo'yilgan bo'lsa, u boshlanganidan beri BUSY_MAX_MS ichida kompyuter online va band.
  const busyLive = r.worker_state === "busy" && ago(r.worker_seen_utc) < BUSY_MAX_MS;
  const idleLive = r.worker_state === "idle" && ago(r.worker_seen_utc) < ONLINE_MS;
  return {
    name: r.machine, lastSeen: r.last_seen_utc.toISOString(), online: ago(r.last_seen_utc) < ONLINE_MS || busyLive,
    app: { open: r.app_open && ago(r.app_seen_utc) < APP_MS, seen: r.app_seen_utc?.toISOString() ?? null },
    worker: {
      state: busyLive ? "busy" : idleLive ? "idle" : "unknown",
      jobId: busyLive ? r.worker_job_id : null,
      detail: busyLive || idleLive ? r.worker_detail : null,
      seen: r.worker_seen_utc?.toISOString() ?? null,
    },
    version: r.version, run: { requested: r.run_requested, requestedAt: r.run_requested_at_utc?.toISOString() ?? null },
    check: { requested: r.check_requested, requestedAt: r.check_requested_at_utc?.toISOString() ?? null, finishedAt: r.check_finished_at_utc?.toISOString() ?? null, durationSec, results },
    tools: null, toolsUpdatedAt: null,
  };
}

export async function getMachines(): Promise<Machine[]> {
  if (isDemo()) return demoMachines();
  const rows = await sql()<Row[]>`SELECT * FROM ytauto.machines ORDER BY last_seen_utc DESC`;
  // Kerakli dasturlar holati (har kompyuter yozadi).
  const st = await sql()`SELECT key, value FROM ytauto.shared_settings WHERE key LIKE 'tools.status.%'`;
  const byMachine = new Map<string, { items?: { key?: string; name: string; ok: boolean; detail: string }[]; updatedAtUtc?: string }>();
  for (const s of st) { try { byMachine.set((s.key as string).slice("tools.status.".length), JSON.parse(s.value as string)); } catch { /* buzilgan */ } }
  // Tekshiruv natijasidan keyin bot sozlamalari o'zgargan bo'lsa (masalan Gemini kaliti qo'shilgan) natija eskirgan deb belgilanadi.
  const [chg] = await sql()<{ at: Date | null }[]>`
    SELECT max(updated_at_utc) AS at FROM ytauto.shared_settings WHERE key IN ('config', 'schedule', 'youtube.oauth')`;
  return rows.map((r) => {
    const m = toMachine(r);
    if (chg?.at && r.check_finished_at_utc && chg.at > r.check_finished_at_utc) m.check.stale = true;
    const t = byMachine.get(r.machine);
    return t ? { ...m, tools: (t.items ?? []).map((i) => ({ key: i.key ?? "", name: i.name, ok: !!i.ok, detail: i.detail ?? "" })), toolsUpdatedAt: t.updatedAtUtc ?? null } : m;
  });
}

/** Tekshiruv so'rovi: kompyuter (ilova ~30 s, yengil worker poll ≤10 daqiqa ichida) oladi va natijani yozadi. */
export async function requestCheck(machine: string): Promise<{ ok: boolean; message: string }> {
  if (isDemo()) { demoCheck = { requestedAt: new Date() }; return { ok: true, message: "Tekshiruv so'rovi yuborildi. [demo]" }; }
  const n = await sql()`UPDATE ytauto.machines SET check_requested = TRUE, check_requested_at_utc = now() WHERE machine = ${machine}`;
  return n.count ? { ok: true, message: "Tekshiruv so'rovi yuborildi — ilova ochiq bo'lsa 30 soniyada, aks holda ≤10 daqiqada bajariladi." } : { ok: false, message: "Kompyuter topilmadi." };
}

// ------------------------------------------------------------------ demo
let demoCheck: { requestedAt: Date } | null = null;
function demoMachines(): Machine[] {
  const now = Date.now();
  const iso = (ms: number) => new Date(now - ms).toISOString();
  const done = demoCheck && now - demoCheck.requestedAt.getTime() > 4000;
  const items: CheckItem[] = [
    { name: "RAM", level: "Ok", message: "Jami 15.8 GB, bo'sh 5.9 GB." }, { name: "Gemini", level: "Ok", message: "Kalit ishlaydi (gemini-2.5-flash)." },
    { name: "VoiceStudio", level: "Ok", message: "O'rnatilgan, hozir ishlamayapti." }, { name: "OpenFilm CLI", level: "Ok", message: "0.3.2" },
    { name: "Claude Code", level: "Ok", message: "2.1.314" }, { name: "YouTube", level: "Warning", message: "Kanal ulanmagan." },
  ];
  return [
    {
      name: "AIBA", lastSeen: iso(12_000), online: true, app: { open: true, seen: iso(12_000) }, run: { requested: false, requestedAt: null },
      tools: [{ key: "openfilm", name: "OpenFilm", ok: true, detail: "" }, { key: "voicestudio", name: "VoiceStudio", ok: true, detail: "" }, { key: "whisper", name: "Subtitr modeli (Systran/faster-whisper-base)", ok: false, detail: "" }, { key: "tts", name: "Ovoz modeli (MOSS-TTS-Nano)", ok: true, detail: "MOSS ayol, MOSS erkak" }, { key: "claude", name: "Claude Code", ok: true, detail: "" }, { key: "youtube", name: "YouTube kanali", ok: true, detail: "Fadejob" }, { key: "gemini", name: "Gemini kaliti", ok: true, detail: "1 ta kalit · gemini-2.5-flash" }, { key: "schedule", name: "Windows jadvali", ok: true, detail: "har kuni 01:00 (Asia/Tashkent)" }], toolsUpdatedAt: iso(60_000),
      worker: { state: "busy", jobId: 104, detail: "render: How vaccines train the immune system", seen: iso(9_000) }, version: "1.0.0.0",
      check: done ? { requested: false, requestedAt: demoCheck!.requestedAt.toISOString(), finishedAt: iso(1000), durationSec: 6.2, results: items }
        : { requested: !!demoCheck, requestedAt: demoCheck?.requestedAt.toISOString() ?? null, finishedAt: null, durationSec: null, results: [] },
    },
    {
      name: "STUDIO-PC", lastSeen: iso(25 * 60_000), online: false, app: { open: false, seen: iso(3 * 3600_000) }, run: { requested: false, requestedAt: null },
      tools: null, toolsUpdatedAt: null,
      worker: { state: "unknown", jobId: null, detail: null, seen: iso(25 * 60_000) }, version: "1.0.0.0",
      check: { requested: false, requestedAt: null, finishedAt: null, durationSec: null, results: [] },
    },
  ];
}

/**
 * "Hozir ishga tushirish": kompyuterda worker jadvalni kutmasdan, navbatdagi barcha mavzulardan video tayyorlashni boshlaydi.
 * Ilova ochiq bo'lsa ~30 soniyada, aks holda yengil worker (≤10 daqiqa) olganda boshlanadi. Band bo'lsa so'rov bekor (ikki marta ishlamaydi).
 */
export async function requestRun(machine: string): Promise<{ ok: boolean; message: string }> {
  if (isDemo()) return { ok: true, message: "Ishga tushirish so'rovi yuborildi. [demo]" };
  const [m] = await sql()<Row[]>`SELECT * FROM ytauto.machines WHERE machine = ${machine}`;
  if (!m) return { ok: false, message: "Kompyuter topilmadi." };
  if (toMachine(m).worker.state === "busy") return { ok: false, message: "Worker hozir band — ish tugagach qayta urinib ko'ring." };
  await sql()`UPDATE ytauto.machines SET run_requested = TRUE, run_requested_at_utc = now() WHERE machine = ${machine}`;
  return { ok: true, message: "So'rov yuborildi — kompyuter mavzular soniga qarab ishni boshlaydi (ilova ochiq bo'lsa ~30 soniyada, aks holda ≤10 daqiqada)." };
}

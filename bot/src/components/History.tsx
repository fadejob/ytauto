"use client";

import { useCallback, useEffect, useState } from "react";
import { api, haptic } from "@/lib/client";
import { StatusText, type JobLogs, type RunStats, type State, type WorkerRun } from "@/lib/types";
import type { Toast } from "./App";
import { IconRefresh } from "./icons";
import Machines from "./Machines";

const STAGE_NAME: Record<number, string> = { 1: "Ssenariy", 2: "Ovoz", 3: "Subtitr vaqtlari", 4: "Montaj", 5: "Render", 6: "Sifat tekshiruvi", 7: "Arxiv", 8: "YouTube" };
const STATE_NAME: Record<number, string> = { 0: "kutmoqda", 1: "ishlamoqda", 2: "tayyor", 3: "xato", 4: "o'tkazildi" };
const TRIGGER: Record<string, string> = { scheduled: "Jadval", remote: "Bot so'rovi", manual: "Qo'lda", selftest: "Sinov", check: "Tekshiruv" };

const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const p2 = (n: number) => String(n).padStart(2, "0");
const dt = (iso: string) => { const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}, ${p2(d.getHours())}:${p2(d.getMinutes())}`; };
const tm = (iso: string) => { const d = new Date(iso); return `${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`; };
const dur = (a: string, b: string | null) => {
  if (!b) return "…";
  const m = (new Date(b).getTime() - new Date(a).getTime()) / 60000;
  return m < 1 ? "<1 daq" : m < 90 ? `${m.toFixed(0)} daq` : `${(m / 60).toFixed(1)} soat`;
};

const bytes = (b: number) => (b >= 2 ** 30 ? `${(b / 2 ** 30).toFixed(2)} GB` : b >= 2 ** 20 ? `${(b / 2 ** 20).toFixed(0)} MB` : `${(b / 1024).toFixed(0)} KB`);

function StatsDetail({ s }: { s: RunStats }) {
  return (
    <div className="card formcard" style={{ background: "transparent", boxShadow: "none", padding: 0 }}>
      {s.tools.map((t) => <div key={t.name} className="logline"><span className="t">{t.peakRamMb.toFixed(0)} MB</span>{t.name} · CPU {t.cpuSeconds.toFixed(0)} s</div>)}
      {s.disk.map((d) => <div key={d.name} className="logline"><span className="t">{bytes(d.bytes)}</span>{d.name}</div>)}
      <div className="hint">Bo&apos;sh disk: {s.system.freeDiskGbStart} → {s.system.freeDiskGbEnd} GB ({s.system.diskRoot})</div>
    </div>
  );
}

function outcomeChip(r: WorkerRun) {
  if (!r.outcome) return { text: r.finishedAt ? "tugadi" : "ishlamoqda", cls: "chip warn" };
  if (["ok", "success", "published", "done"].includes(r.outcome)) return { text: r.outcome, cls: "chip ok" };
  if (["failed", "crash", "attention", "error"].includes(r.outcome)) return { text: r.outcome, cls: "chip bad" };
  return { text: r.outcome, cls: "chip mute" };
}

function chipClass(s: number) {
  if (s === 11) return "chip ok";
  if (s === 13 || s === 12) return "chip bad";
  if (s === 14) return "chip mute";
  if (s === 10 || s === 9) return "chip warn";
  return "chip";
}

export default function History({ state, toast }: { state: State | null; toast: Toast }) {
  const [view, setView] = useState<"machines" | "runs" | "videos" | "stats">("machines");
  const [openStats, setOpenStats] = useState<string | null>(null);
  const [runs, setRuns] = useState<WorkerRun[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [logs, setLogs] = useState<JobLogs | null>(null);
  const [logLoading, setLogLoading] = useState<number | null>(null);

  const loadRuns = useCallback(async () => {
    setLoading(true);
    try { setRuns(await api<WorkerRun[]>("/api/history")); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setLoading(false);
  }, [toast]);

  // eslint-disable-next-line react-hooks/set-state-in-effect -- mount'da bir marta yuklanadi
  useEffect(() => { void loadRuns(); }, [loadRuns]);

  async function openLogs(id: number) {
    haptic.tap();
    setLogLoading(id);
    try { setLogs(await api<JobLogs>(`/api/logs?id=${id}`)); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setLogLoading(null);
  }

  const videos = [...(state?.topics ?? [])].sort((a, b) => b.id - a.id);
  const withStats = (runs ?? []).filter((r) => r.stats);
  const latest = withStats[0]?.stats ?? null;
  const avg = (f: (s: RunStats) => number) => (withStats.length ? withStats.reduce((a, r) => a + f(r.stats!), 0) / withStats.length : 0);

  return (
    <>
      <header className="head">
        <div>
          <h1>Tarix</h1>
          <div className="sub">Kompyuterlar, worker ishga tushishlari va videolar loglari</div>
        </div>
        <button className="iconbtn" aria-label="Yangilash" onClick={() => { haptic.tap(); void loadRuns(); }} disabled={loading}><IconRefresh /></button>
      </header>
      <div className="seg">
        <button className={view === "machines" ? "on" : ""} onClick={() => { haptic.tap(); setView("machines"); }}>Kompyuter</button>
        <button className={view === "runs" ? "on" : ""} onClick={() => { haptic.tap(); setView("runs"); }}>Workerlar{runs ? <span className="n">{runs.length}</span> : null}</button>
        <button className={view === "videos" ? "on" : ""} onClick={() => { haptic.tap(); setView("videos"); }}>Videolar<span className="n">{videos.length}</span></button>
        <button className={view === "stats" ? "on" : ""} onClick={() => { haptic.tap(); setView("stats"); }}>Statistika</button>
      </div>

      <div className="list">
        {view === "machines" && <Machines toast={toast} />}
        {view === "runs" && !runs && [0, 1, 2].map((i) => <div key={i} className="skeleton" />)}
        {view === "runs" && runs && runs.length === 0 && <div className="empty">Worker hali ishga tushmagan.</div>}
        {view === "runs" && runs?.map((r) => {
          const c = outcomeChip(r);
          return (
            <article key={r.runId} className="card topic">
              <div className="row">
                <div className="title">{dt(r.startedAt)} · {dur(r.startedAt, r.finishedAt)}</div>
                {r.jobId ? <span className="id">#{r.jobId}</span> : null}
              </div>
              <div className="meta">
                <span className={c.cls}>{c.text}</span>
                <span>{TRIGGER[r.trigger ?? ""] ?? r.trigger ?? "—"}</span>
                {r.machine ? <span>💻 {r.machine}</span> : null}
              </div>
              {r.message ? <div className="notes open">{r.message}</div> : null}
              {r.stats ? (
                <>
                  <div className="hint" onClick={() => setOpenStats(openStats === r.runId ? null : r.runId)} style={{ cursor: "pointer" }}>
                    📊 RAM eng ko&apos;pi {r.stats.system.peakUsedRamGb.toFixed(1)}/{r.stats.system.totalRamGb} GB · disk {bytes(r.stats.diskTotalBytes)} {openStats === r.runId ? "▲" : "▼"}
                  </div>
                  {openStats === r.runId ? <StatsDetail s={r.stats} /> : null}
                </>
              ) : null}
              {r.jobId ? (
                <div className="actions">
                  <button className="btn" disabled={logLoading === r.jobId} onClick={() => openLogs(r.jobId!)}>Video loglari</button>
                </div>
              ) : null}
            </article>
          );
        })}

        {view === "stats" && !runs && <div className="skeleton" />}
        {view === "stats" && runs && !latest && <div className="empty">Statistika hali yo&apos;q: u har worker ishga tushganda yoziladi.</div>}
        {view === "stats" && latest && (
          <>
            <div className="sect">Hozirgi hajmlar ({dt(withStats[0].startedAt)}{latest.machine ? `, ${latest.machine}` : ""})</div>
            <div className="card formcard">
              <div className="logline"><b>Jami: {bytes(latest.diskTotalBytes)}</b> · bo&apos;sh disk {latest.system.freeDiskGbEnd} GB ({latest.system.diskRoot})</div>
              {latest.disk.map((d) => (
                <div key={d.name} className="logline" title={d.path}>
                  <span className="t">{bytes(d.bytes)}</span>{d.name}
                </div>
              ))}
            </div>
            <div className="sect">O&apos;rtacha ({withStats.length} ta ishga tushish)</div>
            <div className="card formcard">
              <div className="logline"><span className="t">{avg((s) => s.system.peakUsedRamGb).toFixed(1)} GB</span>tizimda eng ko&apos;p band RAM</div>
              <div className="logline"><span className="t">{(avg((s) => s.durationSec) / 60).toFixed(1)} daq</span>davomiyligi</div>
              <div className="logline"><span className="t">{(avg((s) => s.tools.reduce((a, t) => a + t.cpuSeconds, 0)) / 60).toFixed(1)} daq</span>dasturlar CPU vaqti</div>
              <div className="hint">RAM/CPU faqat worker o&apos;zi ishga tushirgan dasturlar bo&apos;yicha; «tizimda band RAM» butun kompyuter bo&apos;yicha.</div>
            </div>
            <div className="sect">So&apos;nggi ishga tushishda dasturlar</div>
            <div className="card formcard">
              {latest.tools.length === 0 ? <div className="hint">Og&apos;ir dasturlar ishlatilmagan.</div> : latest.tools.map((t) => (
                <div key={t.name} className="logline"><span className="t">{t.peakRamMb.toFixed(0)} MB</span>{t.name} · CPU {t.cpuSeconds.toFixed(0)} s</div>
              ))}
            </div>
          </>
        )}

        {view === "videos" && videos.length === 0 && <div className="empty">Videolar yo&apos;q.</div>}
        {view === "videos" && videos.map((t) => (
          <article key={t.id} className="card topic">
            <div className="row">
              <div className="title">{t.title || t.topic}</div>
              <span className="id">#{t.id}</span>
            </div>
            <div className="meta">
              <span className={chipClass(t.status)}>{t.claimed && t.status < 9 ? "⏳ " : ""}{StatusText[t.status]}</span>
            </div>
            {t.error ? <div className="err">{t.error}</div> : null}
            <div className="actions">
              <button className="btn" disabled={logLoading === t.id} onClick={() => openLogs(t.id)}>{logLoading === t.id ? "Yuklanmoqda…" : "Loglarni ko'rish"}</button>
            </div>
          </article>
        ))}
      </div>

      {logs && (
        <>
          <div className="backdrop" onClick={() => setLogs(null)} />
          <section className="sheet" role="dialog" aria-label="Video loglari">
            <div className="grab" />
            <h2>#{logs.id} — {logs.topic}</h2>
            <div className="meta" style={{ marginBottom: 8 }}><span className={chipClass(logs.status)}>{StatusText[logs.status]}</span></div>
            {logs.stages.length > 0 && (
              <div className="card formcard" style={{ marginBottom: 10 }}>
                {logs.stages.map((s) => (
                  <div key={s.stage} className="logline">
                    <b>{STAGE_NAME[s.stage] ?? `Bosqich ${s.stage}`}</b> — {STATE_NAME[s.state] ?? s.state}
                    {s.attempts > 1 ? `, ${s.attempts} urinish` : ""}{s.durationMs ? `, ${(s.durationMs / 1000).toFixed(0)} s` : ""}
                    {s.error ? <div className="err">{s.error}</div> : null}
                  </div>
                ))}
              </div>
            )}
            <div className="card formcard logbox">
              {logs.events.length === 0 ? <div className="hint">Hodisalar yo&apos;q.</div> : logs.events.map((e) => (
                <div key={e.id} className={`logline ${e.level.toLowerCase()}`}>
                  <span className="t">{tm(e.at)}</span>{e.stage ? <span className="s">{e.stage}</span> : null}{e.message}
                </div>
              ))}
            </div>
            <button className="btn block" style={{ marginTop: 12 }} onClick={() => setLogs(null)}>Yopish</button>
          </section>
        </>
      )}
    </>
  );
}

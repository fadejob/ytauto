"use client";

import { useCallback, useEffect, useState } from "react";
import { api, confirmBox, haptic } from "@/lib/client";
import type { Machine } from "@/lib/types";
import type { Toast } from "./App";

const ago = (iso: string | null) => {
  if (!iso) return "—";
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s} soniya oldin`;
  if (s < 3600) return `${Math.round(s / 60)} daqiqa oldin`;
  if (s < 86400) return `${Math.round(s / 3600)} soat oldin`;
  return `${Math.round(s / 86400)} kun oldin`;
};

const LEVEL: Record<string, { icon: string; cls: string }> = { Ok: { icon: "✓", cls: "" }, Warning: { icon: "!", cls: "warn" }, Failed: { icon: "✗", cls: "error" } };

/** Kompyuterlar: online/offline, ilova ochiqligi, worker faoliyati va botdan integratsiyalarni tekshirish. */
export default function Machines({ toast }: { toast: Toast }) {
  const [list, setList] = useState<Machine[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [, tick] = useState(0);

  const load = useCallback(async () => {
    try { setList(await api<Machine[]>("/api/machines")); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
  }, [toast]);

  // Har 15 soniyada yangilanadi; "necha soniya oldin" yozuvlari har soniyada emas, shu paytda ham yangilanadi.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- mount'da bir marta yuklanadi
    void load();
    const t = window.setInterval(() => { void load(); tick((n) => n + 1); }, 15000);
    return () => window.clearInterval(t);
  }, [load]);

  async function check(name: string) {
    haptic.tap();
    setBusy(name);
    try {
      const r = await api<{ ok: boolean; message: string }>("/api/machines/check", { machine: name });
      toast(r.message, !r.ok);
      await load();
    } catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  async function runNow(name: string) {
    if (!(await confirmBox(`${name}: ishni hozir boshlaymi? Worker jadvalni kutmasdan, navbatdagi mavzular soniga qarab (1–3 video) tayyorlaydi.`))) return;
    haptic.tap();
    setBusy(name);
    try {
      const r = await api<{ ok: boolean; message: string }>("/api/machines/run", { machine: name });
      toast(r.message, !r.ok);
      await load();
    } catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  if (!list) return <div className="skeleton" />;
  if (list.length === 0) return <div className="empty">Hali hech bir kompyuter xabar bermagan. YtAuto ilovasini yangi versiyada bir marta oching.</div>;

  return (
    <>
      {list.map((m) => {
        const pending = m.check.requested && (!m.check.finishedAt || (m.check.requestedAt && m.check.finishedAt < m.check.requestedAt));
        return (
          <article key={m.name} className="card topic">
            <div className="row">
              <div className="title">💻 {m.name}</div>
              <span className={m.online ? "chip ok" : "chip bad"}>{m.online ? "● online" : "○ offline"}</span>
            </div>
            <div className="meta">
              <span>Oxirgi signal: {ago(m.lastSeen)}</span>
              {m.version ? <span>v{m.version}</span> : null}
            </div>
            <div className="meta">
              <span className={m.app.open ? "chip ok" : "chip mute"}>Ilova: {m.app.open ? "ochiq" : "yopiq"}</span>
              <span className={m.worker.state === "busy" ? "chip warn" : "chip mute"}>
                Worker: {m.worker.state === "busy" ? "ishlayapti" : m.worker.state === "idle" ? "bo'sh" : "ma'lumot yo'q"}
              </span>
              {m.worker.jobId ? <span className="id">#{m.worker.jobId}</span> : null}
            </div>
            {m.worker.state === "busy" ? <div className="notes open">⚙️ Og&apos;ir ish jarayonida (boshlangan: {ago(m.worker.seen)}). Ish davomida holat yuborilmaydi — tugagach avtomatik yangilanadi.</div> : null}
            {!m.online ? <div className="hint">Kompyuter o&apos;chiq yoki internetga ulanmagan (25 daqiqadan beri signal yo&apos;q).</div> : null}
            {m.tools ? (
              <div className="card formcard" style={{ background: "transparent", boxShadow: "none", padding: 0 }}>
                <div className="hint">Kerakli dasturlar ({ago(m.toolsUpdatedAt)}):</div>
                {m.tools.map((t) => (
                  <div key={t.name} className={`logline ${t.ok ? "" : "error"}`}>
                    <span className="t">{t.ok ? "✓" : "✗"}</span><b>{t.name}</b> — {t.ok ? (t.detail ? (t.name.startsWith("YouTube") ? `ulangan: ${t.detail}` : t.name.startsWith("Ovoz") ? `o'rnatilgan · ${t.detail}` : "o'rnatilgan") : t.name.startsWith("YouTube") ? "ulangan" : "o'rnatilgan") : t.name.startsWith("YouTube") ? "ulanmagan" : "o'rnatilmagan"}
                  </div>
                ))}
              </div>
            ) : null}

            <div className="actions">
              <button className="btn primary" disabled={busy === m.name || m.run.requested || m.worker.state === "busy"} onClick={() => runNow(m.name)}>
                {m.run.requested ? "Ishga tushirish kutilmoqda…" : m.worker.state === "busy" ? "Worker band" : "Hozir ishga tushirish"}
              </button>
              <button className="btn" disabled={busy === m.name || !!pending} onClick={() => check(m.name)}>
                {pending ? "Tekshirilmoqda…" : "Integratsiyalarni tekshirish"}
              </button>
            </div>
            {pending ? <div className="hint">So&apos;rov yuborildi. Ilova ochiq bo&apos;lsa ~30 soniyada, yopiq bo&apos;lsa keyingi yengil worker (&le;10 daqiqa) olganda natija keladi.</div> : null}
            {m.check.results.length > 0 && !pending ? (
              <div className="card formcard" style={{ background: "transparent", boxShadow: "none", padding: 0 }}>
                <div className="hint">Oxirgi tekshiruv: {ago(m.check.finishedAt)}{m.check.durationSec ? ` · ${m.check.durationSec.toFixed(0)} s` : ""}</div>
                {m.check.results.map((r) => (
                  <div key={r.name} className={`logline ${LEVEL[r.level]?.cls ?? ""}`}>
                    <span className="t">{LEVEL[r.level]?.icon ?? "•"}</span><b>{r.name}</b> — {r.message}
                  </div>
                ))}
              </div>
            ) : null}
          </article>
        );
      })}
      <div className="hint" style={{ textAlign: "center" }}>
        Online = kompyuterdan oxirgi 25 daqiqada signal kelgan (yengil worker har 10 daqiqada, ilova ochiq bo&apos;lsa har 30 soniyada). Og&apos;ir ish paytida signal yuborilmaydi, shu paytda «band» ko&apos;rinadi. Task Scheduler vazifasi o&apos;rnatilmagan va ilova yopiq bo&apos;lsa, yoniq kompyuter ham offline ko&apos;rinishi mumkin.
      </div>
    </>
  );
}

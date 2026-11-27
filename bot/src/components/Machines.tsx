"use client";

import { Check, Cog, History as HistoryIcon, Laptop, Lock, Monitor, Play, RotateCw, Stethoscope, TriangleAlert, X, Zap } from "lucide-react";
import { api, confirmBox, haptic } from "@/lib/client";
import { setMachines, useMachineStore } from "@/lib/machines-store";
import type { CheckItem, Machine } from "@/lib/types";
import type { Toast } from "./App";
import { Alert, Btn, Empty, Skel, Status, useBusy } from "./ui";
import s from "./Machines.module.css";

/** "1 daqiqa oldin" - oxirgi signal, tekshiruv va h.k. uchun. */
export const ago = (iso: string | null) => {
  if (!iso) return "—";
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (sec < 60) return `${sec} soniya oldin`;
  if (sec < 3600) return `${Math.round(sec / 60)} daqiqa oldin`;
  if (sec < 86400) return `${Math.round(sec / 3600)} soat oldin`;
  return `${Math.round(sec / 86400)} kun oldin`;
};

/** "3 soatdan beri" - oflayn kompyuter uchun. */
const since = (iso: string | null) => {
  if (!iso) return "uzoq vaqtdan beri";
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (sec < 3600) return `${Math.max(1, Math.round(sec / 60))} daqiqadan beri`;
  if (sec < 86400) return `${Math.round(sec / 3600)} soatdan beri`;
  return `${Math.round(sec / 86400)} kundan beri`;
};

/** Kerakli dastur qatorining matni: dasturlar "o'rnatilgan", YouTube "ulangan", Gemini/jadval esa kompyuter yozgan izoh. */
function toolText(t: NonNullable<Machine["tools"]>[number]) {
  if (t.key === "gemini" || t.key === "schedule") return t.detail || (t.ok ? "tayyor" : "sozlanmagan");
  if (t.name.startsWith("YouTube")) return t.ok ? (t.detail ? `ulangan: ${t.detail}` : "ulangan") : "ulanmagan";
  if (!t.ok) return "o'rnatilmagan";
  return t.detail && t.name.startsWith("Ovoz") ? `o'rnatilgan · ${t.detail}` : "o'rnatilgan";
}

/** Tekshiruv so'rovi yuborilgan, kompyuter hali natija yozmagan. */
export const checkPending = (m: Machine) =>
  m.check.requested && (!m.check.finishedAt || (!!m.check.requestedAt && m.check.finishedAt < m.check.requestedAt));

/**
 * Kompyuterlar ro'yxati - butun ilova uchun bitta umumiy manba (lib/machines-store): bitta polling, faqat sahifa ko'rinib turganda.
 * enabled=false (qo'shimcha token) bo'lsa so'rov yuborilmaydi: kompyuter asosiy hisob egasiniki. Xato shu bo'limda ko'rsatiladi.
 */
export function useMachines(_toast: Toast, enabled: boolean) {
  const { list, error, reload } = useMachineStore(enabled);
  return { list, error, reload };
}

/** Kompyuter kartalari skeleti: faqat shu bo'lim o'rnida. */
export function MachinesSkeleton() {
  return (
    <div className="card" aria-busy="true" aria-label="Kompyuterlar yuklanmoqda">
      <div className={s.head}>
        <Skel h={44} w={44} r={14} />
        <div className={s.name}><Skel h={17} w="45%" /><Skel h={12} w="65%" /></div>
      </div>
      <div className={s.strip}>
        {[0, 1, 2].map((i) => <div key={i} className={s.sig}><Skel h={6} r={999} /><Skel h={11} w="60%" /></div>)}
      </div>
      <div className={s.actions}><Skel h={44} r={14} /><Skel h={44} r={14} /></div>
    </div>
  );
}

const MARK: Record<CheckItem["level"], { icon: typeof Check; cls: string }> = {
  Ok: { icon: Check, cls: s.ok }, Warning: { icon: TriangleAlert, cls: s.warn }, Failed: { icon: X, cls: s.bad },
};

/** Kompyuterlar: online/offline, ilova ochiqligi, worker faoliyati, kerakli dasturlar va botdan integratsiyalarni tekshirish. */
/** part: "main" - faqat kompyuter kartasi, "side" - faqat kerakli dasturlar va tekshiruv (Faoliyat sahifasining ikki ustuni uchun). */
export default function Machines({ list, error, reload, toast, part }: { list: Machine[] | null; error: string | null; reload: () => Promise<void>; toast: Toast; part?: "main" | "side" }) {
  const busy = useBusy((m) => toast(m, true));

  async function check(name: string) {
    haptic.tap();
    const r = await busy.run(`check:${name}`, async () => {
      // Javobda yangilangan ro'yxat ham keladi - alohida GET kutilmaydi.
      const r = await api<{ ok: boolean; message: string; machines?: Machine[] }>("/api/machines/check", { machine: name });
      if (r.machines) setMachines(r.machines); else await reload();
      if (!r.ok) throw new Error(r.message);
      return r;
    });
    if (r) toast(r.message, false);
  }

  async function runNow(name: string) {
    if (!(await confirmBox(`${name}: ishni hozir boshlaymi? Worker jadvalni kutmasdan, navbatdagi barcha mavzulardan video tayyorlaydi (chiqish vaqti yaqinlari birinchi).`))) return;
    haptic.tap();
    const r = await busy.run(`run:${name}`, async () => {
      const r = await api<{ ok: boolean; message: string; machines?: Machine[] }>("/api/machines/run", { machine: name });
      if (r.machines) setMachines(r.machines); else await reload();
      if (!r.ok) throw new Error(r.message);
      return r;
    });
    if (r) toast(r.message, false);
  }

  if (!list && error) {
    return (
      <div className="card">
        <Alert>Kompyuterlar holatini olib bo&apos;lmadi: {error}</Alert>
        <Btn tone="secondary" icon={RotateCw} loading={busy.is("reload")} onClick={() => void busy.run("reload", reload)}>Qayta urinish</Btn>
      </div>
    );
  }
  if (!list) return <MachinesSkeleton />;
  if (list.length === 0) {
    return (
      <div className="card">
        <Empty icon={Monitor} title="Kompyuter hali ulanmagan">Hali hech bir kompyuter xabar bermagan. YtAuto ilovasini yangi versiyada bir marta oching.</Empty>
      </div>
    );
  }

  return (
    <>
      {list.map((m) => {
        const pending = checkPending(m);
        const isBusy = m.worker.state === "busy";
        const DeviceIcon = m.online ? Monitor : Laptop;
        const runLabel = m.run.requested ? "Ishga tushirish kutilmoqda" : isBusy ? "Worker band" : "Hozir ishga tushirish";
        const showCheck = m.check.results.length > 0 && !pending;
        return (
          <div key={m.name} className={s.pair}>
            {part !== "side" ? <article className={`card ${m.online ? "" : s.offline}`} aria-label={`Kompyuter ${m.name}`}>
              <div className={s.head}>
                <span className={`${s.tile} ${m.online ? "" : s.dim}`}>
                  <DeviceIcon size={22} />
                  <i className={`${s.dot} ${!m.online ? s.dotOff : isBusy ? s.dotBusy : s.dotOn}`} aria-hidden />
                </span>
                <div className={s.name}>
                  <h2 className={`h2 ${m.online ? "" : s.dimText}`}>{m.name}</h2>
                  <span className="sub tnum">{m.version ? `v${m.version.replace(/\.0$/, "")} · ` : ""}signal {ago(m.lastSeen)}</span>
                </div>
                {m.online && m.agent.live ? (
                  <span className={s.live} title="Doimiy agent (Cloudflare tunnel): bot so'rovlari darhol bajariladi, holat jonli keladi">
                    <Zap size={13} strokeWidth={2.5} />Real vaqt
                  </span>
                ) : null}
              </div>

              <dl className={s.stats}>
                <div className={s.stat}>
                  <dt>Holat</dt>
                  <dd className={m.online ? s.vOk : s.vMuted}><i aria-hidden />{m.online ? "Onlayn" : "Oflayn"}</dd>
                </div>
                <div className={s.stat}>
                  <dt>Aloqa</dt>
                  <dd className={!m.online ? s.vMuted : m.agent.live ? s.vOk : s.vWarn}><i aria-hidden />
                    {!m.online ? "—" : m.agent.live ? "Darhol" : "Sekin"}
                  </dd>
                </div>
                <div className={s.stat}>
                  <dt>Worker</dt>
                  <dd className={isBusy ? s.vBusy : m.worker.state === "idle" ? s.vInk : s.vMuted}><i aria-hidden />
                    {isBusy ? <>Band{m.worker.jobId ? <small className="tnum">#{m.worker.jobId}</small> : null}</> : m.worker.state === "idle" ? "Bo'sh" : "—"}
                  </dd>
                </div>
              </dl>
              {m.online && !m.agent.live ? (
                <div className="hint">Agent ishlamayapti: bot so&apos;rovlari kompyuter keyingi tekshiruvida bajariladi. YtAuto&apos;ning yangi versiyasini o&apos;rnating yoki ilovani bir marta oching.</div>
              ) : null}

              {isBusy ? (
                <div className={s.note}>
                  <Cog size={16} />
                  <span>{m.worker.detail ? <><b>{m.worker.detail}</b> · </> : null}Boshlangan: {ago(m.worker.seen)}.{m.agent.live ? " Holat jonli yangilanadi." : " Ish tugagach bu yer o'zi yangilanadi."}</span>
                </div>
              ) : null}
              {!m.online ? <div className="hint">Kompyuter o&apos;chiq yoki internetga ulanmagan — {since(m.lastSeen)} signal yo&apos;q.</div> : null}

              <div className={s.actions}>
                <Btn
                  tone={m.online && !isBusy && !m.run.requested ? "primary" : "secondary"}
                  icon={isBusy ? Lock : Play}
                  loading={busy.is(`run:${m.name}`) || m.run.requested}
                  disabled={isBusy}
                  onClick={() => void runNow(m.name)}
                >{runLabel}</Btn>
                <Btn
                  tone="secondary"
                  icon={Stethoscope}
                  loading={busy.is(`check:${m.name}`) || pending}
                  done={busy.done(`check:${m.name}`) && !pending}
                  onClick={() => void check(m.name)}
                >{pending ? "Tekshirilmoqda" : "Tekshirish"}</Btn>
              </div>
              {pending ? <div className="hint">So&apos;rov yuborildi. Kompyuter {m.agent.live ? "darhol bajarib" : "yoqilganda olib"}, natijani yuboradi.</div> : null}
            </article> : null}

            {part !== "main" && (m.tools || showCheck) ? (
              <section className="card" aria-label={`${m.name}: kerakli dasturlar`}>
                {m.tools ? (
                  <>
                    <div className="card-head">
                      <div className="t">
                        <h2 className="h2">Kerakli dasturlar</h2>
                        {list.length > 1 ? <span className="sub">{m.name}</span> : null}
                      </div>
                      <span className="sub tnum">{ago(m.toolsUpdatedAt)}</span>
                    </div>
                    <ul className={s.tools}>
                      {m.tools.map((t) => (
                        <li key={t.name}>
                          <span className={`${s.mark} ${t.ok ? s.ok : s.bad}`} aria-label={t.ok ? "tayyor" : "yetishmaydi"}>{t.ok ? <Check size={13} /> : <X size={13} />}</span>
                          <b>{t.name}</b>
                          <span className={t.ok ? s.detail : `${s.detail} ${s.detailBad}`}>{toolText(t)}</span>
                        </li>
                      ))}
                    </ul>
                  </>
                ) : null}

                {showCheck ? (
                  <>
                    {m.tools ? <div className={s.divider} /> : null}
                    <div className="card-head">
                      <div className="t">
                        <span className={s.checkTitle}>Oxirgi tekshiruv</span>
                        <span className="sub tnum">{ago(m.check.finishedAt)}{m.check.durationSec ? ` · ${m.check.durationSec.toFixed(0)} s` : ""}</span>
                      </div>
                      {m.check.stale ? <Status>Eskirgan</Status> : null}
                    </div>
                    {m.check.stale ? (
                      <div className={s.note}>
                        <HistoryIcon size={16} />
                        <span>Tekshiruvdan keyin sozlamalar (Gemini, jadval yoki YouTube) o&apos;zgargan. Hozirgi holat {m.tools ? "yuqoridagi ro'yxatda" : "«Kerakli dasturlar» ro'yxatida"} — to&apos;liq tekshiruv uchun «Tekshirish»ni bosing.</span>
                      </div>
                    ) : null}
                    <ul className={`${s.results} ${m.check.stale ? s.stale : ""}`}>
                      {m.check.results.map((r) => {
                        const mk = MARK[r.level] ?? { icon: Check, cls: "" };
                        const Icon = mk.icon;
                        return (
                          <li key={r.name}>
                            <span className={`${s.mark} ${m.check.stale ? "" : mk.cls}`} aria-label={r.level}><Icon size={13} /></span>
                            <div>
                              <b>{r.name}</b>
                              <span>{r.message}</span>
                            </div>
                          </li>
                        );
                      })}
                    </ul>
                  </>
                ) : null}
              </section>
            ) : null}
          </div>
        );
      })}
    </>
  );
}

"use client";

import { useCallback, useEffect, useState } from "react";
import { CircleAlert, Gauge, RotateCw, ScrollText } from "lucide-react";
import { api, haptic } from "@/lib/client";
import { useSchedule, type Schedule } from "@/lib/settings-data";
import { StatusText, type JobLogs, type Machine, type RunStats, type State, type Topic, type WorkerRun } from "@/lib/types";
import type { Toast } from "./App";
import Machines, { ago, useMachines } from "./Machines";
import { Alert, Btn, Empty, Sheet, Skel, Status, useBusy } from "./ui";
import s from "./History.module.css";

/** Desktop worker hodisalaridagi bosqich nomi (script, voice...) -> ko'rinadigan nom (VideoDetail bilan bir xil). */
const EVENT_TAG: Record<string, string> = { script: "Ssenariy", voice: "Ovoz", align: "Subtitr", edit: "Montaj", render: "Render", validate: "Sifat", archive: "Arxiv", upload: "YouTube" };
const STAGE_NAME: Record<number, string> = { 1: "Ssenariy", 2: "Ovoz", 3: "Subtitr vaqtlari", 4: "Montaj", 5: "Render", 6: "Sifat tekshiruvi", 7: "Arxiv", 8: "YouTube" };
const STATE_NAME: Record<number, string> = { 0: "kutmoqda", 1: "ishlamoqda", 2: "tayyor", 3: "xato", 4: "o'tkazildi" };
const STATE_TONE: Record<number, Tone> = { 1: "accent", 2: "ok", 3: "bad" };
const TRIGGER: Record<string, string> = { scheduled: "Jadval", remote: "Bot so'rovi", bot: "Bot so'rovi", manual: "Qo'lda", selftest: "Sinov", check: "Tekshiruv" };

type Tone = "neutral" | "accent" | "ok" | "bad";

// ------------------------------------------------------------------ formatlash (o'nlik vergul, minglik bo'shliq: "1,4 soat", "6 820 MB")
const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const p2 = (n: number) => String(n).padStart(2, "0");
const num = (n: number, d = 0) => {
  const [i, f] = Math.abs(n).toFixed(d).split(".");
  return (n < 0 ? "-" : "") + i.replace(/\B(?=(\d{3})+(?!\d))/g, " ") + (f ? "," + f : "");
};
const hm = (d: Date) => `${p2(d.getHours())}:${p2(d.getMinutes())}`;
const dt = (iso: string) => { const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}, ${hm(d)}`; };
/** "Bugun, 01:00", "Kecha, 01:00" yoki "21 noy, 14:22". */
const day = (iso: string) => {
  const d = new Date(iso);
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const diff = Math.round((today.getTime() - new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()) / 86400000);
  return diff === 0 ? `Bugun, ${hm(d)}` : diff === 1 ? `Kecha, ${hm(d)}` : dt(iso);
};
const tm = (iso: string) => { const d = new Date(iso); return `${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`; };
/** Tugamagan ishga tushish uchun davomiylik hozirgacha hisoblanadi («34 daq»). */
const dur = (a: string, b: string | null) => {
  const m = ((b ? new Date(b).getTime() : Date.now()) - new Date(a).getTime()) / 60000;
  return m < 1 ? "<1 daq" : m < 90 ? `${num(m)} daq` : `${num(m / 60, 1)} soat`;
};
const bytes = (b: number) => (b >= 2 ** 30 ? `${num(b / 2 ** 30, 2)} GB` : b >= 2 ** 20 ? `${num(b / 2 ** 20)} MB` : `${num(b / 1024)} KB`);

// ------------------------------------------------------------------ holat belgilari
const OUTCOME: Record<string, { text: string; tone: Tone }> = {
  completed: { text: "Tayyor", tone: "ok" }, ok: { text: "Tayyor", tone: "ok" }, success: { text: "Tayyor", tone: "ok" }, done: { text: "Tayyor", tone: "ok" },
  published: { text: "Chiqdi", tone: "ok" },
  failed: { text: "Xato", tone: "bad" }, error: { text: "Xato", tone: "bad" }, crash: { text: "Ishdan chiqdi", tone: "bad" },
  needsattention: { text: "E'tibor kerak", tone: "bad" }, attention: { text: "E'tibor kerak", tone: "bad" },
  queueempty: { text: "Navbat bo'sh", tone: "neutral" }, idle: { text: "Navbat bo'sh", tone: "neutral" },
  cancelled: { text: "Bekor qilindi", tone: "neutral" }, stopped: { text: "To'xtatildi", tone: "neutral" },
  stoppedbeforeupload: { text: "Yuklashdan oldin to'xtadi", tone: "neutral" }, refresh: { text: "Yangilash", tone: "neutral" },
};

function outcome(r: WorkerRun): { text: string; tone: Tone } {
  if (!r.outcome) return r.finishedAt ? { text: "Tugadi", tone: "neutral" } : { text: "Ishlamoqda", tone: "accent" };
  const known = OUTCOME[r.outcome.toLowerCase()];
  if (known) return known;
  // Bir nechta video: "2/3 video tayyor".
  const m = /^(\d+)\/(\d+)\b/.exec(r.outcome);
  if (m) return { text: r.outcome, tone: m[1] === m[2] ? "ok" : "bad" };
  return { text: r.outcome, tone: "neutral" };
}

function topicTone(t: Topic): Tone {
  if (t.status === 11) return "ok";
  if (t.status === 12 || t.status === 13) return "bad";
  if (t.claimed && t.status < 9) return "accent";
  return "neutral";
}

const errMsg = (e: unknown) => (e instanceof Error ? e.message : String(e));
const RUNS_PAGE = 10;
const VIDEOS_PAGE = 8;

// ------------------------------------------------------------------ fokus: kompyuter hozir ishlayaptimi
type Focal = { text: string; tone: Tone; who?: string; rest: string } | null;

function ownerFocal(list: Machine[] | null, error: string | null, schedule: Schedule | null): Focal {
  if (!list) return error ? { text: "Noma'lum", tone: "neutral", rest: "Kompyuter holatini olib bo'lmadi." } : null;
  const busyM = list.find((m) => m.worker.state === "busy");
  if (busyM) {
    return {
      text: "Ishlayapti", tone: "accent", who: busyM.name,
      rest: `${busyM.worker.jobId ? `#${busyM.worker.jobId}-videoni tayyorlamoqda` : "og'ir ish jarayonida"} · ${ago(busyM.worker.seen)} boshlandi`,
    };
  }
  const onlineM = list.find((m) => m.online);
  if (onlineM) {
    const rest = onlineM.run.requested ? "ishga tushirish so'rovi yuborildi — kompyuter darhol boshlaydi"
      : schedule?.enabled ? `onlayn · navbatdagi videolar ${schedule.startTime} da tayyorlanadi`
      : "onlayn · hozir ish yo'q";
    return { text: "Kutish rejimida", tone: "ok", who: onlineM.name, rest };
  }
  if (list.length === 0) return { text: "Oflayn", tone: "bad", rest: "Hali hech bir kompyuter xabar bermagan." };
  return { text: "Oflayn", tone: "bad", who: list[0].name, rest: `oxirgi signal ${ago(list[0].lastSeen)}` };
}

function guestFocal(state: State | null): Focal {
  if (!state) return null;
  const host = state.account.hostLabel ? `«${state.account.hostLabel}»` : "Asosiy hisob kompyuteri";
  const working = state.topics.find((t) => t.claimed && t.status >= 1 && t.status <= 8);
  if (working) return { text: "Ishlayapti", tone: "accent", who: host, rest: `#${working.id}-videoni tayyorlamoqda` };
  return { text: "Kutish rejimida", tone: "neutral", who: host, rest: "videolaringizni o'z ish vaqtida tayyorlaydi" };
}

function windowText(sc: Schedule | null) {
  if (!sc) return null;
  if (!sc.enabled) return "Jadval o'chiq — ish faqat «Hozir ishga tushirish» bilan";
  const h = Number(sc.startTime.slice(0, 2));
  return `${h < 6 || h >= 20 ? "Tungi ish oynasi" : "Ish oynasi"} ${sc.startTime}–${sc.endTime}`;
}

// ------------------------------------------------------------------ bitta ishga tushishning resurslari (ochiladigan)
const DISK_COLORS = ["var(--ink)", "var(--muted)", "var(--faint)", "var(--line)"];

export default function History({ state, toast }: { state: State | null; toast: Toast }) {
  // Qo'shimcha token: kompyuter va worker ishga tushishlari kompyuter egasiniki - faqat o'z videolari loglari ko'rinadi.
  const guest = state?.account?.guest === true;
  const machines = useMachines(toast, !guest);
  const { schedule, error: scheduleError } = useSchedule();
  const busy = useBusy((m) => toast(m, true));

  const [runs, setRuns] = useState<WorkerRun[] | null>(null);
  const [runsError, setRunsError] = useState<string | null>(null);
  const [runsShown, setRunsShown] = useState(RUNS_PAGE);
  const [videosShown, setVideosShown] = useState(VIDEOS_PAGE);
  const [logs, setLogs] = useState<JobLogs | null>(null);

  const loadRuns = useCallback(async () => {
    try { setRuns(await api<WorkerRun[]>("/api/history")); setRunsError(null); }
    catch (e) { setRunsError(errMsg(e)); toast(errMsg(e), true); }
  }, [toast]);

  useEffect(() => {
    if (guest) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- mount'da bir marta yuklanadi
    void loadRuns();
  }, [loadRuns, guest]);

  const closeLogs = useCallback(() => setLogs(null), []);

  async function openLogs(key: string, id: number) {
    haptic.tap();
    const r = await busy.run(key, () => api<JobLogs>(`/api/logs?id=${id}`));
    if (r) setLogs(r);
  }

  function refresh() {
    haptic.tap();
    void busy.run("refresh", () => Promise.all([guest ? Promise.resolve() : loadRuns(), guest ? Promise.resolve() : machines.reload()]));
  }

  const videos = [...(state?.topics ?? [])].sort((a, b) => b.id - a.id);
  const withStats = (runs ?? []).filter((r) => r.stats);
  const latest = withStats[0]?.stats ?? null;
  const avg = (f: (st: RunStats) => number) => (withStats.length ? withStats.reduce((a, r) => a + f(r.stats!), 0) / withStats.length : 0);
  const focal = guest ? guestFocal(state) : ownerFocal(machines.list, machines.error, schedule);
  const win = guest ? (state?.account.hostLabel ? `Videolar «${state.account.hostLabel}» kompyuterida tayyorlanadi` : "Videolar asosiy hisob kompyuterida tayyorlanadi") : windowText(schedule);
  const maxRam = latest ? Math.max(1, ...latest.tools.map((t) => t.peakRamMb)) : 1;

  return (
    <div className="page">
      <header className={s.top}>
        <div className={s.context}>
          <h1 className="label">Faoliyat</h1>
          {win ? <span className={s.window}>{win}</span> : state && !guest && !scheduleError ? <Skel h={13} w={180} /> : null}
        </div>
        <button type="button" className={`round ${s.refresh}`} aria-label="Yangilash" disabled={busy.is("refresh")} onClick={refresh}>
          <RotateCw size={20} className={busy.is("refresh") ? "spin" : undefined} />
        </button>
      </header>

      <section className={s.summary} aria-live="polite">
        {focal ? (
          <>
            <div className={s.focalRow}>
              <span className={`${s.dot} ${s[focal.tone] ?? ""}`} aria-hidden><i /></span>
              <p className="focal">{focal.text}</p>
            </div>
            <p className={s.focalSub}>{focal.who ? <span className={s.who}>{focal.who} </span> : null}{focal.rest}</p>
          </>
        ) : (
          <div className={s.focalSkel} aria-busy="true" aria-label="Yuklanmoqda"><Skel h={40} w="62%" /><Skel h={14} w="85%" /></div>
        )}
      </section>

      {!guest ? (
        <div className={`cards ${s.flow}`}>
          <Machines list={machines.list} error={machines.error} reload={machines.reload} toast={toast} />
        </div>
      ) : null}

      <div className={`cards ${s.flow}`}>
        {!guest ? (
          <section className={s.runs} aria-labelledby="runs-title">
            <div className={s.sectionHead}>
              <h2 className="h2" id="runs-title">Ishga tushishlar</h2>
              {runs && runs.length ? <span className="sub tnum">so&apos;nggi {runs.length} ta</span> : null}
            </div>

            {!runs && runsError ? (
              <div className="card">
                <Alert>Ishga tushishlar ro&apos;yxatini olib bo&apos;lmadi: {runsError}</Alert>
                <Btn tone="secondary" icon={RotateCw} loading={busy.is("runs-retry")} onClick={() => void busy.run("runs-retry", loadRuns)}>Qayta urinish</Btn>
              </div>
            ) : !runs ? (
              <ol className={s.timeline} aria-busy="true" aria-label="Yuklanmoqda">
                {[0, 1, 2].map((i) => (
                  <li key={i} className={s.run}>
                    <div className={s.rail}><span className={s.dot}><i /></span>{i < 2 ? <span className={s.line} /> : null}</div>
                    <div className={s.body}><Skel h={16} w="55%" /><Skel h={12} w="75%" /></div>
                  </li>
                ))}
              </ol>
            ) : runs.length === 0 ? (
              <Empty icon={Gauge} title="Hali ishga tushish yo'q">Worker hali ishga tushmagan. Videolar ish oynasida yoki «Hozir ishga tushirish» bilan tayyorlanadi.</Empty>
            ) : (
              <>
                <ol className={s.timeline}>
                  {runs.slice(0, runsShown).map((r, idx, arr) => {
                    const o = outcome(r);
                    const last = idx === arr.length - 1;
                    return (
                      <li key={r.runId} className={s.run}>
                        <div className={s.rail}>
                          <span className={`${s.dot} ${s[o.tone] ?? ""}`} aria-hidden><i /></span>
                          {!last ? <span className={s.line} /> : null}
                        </div>
                        <div className={`${s.body} ${last ? s.bodyLast : ""}`}>
                          <div className={s.line1}>
                            <span className={s.when}>{day(r.startedAt)}<span className="muted"> · {dur(r.startedAt, r.finishedAt)}</span></span>
                            {r.jobId ? <span className={s.jobId}>#{r.jobId}</span> : null}
                          </div>
                          <div className={s.line2}>
                            <Status tone={o.tone}>{o.text}</Status>
                            <span className="sub">{TRIGGER[r.trigger ?? ""] ?? r.trigger ?? "—"}{r.machine ? ` · ${r.machine}` : ""}</span>
                          </div>
                        </div>
                      </li>
                    );
                  })}
                </ol>
                {runs.length > runsShown ? (
                  <Btn tone="secondary" size="sm" block onClick={() => { haptic.tap(); setRunsShown((n) => n + RUNS_PAGE); }}>
                    Yana {Math.min(RUNS_PAGE, runs.length - runsShown)} ta ko&apos;rsatish
                  </Btn>
                ) : null}
              </>
            )}
          </section>
        ) : null}

        {!guest ? (
          !runs ? (
            runsError ? null : (
              <div className="card" aria-busy="true" aria-label="Yuklanmoqda">
                <Skel h={18} w="50%" /><Skel h={10} r={999} /><Skel h={12} /><Skel h={12} w="80%" />
              </div>
            )
          ) : !latest ? (
            <div className="card">
              <Empty icon={Gauge} title="Statistika hali yo'q">U har worker ishga tushganda yoziladi.</Empty>
            </div>
          ) : (
            <section className="card" aria-labelledby="stats-title">
              <div className="card-head">
                <h2 className="h2" id="stats-title">Hajm va resurslar</h2>
                <span className="sub tnum">{dt(withStats[0].startedAt)}{latest.machine ? ` · ${latest.machine}` : ""}</span>
              </div>
              <div className={s.total}>
                <b className="tnum">{bytes(latest.diskTotalBytes)} band</b>
                <span className="sub tnum">bo&apos;sh disk {num(latest.system.freeDiskGbStart, 0)} → {num(latest.system.freeDiskGbEnd, 0)} GB ({latest.system.diskRoot})</span>
              </div>
              {latest.diskTotalBytes > 0 ? (
                <div className={s.diskBar} role="img" aria-label={latest.disk.map((d) => `${d.name}: ${bytes(d.bytes)}`).join(", ")}>
                  {latest.disk.map((d, i) => (
                    <i key={d.name} style={{ width: `${(d.bytes / latest.diskTotalBytes) * 100}%`, background: DISK_COLORS[Math.min(i, DISK_COLORS.length - 1)] }} />
                  ))}
                </div>
              ) : null}
              <ul className={s.diskList}>
                {latest.disk.map((d, i) => (
                  <li key={d.name} title={d.path}>
                    <i style={{ background: DISK_COLORS[Math.min(i, DISK_COLORS.length - 1)] }} />
                    <span>{d.name}</span>
                    <b className="tnum">{bytes(d.bytes)}</b>
                  </li>
                ))}
              </ul>
              <div className={s.divider} />
              <span className="label">O&apos;rtacha · {withStats.length} ta ishga tushish</span>
              <div className={s.avgs}>
                <div><b className="tnum">{num(avg((st) => st.system.peakUsedRamGb), 1)} GB</b><span>band RAM</span></div>
                <div><b className="tnum">{num(avg((st) => st.durationSec) / 60, 1)} daq</b><span>davomiylik</span></div>
                <div><b className="tnum">{num(avg((st) => st.tools.reduce((a, t) => a + t.cpuSeconds, 0)) / 60, 1)} daq</b><span>CPU vaqti</span></div>
              </div>
              <span className="label">So&apos;nggi ishga tushishda dasturlar</span>
              {latest.tools.length === 0 ? <div className="hint">Og&apos;ir dasturlar ishlatilmagan.</div> : (
                <ul className={s.usage}>
                  {latest.tools.map((t) => (
                    <li key={t.name}>
                      <div className={s.usageText}>
                        <b>{t.name}</b>
                        <span className="sub tnum">CPU {num(t.cpuSeconds)} s</span>
                        <strong className="tnum">{num(t.peakRamMb)} MB</strong>
                      </div>
                      <div className={s.track}><i style={{ width: `${(t.peakRamMb / maxRam) * 100}%` }} /></div>
                    </li>
                  ))}
                </ul>
              )}
              <div className={s.note}>RAM/CPU — faqat worker ishga tushirgan dasturlar bo&apos;yicha; «band RAM» esa butun kompyuter bo&apos;yicha.</div>
            </section>
          )
        ) : null}

        <section className="card" aria-labelledby="videos-title">
          <div className="card-head">
            <div className="t">
              <h2 className="h2" id="videos-title">Videolar loglari</h2>
              <span className="sub">Har video bosqichlari va hodisalari</span>
            </div>
            <span className="sub tnum">{videos.length} ta</span>
          </div>
          {!state ? (
            <div className={s.videoSkel} aria-busy="true" aria-label="Yuklanmoqda"><Skel h={14} /><Skel h={14} w="80%" /><Skel h={14} w="60%" /></div>
          ) : videos.length === 0 ? (
            <div className="hint">Videolar yo&apos;q.</div>
          ) : (
            <>
              <ul className={s.videos}>
                {videos.slice(0, videosShown).map((t) => (
                  <li key={t.id}>
                    <div className={s.videoText}>
                      <b>{t.title || t.topic}</b>
                      <div className={s.videoMeta}>
                        <span className="tnum">#{t.id}</span>
                        <Status tone={topicTone(t)}>{StatusText[t.status]}</Status>
                      </div>
                      {t.error ? <div className={s.videoErr}><CircleAlert size={14} />{t.error}</div> : null}
                    </div>
                    <Btn size="sm" tone="secondary" icon={ScrollText} loading={busy.is(`video-logs:${t.id}`)} onClick={() => void openLogs(`video-logs:${t.id}`, t.id)} aria-label={`#${t.id} loglarini ko'rish`}>
                      {busy.is(`video-logs:${t.id}`) ? "Yuklanmoqda" : "Loglar"}
                    </Btn>
                  </li>
                ))}
              </ul>
              {videos.length > videosShown ? (
                <Btn tone="secondary" size="sm" block onClick={() => { haptic.tap(); setVideosShown((n) => n + VIDEOS_PAGE); }}>
                  Yana {Math.min(VIDEOS_PAGE, videos.length - videosShown)} ta ko&apos;rsatish
                </Btn>
              ) : null}
            </>
          )}
        </section>
      </div>

      {!guest ? (
        <p className={s.footnote}>
          Onlayn — kompyuterdagi YtAuto agenti har 30 soniyada signal beradi va botdagi so&apos;rovlarni darhol bajaradi (Cloudflare tunnel); videolar esa tunda, ish oynasida tayyorlanadi.
          Agent Windows&apos;ga kirilganda o&apos;zi ishga tushadi; kompyuter o&apos;chiq yoki internetsiz bo&apos;lsa oflayn ko&apos;rinadi, so&apos;rovlar esa yoqilgach bajariladi.
        </p>
      ) : null}

      {logs ? (
        <Sheet
          title={logs.topic}
          sub={<span className="tnum">#{logs.id} · {StatusText[logs.status]}</span>}
          label="Video loglari"
          onClose={closeLogs}
          footer={<Btn tone="secondary" block onClick={closeLogs}>Yopish</Btn>}
        >
          {logs.stages.length > 0 ? (
            <div className="group">
              {logs.stages.map((st) => (
                <div key={st.stage} className={s.stage}>
                  <div className={s.stageRow}>
                    <b>{STAGE_NAME[st.stage] ?? `Bosqich ${st.stage}`}</b>
                    <Status tone={STATE_TONE[st.state] ?? "neutral"}>{STATE_NAME[st.state] ?? String(st.state)}</Status>
                  </div>
                  {st.attempts > 1 || st.durationMs ? (
                    <span className="sub tnum">{[st.attempts > 1 ? `${st.attempts} urinish` : "", st.durationMs ? `${num(st.durationMs / 1000)} s` : ""].filter(Boolean).join(" · ")}</span>
                  ) : null}
                  {st.error ? <Alert>{st.error}</Alert> : null}
                </div>
              ))}
            </div>
          ) : null}
          <div className={s.events}>
            <span className="label">Hodisalar</span>
            <div className={`logbox ${s.logbox}`}>
              {logs.events.length === 0 ? <div className="hint">Hodisalar yo&apos;q.</div> : logs.events.map((e) => (
                <div key={e.id} className={`logline ${e.level.toLowerCase()}`}>
                  <span className="t">{tm(e.at)}</span>{e.stage ? <span className="s">{EVENT_TAG[e.stage.toLowerCase()] ?? e.stage}</span> : null}{e.message}
                </div>
              ))}
            </div>
          </div>
        </Sheet>
      ) : null}
    </div>
  );
}

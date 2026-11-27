"use client";

import { useCallback, useEffect, useId, useMemo, useState } from "react";
import { Globe, Lock, Monitor, MonitorOff, RotateCw } from "lucide-react";
import { confirmBox, haptic } from "@/lib/client";
import { useMachineStore } from "@/lib/machines-store";
import { differs, useSchedule, type Schedule } from "@/lib/settings-data";
import type { Machine } from "@/lib/types";
import { Alert, Btn, Card, Empty, Field, PageHead, SaveBtn, Skel, SkelCard, Status, ToggleRow, useBusy } from "../ui";
import type { SettingsPageProps } from "./types";
import s from "./Computer.module.css";

/**
 * «Kompyuter va jadval» (dizayn: Sozlamalar → 2c0htu). Kompyuter darajasidagi sozlamalar - faqat asosiy hisob egasi uchun:
 * kunlik ish oynasi (24 soatlik halqa: oyna, «hozir» belgisi, necha vaqt qoldi) va kompyuterlar ro'yxati (faqat ko'rish).
 * Cheklovlar va VoiceStudio sozlamalari botdan olib tashlandi - kompyuterdagi standart qiymatlar ishlatiladi.
 * Qo'shimcha token (mehmon) bu sahifani hub'da ko'rmaydi; baribir ochilsa - faqat izoh, hech narsa yuklanmaydi.
 */
export default function Computer(props: SettingsPageProps) {
  const { state, back } = props;
  const head = <Head back={back} />;

  if (!state) return <div className="page narrow">{head}<SkelCard lines={6} /><SkelCard lines={3} /></div>;

  if (state.account.guest) {
    const host = state.account.hostLabel;
    return (
      <div className="page narrow">
        {head}
        <Card title="Kompyuter egasida" action={<Status>Mehmon</Status>}>
          <div className={s.guest}>
            <span className={s.tile}><Lock size={20} /></span>
            <div className="sub">
              Videolaringiz {host ? <b>«{host}»</b> : "token bergan hisob"} kompyuterida, uning ish vaqtida tayyorlanadi.
              «Hozir chiqarish» va kompyuter sozlamalari (jadval, Claude, render, limitlar) kompyuter egasida.
            </div>
          </div>
        </Card>
      </div>
    );
  }

  return <OwnerComputer {...props} />;
}

function Head({ back }: { back: () => void }) {
  return <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Kompyuter va jadval" sub="Kompyuter qachon va qanday sharoitda ishlaydi" />;
}

/** Saqlashdan oldingi tasdiq (eski sozlamalardagidek). */
const CONFIRM = "Sozlamalar saqlansinmi? Yangi videolardan boshlab qo'llanadi.";

function OwnerComputer({ toast, back }: SettingsPageProps) {
  const fail = useCallback((m: string) => { haptic.fail(); toast(m, true); }, [toast]);
  const busy = useBusy(fail);
  /** Jadval yuklanmasa «Qayta urinish» bo'limni qayta o'rnatadi (useSchedule mount'da yuklaydi). */
  const [schKey, setSchKey] = useState(0);

  return (
    <div className="page narrow">
      <Head back={back} />
      <ScheduleSection key={schKey} busy={busy} toast={toast} onRetry={() => setSchKey((k) => k + 1)} />
      <MachinesSection />
    </div>
  );
}

// ------------------------------------------------------------------ vaqt yordamchilari
const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;
const toMin = (t: string) => { const [h, m] = t.split(":").map(Number); return h * 60 + m; };
const pad = (n: number) => String(n).padStart(2, "0");
const clock = (min: number) => `${pad(Math.floor(min / 60) % 24)}:${pad(min % 60)}`;

/** "130" → "01:30", "1:5" → "01:05" (maydondan chiqqanda). Tanib bo'lmasa o'zgarmaydi. */
function normalizeTime(v: string): string {
  const t = v.trim().replace(/[.\s]/g, ":");
  let m = /^(\d{1,2}):(\d{1,2})$/.exec(t);
  if (m) return `${pad(Number(m[1]))}:${pad(Number(m[2]))}`;
  m = /^(\d{1,2})(\d{2})$/.exec(t);
  if (m) return `${pad(Number(m[1]))}:${m[2]}`;
  if (/^\d{1,2}$/.test(t)) return `${pad(Number(t))}:00`;
  return v;
}

/** "3 soat 20 daqiqa" */
function span(min: number): string {
  const h = Math.floor(min / 60), m = min % 60;
  if (!h) return `${m} daqiqa`;
  return m ? `${h} soat ${m} daqiqa` : `${h} soat`;
}

const validTz = (tz: string) => { try { new Intl.DateTimeFormat("en", { timeZone: tz }); return !!tz.trim(); } catch { return false; } };

/** Berilgan vaqt zonasidagi joriy daqiqa (0..1439). Zona noto'g'ri bo'lsa - qurilmaning o'z vaqti. */
function minutesIn(tz: string, now: number): number {
  const d = new Date(now);
  try {
    const parts = new Intl.DateTimeFormat("en-GB", { timeZone: tz, hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).formatToParts(d);
    const h = Number(parts.find((p) => p.type === "hour")?.value), m = Number(parts.find((p) => p.type === "minute")?.value);
    if (Number.isFinite(h) && Number.isFinite(m)) return (h % 24) * 60 + m;
  } catch { /* noto'g'ri zona */ }
  return d.getHours() * 60 + d.getMinutes();
}

/** Har 30 soniyada yangilanadigan "hozir" (halqadagi belgi va "qoldi" matni uchun). */
function useNow(ms = 30_000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), ms); return () => window.clearInterval(t); }, [ms]);
  return now;
}

/** Vaqt zonalari ro'yxati (maydon takliflari uchun; eski brauzerlarda bo'sh). */
function useTimeZones(): string[] {
  return useMemo(() => {
    try {
      const f = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf;
      return f ? f("timeZone") : [];
    } catch { return []; }
  }, []);
}

// ------------------------------------------------------------------ jadval
type Busy = ReturnType<typeof useBusy>;

function ScheduleSection({ busy, toast, onRetry }: { busy: Busy; toast: SettingsPageProps["toast"]; onRetry: () => void }) {
  const { schedule, error, saveSchedule } = useSchedule();
  if (schedule) return <ScheduleCard schedule={schedule} busy={busy} toast={toast} save={saveSchedule} />;
  if (error) {
    return (
      <Card title="Kunlik jadval">
        <Alert>Jadvalni yuklab bo&apos;lmadi: {error}</Alert>
        <Btn tone="secondary" icon={RotateCw} onClick={onRetry}>Qayta urinish</Btn>
      </Card>
    );
  }
  return (
    <div className="card" aria-busy="true" aria-label="Jadval yuklanmoqda">
      <Skel h={18} w="55%" />
      <div className={s.dialSkel}><Skel h={216} w={216} r={999} /></div>
      <div className="grid2"><Skel h={46} /><Skel h={46} /></div>
      <Skel h={44} r={14} />
    </div>
  );
}

function ScheduleCard({ schedule, busy, toast, save }: { schedule: Schedule; busy: Busy; toast: SettingsPageProps["toast"]; save: (s: Schedule) => Promise<Schedule> }) {
  /** Qoralama: null - saqlangan qiymat ko'rsatiladi (serverdan yangisi kelsa ham). */
  const [draft, setDraft] = useState<Schedule | null>(null);
  const [tzOpen, setTzOpen] = useState(false);
  const now = useNow();
  const zones = useTimeZones();
  const tzListId = useId();
  const cur = draft ?? schedule;
  const set = (p: Partial<Schedule>) => setDraft({ ...cur, ...p });

  const startErr = HHMM.test(cur.startTime) ? null : "HH:mm formatida (masalan 01:00)";
  const endErr = HHMM.test(cur.endTime) ? null : "HH:mm formatida (masalan 04:00)";
  const tzErr = validTz(cur.timeZoneId) ? null : "Vaqt zonasi noto'g'ri (masalan Asia/Tashkent)";
  const dirty = !!draft && differs(draft, schedule);
  const invalid = !!(startErr || endErr || tzErr);
  const saving = busy.is("schedule");

  // Halqa: noto'g'ri yozilayotgan vaqt o'rniga saqlangani ko'rsatiladi.
  const start = toMin(startErr ? schedule.startTime : cur.startTime);
  const end = toMin(endErr ? schedule.endTime : cur.endTime);
  const tz = tzErr ? schedule.timeZoneId : cur.timeZoneId;
  const nowMin = minutesIn(tz, now);
  const dur = (end - start + 1440) % 1440;
  const inside = dur > 0 && (nowMin - start + 1440) % 1440 < dur;
  const left = !cur.enabled ? "Jadval o'chiq — faqat «Hozir ishga tushirish»"
    : dur === 0 ? "Boshlanish va tugash bir xil"
    : inside ? `Ish oynasida · tugashiga ${span((end - nowMin + 1440) % 1440)}`
    : `${span((start - nowMin + 1440) % 1440)} qoldi`;

  async function onSave() {
    if (!dirty || invalid || !draft) return;
    if (!(await confirmBox(CONFIRM))) return;
    const r = await busy.run("schedule", async () => { await save(draft); return true; });
    if (r) { setDraft(null); setTzOpen(false); haptic.ok(); toast("Jadval saqlandi."); }
  }

  return (
    <section className="card" aria-label="Kunlik jadval">
      <ToggleRow title="Kunlik jadval yoqilgan" sub="Har tun o'zi ishga tushadi" on={cur.enabled} onChange={(v) => set({ enabled: v })} />

      <Dial start={start} end={end} now={nowMin} enabled={cur.enabled} left={left} inside={inside} />

      <div className={s.legend}>
        <span className={s.key}><i className={s.dotAccent} />Ish oynasi</span>
        <span className={`${s.key} tnum`}><i className={s.dotInk} />Hozir {clock(nowMin)}</span>
        <button type="button" className={s.tzChip} aria-expanded={tzOpen || !!tzErr} onClick={() => setTzOpen((o) => !o)}>
          <Globe size={14} /><span>{cur.timeZoneId || "Vaqt zonasi"}</span>
        </button>
      </div>

      <div className="grid2">
        <Field label="Boshlanishi" error={startErr}>
          <input className={`inp tnum${startErr ? " err" : ""}`} value={cur.startTime} inputMode="numeric" placeholder="01:00" maxLength={5}
            aria-label="Ish oynasi boshi (HH:mm)" aria-invalid={!!startErr}
            onChange={(e) => set({ startTime: e.target.value })} onBlur={(e) => { const v = normalizeTime(e.target.value); if (v !== cur.startTime) set({ startTime: v }); }} />
        </Field>
        <Field label="Tugashi" error={endErr}>
          <input className={`inp tnum${endErr ? " err" : ""}`} value={cur.endTime} inputMode="numeric" placeholder="04:00" maxLength={5}
            aria-label="Ish oynasi oxiri (HH:mm)" aria-invalid={!!endErr}
            onChange={(e) => set({ endTime: e.target.value })} onBlur={(e) => { const v = normalizeTime(e.target.value); if (v !== cur.endTime) set({ endTime: v }); }} />
        </Field>
      </div>

      {tzOpen || tzErr ? (
        <Field label="Vaqt zonasi" error={tzErr} hint="IANA nomi: Asia/Tashkent, Europe/Moscow…">
          <input className={`inp${tzErr ? " err" : ""}`} value={cur.timeZoneId} list={zones.length ? tzListId : undefined} autoComplete="off" spellCheck={false}
            aria-label="Vaqt zonasi" aria-invalid={!!tzErr} onChange={(e) => set({ timeZoneId: e.target.value })} />
          {zones.length ? <datalist id={tzListId}>{zones.map((z) => <option key={z} value={z} />)}</datalist> : null}
        </Field>
      ) : null}

      <ToggleRow title="Uyqudan uyg'otish" sub="Uxlab qolgan kompyuterni o'zi uyg'otadi" on={cur.wakeToRun} onChange={(v) => set({ wakeToRun: v })} />

      <div className="hint">
        Jadval o&apos;chiq bo&apos;lsa ham «Hozir ishga tushirish» ishlaydi.
        {schedule.executionLimitHours ? <> Bir ishga tushishda ko&apos;pi bilan <span className="tnum">{schedule.executionLimitHours}</span> soat ishlaydi.</> : null}
      </div>

      <SaveBtn size="md" block dirty={dirty && !invalid} saving={saving} done={busy.done("schedule")} label={saving ? "Saqlanmoqda…" : "Saqlash"} onClick={() => void onSave()} />
    </section>
  );
}

/** 24 soatlik halqa: 00 tepada, soat yo'nalishida. Urg'u yoyi - ish oynasi, qora nuqta - hozir. */
function Dial({ start, end, now, enabled, left, inside }: { start: number; end: number; now: number; enabled: boolean; left: string; inside: boolean }) {
  const size = 216, c = size / 2, r = 100;
  const C = 2 * Math.PI * r;
  const dur = (end - start + 1440) % 1440;
  const pt = (min: number) => { const a = (min / 1440) * 2 * Math.PI - Math.PI / 2; return { x: c + r * Math.cos(a), y: c + r * Math.sin(a) }; };
  const a = pt(start), b = pt(end), n = pt(now);
  const h = Math.floor(dur / 60), m = dur % 60;
  return (
    <div className={`${s.dial}${enabled ? "" : " " + s.off}`} role="img"
      aria-label={`Ish oynasi ${clock(start)}–${clock(end)}, ${span(dur) || "0 daqiqa"}. Hozir ${clock(now)}. ${left}.`}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden>
        <circle className={s.track} cx={c} cy={c} r={r} />
        {dur > 0 ? (
          <circle className={s.arc} cx={c} cy={c} r={r} transform={`rotate(-90 ${c} ${c})`}
            strokeDasharray={`${(dur / 1440) * C} ${C}`} strokeDashoffset={-(start / 1440) * C} />
        ) : null}
        <text className={s.hour} x={c} y={c - 70}>00</text>
        <text className={s.hour} x={c + 72} y={c}>06</text>
        <text className={s.hour} x={c} y={c + 70}>12</text>
        <text className={s.hour} x={c - 72} y={c}>18</text>
        <circle className={s.handle} cx={a.x} cy={a.y} r={8} />
        <circle className={s.handle} cx={b.x} cy={b.y} r={8} />
        <circle className={s.now} cx={n.x} cy={n.y} r={5.5} />
      </svg>
      <div className={s.center}>
        <p className="focal">{!h && m ? m : m ? `${h}:${pad(m)}` : h} <span className="dim">{!h && m ? "daq" : "soat"}</span></p>
        <b className="tnum">{clock(start)} – {clock(end)}</b>
        <span className={`${s.left}${inside && enabled ? " " + s.inside : ""}`}>{left}</span>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ kompyuterlar (faqat ko'rish)
const ago = (iso: string | null, now: number) => {
  if (!iso) return "—";
  const sec = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (sec < 60) return `${sec} soniya oldin`;
  if (sec < 3600) return `${Math.round(sec / 60)} daqiqa oldin`;
  if (sec < 86400) return `${Math.round(sec / 3600)} soat oldin`;
  return `${Math.round(sec / 86400)} kun oldin`;
};
const since = (iso: string | null, now: number) => {
  if (!iso) return "Uzoq vaqtdan beri";
  const sec = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (sec < 3600) return `${Math.max(1, Math.round(sec / 60))} daqiqadan beri`;
  if (sec < 86400) return `${Math.round(sec / 3600)} soatdan beri`;
  return `${Math.round(sec / 86400)} kundan beri`;
};

type Tool = NonNullable<Machine["tools"]>[number];
/** Qisqa nom (dizayn: Gemini · Ovoz · Claude · OpenFilm · YouTube). */
const TOOL_SHORT: Record<string, string> = {
  gemini: "Gemini", tts: "Ovoz", voicestudio: "VoiceStudio", whisper: "Subtitr", claude: "Claude", openfilm: "OpenFilm", youtube: "YouTube", schedule: "Jadval",
};
const toolShort = (t: Tool) => TOOL_SHORT[t.key] ?? t.name.replace(/\s*\(.*\)\s*$/, "");

/** "OpenFilm o'rnatilmagan. YouTube ulanmagan." - ishlamayotgan dasturlar. */
function toolProblems(tools: Tool[]): string {
  const bad = tools.filter((t) => !t.ok);
  const install = bad.filter((t) => !["gemini", "schedule", "youtube"].includes(t.key)).map(toolShort);
  const out: string[] = [];
  if (install.length) out.push(`${install.join(", ")} o'rnatilmagan.`);
  if (bad.some((t) => t.key === "youtube")) out.push("YouTube ulanmagan.");
  const conf = bad.filter((t) => t.key === "gemini" || t.key === "schedule").map(toolShort);
  if (conf.length) out.push(`${conf.join(", ")} sozlanmagan.`);
  return out.join(" ");
}

const checkPending = (m: Machine) => m.check.requested && (!m.check.finishedAt || (!!m.check.requestedAt && m.check.finishedAt < m.check.requestedAt));

function lastCheck(m: Machine, now: number): string | null {
  if (checkPending(m)) return m.agent.live ? "Tekshiruv bajarilmoqda…" : "Tekshiruv kutilmoqda — kompyuter yoqilganda bajaradi";
  if (!m.check.finishedAt) return null;
  const failed = m.check.results.filter((r) => r.level === "Failed").length;
  const warn = m.check.results.filter((r) => r.level === "Warning").length;
  const res = failed ? `${failed} ta xato` : warn ? `${warn} ta ogohlantirish` : "hammasi ishlaydi";
  return `Oxirgi tekshiruv ${ago(m.check.finishedAt, now)} · ${res}${m.check.stale ? " · eskirgan" : ""}`;
}

/** Kompyuterlar - umumiy manbadan (lib/machines-store: bitta polling butun ilova uchun). Xato shu bo'limda ko'rsatiladi. */
function useMachineList() {
  return useMachineStore(true);
}

function MachinesSection() {
  const { list, error, reload, now } = useMachineList();
  const busy = useBusy();
  return (
    <>
      <div className="section-label" role="heading" aria-level={2}>
        <span>Kompyuterlar</span>
        {list?.length ? <span className={`${s.sectionHint} tnum`}>{list.length} ta · 15 s da yangilanadi</span> : null}
      </div>
      {!list ? (
        error ? (
          <Card>
            <Alert>Kompyuterlarni yuklab bo&apos;lmadi: {error}</Alert>
            <Btn tone="secondary" icon={RotateCw} loading={busy.is("reload")} onClick={() => void busy.run("reload", reload)}>Qayta urinish</Btn>
          </Card>
        ) : (
          <div className="card" aria-busy="true" aria-label="Kompyuterlar yuklanmoqda">
            <div className={s.mHead}><Skel h={40} w={40} r={10} /><div className={s.mName}><Skel h={17} w="45%" /><Skel h={12} w="65%" /></div></div>
            <div className={s.tools}>{[0, 1, 2, 3, 4].map((i) => <div key={i} className={s.tool}><Skel h={4} r={999} /><Skel h={10} w="70%" /></div>)}</div>
          </div>
        )
      ) : !list.length ? (
        <Card><Empty icon={Monitor} title="Kompyuter hali ulanmagan">Hali hech bir kompyuter xabar bermagan. YtAuto ilovasini yangi versiyada bir marta oching.</Empty></Card>
      ) : (
        <>
          <div className="group">
            {list.map((m) => <MachineRow key={m.name} m={m} now={now} />)}
          </div>
          <div className="hint">«Real vaqt» — kompyuterda doimiy YtAuto agenti ishlayapti (Cloudflare tunnel): botdagi so&apos;rovlar darhol bajariladi, video holati jonli keladi, tayyor videoni shu yerda ko&apos;rish mumkin.</div>
        </>
      )}
    </>
  );
}

function MachineRow({ m, now }: { m: Machine; now: number }) {
  const Icon = m.online ? Monitor : MonitorOff;
  const problems = m.tools ? toolProblems(m.tools) : "";
  const check = m.online ? lastCheck(m, now) : null;
  return (
    <article className={s.machine} aria-label={`${m.name}: ${m.online ? "onlayn" : "oflayn"}`}>
      <div className={s.mHead}>
        <span className={`${s.tile}${m.online ? "" : " " + s.tileOff}`}><Icon size={20} /></span>
        <div className={s.mName}>
          <b>{m.name}</b>
          <span className="sub tnum">{[m.version ? `v${m.version}` : null, `signal ${ago(m.lastSeen, now)}`].filter(Boolean).join(" · ")}</span>
        </div>
        <Status tone={m.online ? "ok" : "neutral"}>{m.online ? "Onlayn" : "Oflayn"}</Status>
      </div>

      {m.online ? (
        <div className={s.chips}>
          {m.agent.live ? <Status tone="ok">Real vaqt</Status> : null}
          <Status tone={m.app.open ? "ok" : "neutral"}>{m.app.open ? "Ilova ochiq" : "Ilova yopiq"}</Status>
          {m.worker.state === "busy" ? <Status tone="accent">Worker band{m.worker.jobId ? ` · #${m.worker.jobId}` : ""}</Status>
            : m.worker.state === "idle" ? <Status>Worker bo&apos;sh</Status> : null}
        </div>
      ) : null}
      {m.online && m.worker.state === "busy" && m.worker.detail ? <div className={`sub ${s.detail}`}>{m.worker.detail}</div> : null}

      {m.tools?.length ? (
        <ul className={s.tools} aria-label="Kerakli dasturlar">
          {m.tools.map((t) => (
            <li key={t.key || t.name} className={`${s.tool}${t.ok ? "" : " " + s.toolBad}`} title={`${t.name}${t.detail ? ` — ${t.detail}` : ""}`}>
              <i aria-hidden /><span>{toolShort(t)}</span><span className="sr-only">{t.ok ? " — tayyor" : " — ishlamaydi"}</span>
            </li>
          ))}
        </ul>
      ) : null}

      {check ? <div className="sub">{check}</div> : null}
      {!m.online || problems ? (
        <div className="sub">
          {!m.online ? `${since(m.lastSeen, now)} signal yo'q: kompyuter o'chiq yoki internetsiz.` : null}
          {!m.online && problems ? " " : null}
          {problems}
        </div>
      ) : null}
    </article>
  );
}

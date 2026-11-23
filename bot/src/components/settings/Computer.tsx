"use client";

import { useCallback, useEffect, useId, useMemo, useState } from "react";
import { ChevronDown, Globe, Lock, Minus, Monitor, MonitorOff, Plus, RotateCw, SlidersHorizontal } from "lucide-react";
import { api, confirmBox, haptic } from "@/lib/client";
import { differs, useBotConfig, useSchedule, type Schedule } from "@/lib/settings-data";
import type { BotConfig, Machine } from "@/lib/types";
import { Alert, Btn, Card, Empty, Field, PageHead, SaveBtn, Skel, SkelCard, Status, ToggleRow, useBusy } from "../ui";
import type { SettingsPageProps } from "./types";
import s from "./Computer.module.css";

/**
 * «Kompyuter va jadval» (dizayn: Sozlamalar → 2c0htu). Kompyuter darajasidagi sozlamalar - faqat asosiy hisob egasi uchun:
 * kunlik ish oynasi (24 soatlik halqa: oyna, «hozir» belgisi, necha vaqt qoldi), kompyuterlar ro'yxati (faqat ko'rish),
 * cheklovlar va yopiq «Kengaytirilgan (VoiceStudio)». Har karta o'z qismini o'zi saqlaydi (o'z «Saqlash» tugmasi).
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

/** Saqlashdan oldingi tasdiq (eski sozlamalardagidek): kompyuterlar sozlamani ~2 daqiqada oladi. */
const CONFIRM = "Sozlamalar saqlansinmi? Kompyuterlar ~2 daqiqada yangilanadi, yangi videolardan boshlab qo'llanadi.";

function OwnerComputer({ toast, onChanged, back }: SettingsPageProps) {
  const fail = useCallback((m: string) => { haptic.fail(); toast(m, true); }, [toast]);
  const busy = useBusy(fail);
  const cfg = useBotConfig();
  /** Jadval yuklanmasa «Qayta urinish» bo'limni qayta o'rnatadi (useSchedule mount'da yuklaydi). */
  const [schKey, setSchKey] = useState(0);

  /** Bitta config kartasini saqlash: tasdiq → faqat shu qism → toast. Muvaffaqiyatli bo'lsa true. */
  const saveCard = useCallback(async (key: string, label: string, patch: Parameters<typeof cfg.saveConfig>[0]) => {
    if (!(await confirmBox(CONFIRM))) return false;
    const r = await busy.run(key, async () => { await cfg.saveConfig(patch); return true; });
    if (r) { haptic.ok(); toast(`${label} saqlandi.`); onChanged(); }
    return r === true;
  }, [busy, cfg, toast, onChanged]);

  return (
    <div className="page narrow">
      <Head back={back} />
      <ScheduleSection key={schKey} busy={busy} toast={toast} onRetry={() => setSchKey((k) => k + 1)} />
      <MachinesSection />
      {cfg.config ? (
        <>
          <LimitsCard limits={cfg.config.limits} saving={busy.is("limits")} done={busy.done("limits")}
            onSave={(l) => saveCard("limits", "Cheklovlar", (d) => ({ ...d, limits: l }))} />
          <AdvancedCard settings={cfg.config.voice.settings} saving={busy.is("voice")} done={busy.done("voice")}
            onSave={(v) => saveCard("voice", "VoiceStudio sozlamalari", (d) => ({ ...d, voice: { ...d.voice, settings: v } }))} />
        </>
      ) : cfg.error ? (
        <Card title="Cheklovlar">
          <Alert>Sozlamalarni yuklab bo&apos;lmadi: {cfg.error}</Alert>
          <Btn tone="secondary" icon={RotateCw} loading={busy.is("cfg-reload")} onClick={() => void busy.run("cfg-reload", cfg.reload)}>Qayta urinish</Btn>
        </Card>
      ) : <SkelCard lines={4} />}
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
  if (checkPending(m)) return "Tekshiruv kutilmoqda — kompyuter ~2 daqiqada bajaradi";
  if (!m.check.finishedAt) return null;
  const failed = m.check.results.filter((r) => r.level === "Failed").length;
  const warn = m.check.results.filter((r) => r.level === "Warning").length;
  const res = failed ? `${failed} ta xato` : warn ? `${warn} ta ogohlantirish` : "hammasi ishlaydi";
  return `Oxirgi tekshiruv ${ago(m.check.finishedAt, now)} · ${res}${m.check.stale ? " · eskirgan" : ""}`;
}

/** /api/machines: har 15 soniyada (sahifa ko'rinib turganda) yangilanadi. Xato shu bo'limda ko'rsatiladi (toast emas). */
function useMachineList() {
  const [list, setList] = useState<Machine[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const reload = useCallback(async () => {
    try { setList(await api<Machine[]>("/api/machines")); setError(null); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    setNow(Date.now());
  }, []);
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- mount'da bir marta yuklanadi (holat await'dan keyin o'rnatiladi)
    void reload();
    const t = window.setInterval(() => { if (document.visibilityState === "visible") void reload(); }, 15_000);
    return () => window.clearInterval(t);
  }, [reload]);
  return { list, error, reload, now };
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
          <div className="hint">Onlayn — oxirgi 6 daqiqada signal kelgan. Og&apos;ir ish paytida kompyuter jim turadi va «Worker band» ko&apos;rinadi.</div>
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

// ------------------------------------------------------------------ cheklovlar
type Limits = BotConfig["limits"];
type LimitKey = keyof Limits;
const LIMITS: { key: LimitKey; title: string; sub: string; unit: string; lo: number; hi: number; step: number; int: boolean }[] = [
  { key: "maxRunAttempts", title: "Urinishlar soni", sub: "Xato bo'lsa qayta urinish", unit: "", lo: 1, hi: 10, step: 1, int: true },
  { key: "maxQaFixCycles", title: "Sifat tuzatish", sub: "Shablonga solishtirish sikli", unit: "sikl", lo: 0, hi: 10, step: 1, int: true },
  { key: "minFreeDiskGb", title: "Min. bo'sh disk", sub: "Kamida shuncha joy", unit: "GB", lo: 0, hi: 1000, step: 1, int: false },
  { key: "minFreeRamGbForTts", title: "Min. RAM (ovoz)", sub: "VoiceStudio uchun", unit: "GB", lo: 0, hi: 128, step: 1, int: false },
];

const numStr = (v: string) => v.trim().replace(",", ".");
/** Raqam maydoni xatosi (server oralig'i bilan bir xil). */
function rangeErr(v: string, lo: number, hi: number, int = false): string | null {
  const t = numStr(v);
  const n = Number(t);
  if (t === "" || !Number.isFinite(n)) return "Raqam kiriting";
  if (int && !Number.isInteger(n)) return "Butun son kiriting";
  if (n < lo || n > hi) return `${lo}–${hi} oralig'ida bo'lsin`;
  return null;
}

type LimitsDraft = Record<LimitKey, string>;
const limitsToDraft = (l: Limits): LimitsDraft => ({
  maxRunAttempts: String(l.maxRunAttempts), maxQaFixCycles: String(l.maxQaFixCycles), minFreeDiskGb: String(l.minFreeDiskGb), minFreeRamGbForTts: String(l.minFreeRamGbForTts),
});

function LimitsCard({ limits, saving, done, onSave }: { limits: Limits; saving: boolean; done: boolean; onSave: (l: Limits) => Promise<boolean> }) {
  const [draft, setDraft] = useState<LimitsDraft | null>(null);
  const base = limitsToDraft(limits);
  const cur = draft ?? base;
  const errs = Object.fromEntries(LIMITS.map((f) => [f.key, rangeErr(cur[f.key], f.lo, f.hi, f.int)])) as Record<LimitKey, string | null>;
  const invalid = Object.values(errs).some(Boolean);
  const dirty = !!draft && LIMITS.some((f) => Number(numStr(cur[f.key])) !== limits[f.key] || cur[f.key].trim() === "");

  const set = (k: LimitKey, v: string) => setDraft({ ...cur, [k]: v });
  const step = (f: (typeof LIMITS)[number], dir: 1 | -1) => {
    const n = Number(numStr(cur[f.key]));
    const v = Math.min(f.hi, Math.max(f.lo, (Number.isFinite(n) ? (dir > 0 ? Math.floor(n) : Math.ceil(n)) : f.lo) + dir * f.step));
    set(f.key, String(v));
  };

  async function save() {
    if (!dirty || invalid) return;
    const l = Object.fromEntries(LIMITS.map((f) => [f.key, Number(numStr(cur[f.key]))])) as Limits;
    if (await onSave(l)) setDraft(null);
  }

  return (
    <Card title="Cheklovlar" sub="Xatoda necha marta urinadi, qancha joy kerak">
      <div className={s.limits}>
        {LIMITS.map((f) => {
          const n = Number(numStr(cur[f.key]));
          const id = `limit-${f.key}`;
          return (
            <div key={f.key} className={s.limit}>
              <div className={s.limitRow}>
                <label className={s.limitText} htmlFor={id}><b>{f.title}</b><span className="sub">{f.sub}</span></label>
                <div className={s.stepper}>
                  <button type="button" className={s.stepBtn} aria-label={`${f.title}: kamaytirish`} disabled={Number.isFinite(n) && n <= f.lo} onClick={() => step(f, -1)}><Minus size={18} /></button>
                  <span className={s.value}>
                    <input id={id} className={`${s.valueInp} tnum${errs[f.key] ? " " + s.valueErr : ""}`} value={cur[f.key]} inputMode={f.int ? "numeric" : "decimal"}
                      aria-invalid={!!errs[f.key]} onChange={(e) => set(f.key, e.target.value)} size={Math.max(1, cur[f.key].length)} />
                    {f.unit ? <span className={s.unit}>{f.unit}</span> : null}
                  </span>
                  <button type="button" className={s.stepBtn} aria-label={`${f.title}: oshirish`} disabled={Number.isFinite(n) && n >= f.hi} onClick={() => step(f, 1)}><Plus size={18} /></button>
                </div>
              </div>
              {errs[f.key] ? <div className="field-err">{errs[f.key]}</div> : null}
            </div>
          );
        })}
      </div>
      <div className={s.foot}>
        <span className="hint">Kompyuterlar ~2 daqiqada yangilanadi</span>
        <SaveBtn size="md" dirty={dirty && !invalid} saving={saving} done={done} label={saving ? "Saqlanmoqda…" : "Saqlash"} onClick={() => void save()} />
      </div>
    </Card>
  );
}

// ------------------------------------------------------------------ kengaytirilgan (VoiceStudio)
type VoiceSettings = BotConfig["voice"]["settings"];
type NumKey = Exclude<keyof VoiceSettings, "baseUrl" | "whisperModel">;
const VS_NUM: { key: NumKey; label: string; hint: string; lo: number; hi: number; int: boolean }[] = [
  { key: "startupTimeoutSec", label: "Ishga tushishini kutish, s", hint: "VoiceStudio ochilguncha", lo: 30, hi: 1800, int: true },
  { key: "requestTimeoutSec", label: "TTS so'rov vaqti, s", hint: "Bitta bo'lak uchun", lo: 30, hi: 3600, int: true },
  { key: "chunkChars", label: "Bo'lak hajmi, belgi", hint: "Matn shu uzunlikda bo'linadi", lo: 100, hi: 2000, int: true },
  { key: "pauseBetweenChunksSec", label: "Bo'laklar orasidagi pauza, s", hint: "0–5 s", lo: 0, hi: 5, int: false },
  { key: "alignTimeoutSec", label: "Subtitr vaqti timeouti, s", hint: "Whisper bilan moslash", lo: 60, hi: 7200, int: true },
];
const LOCAL_URL = /^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?\/?$/i;
type VsDraft = Record<keyof VoiceSettings, string>;
const vsToDraft = (v: VoiceSettings): VsDraft => ({
  baseUrl: v.baseUrl, whisperModel: v.whisperModel, startupTimeoutSec: String(v.startupTimeoutSec), requestTimeoutSec: String(v.requestTimeoutSec),
  chunkChars: String(v.chunkChars), pauseBetweenChunksSec: String(v.pauseBetweenChunksSec), alignTimeoutSec: String(v.alignTimeoutSec),
});
/** "Systran/faster-whisper-base" → "base" */
const whisperShort = (m: string) => m.split("/").pop()?.replace(/^(faster-)?whisper-/i, "") || m;

function AdvancedCard({ settings, saving, done, onSave }: { settings: VoiceSettings; saving: boolean; done: boolean; onSave: (v: VoiceSettings) => Promise<boolean> }) {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<VsDraft | null>(null);
  const bodyId = useId();
  const cur = draft ?? vsToDraft(settings);
  const set = (k: keyof VoiceSettings, v: string) => setDraft({ ...cur, [k]: v });

  const urlErr = LOCAL_URL.test(cur.baseUrl.trim()) ? null : "Faqat lokal manzil (masalan http://127.0.0.1:3900)";
  const errs = Object.fromEntries(VS_NUM.map((f) => [f.key, rangeErr(cur[f.key], f.lo, f.hi, f.int)])) as Record<NumKey, string | null>;
  const invalid = !!urlErr || Object.values(errs).some(Boolean);
  const dirty = !!draft && differs(draft, vsToDraft(settings));

  async function save() {
    if (!dirty || invalid) return;
    const v: VoiceSettings = {
      baseUrl: cur.baseUrl.trim(), whisperModel: cur.whisperModel.trim(),
      ...(Object.fromEntries(VS_NUM.map((f) => [f.key, Number(numStr(cur[f.key]))])) as Record<NumKey, number>),
    };
    if (await onSave(v)) setDraft(null);
  }

  return (
    <section className={`card ${s.adv}`}>
      <button type="button" className={s.advHead} aria-expanded={open} aria-controls={bodyId} onClick={() => setOpen((o) => !o)}>
        <span className={s.tile}><SlidersHorizontal size={20} /></span>
        <span className={s.advText}>
          <b>Kengaytirilgan (VoiceStudio)</b>
          <span className="sub tnum">Bo&apos;lak {settings.chunkChars} · pauza {settings.pauseBetweenChunksSec} s · Whisper {whisperShort(settings.whisperModel)}</span>
        </span>
        <ChevronDown size={18} className={`${s.chev}${open ? " " + s.chevOpen : ""}`} />
      </button>
      {open ? (
        <div id={bodyId} className={s.advBody}>
          <Field label="VoiceStudio manzili" error={urlErr} hint="Kompyuterning o'zidagi VoiceStudio (lokal)">
            <input className={`inp${urlErr ? " err" : ""}`} value={cur.baseUrl} inputMode="url" autoComplete="off" spellCheck={false} placeholder="http://127.0.0.1:3900"
              aria-label="VoiceStudio manzili" aria-invalid={!!urlErr} onChange={(e) => set("baseUrl", e.target.value)} />
          </Field>
          <div className="grid2">
            {VS_NUM.map((f) => (
              <Field key={f.key} label={f.label} error={errs[f.key]} hint={f.hint}>
                <input className={`inp tnum${errs[f.key] ? " err" : ""}`} value={cur[f.key]} inputMode={f.int ? "numeric" : "decimal"}
                  aria-label={f.label} aria-invalid={!!errs[f.key]} onChange={(e) => set(f.key, e.target.value)} />
              </Field>
            ))}
          </div>
          <Field label="Whisper modeli" hint="Bo'sh qolsa — standart (Systran/faster-whisper-base)">
            <input className="inp" value={cur.whisperModel} autoComplete="off" spellCheck={false} placeholder="Systran/faster-whisper-base" aria-label="Whisper modeli" onChange={(e) => set("whisperModel", e.target.value)} />
          </Field>
          <div className={s.foot}>
            <span className="hint">Kompyuterlar ~2 daqiqada yangilanadi</span>
            <SaveBtn size="md" dirty={dirty && !invalid} saving={saving} done={done} label={saving ? "Saqlanmoqda…" : "Saqlash"} onClick={() => void save()} />
          </div>
        </div>
      ) : null}
    </section>
  );
}

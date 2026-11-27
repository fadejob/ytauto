"use client";

/**
 * Video tafsiloti (dizayn: ytauto.dsai → Screens → «Video tafsiloti» D72poK va «Video tafsiloti · amallar» orRRSo).
 * Tepada: orqaga «Videolar» + yangilash, sarlavha, holat va meta. Keyin bosqichlar (vertikal chiziq, /api/logs?id=… dan),
 * boshlang'ich savol, video ma'lumotlari, ikkinchi darajali amallar, loglar va xavfli amallar. Holatga mos bitta asosiy amal
 * telefon ekranida pastda (menyu ustida) turadi. Har bir amal tugmasi o'z spinneri bilan (useBusy).
 */
import { useCallback, useEffect, useRef, useState } from "react";
import {
  CalendarClock, Check, ChevronDown, ChevronLeft, ChevronUp, CircleX, Clapperboard, Download, LoaderCircle, Pencil, Play, RefreshCw, RotateCcw, SlidersHorizontal, Trash2, Upload, Zap,
} from "lucide-react";
import { api, confirmBox, haptic, openExternal } from "@/lib/client";
import { useLiveConnected, useLiveJob, useLiveRefresh } from "@/lib/live";
import { FormatText, StatusText, type Action, type JobEventRow, type JobLogs, type JobStageRow, type State, type Topic } from "@/lib/types";
import type { Run, Toast } from "./App";
import { Alert, Btn, Card, Empty, HookSlab, Skel, SkelCard, Status, useBusy, type IconT } from "./ui";
import TopicSheet, { RescheduleSheet } from "./TopicSheet";
import s from "./VideoDetail.module.css";

// ------------------------------------------------------------------ yordamchilar
const STAGES: { n: number; name: string; tag: string }[] = [
  { n: 1, name: "Ssenariy", tag: "Ssenariy" },
  { n: 2, name: "Ovoz", tag: "Ovoz" },
  { n: 3, name: "Subtitr vaqtlari", tag: "Subtitr" },
  { n: 4, name: "Montaj", tag: "Montaj" },
  { n: 5, name: "Render", tag: "Render" },
  { n: 6, name: "Sifat tekshiruvi", tag: "Sifat" },
  { n: 7, name: "Arxiv", tag: "Arxiv" },
  { n: 8, name: "YouTube", tag: "YouTube" },
];
/** Desktop worker hodisalaridagi bosqich nomi (YtAuto.Core StageKind) → bosqich raqami. */
const EVENT_STAGE: Record<string, number> = { script: 1, voice: 2, align: 3, edit: 4, render: 5, validate: 6, archive: 7, upload: 8 };
const stageOfEvent = (e: JobEventRow) => (e.stage ? EVENT_STAGE[e.stage.toLowerCase()] ?? null : null);
const tagOfEvent = (e: JobEventRow) => { const n = stageOfEvent(e); return n ? STAGES[n - 1].tag : e.stage; };

const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const p2 = (n: number) => String(n).padStart(2, "0");
const fmt = (d: { date: string; time: string }) => `${Number(d.date.slice(8, 10))} ${MONTHS[Number(d.date.slice(5, 7)) - 1]}, ${d.time}`;
const tm = (iso: string) => { const d = new Date(iso); return `${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`; };
const dtFull = (iso: string) => { const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}, ${tm(iso)}`; };
/** Desktop ilovadagi qoida: vaqt o'tib ketgan yoki 30 daqiqadan yaqin bo'lsa, video yuklangandan 1 soat keyin chiqadi. */
const isLate = (iso: string) => new Date(iso).getTime() <= Date.now() + 30 * 60000;
function dur(ms: number) {
  const sec = Math.round(ms / 1000);
  if (sec < 60) return `${sec} s`;
  const m = Math.floor(sec / 60), r = sec % 60;
  if (m < 60) return r ? `${m} daq ${r} s` : `${m} daq`;
  const h = Math.floor(m / 60), rm = m % 60;
  return rm ? `${h} soat ${rm} daq` : `${h} soat`;
}
const lengthLabel = (format: number) => (format === 0 ? "2–3 daqiqa" : "55 s gacha");

function statusTone(t: Topic): "neutral" | "accent" | "ok" | "bad" {
  if (t.status === 11) return "ok";
  if (t.status === 12 || t.status === 13) return "bad";
  if (t.status === 14 || t.status === 10) return "neutral";
  if (t.status === 0) return t.claimed ? "accent" : "neutral";
  return "accent";
}

type StageView = { n: number; name: string; state: "done" | "now" | "fail" | "skip" | "todo"; right: string | null; sub: string | null };

/** Bosqichlar: baza qatori bo'lsa undan, bo'lmasa video holatidan taxmin (eski videolarda bosqich qatorlari yo'q bo'lishi mumkin). */
function buildStages(t: Topic, logs: JobLogs | null): StageView[] {
  const rows = new Map<number, JobStageRow>((logs?.stages ?? []).map((r) => [r.stage, r]));
  const lastMsg = new Map<number, string>();
  for (const e of logs?.events ?? []) { const n = stageOfEvent(e); if (n) lastMsg.set(n, e.message); }
  return STAGES.map(({ n, name }) => {
    const r = rows.get(n);
    let state: StageView["state"];
    if (r) state = r.state === 2 ? "done" : r.state === 1 ? "now" : r.state === 3 ? "fail" : r.state === 4 ? "skip" : "todo";
    else if (t.status === 10 || t.status === 11) state = "done";
    else if (t.status === 9) state = n < 8 ? "done" : "now";
    else if (t.status >= 1 && t.status <= 8) state = n < t.status ? "done" : n === t.status ? (t.claimed ? "now" : "todo") : "todo";
    else state = "todo";
    const subs: string[] = [];
    if (state === "fail" && r?.error) subs.push(r.error);
    else if ((state === "now" || state === "done") && lastMsg.has(n)) subs.push(lastMsg.get(n)!);
    if (r && r.attempts > 1) subs.push(`${r.attempts} urinish`);
    let right: string | null = r?.durationMs ? dur(r.durationMs) : null;
    if (!right && state === "now") right = "ishlamoqda";
    if (!right && state === "fail") right = "xato";
    if (!right && state === "skip") right = "o'tkazildi";
    if (!right && n === 8 && state === "todo") right = fmt(t.publishLocal);
    return { n, name, state, right, sub: subs.length ? subs.join(" · ") : null };
  });
}

type Kind = "publish" | "unpublish" | "upload" | "unupload" | "delete" | "retry" | "retry_settings" | "cancel" | "force_upload";
const ACTION: Record<Kind, Action["type"]> = {
  publish: "publish_now", unpublish: "cancel_publish_now", upload: "upload_ready", unupload: "cancel_upload_ready",
  retry: "retry", retry_settings: "retry_settings", cancel: "cancel", force_upload: "force_new_upload", delete: "delete_topic",
};

const LOG_PREVIEW = 5;

// ------------------------------------------------------------------ sahifa
export default function VideoDetail({ state, id, back, run, toast, refresh }: { state: State | null; id: number; back: () => void; run: Run; toast: Toast; refresh: () => Promise<void> }) {
  const t = state?.topics.find((x) => x.id === id) ?? null;
  // Amal natijasi xabarini App (run) o'zi ko'rsatadi: bu yerda faqat kutilmagan xatolar (bo'sh xabar - allaqachon ko'rsatilgan).
  const busy = useBusy((m) => { if (m) toast(m, true); });
  const [logs, setLogs] = useState<JobLogs | null>(null);
  const [logErr, setLogErr] = useState<string | null>(null);
  const [allLogs, setAllLogs] = useState(false);
  const [sheet, setSheet] = useState<"edit" | "resched" | null>(null);
  const [notesOpen, setNotesOpen] = useState(false);

  const loadLogs = useCallback(async () => {
    try { setLogs(await api<JobLogs>(`/api/logs?id=${id}`)); setLogErr(null); }
    catch (e) { setLogErr(e instanceof Error ? e.message : String(e)); }
  }, [id]);

  // eslint-disable-next-line react-hooks/set-state-in-effect -- video ochilganda loglar bir marta yuklanadi
  useEffect(() => { void loadLogs(); }, [loadLogs]);

  // Video tayyorlanayotgan bo'lsa, bosqichlar va loglar fonda yangilanib turadi (skeletsiz).
  const working = !!t && (t.claimed || (t.status >= 1 && t.status <= 9));
  const loadRef = useRef(loadLogs);
  useEffect(() => { loadRef.current = loadLogs; }, [loadLogs]);
  // Real vaqt: kompyuter agenti shu video bosqichi/hodisasi o'zgarganini yuboradi.
  //  - Oddiy log qatori - ro'yxatga joyida qo'shiladi (so'rovsiz).
  //  - Bosqich o'zgarsa - loglar va bosqichlar qayta olinadi (/api/logs).
  //  - Faqat holat/band/video/YouTube o'zgarsa - butun ilova holati (/api/state, eng og'ir so'rov).
  const live = useLiveConnected();
  const liveJob = useLiveJob(id);
  useEffect(() => {
    // Jonli ulanish bo'lsa hodisalar o'zi keladi - 10 s polling faqat ulanish yo'q paytda.
    if (!working || live) return;
    const h = window.setInterval(() => document.visibilityState === "visible" && void loadRef.current(), 10000);
    return () => window.clearInterval(h);
  }, [working, live]);
  const seen = useRef({ stage: "", state: "" });
  useLiveRefresh((e) => {
    if (e.type !== "job" || e.job.id !== id) return false;
    const j = e.job;
    if (j.message && j.at) {
      const row = { id: -Date.parse(j.at), at: j.at, level: j.level ?? "Info", stage: j.stage, message: j.message };
      setLogs((l) => (l && !l.events.some((x) => x.at === row.at && x.message === row.message) ? { ...l, events: [...l.events, row] } : l));
    }
    const stage = `${j.stage ?? ""}|${j.stageRunning}|${j.doneStage ?? ""}`;
    const state = `${j.status}|${j.claimed}|${j.hasVideo}|${j.youtubeId ?? ""}`;
    const stageChanged = stage !== seen.current.stage, stateChanged = state !== seen.current.state;
    seen.current = { stage, state };
    if (stateChanged) void refresh();
    return stageChanged;
  }, () => { void loadLogs(); }, 500);

  const refreshAll = () => { haptic.tap(); void busy.run("refresh", () => Promise.all([refresh(), loadLogs()])); };

  async function act(k: Kind) {
    if (!t) return;
    if (k === "retry_settings" && !(await confirmBox("Vazifa joriy sozlamalar va promptlar bilan qayta snapshot qilinadi, o'zgargan bosqichlar qayta bajariladi. Davom etilsinmi?"))) return;
    if (k === "cancel" && !(await confirmBox(`"${t.topic}" bekor qilinsinmi? YouTube'ga yuklangan video o'chirilmaydi.`))) return;
    if (k === "force_upload" && !(await confirmBox("YouTube Studio'da bu video YO'QLIGINI tekshirdingizmi? Aks holda ikki marta yuklanadi.\n\nYangi upload boshlansinmi?"))) return;
    if (k === "publish" && !(await confirmBox(t.youtubeUrl
      ? `"${t.topic}" YouTube'da hozir public qilinsinmi?`
      : `"${t.topic}" hozir tayyorlanib, YouTube'ga darhol chiqarilsinmi?`))) return;
    if (k === "upload" && !(await confirmBox(
      `"${t.title || t.topic}" tayyor videosi YouTube'ga yuklansinmi?\n\nVideo qayta tayyorlanmaydi. Chiqish vaqti: ${isLate(t.publishAt) ? "o'tib ketgan — yuklangandan 1 soat keyin" : fmt(t.publishLocal)}.`))) return;
    if (k === "delete" && !(await confirmBox(`"${t.topic}" o'chirilsinmi? Qaytarib bo'lmaydi.`))) return;
    const results = await busy.run(k, async () => {
      const r = await run([{ type: ACTION[k], id: t.id } as Action]);
      if (!r.every((x) => x.ok)) throw new Error("");
      return r;
    });
    if (results && k === "delete") { back(); return; }
    if (results) void loadLogs();
  }

  // --- holatlar: yuklanmoqda / topilmadi
  if (!state) {
    return (
      <div className="page" aria-busy="true">
        <Header back={back} onRefresh={refreshAll} refreshing={busy.is("refresh")} />
        <div style={{ display: "grid", gap: 10 }}><Skel h={28} w="80%" /><Skel h={16} w="55%" /></div>
        <SkelCard lines={6} />
      </div>
    );
  }
  if (!t) {
    return (
      <div className="page">
        <Header back={back} onRefresh={refreshAll} refreshing={busy.is("refresh")} />
        <Empty icon={Clapperboard} title="Video topilmadi" action={<Btn tone="secondary" onClick={back}>Videolarga qaytish</Btn>}>
          #{id} o&apos;chirilgan yoki boshqa kanalga tegishli bo&apos;lishi mumkin.
        </Empty>
      </div>
    );
  }

  // --- amallar: holatga mos bitta asosiy amal, qolgani ikkinchi darajali, xavflisi pastda alohida
  type A = { key: Kind | "edit" | "resched" | "youtube"; label: string; icon: IconT; onClick: () => void };
  const primaryCandidates: A[] = [];
  if (t.canRetry) primaryCandidates.push({ key: "retry", label: "Qayta urinish", icon: RotateCcw, onClick: () => void act("retry") });
  if (t.canUploadReady) primaryCandidates.push({ key: "upload", label: "YouTube'ga yuklash", icon: Upload, onClick: () => void act("upload") });
  if (t.canPublishNow) primaryCandidates.push({ key: "publish", label: "Hozir chiqarish", icon: Zap, onClick: () => void act("publish") });
  if (t.youtubeUrl) primaryCandidates.push({ key: "youtube", label: "YouTube'da ochish", icon: Play, onClick: () => { haptic.tap(); openExternal(t.youtubeUrl!); } });
  const primary = primaryCandidates[0] ?? null;
  const secondary: A[] = primaryCandidates.slice(1);
  if (t.editable) secondary.push({ key: "edit", label: "Tahrirlash", icon: Pencil, onClick: () => { haptic.tap(); setSheet("edit"); } });
  if (t.canRetry) secondary.push({ key: "retry_settings", label: "Joriy sozlamalar bilan qayta", icon: SlidersHorizontal, onClick: () => void act("retry_settings") });
  if (t.canForceUpload) secondary.push({ key: "force_upload", label: "Boshidan yuklash", icon: Upload, onClick: () => void act("force_upload") });
  if (t.canReschedule) secondary.push({ key: "resched", label: "Vaqtni o'zgartirish", icon: CalendarClock, onClick: () => { haptic.tap(); setSheet("resched"); } });
  // youtube havolasi faqat ikkinchi darajaga tushganda shu ro'yxat oxirida ko'rinsin
  secondary.sort((a, b) => (a.key === "youtube" ? 1 : 0) - (b.key === "youtube" ? 1 : 0));

  const isKind = (k: A["key"]): k is Kind => k in ACTION;
  const btnProps = (a: A) => ({ loading: isKind(a.key) ? busy.is(a.key) : false, done: isKind(a.key) ? busy.done(a.key) : false, icon: a.icon, onClick: a.onClick });

  const stages = buildStages(t, logs);
  const doneCount = stages.filter((x) => x.state === "done" || x.state === "skip").length;
  const totalMs = (logs?.stages ?? []).reduce((a, r) => a + (r.durationMs ?? 0), 0);
  const events = [...(logs?.events ?? [])].reverse();
  const shown = allLogs ? events : events.slice(0, LOG_PREVIEW);
  const lib = state.catalog.music;
  const musicText = t.music === "none" ? "Musiqasiz" : t.music ? (lib?.tracks.find((m) => m.id === t.music)?.title ?? "O'chirilgan trek") : "Avtomatik";
  const multiChannel = (state.channel?.total ?? 0) > 1;

  return (
    <div className={`page ${s.root}${primary ? " " + s.withCta : ""}`}>
      <Header back={back} onRefresh={refreshAll} refreshing={busy.is("refresh")} />

      <div className={s.titleBlock}>
        <h1 className={s.title}>{t.title || t.topic}</h1>
        <div className={s.meta}>
          <Status tone={statusTone(t)}>{StatusText[t.status] ?? `Holat ${t.status}`}</Status>
          {/* Bitta matn: qator o'ralganda «·» yangi qator boshida qolmasin. */}
          <span className="sub tnum">
            {[`#${t.id}`, FormatText[t.format] ?? FormatText[0], fmt(t.publishLocal), state.channel?.title].filter(Boolean).join(" · ")}
          </span>
        </div>
      </div>

      {t.error ? <Alert>{t.error}</Alert> : null}

      <div className={s.cols}>
        <div className={s.col}>
          {/* ---- bosqichlar */}
          {!logs && !logErr ? <SkelCard lines={7} /> : (
            <Card title="Bosqichlar" action={<span className="sub tnum">{live && working ? <span className={s.live} title="Kompyuterdan real vaqtda">Jonli · </span> : null}{doneCount}/8 tayyor{totalMs > 0 ? ` · ${dur(totalMs)}` : ""}</span>}>
              {logErr ? (
                <Alert>
                  Bosqichlarni olib bo&apos;lmadi: {logErr}{" "}
                  <button type="button" className="linkbtn" onClick={() => void busy.run("logs", loadLogs)}>Qayta urinish</button>
                </Alert>
              ) : null}
              <ol className={s.timeline} aria-label="Bosqichlar">
                {stages.map((st, i) => (
                  <li key={st.n} className={`${s.stage} ${st.state === "todo" ? "" : s[st.state]}`}>
                    <div className={s.rail} aria-hidden>
                      <span className={s.node}>
                        {st.state === "done" ? <Check size={14} strokeWidth={3} /> : st.state === "now" ? <LoaderCircle size={14} className="spin" /> : st.state === "fail" ? <CircleX size={14} /> : null}
                      </span>
                      {i < stages.length - 1 ? <span className={s.line} /> : null}
                    </div>
                    <div className={s.body}>
                      <div className={s.line1}>
                        <span className={s.stageName}>{st.name}</span>
                        {st.right ? <span className={`${s.right} tnum`}>{st.right}</span> : null}
                      </div>
                      {st.sub ? <div className={s.stageSub}>{st.sub}</div> : null}
                      <span className="sr-only">
                        {st.state === "done" ? "tayyor" : st.state === "now" ? "ishlamoqda" : st.state === "fail" ? "xato" : st.state === "skip" ? "o'tkazildi" : "kutmoqda"}
                      </span>
                    </div>
                  </li>
                ))}
              </ol>
            </Card>
          )}

          {/* ---- tayyor video: kompyuterdan to'g'ridan-to'g'ri (Cloudflare tunnel) */}
          {t.hasVideo || liveJob?.hasVideo ? <Player id={t.id} format={t.format} toast={toast} /> : null}

          {/* ---- boshlang'ich savol */}
          {t.hook ? <HookSlab><div className="q">{t.hook}</div></HookSlab> : null}

          {/* ---- video ma'lumotlari */}
          <Card title="Video">
            <dl className={s.rows}>
              <Row k="Mavzu" v={t.topic} />
              {state.channel?.title ? <Row k="Kanal" v={state.channel.title + (multiChannel ? ` (${state.channel.total} kanaldan)` : "")} /> : null}
              <Row k="Format" v={`${FormatText[t.format] ?? FormatText[0]} · ${lengthLabel(t.format)}`} />
              <Row k="Chiqish" v={`${fmt(t.publishLocal)} · ${state.catalog.youtube.timeZone}`} />
              <Row k="Matn" v={t.hasAudio ? "Tayyor audio" : t.manualScript ? "O'z matnim" : "Gemini yozadi"} />
              {t.hasAudio ? null : <Row k="Ovoz" v={t.voice || "Kompyuterdagi standart"} />}
              <Row k="Fon musiqasi" v={musicText} />
              <Row k="Shablon" v={t.template || "Shablonsiz"} />
            </dl>
            {t.notes ? (
              <button type="button" className={`${s.notes}${notesOpen ? " " + s.open : ""}`} aria-expanded={notesOpen} onClick={() => setNotesOpen((v) => !v)}>
                <span className="label">Tushuntirish</span>
                <span>{t.notes}</span>
              </button>
            ) : null}
          </Card>
        </div>

        <div className={s.col}>
          {/* ---- so'rov yuborilgan (kompyuter kutilmoqda) */}
          {t.publishNow || t.uploadRequested ? (
            <Card title="Kompyuter kutilmoqda" sub="So'rov ketdi, kompyuter hali bajarmadi — qaytarib olish mumkin.">
              {t.publishNow ? (
                <div className={s.pending}>
                  <Status tone="accent">Hozir chiqarish kutilmoqda</Status>
                  <Btn tone="ghost" size="sm" loading={busy.is("unpublish")} done={busy.done("unpublish")} onClick={() => void act("unpublish")}>Bekor qilish</Btn>
                </div>
              ) : null}
              {t.uploadRequested ? (
                <div className={s.pending}>
                  <Status tone="accent">Yuklash kutilmoqda</Status>
                  <Btn tone="ghost" size="sm" loading={busy.is("unupload")} done={busy.done("unupload")} onClick={() => void act("unupload")}>Bekor qilish</Btn>
                </div>
              ) : null}
            </Card>
          ) : null}

          {/* ---- ikkinchi darajali amallar */}
          {secondary.length > 0 || primary ? (
            <div className={s.actions}>
              {primary ? <div className={s.cta}><Btn block {...btnProps(primary)}>{primary.label}</Btn></div> : null}
              {secondary.map((a) => <Btn key={a.key} tone="secondary" block {...btnProps(a)}>{a.label}</Btn>)}
              {t.canForceUpload ? <Alert tone="note">Avval YouTube Studio&apos;da bu video yo&apos;qligini tekshiring — aks holda ikki marta yuklanadi.</Alert> : null}
              {t.canUploadReady ? <div className="hint">Video qayta tayyorlanmaydi. Chiqish: {isLate(t.publishAt) ? "o'tib ketgan — yuklangandan 1 soat keyin" : fmt(t.publishLocal)}.</div> : null}
            </div>
          ) : null}

          {/* ---- loglar */}
          {!logs && !logErr ? <SkelCard lines={5} /> : logs ? (
            <Card title="Loglar" sub={`${logs.events.length} hodisa`}
              action={<Btn tone="secondary" size="sm" icon={RefreshCw} loading={busy.is("logs")} onClick={() => { haptic.tap(); void busy.run("logs", loadLogs); }}>{busy.is("logs") ? "Yangilanmoqda" : "Yangilash"}</Btn>}>
              {events.length === 0 ? <div className="hint">Hodisalar yo&apos;q.</div> : (
                <ul className={s.events}>
                  {shown.map((e) => {
                    const lvl = e.level.toLowerCase();
                    const tag = tagOfEvent(e);
                    return (
                      <li key={e.id} className={`${s.event} ${lvl === "error" ? s.err : lvl === "warn" || lvl === "warning" ? s.warn : ""}`}>
                        <time className={`${s.time} tnum`} dateTime={e.at} title={dtFull(e.at)}>{tm(e.at)}</time>
                        <span className={s.dot} aria-hidden />
                        <div className={s.evText}>
                          {tag ? <span className={s.tag}>{tag}</span> : null}
                          <span className={s.msg}>{e.message}</span>
                        </div>
                      </li>
                    );
                  })}
                </ul>
              )}
              {events.length > LOG_PREVIEW ? (
                <button type="button" className={s.expand} aria-expanded={allLogs} onClick={() => { haptic.tap(); setAllLogs((v) => !v); }}>
                  {allLogs ? <>Yig&apos;ish <ChevronUp size={16} /></> : <>Hammasini ko&apos;rsatish ({events.length}) <ChevronDown size={16} /></>}
                </button>
              ) : null}
            </Card>
          ) : null}

          {/* ---- xavfli amallar */}
          {t.deletable || t.canCancel ? (
            <div className={s.danger}>
              {t.deletable ? (
                <Btn tone="ghost" block className={s.bad} icon={Trash2} loading={busy.is("delete")} onClick={() => void act("delete")}>O&apos;chirish</Btn>
              ) : (
                <>
                  <Btn tone="ghost" block className={s.bad} icon={CircleX} loading={busy.is("cancel")} done={busy.done("cancel")} onClick={() => void act("cancel")}>Videoni bekor qilish</Btn>
                  <div className="hint" style={{ textAlign: "center" }}>YouTube&apos;ga yuklangan video o&apos;chirilmaydi.</div>
                </>
              )}
            </div>
          ) : null}
        </div>
      </div>

      {primary ? <div className="fade-bottom" aria-hidden /> : null}

      {/* TopicSheet - Videolar ekranidagi yangi/tahrir oynasi (components/TopicSheet.tsx). */}
      {sheet === "edit" ? (
        <TopicSheet sheet={{ mode: "edit", topic: t }} catalog={state.catalog} channelTitle={multiChannel ? state.channel?.title ?? "" : ""} close={() => { setSheet(null); void loadLogs(); }} run={run} customCover={!!state.channel?.customCover} />
      ) : null}
      {sheet === "resched" ? <RescheduleSheet topic={t} close={() => setSheet(null)} run={run} /> : null}
    </div>
  );
}

function Header({ back, onRefresh, refreshing }: { back: () => void; onRefresh: () => void; refreshing: boolean }) {
  return (
    <header className={s.head}>
      <button type="button" className="back" onClick={back}><span className="circle"><ChevronLeft size={18} /></span>Videolar</button>
      <button type="button" className="round" aria-label="Yangilash" aria-busy={refreshing || undefined} disabled={refreshing} onClick={onRefresh}>
        {refreshing ? <LoaderCircle size={20} className="spin" /> : <RefreshCw size={20} />}
      </button>
    </header>
  );
}


type Media = { video: string; download: string; cover: string | null; subtitles: string | null; machine: string };

/**
 * Tayyor video pleeri: fayl kompyuterdan agent orqali (imzolangan public havola, Range - oldinga/orqaga o'tish ishlaydi).
 * Havola tunnel manziliga bog'liq: kompyuter qayta yonsa yangisi olinadi. Kompyuter o'chiq bo'lsa - izoh.
 */
function Player({ id, format, toast }: { id: number; format: number; toast: Toast }) {
  const [media, setMedia] = useState<Media | null | undefined>(undefined);
  const [failed, setFailed] = useState(false);
  const load = useCallback(async () => {
    try { setMedia((await api<{ media: Media | null }>(`/api/agent?media=${id}`)).media); setFailed(false); }
    catch { setMedia(null); }
  }, [id]);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- video ochilganda havola bir marta olinadi
  useEffect(() => { void load(); }, [load]);
  const ratio = format === 1 ? "9 / 16" : format === 2 ? "1 / 1" : "16 / 9";
  return (
    <Card title="Tayyor video" sub={media ? `Kompyuterdan (${media.machine}) to'g'ridan-to'g'ri` : undefined}
      action={media ? <Btn tone="secondary" size="sm" icon={Download} onClick={() => { haptic.tap(); openExternal(media.download); }}>Yuklab olish</Btn> : null}>
      {media === undefined ? <Skel h={180} /> : media && !failed ? (
        <video className={s.player} style={{ aspectRatio: ratio }} src={media.video} poster={media.cover ?? undefined} controls playsInline preload="metadata"
          onError={() => { setFailed(true); toast("Video ochilmadi — kompyuter manzili o'zgargan bo'lishi mumkin. Yangilanmoqda…", true); void load(); }} />
      ) : (
        <div className="hint">
          Videoni ko&apos;rish uchun kompyuter yoqiq va YtAuto agenti ishlayotgan bo&apos;lishi kerak (video kompyuterda saqlanadi).{" "}
          <button type="button" className="linkbtn" onClick={() => { haptic.tap(); setMedia(undefined); void load(); }}>Qayta urinish</button>
        </div>
      )}
    </Card>
  );
}

function Row({ k, v }: { k: string; v: string }) {
  return <div className={s.row}><dt>{k}</dt><dd title={v}>{v}</dd></div>;
}

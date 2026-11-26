"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Activity, ChevronRight, CircleAlert, CircleCheck, Clapperboard, KeyRound, Lock, MonitorOff, Package, RefreshCw, Settings2, Sparkles, Tv, UserRound } from "lucide-react";
import { applyOptimistic } from "@/lib/optimistic";
import { ApiError, api, cache, followSystemTheme, haptic, initTelegram, telegramBack } from "@/lib/client";
import type { Action, ActionResult, State } from "@/lib/types";
import Topics from "./Topics";
import VideoDetail from "./VideoDetail";
import Channel from "./Channel";
import Ai from "./Ai";
import History from "./History";
import TokenLogin from "./TokenLogin";
import { ConfirmHost, Status } from "./ui";
import { useMachines } from "./Machines";
import Hub from "./settings/Hub";
import Channels from "./settings/Channels";
import Voice from "./settings/Voice";
import VideoQuality from "./settings/VideoQuality";
import TemplatesMusic from "./settings/TemplatesMusic";
import AiPrompts from "./settings/AiPrompts";
import Computer from "./settings/Computer";
import Account from "./settings/Account";
import type { SettingsPage, SettingsPageProps } from "./settings/types";

type Tab = "videos" | "channel" | "ai" | "activity" | "settings";
export type Run = (actions: Action[]) => Promise<ActionResult[]>;
/** Toast ichidagi amal (masalan «Bekor qilish»): bosilsa run chaqiriladi va toast yopiladi. */
export type ToastAction = { label: string; run: () => void | Promise<void> };
export type Toast = (text: string, bad?: boolean, action?: ToastAction) => void;

/** Dizayndagi karta ko'rinishli banner: oq ikon-tile, qalin sarlavha, matn va (ixtiyoriy) tugma. tone="bad" - ish to'xtaydi, "info" - bilib qo'ying. */
function Notice({ tone, icon: Icon, title, status, children, action }: {
  tone: "bad" | "info"; icon: typeof CircleAlert; title: string; status?: string; children: React.ReactNode; action?: React.ReactNode;
}) {
  return (
    <section className={`notice ${tone}`} role={tone === "bad" ? "alert" : undefined}>
      <span className="ic" aria-hidden><Icon size={20} /></span>
      <div className="body">
        <div className="trow"><b>{title}</b>{status ? <Status>{status}</Status> : null}</div>
        <p>{children}</p>
        {action ? <div className="foot">{action}</div> : null}
      </div>
    </section>
  );
}

/** Kompyuter oxirgi signaldan beri: «6 daqiqadan beri». */
function sinceText(iso: string) {
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (sec < 3600) return `${Math.max(1, Math.round(sec / 60))} daqiqadan beri`;
  if (sec < 86400) return `${Math.round(sec / 3600)} soatdan beri`;
  return `${Math.round(sec / 86400)} kundan beri`;
}
const hhmm = (iso: string) => { const d = new Date(iso); return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`; };

const PAGES: Record<SettingsPage, (p: SettingsPageProps) => React.ReactNode> = {
  channels: Channels, voice: Voice, video: VideoQuality, library: TemplatesMusic, ai: AiPrompts, computer: Computer, account: Account,
};

export default function App() {
  const [tab, setTab] = useState<Tab>("videos");
  /** Videolar ichida ochilgan video (tafsilot sahifasi). */
  const [videoId, setVideoId] = useState<number | null>(null);
  /** Sozlamalar ichidagi sahifa (null - markaz). */
  const [page, setPage] = useState<SettingsPage | null>(null);
  const [state, setState] = useState<State | null>(null);
  const [loading, setLoading] = useState(true);
  const [denied, setDenied] = useState<{ text: string; id?: number } | null>(null);
  const [needToken, setNeedToken] = useState(false);
  const [toast, setToast] = useState<{ text: string; bad?: boolean; action?: ToastAction } | null>(null);

  const show: Toast = useCallback((text, bad, action) => {
    setToast({ text, bad, action });
    // Amalli toast bosishga ulgurish uchun xato xabari kabi uzoqroq turadi.
    window.setTimeout(() => setToast((t) => (t?.text === text ? null : t)), bad || action ? 5000 : 2800);
  }, []);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const s = await api<State>("/api/state");
      setState(s);
      cache.set("state", s);
      setDenied(null);
      setNeedToken(false);
    } catch (e) {
      if (e instanceof ApiError && e.code === "token_required") { setNeedToken(true); setState(null); cache.set("state", null); }
      else if (e instanceof ApiError && (e.status === 401 || e.status === 403)) setDenied({ text: e.message, id: e.telegramId });
      else show(e instanceof Error ? e.message : String(e), true);
    } finally {
      setLoading(false);
    }
  }, [show]);

  useEffect(() => {
    initTelegram();
    const stopTheme = followSystemTheme();
    const cached = cache.get<State>("state");
    // Darhol ko'rsatamiz, yangisini fonda olamiz. Eski versiya keshi (boshqa tuzilishdagi) ishlatilmaydi.
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi (hydration mos kelishi uchun)
    if (cached?.viewer && cached.channel && cached.catalog) setState(cached);
    void refresh();
    const onVisible = () => document.visibilityState === "visible" && void refresh();
    document.addEventListener("visibilitychange", onVisible);
    return () => { document.removeEventListener("visibilitychange", onVisible); stopTheme(); };
  }, [refresh]);

  // Kompyuter holati (offline banneri uchun): faqat egasi, holat yuklangach; xato bo'lsa banner ko'rsatilmaydi (toast ham yo'q).
  const quiet = useCallback(() => {}, []);
  const machines = useMachines(quiet, !!state && !state.account?.guest);
  const [recheck, setRecheck] = useState(false);
  const recheckMachines = async () => { setRecheck(true); try { await machines.reload(); } finally { setRecheck(false); } };

  /** Sozlama sahifasi ichidagi ichki ekran (masalan «Mening ovozim», prompt muharriri) o'z «orqaga»sini shu yerda ro'yxatdan o'tkazadi. */
  const subBack = useRef<(() => void) | null>(null);
  const setSubBack = useCallback((fn: (() => void) | null) => { subBack.current = fn; }, []);

  // Ichki sahifada (video tafsiloti, sozlama sahifasi) Telegram'ning tepadagi «orqaga» tugmasi.
  // Sozlama sahifasining ichki ekrani ochiq bo'lsa, avval o'sha ekran yopiladi (sahifa ro'yxatiga qaytiladi).
  const inner = (tab === "videos" && videoId !== null) || (tab === "settings" && page !== null);
  useEffect(() => telegramBack(inner ? () => {
    if (tab === "videos") setVideoId(null);
    else if (subBack.current) subBack.current();
    else setPage(null);
  } : null), [inner, tab]);

  // Amal: holat darhol (optimistik) o'zgaradi, server javobida kelgan yangi holat bilan almashtiriladi - alohida /api/state kutilmaydi.
  const stateRef = useRef(state);
  useEffect(() => { stateRef.current = state; }, [state]);
  const run: Run = useCallback(async (actions) => {
    const before = stateRef.current;
    if (before) setState(applyOptimistic(before, actions));
    try {
      const r = await api<{ results: ActionResult[]; state?: State }>("/api/actions", { actions });
      const bad = r.results.find((x) => !x.ok);
      if (bad) { haptic.fail(); show(bad.message, true); } else { haptic.ok(); show(r.results.map((x) => x.message).join(" ")); }
      if (r.state) { setState(r.state); cache.set("state", r.state); }
      else void refresh();
      return r.results;
    } catch (e) {
      if (before) setState(before); // tarmoq xatosi: optimistik o'zgarish bekor
      haptic.fail();
      const msg = e instanceof Error ? e.message : String(e);
      show(msg, true);
      return actions.map(() => ({ ok: false, message: msg }));
    }
  }, [refresh, show]);

  if (needToken) return <TokenLogin onDone={() => { setTab("videos"); void refresh(); }} />;

  if (denied)
    return (
      <main className="shell">
        <div className="empty">
          <span className="ic"><Lock size={24} /></span>
          <b>Kirish mumkin emas</b>
          <div>{denied.text}</div>
          {denied.id ? <div className="hint">Telegram ID: <b className="tnum">{denied.id}</b></div> : null}
        </div>
      </main>
    );

  // Gemini kaliti yo'q: video qo'shish va AI yordamchi o'chiq, mavjud videolar muzlatilgan (worker ularni tayyorlamaydi).
  const geminiOff = !!state && state.gemini?.ready === false;
  // Offline banneri: kompyuterlar haqiqiy ro'yxati kelgan va birortasi ham onlayn bo'lmasa (ro'yxat bo'sh yoki yuklanmagan bo'lsa ko'rsatilmaydi).
  const offlineMachine = !state?.account?.guest && machines.list && machines.list.length > 0 && !machines.list.some((m) => m.online)
    ? [...machines.list].sort((a, b) => b.lastSeen.localeCompare(a.lastSeen))[0] : null;
  const waiting = state ? state.topics.filter((t) => t.status === 0 && !t.claimed).length : 0;
  const go = (t: Tab) => {
    if (t === "ai" && geminiOff) { haptic.fail(); show("AI yordamchi uchun avval Sozlamalar → AI va promptlar'da Gemini kalitini qo'shing.", true); return; }
    haptic.tap();
    if (t === tab) { if (t === "videos") setVideoId(null); if (t === "settings") setPage(null); }
    setTab(t);
    window.scrollTo({ top: 0 });
  };
  const openSettings = (p: SettingsPage | null) => { haptic.tap(); setTab("settings"); setPage(p); window.scrollTo({ top: 0 }); };
  const logout = async () => {
    try { await api("/api/login", { logout: true }); } catch {}
    cache.set("state", null);
    setState(null); setNeedToken(true);
  };
  const pageProps: SettingsPageProps = {
    state, toast: show, run, back: () => { setPage(null); window.scrollTo({ top: 0 }); }, open: (p) => openSettings(p), onChanged: () => void refresh(), onLogout: logout, setSubBack,
  };
  const Page = page ? PAGES[page] : null;

  return (
    <main className="shell">
      <ConfirmHost />
      {state?.demo ? <div className="banner" style={{ marginBottom: 12 }}><CircleAlert />Demo rejim: namunaviy ma&apos;lumotlar (baza ulanmagan).</div>
        : state?.viewer.dev ? <div className="banner" style={{ marginBottom: 12 }}><CircleAlert />Brauzer (dev) rejimi — Telegram&apos;siz. Productionda ALLOW_BROWSER_DEV=0 qiling.</div> : null}
      {state && !state.catalog.known && tab === "videos" ? (
        <Notice tone="info" icon={Package} title="Desktop ilova hali katalog yubormagan">
          Mavzular standart ovoz va shablon bilan qo&apos;shiladi. Kompyuterda YtAuto&apos;ni bir marta oching — ovozlar va shablonlar shu yerda paydo bo&apos;ladi.
        </Notice>
      ) : null}
      {state?.account?.guest && tab === "videos" && videoId === null ? (
        <Notice tone="info" icon={UserRound} title="Qo'shimcha token" status="Mehmon">
          Videolaringiz {state.account.hostLabel ? `«${state.account.hostLabel}»` : "asosiy hisob"} kompyuterida, uning ish vaqtida tayyorlanadi. «Hozir chiqarish» va kompyuter sozlamalari egasida;
          Gemini kaliti, promptlar, ovoz va YouTube kanalingiz — o&apos;zingizniki.
        </Notice>
      ) : null}
      {geminiOff && !(tab === "settings" && page === "ai") ? (
        <Notice tone="bad" icon={KeyRound} title="Gemini kaliti kiritilmagan"
          action={<button type="button" className="pill" onClick={() => openSettings("ai")}>Sozlamalar → AI<ChevronRight size={16} aria-hidden /></button>}>
          Mavzular muzlatilgan: yangi mavzu qo&apos;shish va AI yordamchi o&apos;chiq. Kalit qo&apos;shilgach navbat o&apos;zi davom etadi.
        </Notice>
      ) : null}
      {offlineMachine ? (
        <Notice tone="bad" icon={MonitorOff} title={`«${offlineMachine.name}» offline`}
          action={<button type="button" className="pill ink" disabled={recheck} onClick={() => void recheckMachines()}><RefreshCw size={16} className={recheck ? "spin" : undefined} aria-hidden />Qayta tekshirish</button>}>
          Kompyuter o&apos;chiq yoki internetga ulanmagan — {sinceText(offlineMachine.lastSeen)} signal yo&apos;q (oxirgisi {hhmm(offlineMachine.lastSeen)}).
          {waiting > 0 ? ` Yoqilmasa, navbatdagi ${waiting} video tayyorlanmaydi.` : ""}
        </Notice>
      ) : null}

      {tab === "videos" && videoId === null && (
        <Topics state={state} loading={loading} refresh={refresh} run={run} locked={geminiOff} openVideo={(id) => { haptic.tap(); setVideoId(id); window.scrollTo({ top: 0 }); }} openSettings={openSettings} openAi={() => go("ai")} />
      )}
      {tab === "videos" && videoId !== null && <VideoDetail state={state} id={videoId} back={() => setVideoId(null)} run={run} toast={show} refresh={refresh} />}
      {tab === "channel" && <Channel toast={show} onTopicsChanged={() => void refresh()} openSettings={() => openSettings("channels")} />}
      {tab === "ai" && !geminiOff && <Ai run={run} toast={show} />}
      {tab === "activity" && <History state={state} toast={show} />}
      {tab === "settings" && (Page ? <Page {...pageProps} /> : <Hub state={state} open={(p) => openSettings(p)} />)}

      {toast ? (
        <div className={`toast${toast.bad ? " bad" : ""}${toast.action ? " has-act" : ""}`} role="status" aria-live="polite">
          {toast.bad ? <CircleAlert /> : toast.action ? <Tv className="info" /> : <CircleCheck />}<span>{toast.text}</span>
          {toast.action ? (
            <button type="button" className="act" onClick={() => { const a = toast.action!; setToast(null); haptic.tap(); void a.run(); }}>{toast.action.label}</button>
          ) : null}
        </div>
      ) : null}

      <nav className="tabbar" aria-label="Bo'limlar">
        <span className="brand"><i><Clapperboard size={18} /></i>YtAuto</span>
        <button className={tab === "videos" ? "on" : ""} aria-label="Videolar" title="Videolar" aria-current={tab === "videos" ? "page" : undefined} onClick={() => go("videos")}><Clapperboard /><span className="lbl">Videolar</span></button>
        <button className={tab === "channel" ? "on" : ""} aria-label="Kanal" title="Kanal" aria-current={tab === "channel" ? "page" : undefined} onClick={() => go("channel")}><Tv /><span className="lbl">Kanal</span></button>
        <button className={tab === "ai" ? "on" : ""} aria-label="AI" title="AI" aria-current={tab === "ai" ? "page" : undefined} onClick={() => go("ai")} disabled={geminiOff}><Sparkles /><span className="lbl">AI</span></button>
        <button className={tab === "activity" ? "on" : ""} aria-label="Faoliyat" title="Faoliyat" aria-current={tab === "activity" ? "page" : undefined} onClick={() => go("activity")}><Activity /><span className="lbl">Faoliyat</span></button>
        <button className={tab === "settings" ? "on" : ""} aria-label="Sozlamalar" title="Sozlamalar" aria-current={tab === "settings" ? "page" : undefined} onClick={() => go("settings")}><Settings2 /><span className="lbl">Sozlamalar</span></button>
      </nav>
    </main>
  );
}

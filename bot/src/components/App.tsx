"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Activity, CircleAlert, CircleCheck, Clapperboard, Lock, Settings2, Sparkles, Tv } from "lucide-react";
import { ApiError, api, cache, followSystemTheme, haptic, initTelegram, telegramBack } from "@/lib/client";
import type { Action, ActionResult, State } from "@/lib/types";
import Topics from "./Topics";
import VideoDetail from "./VideoDetail";
import Channel from "./Channel";
import Ai from "./Ai";
import History from "./History";
import TokenLogin from "./TokenLogin";
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
export type Toast = (text: string, bad?: boolean) => void;

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
  const [toast, setToast] = useState<{ text: string; bad?: boolean } | null>(null);

  const show: Toast = useCallback((text, bad) => {
    setToast({ text, bad });
    window.setTimeout(() => setToast((t) => (t?.text === text ? null : t)), bad ? 5000 : 2800);
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

  const run: Run = useCallback(async (actions) => {
    try {
      const { results } = await api<{ results: ActionResult[] }>("/api/actions", { actions });
      const bad = results.find((r) => !r.ok);
      if (bad) { haptic.fail(); show(bad.message, true); } else { haptic.ok(); show(results.map((r) => r.message).join(" ")); }
      void refresh();
      return results;
    } catch (e) {
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
      {state?.demo ? <div className="banner" style={{ marginBottom: 12 }}><CircleAlert />Demo rejim: namunaviy ma&apos;lumotlar (baza ulanmagan).</div>
        : state?.viewer.dev ? <div className="banner" style={{ marginBottom: 12 }}><CircleAlert />Brauzer (dev) rejimi — Telegram&apos;siz. Productionda ALLOW_BROWSER_DEV=0 qiling.</div> : null}
      {state && !state.catalog.known && tab === "videos" ? (
        <div className="banner" style={{ marginBottom: 12 }}><CircleAlert />Desktop ilova hali katalog yubormagan — videolar standart ovoz va shablon bilan qo&apos;shiladi. Kompyuterda YtAuto&apos;ni bir marta oching.</div>
      ) : null}
      {geminiOff && !(tab === "settings" && page === "ai") ? (
        <div className="banner bad" style={{ marginBottom: 12 }}>
          <CircleAlert />
          <div>Gemini kaliti kiritilmagan — yangi video qo&apos;shish va AI yordamchi o&apos;chiq, navbatdagi videolar tayyorlanmaydi.{" "}
            <button className="linkbtn" onClick={() => openSettings("ai")}>Kalit qo&apos;shish</button></div>
        </div>
      ) : null}

      {tab === "videos" && videoId === null && (
        <Topics state={state} loading={loading} refresh={refresh} run={run} locked={geminiOff} openVideo={(id) => { haptic.tap(); setVideoId(id); window.scrollTo({ top: 0 }); }} openSettings={openSettings} />
      )}
      {tab === "videos" && videoId !== null && <VideoDetail state={state} id={videoId} back={() => setVideoId(null)} run={run} toast={show} refresh={refresh} />}
      {tab === "channel" && <Channel toast={show} onTopicsChanged={() => void refresh()} openSettings={() => openSettings("channels")} />}
      {tab === "ai" && !geminiOff && <Ai run={run} toast={show} />}
      {tab === "activity" && <History state={state} toast={show} />}
      {tab === "settings" && (Page ? <Page {...pageProps} /> : <Hub state={state} open={(p) => openSettings(p)} />)}

      {toast ? (
        <div className={`toast${toast.bad ? " bad" : ""}`} role="status" aria-live="polite">
          {toast.bad ? <CircleAlert /> : <CircleCheck />}<span>{toast.text}</span>
        </div>
      ) : null}

      <nav className="tabbar" aria-label="Bo'limlar">
        <span className="brand"><i><Clapperboard size={18} /></i>YtAuto</span>
        <button className={tab === "videos" ? "on" : ""} aria-current={tab === "videos" ? "page" : undefined} onClick={() => go("videos")}><Clapperboard />Videolar</button>
        <button className={tab === "channel" ? "on" : ""} aria-current={tab === "channel" ? "page" : undefined} onClick={() => go("channel")}><Tv />Kanal</button>
        <button className={tab === "ai" ? "on" : ""} aria-current={tab === "ai" ? "page" : undefined} onClick={() => go("ai")} disabled={geminiOff}><Sparkles />AI</button>
        <button className={tab === "activity" ? "on" : ""} aria-current={tab === "activity" ? "page" : undefined} onClick={() => go("activity")}><Activity />Faoliyat</button>
        <button className={tab === "settings" ? "on" : ""} aria-current={tab === "settings" ? "page" : undefined} onClick={() => go("settings")}><Settings2 />Sozlamalar</button>
      </nav>
    </main>
  );
}

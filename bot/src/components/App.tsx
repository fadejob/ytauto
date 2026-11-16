"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError, api, cache, haptic, initTelegram } from "@/lib/client";
import type { Action, ActionResult, State } from "@/lib/types";
import { IconDoc, IconGear, IconHistory, IconList, IconSpark } from "./icons";
import Topics from "./Topics";
import Ai from "./Ai";
import Prompts from "./Prompts";
import History from "./History";
import Settings from "./Settings";

type Tab = "topics" | "ai" | "prompts" | "history" | "settings";
export type Run = (actions: Action[]) => Promise<ActionResult[]>;
export type Toast = (text: string, bad?: boolean) => void;

export default function App() {
  const [tab, setTab] = useState<Tab>("topics");
  const [state, setState] = useState<State | null>(null);
  const [loading, setLoading] = useState(true);
  const [denied, setDenied] = useState<{ text: string; id?: number } | null>(null);
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
    } catch (e) {
      if (e instanceof ApiError && (e.status === 401 || e.status === 403)) setDenied({ text: e.message, id: e.telegramId });
      else show(e instanceof Error ? e.message : String(e), true);
    } finally {
      setLoading(false);
    }
  }, [show]);

  useEffect(() => {
    initTelegram();
    const cached = cache.get<State>("state");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi (hydration mos kelishi uchun)
    if (cached) setState(cached); // darhol ko'rsatamiz, yangisini fonda olamiz
    void refresh();
    const onVisible = () => document.visibilityState === "visible" && void refresh();
    document.addEventListener("visibilitychange", onVisible);
    return () => document.removeEventListener("visibilitychange", onVisible);
  }, [refresh]);

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

  if (denied)
    return (
      <main className="shell">
        <div className="empty">
          <p style={{ fontSize: 40, margin: 0 }}>🔒</p>
          <p>{denied.text}</p>
          {denied.id ? <p className="hint">Telegram ID: <b>{denied.id}</b></p> : null}
        </div>
      </main>
    );

  const go = (t: Tab) => { if (t !== tab) { haptic.tap(); setTab(t); window.scrollTo({ top: 0 }); } };

  return (
    <main className="shell">
      {state?.demo ? <div className="banner">Demo rejim: namunaviy ma'lumotlar (baza ulanmagan). AI yordamchi haqiqiy Groq bilan ishlaydi.</div>
        : state?.viewer.dev ? <div className="banner">Brauzer (dev) rejimi — Telegram'siz. Productionda ALLOW_BROWSER_DEV=0 qiling.</div> : null}
      {state && !state.catalog.known ? (
        <div className="banner">Desktop ilova hali katalog yubormagan — mavzular standart ovoz/shablon bilan qo'shiladi. YtAuto'ni bir marta oching.</div>
      ) : null}

      {tab === "topics" && <Topics state={state} loading={loading} refresh={refresh} run={run} />}
      {tab === "ai" && <Ai run={run} toast={show} />}
      {tab === "prompts" && <Prompts run={run} toast={show} />}
      {tab === "history" && <History state={state} toast={show} />}
      {tab === "settings" && <Settings catalog={state?.catalog ?? null} toast={show} />}

      {toast ? <div className={`toast${toast.bad ? " bad" : ""}`} role="status">{toast.text}</div> : null}

      <nav className="tabbar">
        <button className={tab === "topics" ? "on" : ""} onClick={() => go("topics")}><IconList />Mavzular</button>
        <button className={tab === "ai" ? "on" : ""} onClick={() => go("ai")}><IconSpark />AI yordamchi</button>
        <button className={tab === "prompts" ? "on" : ""} onClick={() => go("prompts")}><IconDoc />Promptlar</button>
        <button className={tab === "history" ? "on" : ""} onClick={() => go("history")}><IconHistory />Tarix</button>
        <button className={tab === "settings" ? "on" : ""} onClick={() => go("settings")}><IconGear />Sozlamalar</button>
      </nav>
    </main>
  );
}

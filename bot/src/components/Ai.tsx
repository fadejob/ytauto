"use client";

import { useEffect, useRef, useState } from "react";
import { api, haptic } from "@/lib/client";
import type { ChatMessage, Proposal } from "@/lib/types";
import type { Run, Toast } from "./App";
import { IconSend } from "./icons";

type Item =
  | { kind: "msg"; msg: ChatMessage }
  | { kind: "proposal"; p: Proposal; status: "pending" | "running" | "done" | "rejected" | "failed"; result?: string };

const SUGGESTIONS = [
  "Kosmos haqida 3 ta qiziqarli mavzu qo'sh",
  "Navbatdagi birinchi videoni hozir chiqar",
  "Ssenariy promptida videolar qisqaroq bo'lsin",
];

const STORE = "ytauto:chat";

export default function Ai({ run, toast }: { run: Run; toast: Toast }) {
  const [items, setItems] = useState<Item[]>([]);
  const [text, setText] = useState("");
  const [thinking, setThinking] = useState(false);
  const end = useRef<HTMLDivElement>(null);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- sessionStorage faqat mount'dan keyin o'qiladi
    try { const v = sessionStorage.getItem(STORE); if (v) setItems(JSON.parse(v)); } catch {}
  }, []);
  useEffect(() => {
    try { sessionStorage.setItem(STORE, JSON.stringify(items.slice(-60))); } catch {}
    end.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [items, thinking]);

  async function send(content: string) {
    const msg = content.trim();
    if (!msg || thinking) return;
    haptic.tap();
    const next: Item[] = [...items, { kind: "msg", msg: { role: "user", content: msg } }];
    setItems(next);
    setText("");
    setThinking(true);
    try {
      const history = next.flatMap((i) => (i.kind === "msg" ? [i.msg] : i.status === "done" ? [{ role: "assistant" as const, content: `(Bajarildi: ${i.p.summary})` }] : []));
      const res = await api<{ reply: string; proposals: Proposal[] }>("/api/ai", { messages: history });
      setItems((cur) => [
        ...cur,
        { kind: "msg", msg: { role: "assistant", content: res.reply } },
        ...res.proposals.map((p) => ({ kind: "proposal" as const, p, status: "pending" as const })),
      ]);
    } catch (e) {
      toast(e instanceof Error ? e.message : String(e), true);
    } finally {
      setThinking(false);
    }
  }

  async function decide(ids: string[], approve: boolean) {
    const targets = items.filter((i): i is Extract<Item, { kind: "proposal" }> => i.kind === "proposal" && ids.includes(i.p.id) && i.status === "pending" && !i.p.error);
    if (!approve) {
      setItems((cur) => cur.map((i) => (i.kind === "proposal" && ids.includes(i.p.id) && i.status === "pending" ? { ...i, status: "rejected" } : i)));
      return;
    }
    setItems((cur) => cur.map((i) => (i.kind === "proposal" && targets.some((t) => t.p.id === i.p.id) ? { ...i, status: "running" } : i)));
    const results = await run(targets.map((t) => t.p.action));
    setItems((cur) => cur.map((i) => {
      if (i.kind !== "proposal") return i;
      const k = targets.findIndex((t) => t.p.id === i.p.id);
      return k < 0 ? i : { ...i, status: results[k]?.ok ? "done" : "failed", result: results[k]?.message };
    }));
  }

  const pending = items.filter((i) => i.kind === "proposal" && i.status === "pending" && !i.p.error).map((i) => (i as Extract<Item, { kind: "proposal" }>).p.id);

  return (
    <>
      <header className="head">
        <div>
          <h1>AI yordamchi</h1>
          <div className="sub">Groq · har o'zgarish sizning ruxsatingiz bilan</div>
        </div>
        {items.length ? <button className="btn ghost" onClick={() => setItems([])}>Tozalash</button> : null}
      </header>

      <div className="chat" style={{ paddingBottom: 120 }}>
        {items.length === 0 && (
          <div className="empty" style={{ paddingTop: 24 }}>
            Istalgan tilda yozing: mavzu qo'shish (bir vaqtda 5 tagacha), o'zgartirish, o'chirish, videoni hozir chiqarish yoki promptni o'zgartirish.
            <br />Video har doim ingliz tilida bo'ladi.
          </div>
        )}
        {items.map((it, idx) =>
          it.kind === "msg" ? (
            <div key={idx} className={`msg ${it.msg.role}`}>{it.msg.content}</div>
          ) : (
            <div key={it.p.id} className="proposal">
              <div className="what">{it.status === "done" ? "✅" : it.status === "rejected" ? "✖️" : it.status === "failed" ? "⚠️" : "📝"} {it.p.summary}</div>
              {it.p.preview ? <pre>{it.p.preview}</pre> : null}
              {it.p.error ? <div className="err">{it.p.error}</div> : null}
              {it.status === "pending" && !it.p.error ? (
                <div className="actions">
                  <button className="btn primary" onClick={() => decide([it.p.id], true)}>Ruxsat berish</button>
                  <button className="btn ghost" onClick={() => decide([it.p.id], false)}>Bekor</button>
                </div>
              ) : null}
              {it.status === "running" ? <div className="hint">Bajarilmoqda…</div> : null}
              {it.result ? <div className={`done ${it.status === "failed" ? "err" : "hint"}`}>{it.result}</div> : null}
            </div>
          ),
        )}
        {pending.length > 1 ? <button className="btn primary block" onClick={() => decide(pending, true)}>Hammasiga ruxsat ({pending.length})</button> : null}
        {thinking ? <div className="msg assistant"><span className="typing"><i /><i /><i /></span></div> : null}
        <div ref={end} />
      </div>

      <div className="composer">
        {items.length === 0 ? (
          <div className="suggest" style={{ padding: "0 0 8px" }}>
            {SUGGESTIONS.map((s) => <button key={s} onClick={() => send(s)}>{s}</button>)}
          </div>
        ) : null}
        <div className="box">
          <textarea
            rows={1}
            value={text}
            placeholder="Xabar yozing…"
            onChange={(e) => { setText(e.target.value); e.target.style.height = "auto"; e.target.style.height = `${e.target.scrollHeight}px`; }}
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey && window.matchMedia("(pointer: fine)").matches) { e.preventDefault(); void send(text); } }}
          />
          <button className="send" aria-label="Yuborish" disabled={!text.trim() || thinking} onClick={() => send(text)}><IconSend /></button>
        </div>
      </div>
    </>
  );
}

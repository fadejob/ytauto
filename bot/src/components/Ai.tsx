"use client";

/**
 * AI yordamchi (dizayn: ytauto.dsai → Screens: «AI yordamchi», «· takliflar», «· natija», «· bo'sh»).
 * Chat /api/ai orqali; AI faqat taklif qiladi - har o'zgarish foydalanuvchi «Ruxsat berish» bosgandan keyin /api/actions orqali bajariladi.
 * Taklif kartasining holati chekka rangida: kutmoqda - urg'u, bajarildi - yashil, xato - qizil, rad etildi - uzuq-uzuq kulrang.
 */
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import {
  ArrowUp, Ban, CalendarClock, Check, CheckCheck, CircleAlert, FilePenLine, Languages, ListPlus, MessageCircleQuestion, Music,
  Pencil, RotateCcw, Send, Sparkles, Trash2, TriangleAlert, Undo2, Upload, WifiOff, X,
} from "lucide-react";
import { api, haptic } from "@/lib/client";
import type { Action, ChatMessage, Proposal } from "@/lib/types";
import type { Run, Toast } from "./App";
import { Alert, Btn, PageHead, useBusy, type IconT } from "./ui";
import st from "./Ai.module.css";

type PStatus = "pending" | "running" | "done" | "rejected" | "failed";
type PItem = { kind: "proposal"; p: Proposal; status: PStatus; result?: string };
type Item = { kind: "msg"; msg: ChatMessage } | PItem;

const SUGGESTIONS = [
  "Kosmos haqida 3 ta qiziqarli mavzu qo'sh",
  "Navbatdagi birinchi videoni hozir chiqar",
  "Ssenariy promptida videolar qisqaroq bo'lsin",
];

const CAPABILITIES: { icon: IconT; title: string; sub: string }[] = [
  { icon: ListPlus, title: "Mavzu qo'shish", sub: "5 tagacha, har biriga boshlang'ich savol" },
  { icon: Pencil, title: "O'zgartirish yoki o'chirish", sub: "Video hali tayyorlanmagan mavzularni" },
  { icon: Send, title: "Hozir chiqarish yoki yuklash", sub: "Navbatni kutmasdan YouTube'ga" },
  { icon: FilePenLine, title: "Promptni o'zgartirish", sub: "Ssenariy (Gemini) yoki montaj (Claude)" },
];

const STORE = "ytauto:chat";

function ActionIcon({ action: a }: { action: Action }) {
  const z = 18;
  switch (a.type) {
    case "create_topics": return <ListPlus size={z} />;
    case "update_topic": {
      const fields = (["topic", "notes", "hook", "publishAt", "template", "format", "voice", "script", "title", "music"] as const).filter((k) => a[k] !== undefined);
      return fields.length === 1 && fields[0] === "music" ? <Music size={z} /> : <Pencil size={z} />;
    }
    case "delete_topic": return <Trash2 size={z} />;
    case "publish_now": return <Send size={z} />;
    case "upload_ready": case "force_new_upload": return <Upload size={z} />;
    case "cancel_publish_now": case "cancel_upload_ready": return <Undo2 size={z} />;
    case "update_prompt": return <FilePenLine size={z} />;
    case "retry": case "retry_settings": return <RotateCcw size={z} />;
    case "cancel": return <Ban size={z} />;
    case "reschedule": return <CalendarClock size={z} />;
    default: return <Sparkles size={z} />;
  }
}

/** «#19 tayyor videosini YouTube'ga yuklash (qayta tayyorlanmaydi)» → sarlavha + izoh. */
function splitSummary(summary: string): { title: string; note?: string } {
  const m = /^(.*\S)\s*\(([^()]+)\)$/.exec(summary.trim());
  return m ? { title: m[1], note: m[2] } : { title: summary };
}

/** Server (lib/ai.ts describe) yozgan ko'rinishni o'qiladigan qilib chizadi; tanilmagan shakl - matn sifatida. */
function Preview({ p }: { p: Proposal }) {
  const text = p.preview?.trim();
  if (!text) return null;
  const a = p.action;

  if (a.type === "create_topics") {
    return (
      <div className={st.box}>
        {text.split(/\n\s*\n/).map((block, i) => {
          const [first, ...rest] = block.split("\n");
          return (
            <div key={i} className={st.topic}>
              <b>{first.replace(/^\s*\d+\.\s*/, "")}</b>
              {rest.map((raw, j) => {
                const line = raw.trim();
                if (!line) return null;
                if (line.startsWith("❓")) return <div key={j} className={st.hook}><MessageCircleQuestion size={16} />{line.replace(/^❓\s*/, "")}</div>;
                if (line.startsWith("🎵")) return <div key={j} className={st.meta}><Music size={14} />{line.replace(/^🎵\s*/, "")}</div>;
                return <div key={j} className={st.notes}>{line}</div>;
              })}
            </div>
          );
        })}
      </div>
    );
  }

  if (a.type === "update_prompt" && a.edits?.length) {
    // Har o'zgarish: «− eski» va «+ yangi» (ko'p qatorli bo'lishi mumkin - belgi keyingi belgigacha amal qiladi).
    const blocks = text.split(/\n\s*\n(?=[−+-] )/);
    return (
      <div className={st.box}>
        {blocks.map((block, i) => {
          const rows: { sign: "-" | "+" | ""; text: string }[] = [];
          for (const line of block.split("\n")) {
            const m = /^([−+-]) ?(.*)$/.exec(line);
            if (m) rows.push({ sign: m[1] === "+" ? "+" : "-", text: m[2] });
            else if (rows.length) rows[rows.length - 1].text += "\n" + line;
            else rows.push({ sign: "", text: line });
          }
          return (
            <div key={i} className={st.diff}>
              {rows.map((r, j) => (
                <div key={j} className={r.sign === "+" ? st.add : r.sign === "-" ? st.del : undefined}>
                  <span className={st.sign} aria-hidden>{r.sign === "-" ? "−" : r.sign}</span>
                  <span className={st.dtext}>{r.sign === "-" ? <><span className="sr-only">O&apos;chiriladi: </span><s>{r.text}</s></> : r.sign === "+" ? <><span className="sr-only">Qo&apos;shiladi: </span>{r.text}</> : r.text}</span>
                </div>
              ))}
            </div>
          );
        })}
      </div>
    );
  }

  if (a.type === "update_topic") {
    const lines = text.split("\n").filter((l) => l.trim());
    return (
      <div className={st.box}>
        {lines.map((line, i) => {
          const m = /^([^:]{1,40}):\s?(.*)$/.exec(line);
          if (m && m[1] === "Hook") return <div key={i} className={st.hook}><MessageCircleQuestion size={16} />{m[2]}</div>;
          if (m && m[1] === "Fon musiqasi") return <div key={i} className={st.meta}><Music size={14} />{m[2]}</div>;
          return m ? <div key={i} className={st.kv}><span>{m[1]}</span>{m[2]}</div> : <div key={i} className={st.notes}>{line}</div>;
        })}
      </div>
    );
  }

  return <pre className="pre">{text}</pre>;
}

function ProposalCard({ it, primary, approving, locked, onApprove, onReject }: {
  it: PItem; primary: boolean; approving: boolean; locked: boolean; onApprove: () => void; onReject: () => void;
}) {
  const { p, status } = it;
  const { title, note } = splitSummary(p.summary);

  // Yakunlangan taklif - ixcham qator (dizayn: «AI yordamchi · natija»).
  if (status === "done" || status === "failed" || status === "rejected") {
    const tone = status === "done" ? st.done : status === "failed" ? st.failed : st.rejected;
    const Mark = status === "done" ? Check : status === "failed" ? TriangleAlert : X;
    const line = status === "done" ? "Bajarildi" : status === "rejected" ? "Rad etildi" : it.result || "Bajarilmadi";
    return (
      <article className={`${st.prop} ${st.compact} ${tone}`} aria-label={`${title}: ${line}`}>
        <span className={st.mark}><Mark size={18} strokeWidth={2.4} /></span>
        <div className={st.ptext}>
          <b className={st.ptitle}>{title}</b>
          <span className={st.pstate}>{line}</span>
          {status === "done" && it.result ? <span className={st.presult}>{it.result}</span> : null}
        </div>
      </article>
    );
  }

  const bad = !!p.error;
  return (
    <article className={`${st.prop} ${bad ? st.invalid : st.waiting}`} aria-busy={status === "running" || undefined}>
      <div className={st.phead}>
        <span className={st.picon}><ActionIcon action={p.action} /></span>
        <div className={st.ptext}>
          <b className={st.ptitle}>{title}</b>
          {note ? <span className="sub">{note}</span> : null}
        </div>
      </div>
      <Preview p={p} />
      {bad ? <Alert>{p.error}</Alert> : (
        <div className={st.pacts}>
          <Btn tone={primary ? "primary" : "secondary"} loading={approving || (status === "running" && !locked)} disabled={locked} onClick={onApprove}>Ruxsat berish</Btn>
          <Btn tone={primary ? "secondary" : "ghost"} disabled={status === "running" || locked} onClick={onReject}>Rad etish</Btn>
        </div>
      )}
    </article>
  );
}

function Bot({ children }: { children: ReactNode }) {
  return (
    <div className={st.botrow}>
      <span className={st.avatar} aria-hidden><Sparkles size={16} /></span>
      {children}
    </div>
  );
}

/** fetch o'zi yiqilgan (server javob bermagan): brauzerning tarmoq xatosi yoki qurilma oflayn. */
const offline = (m: string) => /failed to fetch|networkerror|load failed/i.test(m) || (typeof navigator !== "undefined" && navigator.onLine === false);

export default function Ai({ run, toast }: { run: Run; toast: Toast }) {
  const [items, setItems] = useState<Item[]>([]);
  const [text, setText] = useState("");
  const [thinking, setThinking] = useState(false);
  const [sendErr, setSendErr] = useState<string | null>(null);
  const [dockH, setDockH] = useState(180);
  const end = useRef<HTMLDivElement>(null);
  const dock = useRef<HTMLDivElement>(null);
  const area = useRef<HTMLTextAreaElement>(null);
  const busy = useBusy((m) => toast(m, true));

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- sessionStorage faqat mount'dan keyin o'qiladi
    try { const v = sessionStorage.getItem(STORE); if (v) setItems(JSON.parse(v)); } catch {}
  }, []);
  useEffect(() => {
    try { sessionStorage.setItem(STORE, JSON.stringify(items.slice(-60))); } catch {}
    end.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [items, thinking, sendErr]);

  // Pastdagi (takliflar + yozish maydoni) blok balandligi o'zgaradi - chat oxiri uning ostida qolib ketmasin.
  useLayoutEffect(() => {
    const el = dock.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(() => setDockH(el.offsetHeight));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  function fit(el: HTMLTextAreaElement | null) {
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }

  async function send(content: string) {
    const msg = content.trim();
    if (!msg || thinking) return;
    haptic.tap();
    const mine: Item = { kind: "msg", msg: { role: "user", content: msg } };
    const next: Item[] = [...items, mine];
    setItems(next);
    setText("");
    setSendErr(null);
    requestAnimationFrame(() => fit(area.current));
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
      // Xabar yetib bormadi: chatdan olib, matnni yozish maydoniga qaytaramiz - qayta yuborish bir bosish.
      haptic.fail();
      setItems((cur) => (cur[cur.length - 1] === mine ? cur.slice(0, -1) : cur));
      setText((t) => t || msg);
      requestAnimationFrame(() => fit(area.current));
      setSendErr(e instanceof Error ? e.message : String(e));
    } finally {
      setThinking(false);
    }
  }

  async function decide(ids: string[], approve: boolean) {
    const targets = items.filter((i): i is PItem => i.kind === "proposal" && ids.includes(i.p.id) && i.status === "pending" && !i.p.error);
    if (!approve) {
      haptic.tap();
      setItems((cur) => cur.map((i) => (i.kind === "proposal" && ids.includes(i.p.id) && i.status === "pending" ? { ...i, status: "rejected" } : i)));
      return;
    }
    if (!targets.length) return;
    setItems((cur) => cur.map((i) => (i.kind === "proposal" && targets.some((t) => t.p.id === i.p.id) ? { ...i, status: "running" } : i)));
    const results = await run(targets.map((t) => t.p.action));
    setItems((cur) => cur.map((i) => {
      if (i.kind !== "proposal") return i;
      const k = targets.findIndex((t) => t.p.id === i.p.id);
      return k < 0 ? i : { ...i, status: results[k]?.ok ? "done" : "failed", result: results[k]?.message };
    }));
  }

  const proposals = items.filter((i): i is PItem => i.kind === "proposal");
  const pending = proposals.filter((i) => i.status === "pending" && !i.p.error).map((i) => i.p.id);
  const running = proposals.filter((i) => i.status === "running").length;
  const allBusy = busy.is("all");
  const showAll = pending.length > 1 || allBusy;
  const empty = items.length === 0;

  return (
    <div className={`page ${st.wrap}`} style={{ paddingBottom: dockH + 8 }}>
      <PageHead
        title="AI yordamchi"
        sub="Gemini · har o'zgarish ruxsatingiz bilan"
        right={!empty ? <Btn tone="secondary" size="sm" disabled={thinking || running > 0} onClick={() => { haptic.tap(); setItems([]); setSendErr(null); }}>Tozalash</Btn> : null}
      />

      {empty ? (
        <section className={`card ${st.intro}`} aria-labelledby="ai-intro">
          <div className={st.introHead}>
            <span className={st.avatar} aria-hidden><Sparkles size={16} /></span>
            <h2 id="ai-intro" className="h2">Istalgan tilda yozing — men taklif qilaman, siz ruxsat berasiz</h2>
          </div>
          <ul className={st.caps}>
            {CAPABILITIES.map(({ icon: I, title, sub }) => (
              <li key={title}>
                <span className={st.picon}><I size={18} /></span>
                <div className={st.ptext}><b>{title}</b><span className="sub">{sub}</span></div>
              </li>
            ))}
          </ul>
          <div className={st.lang}><Languages size={16} />Video har doim ingliz tilida bo&apos;ladi</div>
        </section>
      ) : (
        <div className={st.chat} role="log" aria-live="polite" aria-label="AI bilan suhbat">
          {items.map((it, idx) =>
            it.kind === "msg" ? (
              it.msg.role === "assistant"
                ? <Bot key={idx}><div className="msg assistant">{it.msg.content}</div></Bot>
                : <div key={idx} className="msg user">{it.msg.content}</div>
            ) : (
              <ProposalCard
                key={it.p.id}
                it={it}
                primary={!showAll}
                approving={busy.is(`p:${it.p.id}`)}
                locked={allBusy}
                onApprove={() => { haptic.tap(); void busy.run(`p:${it.p.id}`, () => decide([it.p.id], true)); }}
                onReject={() => void decide([it.p.id], false)}
              />
            ),
          )}
          {showAll ? (
            <Btn block icon={CheckCheck} loading={allBusy} onClick={() => { haptic.tap(); void busy.run("all", () => decide(pending, true)); }}>
              Hammasiga ruxsat ({allBusy ? running : pending.length})
            </Btn>
          ) : null}
          {thinking ? <Bot><div className="msg assistant" aria-label="AI yozmoqda"><span className="typing"><i /><i /><i /></span></div></Bot> : null}
        </div>
      )}
      <div ref={end} />

      <div ref={dock} className={`composer ${st.dock}`}>
        {sendErr ? (
          <div className={st.sendErr} role="alert">
            {offline(sendErr) ? <WifiOff size={18} /> : <CircleAlert size={18} />}
            <span>Xabar yuborilmadi: {offline(sendErr) ? "internet aloqasi yo'q" : sendErr}</span>
            <button type="button" className={st.errX} aria-label="Yopish" onClick={() => setSendErr(null)}><X size={16} /></button>
          </div>
        ) : null}
        <div className={empty ? st.chipsCol : st.chipsRow} aria-label="Tayyor so'rovlar">
          {SUGGESTIONS.map((q) => (
            <button key={q} type="button" className={st.chip} disabled={thinking} onClick={() => send(q)}><Sparkles size={15} />{q}</button>
          ))}
        </div>
        <div className={`box ${st.box2}`}>
          <textarea
            ref={area}
            rows={1}
            value={text}
            placeholder="Xabar yozing…"
            aria-label="Xabar"
            onChange={(e) => { setText(e.target.value); if (sendErr) setSendErr(null); fit(e.target); }}
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey && window.matchMedia("(pointer: fine)").matches) { e.preventDefault(); void send(text); } }}
          />
          <button type="button" className={`send ${st.send}`} aria-label="Yuborish" disabled={!text.trim() || thinking} onClick={() => send(text)}><ArrowUp /></button>
        </div>
      </div>
    </div>
  );
}

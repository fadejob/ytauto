"use client";

import { useCallback, useEffect, useState } from "react";
import { api, cache, confirmBox, haptic } from "@/lib/client";
import type { ActionResult, ChannelState, VideoInsight } from "@/lib/types";
import type { Toast } from "./App";
import { IconPlay, IconPlus, IconRefresh } from "./icons";

const ago = (iso: string | null) => {
  if (!iso) return "—";
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return "hozirgina";
  if (s < 3600) return `${Math.round(s / 60)} daqiqa oldin`;
  if (s < 86400) return `${Math.round(s / 3600)} soat oldin`;
  return `${Math.round(s / 86400)} kun oldin`;
};
const num = (n: number) => (n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}K` : String(n));
const mmss = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
const GENDER: Record<string, string> = { female: "ayol", male: "erkak", unknown: "aniqlanmadi" };

/**
 * «Kanal»: ulangan YouTube kanalining tahlili (oxirgi 3 video: montaj, ovoz, subtitr, ssenariy), shundan promptlarga
 * qo'shiladigan blok va bugungi mavzu tavsiyalari. Tahlil fonda ishlaydi — tugaguncha sahifa o'zi yangilanadi.
 */
export default function Channel({ toast, onTopicsChanged, openSettings }: { toast: Toast; onTopicsChanged: () => void; openSettings: () => void }) {
  const [st, setSt] = useState<ChannelState | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [showBlocks, setShowBlocks] = useState(false);

  const load = useCallback(async () => {
    try { const s = await api<ChannelState>("/api/channel"); setSt(s); cache.set("channel", s); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
  }, [toast]);

  useEffect(() => {
    const c = cache.get<ChannelState>("channel");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi
    if (c) setSt(c);
    void load();
  }, [load]);

  // Tahlil yoki tavsiyalar tayyorlanayotganda har 8 soniyada yangilanadi.
  const waiting = st?.status === "running" || !!st?.suggestionsPending;
  useEffect(() => {
    if (!waiting) return;
    const t = window.setInterval(() => void load(), 8000);
    return () => window.clearInterval(t);
  }, [waiting, load]);

  async function act(key: string, body: Record<string, unknown>, ok?: () => void) {
    haptic.tap();
    setBusy(key);
    try {
      const r = await api<ActionResult>("/api/channel", body);
      toast(r.message, !r.ok);
      if (r.ok) { haptic.ok(); ok?.(); } else haptic.fail();
      await load();
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  const reanalyze = async () => {
    if (!(await confirmBox("Kanal qayta o'rganilsinmi? Oxirgi 3 video Gemini bilan ko'rib chiqiladi (1–3 daqiqa, Gemini limitidan sarflanadi)."))) return;
    await act("analyze", { action: "analyze" });
  };

  const head = (
    <header className="head">
      <div>
        <h1>Kanal</h1>
        <div className="sub">{st?.channelTitle ? `${st.channelTitle} · ${st.analyzedAt ? `o'rganilgan ${ago(st.analyzedAt)}` : "hali o'rganilmagan"}` : "Kanal konteksti va kunlik tavsiyalar"}</div>
      </div>
      <button className="iconbtn" aria-label="Yangilash" onClick={() => { haptic.tap(); void load(); }}><IconRefresh /></button>
    </header>
  );

  if (!st) return <>{head}<div className="list"><div className="skeleton" /><div className="skeleton" /></div></>;

  if (!st.connected)
    return (
      <>
        {head}
        <div className="list">
          <div className="card formcard">
            <div>YouTube kanali ulanmagan. Kanal ulangach bot uni o&apos;zi o&apos;rganadi: nomi, tavsifi, sarlavhalari va oxirgi 3 videoning montaji, ovozi, subtitri va ssenariysi.
              Shundan ssenariy va montaj promptlari, ovoz tanlovi va kunlik mavzu tavsiyalari kanalingizga moslashadi.</div>
            <button className="btn primary block" onClick={openSettings}>Sozlamalar → YouTube&apos;ni ulash</button>
          </div>
        </div>
      </>
    );

  const c = st.context;
  const running = st.status === "running";

  return (
    <>
      {head}
      <div className="list" style={{ paddingBottom: 12 }}>
        {running ? (
          <div className="card ctx pulse">
            <b>Kanal o&apos;rganilmoqda…</b>
            <span>Oxirgi 3 video Gemini bilan ko&apos;rib chiqilmoqda: montaj, ovoz, subtitr va ssenariy. Odatda 1–3 daqiqa — sahifa o&apos;zi yangilanadi.</span>
          </div>
        ) : null}
        {st.error ? <div className="card ctx"><b style={{ color: "var(--danger)" }}>Oxirgi tahlil xatosi</b><span>{st.error}</span></div> : null}

        {!c && !running ? (
          <div className="card formcard">
            <div>Kanal hali o&apos;rganilmagan.</div>
            <button className="btn primary block" disabled={busy === "analyze"} onClick={reanalyze}>Kanalni o&apos;rganish</button>
          </div>
        ) : null}

        {c ? (
          <>
            {/* Kanal xulosasi */}
            <article className="card topic">
              <div className="row">
                <div className="title">{c.channel.title}</div>
                <a className="btn" href={c.channel.url} target="_blank" rel="noreferrer"><IconPlay />Kanal</a>
              </div>
              <div className="meta">
                <span>{num(c.channel.subscribers)} obunachi</span><span>{c.channel.videoCount} video</span>
                {c.niche ? <span className="chip">{c.niche}</span> : null}
              </div>
              <div className="ctx">
                <span>{c.summary}</span>
                {c.audience ? <span><b>Auditoriya:</b> {c.audience}</span> : null}
                {c.tone ? <span><b>Ohang:</b> {c.tone}</span> : null}
                <span><b>Ovoz:</b> {GENDER[c.voice.gender] ?? c.voice.gender}{c.voice.style ? ` · ${c.voice.style}` : ""}{c.voice.pace ? ` · ${c.voice.pace}` : ""}
                  {c.voice.recommendedProfile ? <> → profil <b style={{ color: "var(--text)" }}>{c.voice.recommendedProfile}</b></> : " (mos profil topilmadi — standart ishlatiladi)"}</span>
              </div>
              <label className="chk">
                <input type="checkbox" checked={st.enabled} disabled={busy === "toggle"} onChange={(e) => act("toggle", { action: "toggle", enabled: e.target.checked })} />
                <span>Kontekstni ssenariy va montaj promptlariga qo&apos;shish</span>
              </label>
              <div className="actions">
                <button className="btn" disabled={running || busy === "analyze"} onClick={reanalyze}><IconRefresh />Qayta o&apos;rganish</button>
                <button className="btn ghost" onClick={() => setShowBlocks((v) => !v)}>{showBlocks ? "Blokni yashirish" : "Promptlarga qo'shiladigan blok"}</button>
              </div>
              {showBlocks && st.blocks ? (
                <div className="ctx">
                  <b>Ssenariy (Gemini) — {"{channel_context}"} o&apos;rniga</b>
                  <div className="pre">{st.blocks.script}</div>
                  <b>Montaj (Claude) — «Channel style» bo&apos;limi</b>
                  <div className="pre">{st.blocks.montage}</div>
                </div>
              ) : null}
            </article>

            {/* Bugungi tavsiyalar */}
            <div className="sect">Bugungi mavzu tavsiyalari</div>
            {st.suggestions.length === 0 ? (
              <div className="card hint">{st.suggestionsPending || running ? <span className="pulse">Tavsiyalar tayyorlanmoqda…</span> : "Bugun uchun tavsiya yo'q. Ertaga yangilari chiqadi."}</div>
            ) : st.suggestions.map((s) => (
              <article key={s.id} className="card topic" style={s.status !== "new" ? { opacity: 0.6 } : undefined}>
                <div className="row">
                  <div className="title">{s.topic}</div>
                  {s.status === "accepted" ? <span className="chip ok">navbatga qo&apos;shildi</span> : s.status === "dismissed" ? <span className="chip mute">rad etildi</span> : null}
                </div>
                {s.notes ? <div className="hint" style={{ fontSize: 13 }}>{s.notes}</div> : null}
                {s.reason ? <div className="ctx"><span>💡 {s.reason}</span></div> : null}
                {s.status === "new" ? (
                  <div className="actions">
                    <button className="btn primary" disabled={busy === `a${s.id}`} onClick={() => act(`a${s.id}`, { action: "accept", id: s.id }, onTopicsChanged)}><IconPlus />Navbatga qo&apos;shish</button>
                    <button className="btn ghost" disabled={busy === `d${s.id}`} onClick={() => act(`d${s.id}`, { action: "dismiss", id: s.id })}>Rad etish</button>
                  </div>
                ) : null}
              </article>
            ))}

            {/* Oxirgi 3 video */}
            <div className="sect">Oxirgi videolar tahlili</div>
            {c.videos.map((v) => <VideoCard key={v.videoId} v={v} open={open === v.videoId} toggle={() => setOpen(open === v.videoId ? null : v.videoId)} />)}

            {/* Kanal uslubi */}
            <div className="sect">Kanal uslubi (promptlarga shu kiradi)</div>
            <article className="card ctx">
              <List title="Ssenariy" items={c.scriptGuidelines} />
              <List title="Montaj" items={c.montageGuidelines} />
              <List title="Subtitr" items={c.subtitleGuidelines} />
              <List title="Sarlavha naqshlari" items={c.titlePatterns} />
              <List title="Davom ettirish" items={c.doList} />
              <List title="Qochish kerak" items={c.avoidList} />
            </article>
          </>
        ) : null}
      </div>
    </>
  );
}

function List({ title, items }: { title: string; items: string[] }) {
  if (!items?.length) return null;
  return <div><b>{title}</b><ul>{items.map((x, i) => <li key={i}>{x}</li>)}</ul></div>;
}

function VideoCard({ v, open, toggle }: { v: VideoInsight; open: boolean; toggle: () => void }) {
  return (
    <article className="card topic">
      <div className="row">
        <div className="title" onClick={toggle} style={{ cursor: "pointer" }}>{v.title}</div>
        <a className="id" href={`https://youtu.be/${v.videoId}`} target="_blank" rel="noreferrer">▶</a>
      </div>
      <div className="meta">
        <span>{num(v.views)} ko&apos;rish</span><span>{num(v.likes)} layk</span><span>{mmss(v.durationSec)}{v.isShort ? " · Shorts" : ""}</span>
        <span className={v.source === "video" ? "chip ok" : "chip mute"}>{v.source === "video" ? "video ko'rib chiqildi" : "faqat sarlavha/tavsif"}</span>
      </div>
      <div className="ctx">
        <span>{v.summary}</span>
        {v.script.hook ? <span className="quote">{v.script.hook}</span> : null}
        {open ? (
          <>
            <span><b>Ssenariy:</b> {v.script.structure}</span>
            <span><b>Ohang va sur&apos;at:</b> {v.script.tone} · {v.script.pacing}</span>
            {v.script.cta ? <span><b>Chaqiriq (CTA):</b> {v.script.cta}</span> : null}
            <span><b>Ovoz:</b> {GENDER[v.voice.gender] ?? v.voice.gender} · {v.voice.style} · {v.voice.pace} · {v.voice.energy}</span>
            <span><b>Montaj:</b> {v.montage.visualStyle}</span>
            <span><b>Montaj sur&apos;ati:</b> {v.montage.editingPace}</span>
            <span><b>Ekrandagi matn:</b> {v.montage.onScreenText}</span>
            <span><b>O&apos;tishlar:</b> {v.montage.transitions}</span>
            <span><b>Musiqa:</b> {v.montage.music}</span>
            <span><b>Subtitr:</b> {v.subtitles.present ? v.subtitles.style : "yo'q"}</span>
            {v.script.transcriptExcerpt ? <><b>Matn boshlanishi</b><div className="pre">{v.script.transcriptExcerpt}</div></> : null}
          </>
        ) : null}
      </div>
      <button className="btn ghost" onClick={toggle}>{open ? "Yig'ish" : "Batafsil: ssenariy, ovoz, montaj, subtitr"}</button>
    </article>
  );
}

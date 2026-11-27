"use client";

import { useCallback, useEffect, useState } from "react";
import {
  Check, ChevronDown, ChevronUp, ChevronsUpDown, ExternalLink, Eye, FileText, Link as LinkIcon, Play, Plus, RotateCcw, RotateCw, Sparkles, X,
} from "lucide-react";
import { api, cache, confirmBox, haptic, openExternal } from "@/lib/client";
import type { ActionResult, ChannelContext, ChannelState, Suggestion, VideoInsight } from "@/lib/types";
import type { Toast } from "./App";
import { Alert, Btn, HookLine, HookSlab, Skel, SkelCard, Status, ToggleRow, useBusy } from "./ui";
import s from "./Channel.module.css";

const ago = (iso: string | null) => {
  if (!iso) return "—";
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (sec < 60) return "hozirgina";
  if (sec < 3600) return `${Math.round(sec / 60)} daqiqa oldin`;
  if (sec < 86400) return `${Math.round(sec / 3600)} soat oldin`;
  return `${Math.round(sec / 86400)} kun oldin`;
};
const num = (n: number) => (n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}K` : String(n));
const mmss = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, "0")}`;
const GENDER: Record<string, string> = { female: "ayol", male: "erkak", unknown: "aniqlanmadi" };
const initial = (t: string) => (t.trim()[0] ?? "K").toUpperCase();
const cx = (...c: (string | false | null | undefined)[]) => c.filter(Boolean).join(" ");

/**
 * «Kanal»: bugungi g'oyalar (boshlang'ich savol - asosiy), ulangan YouTube kanalining tahlili (oxirgi 3 video: montaj, ovoz,
 * subtitr, ssenariy) va promptlarga qo'shiladigan uslub. Tahlil fonda ishlaydi - tugaguncha sahifa o'zi yangilanadi.
 */
export default function Channel({ toast, onTopicsChanged, openSettings }: { toast: Toast; onTopicsChanged: () => void; openSettings: () => void }) {
  const [st, setSt] = useState<ChannelState | null>(null);
  const busy = useBusy((m) => { haptic.fail(); toast(m, true); });

  const load = useCallback(async () => {
    try { const r = await api<ChannelState>("/api/channel"); setSt(r); cache.set("channel", r); }
    catch (e) { toast(`Yangilab bo'lmadi: ${e instanceof Error ? e.message : String(e)}`, true); }
  }, [toast]);

  useEffect(() => {
    const c = cache.get<ChannelState>("channel");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi
    if (c) setSt(c);
    void load();
  }, [load]);

  // Tahlil yoki g'oyalar tayyorlanayotganda har 8 soniyada yangilanadi.
  const waiting = st?.status === "running" || !!st?.suggestionsPending;
  useEffect(() => {
    if (!waiting) return;
    const t = window.setInterval(() => { if (document.visibilityState === "visible") void load(); }, 8000);
    return () => window.clearInterval(t);
  }, [waiting, load]);

  /** Kanal amali: faqat shu tugma spinner ko'rsatadi; natija - toast, keyin holat qayta olinadi. */
  const act = (key: string, body: Record<string, unknown>, ok?: () => void) =>
    busy.run(key, async () => {
      haptic.tap();
      // Javobda kanalning yangi holati ham keladi - alohida GET kutilmaydi.
      const r = await api<ActionResult & { channel?: ChannelState }>("/api/channel", body);
      toast(r.message, !r.ok);
      if (r.ok) { haptic.ok(); ok?.(); } else haptic.fail();
      if (r.channel) { setSt(r.channel); cache.set("channel", r.channel); } else await load();
      return r.ok;
    });

  const reanalyze = async () => {
    if (!(await confirmBox("Kanal qayta o'rganilsinmi? Oxirgi 3 video Gemini bilan ko'rib chiqiladi (1–3 daqiqa, Gemini limitidan sarflanadi)."))) return;
    await act("analyze", { action: "analyze" });
  };

  const refresh = () => { haptic.tap(); void busy.run("refresh", load); };
  const refreshBtn = (
    <button type="button" className={cx("round", s.refresh)} aria-label="Yangilash" disabled={busy.is("refresh")} onClick={refresh}>
      <RotateCw size={20} className={busy.is("refresh") ? "spin" : undefined} />
    </button>
  );

  // ---------------------------------------------------------------- birinchi yuklanish: skelet faqat shu yerda
  if (!st)
    return (
      <div className="page" aria-busy="true">
        <div className={s.top}><Skel h={44} w={200} r={999} />{refreshBtn}</div>
        <div className={s.summary}><Skel h={40} w="70%" /><Skel h={6} r={999} /><Skel h={13} w="55%" /></div>
        <SkelCard lines={3} />
        <SkelCard lines={2} />
      </div>
    );

  // ---------------------------------------------------------------- kanal ulanmagan
  if (!st.connected)
    return (
      <div className="page">
        {/* Dizayn: Holatlar → Bo'sh · Kanal ulanmagan - uzuq chiziqli karta, ichida ghost-chip, matn va bitta tugma. */}
        <section className={s.emptyCard} aria-label="YouTube kanali ulanmagan">
          <div className={s.ghostChip} aria-hidden><span className={s.ghostAvatar} /><span className={s.ghostName} /></div>
          <div className={s.emptyText}>
            <b>YouTube kanali ulanmagan</b>
            <span>Ulangach bot kanalni o&apos;zi o&apos;rganadi: oxirgi 3 videoning montaji, ovozi, subtitri va ssenariysi. Promptlar va kunlik g&apos;oyalar shunga moslashadi.</span>
          </div>
          <Btn block icon={LinkIcon} onClick={() => { haptic.tap(); openSettings(); }}>Sozlamalar → YouTube&apos;ni ulash</Btn>
        </section>
      </div>
    );

  const c = st.context;
  const running = st.status === "running";
  const fresh = st.suggestions.filter((x) => x.status === "new");
  const accepted = st.suggestions.filter((x) => x.status === "accepted");
  const dismissed = st.suggestions.filter((x) => x.status === "dismissed");
  const showIdeas = !!c || st.suggestions.length > 0;
  const title = st.channelTitle || c?.channel.title || "Kanal";

  const ideasSub = st.suggestions.length === 0
    ? (st.suggestionsPending || running ? "G'oyalar tayyorlanmoqda…" : "Bugun uchun g'oya yo'q · ertaga yangilari chiqadi")
    : fresh.length === 0
      ? `Bugungi ${st.suggestions.length} g'oya ko'rib chiqildi · ertaga yangilari chiqadi`
      : [accepted.length ? `${accepted.length} tasi navbatga qo'shildi` : "", dismissed.length ? `${dismissed.length} tasi kerak emas` : ""].filter(Boolean).join(", ")
        || "Kanalingiz tahlilidan · yoqqanini navbatga qo'shing";

  const runningCard = (
    <section className="card" aria-busy="true" aria-live="polite">
      <div className={s.analysis}>
        <Status tone="accent">Tahlil qilinmoqda</Status>
        <Btn tone="secondary" size="sm" loading>Qayta tahlil</Btn>
      </div>
      <p className={s.runningText}>Oxirgi 3 video Gemini bilan ko&apos;rilmoqda: montaj, ovoz, subtitr, ssenariy. Odatda 1–3 daqiqa, sahifa o&apos;zi yangilanadi.</p>
      {st.error ? <Alert><b>Oxirgi tahlil xatosi</b><br />{st.error}</Alert> : null}
      <div className={s.skelTiles}><Skel h={60} r={10} /><Skel h={60} r={10} /><Skel h={60} r={10} /></div>
      <Skel h={12} /><Skel h={12} w="70%" />
    </section>
  );

  return (
    <div className="page">
      <div className={s.top}>
        <button type="button" className={s.chip} onClick={() => { haptic.tap(); openSettings(); }} aria-label={`Kanal: ${title}. Almashtirish`}>
          <span className={s.avatar}>{initial(title)}</span><span className={s.name}>{title}</span><ChevronsUpDown size={16} />
        </button>
        {refreshBtn}
      </div>

      <div className={s.cols}>
        <div className={s.col}>
          {showIdeas ? (
            <>
              <div className={s.summary}>
                <h1 className="focal">{fresh.length} <span className="dim">g&apos;oya kutmoqda</span></h1>
                {st.suggestions.length ? (
                  <div className={s.ideaStrip} aria-hidden>
                    {accepted.map((x) => <i key={x.id} className={s.ok} />)}
                    {dismissed.map((x) => <i key={x.id} className={s.no} />)}
                    {fresh.map((x) => <i key={x.id} />)}
                  </div>
                ) : null}
                <div className="sub" style={{ fontSize: 15 }}>{ideasSub}</div>
              </div>

              {st.suggestions.length === 0 && (st.suggestionsPending || running) ? <SkelCard lines={2} /> : null}

              {fresh.map((x) => (
                <IdeaCard key={x.id} x={x}
                  accepting={busy.is(`a${x.id}`)} dismissing={busy.is(`d${x.id}`)}
                  onAccept={() => void act(`a${x.id}`, { action: "accept", id: x.id }, onTopicsChanged)}
                  onDismiss={() => void act(`d${x.id}`, { action: "dismiss", id: x.id })} />
              ))}

              {running ? runningCard : null}

              {accepted.length || dismissed.length ? (
                <div className={s.handled} aria-label="Ko'rib chiqilgan g'oyalar">
                  {accepted.map((x) => <HandledRow key={x.id} x={x} />)}
                  {dismissed.map((x) => <HandledRow key={x.id} x={x} />)}
                </div>
              ) : null}
            </>
          ) : (
            <>
              <div className={s.summary}>
                <h1 className={s.title}>{running ? "Kanal o'rganilmoqda" : "Kanal hali o'rganilmagan"}</h1>
                <p className={s.explain}>Bot oxirgi 3 videoni ko&apos;rib chiqqach, kunlik g&apos;oyalar, ssenariy, montaj va ovoz kanalingizga moslashadi.</p>
              </div>
              {running ? runningCard : (
                <section className="card">
                  {st.error ? <Alert><b>Oxirgi tahlil xatosi</b><br />{st.error}</Alert> : null}
                  <div style={{ display: "grid", gap: 8 }}>
                    <Btn block icon={RotateCcw} loading={busy.is("analyze")} onClick={reanalyze}>{st.error ? "Qayta urinish" : "Kanalni o'rganish"}</Btn>
                    <span className={s.actionHint}>1–3 daqiqa · Gemini limitidan sarflanadi</span>
                  </div>
                </section>
              )}
            </>
          )}
        </div>

        {c ? (
          <div className={s.col}>
            <div className={s.sectionHead}>
              <h2 className={s.title}>Kanal qanday tushunilgan</h2>
              <div className="sub" style={{ fontSize: 14 }}>Ssenariy, montaj va ovoz shunga moslashadi</div>
            </div>
            {running ? null : (
              <Understanding st={st} c={c} analyzing={busy.is("analyze")} onReanalyze={reanalyze} />
            )}
            <StyleCard st={st} c={c} toggling={busy.is("toggle")} onToggle={(v) => void act("toggle", { action: "toggle", enabled: v })} />
          </div>
        ) : null}
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ g'oya kartasi va ko'rib chiqilgan qator
function IdeaCard({ x, accepting, dismissing, onAccept, onDismiss }: { x: Suggestion; accepting: boolean; dismissing: boolean; onAccept: () => void; onDismiss: () => void }) {
  return (
    <article className={cx("card", s.idea)}>
      {x.hook ? <HookSlab><div className="q">{x.hook}</div></HookSlab> : null}
      <div className={s.about}>
        <div className={cx(s.topic, !x.hook && s.topicLg)}>{x.topic}</div>
        {x.notes ? <div className={s.notes}>{x.notes}</div> : null}
        {x.reason ? <div className={s.reason}><Sparkles size={14} /><span>{x.reason}</span></div> : null}
      </div>
      <div className={s.actions}>
        <Btn icon={Plus} loading={accepting} disabled={dismissing} onClick={onAccept}>{accepting ? "Qo'shilmoqda" : "Qo'shish"}</Btn>
        <Btn tone="ghost" loading={dismissing} disabled={accepting} onClick={onDismiss}>Kerak emas</Btn>
      </div>
    </article>
  );
}

function HandledRow({ x }: { x: Suggestion }) {
  const ok = x.status === "accepted";
  return (
    <div className={cx(s.row, !ok && s.dismissed)}>
      <span className={s.mark} aria-hidden>{ok ? <Check size={16} /> : <X size={16} />}</span>
      <span className={s.topic}>{x.topic}</span>
      {ok ? <Status tone="ok">Navbatda</Status> : <Status>Kerak emas</Status>}
    </div>
  );
}

// ------------------------------------------------------------------ «Kanal qanday tushunilgan»
function Understanding({ st, c, analyzing, onReanalyze }: { st: ChannelState; c: ChannelContext; analyzing: boolean; onReanalyze: () => void }) {
  const [open, setOpen] = useState<string | null>(null);
  const sel = (c.videos ?? []).find((v) => v.videoId === open) ?? null;
  const failed = st.status === "failed" || !!st.error;
  const voiceDesc = [GENDER[c.voice.gender] ?? c.voice.gender, c.voice.style, c.voice.pace].filter(Boolean).join(", ");

  return (
    <section className="card">
      <div className={s.head}>
        <span className={cx(s.avatar, s.avatarLg)}>{initial(c.channel.title)}</span>
        <div className={s.name}>
          <b>{c.channel.title}</b>
          <span className="sub tnum">{num(c.channel.subscribers)} obunachi · {c.channel.videoCount} video</span>
        </div>
        <button type="button" className={cx("round", s.open)} aria-label="Kanalni YouTube'da ochish" onClick={() => { haptic.tap(); openExternal(c.channel.url); }}>
          <ExternalLink size={18} />
        </button>
      </div>

      <div className={s.analysis}>
        {failed ? <Status tone="bad">Tahlil xatosi</Status> : <Status tone="ok">O&apos;rganilgan {ago(st.analyzedAt)}</Status>}
        <Btn tone="secondary" size="sm" icon={RotateCcw} loading={analyzing} onClick={onReanalyze}>Qayta tahlil</Btn>
      </div>
      {st.error ? <Alert><b>Oxirgi tahlil xatosi</b><br />{st.error}</Alert> : null}
      {c.basis === "description" ? (
        <p className="sub">Kanalda hali video yo&apos;q — nomi va tavsifidan o&apos;rganildi. Birinchi video chiqqach to&apos;liq tahlil qilinadi.</p>
      ) : null}

      {c.videos?.length ? (
        <div className={s.videos}>
          {c.videos.map((v) => (
            <VideoTile key={v.videoId} v={v} selected={open === v.videoId} toggle={() => { haptic.tap(); setOpen(open === v.videoId ? null : v.videoId); }} />
          ))}
        </div>
      ) : null}
      {sel ? <VideoDetail v={sel} close={() => setOpen(null)} /> : null}

      {c.summary ? <p className={s.summaryText}>{c.summary}</p> : null}

      <dl className={s.facts}>
        {c.niche ? <div className={s.fact}><dt>Yo&apos;nalish</dt><dd>{c.niche}</dd></div> : null}
        {c.audience ? <div className={s.fact}><dt>Auditoriya</dt><dd>{c.audience}</dd></div> : null}
        {c.tone ? <div className={s.fact}><dt>Ohang</dt><dd>{c.tone}</dd></div> : null}
        <div className={s.fact}>
          <dt>Ovoz</dt>
          <dd>
            {c.voice.recommendedProfile ? <b style={{ fontWeight: 650 }}>«{c.voice.recommendedProfile}»</b> : <span>Mos profil topilmadi — standart ishlatiladi</span>}
            {voiceDesc ? <small>{voiceDesc}</small> : null}
          </dd>
        </div>
      </dl>
    </section>
  );
}

function VideoTile({ v, selected, toggle }: { v: VideoInsight; selected: boolean; toggle: () => void }) {
  const seen = v.source === "video";
  return (
    <div className={cx(s.video, selected && s.selected)}>
      <button type="button" className={cx(s.thumb, v.isShort && s.short)} aria-label={`YouTube'da ochish: ${v.title}`}
        onClick={() => { haptic.tap(); openExternal(`https://youtu.be/${v.videoId}`); }}>
        {v.isShort ? <span className={s.shortShape}><Play size={16} /></span> : <Play size={20} />}
        <span className={s.dur}>{mmss(v.durationSec)}</span>
      </button>
      <button type="button" className={s.vtext} aria-expanded={selected} onClick={toggle}>
        <span className={s.vtitle}>{v.title}</span>
        <span className={cx(s.vmeta, "tnum")}>{num(v.views)} ko&apos;rish</span>
        <span className={cx(s.source, seen && s.seen)}>{seen ? <Eye size={13} /> : <FileText size={13} />}{seen ? "video ko'rildi" : "faqat sarlavha"}</span>
      </button>
    </div>
  );
}

function VideoDetail({ v, close }: { v: VideoInsight; close: () => void }) {
  const rows: [string, string][] = [
    ["Ssenariy", v.script.structure],
    ["Ohang va sur'at", [v.script.tone, v.script.pacing].filter(Boolean).join(" · ")],
    ["Chaqiriq (CTA)", v.script.cta],
    ["Ovoz", [GENDER[v.voice.gender] ?? v.voice.gender, v.voice.style, v.voice.pace, v.voice.energy].filter(Boolean).join(" · ")],
    ["Montaj", v.montage.visualStyle],
    ["Montaj sur'ati", v.montage.editingPace],
    ["Ekrandagi matn", v.montage.onScreenText],
    ["O'tishlar", v.montage.transitions],
    ["Musiqa", v.montage.music],
    ["Subtitr", v.subtitles.present ? v.subtitles.style || "bor" : "yo'q"],
  ];
  return (
    <div className={s.detail} role="region" aria-label={`Video tahlili: ${v.title}`}>
      <div className={s.detailHead}>
        <div style={{ display: "grid", gap: 2, minWidth: 0 }}>
          <b>{v.title}</b>
          <span className="sub tnum">
            {num(v.views)} ko&apos;rish · {num(v.likes)} layk · {mmss(v.durationSec)}{v.isShort ? " · Shorts" : ""} · {v.source === "video" ? "video ko'rib chiqildi" : "faqat sarlavha/tavsif"}
          </span>
        </div>
        <button type="button" className={cx("round", s.closeBtn)} aria-label="Yig'ish" onClick={close}><ChevronUp size={18} /></button>
      </div>
      {v.summary ? <div>{v.summary}</div> : null}
      {v.script.hook ? <HookLine>{v.script.hook}</HookLine> : null}
      <dl>
        {rows.filter(([, val]) => !!val).map(([k, val]) => <div key={k}><dt>{k}</dt><dd>{val}</dd></div>)}
      </dl>
      {v.script.transcriptExcerpt ? <div style={{ display: "grid", gap: 6 }}><span className="label">Matn boshlanishi</span><div className="pre">{v.script.transcriptExcerpt}</div></div> : null}
      <button type="button" className="linkbtn" onClick={() => openExternal(`https://youtu.be/${v.videoId}`)}><ExternalLink size={14} />YouTube&apos;da ochish</button>
    </div>
  );
}

// ------------------------------------------------------------------ promptlarga qo'shiladigan uslub
function StyleCard({ st, c, toggling, onToggle }: { st: ChannelState; c: ChannelContext; toggling: boolean; onToggle: (v: boolean) => void }) {
  const [full, setFull] = useState(false);
  const [blocks, setBlocks] = useState(false);
  const n = (a?: string[]) => a?.length ?? 0;
  const script = c.scriptGuidelines ?? [];
  const montage = c.montageGuidelines ?? [];
  const more = n(script) > 2 || n(montage) > 2 || n(c.subtitleGuidelines) > 0 || n(c.titlePatterns) > 0 || n(c.doList) > 0 || n(c.avoidList) > 0 || !!st.blocks;

  return (
    <section className="card">
      <ToggleRow
        title="Kanal uslubi promptlarga qo'shiladi"
        sub={toggling ? "Saqlanmoqda…" : "Gemini ssenariysi va Claude montajiga"}
        on={st.enabled} disabled={toggling} onChange={onToggle} />
      <Snippet label="Ssenariy · Gemini" items={full ? script : script.slice(0, 2)} />
      <Snippet label="Montaj · Claude" items={full ? montage : montage.slice(0, 2)} />
      {full ? (
        <>
          <Snippet label="Subtitr" items={c.subtitleGuidelines} />
          <Snippet label="Sarlavha naqshlari" items={c.titlePatterns} />
          <Snippet label="Davom ettirish" items={c.doList} />
          <Snippet label="Qochish kerak" items={c.avoidList} />
          {st.blocks ? (
            <>
              <Btn tone="secondary" size="sm" className={s.more} icon={blocks ? ChevronUp : ChevronDown} aria-expanded={blocks} onClick={() => setBlocks((b) => !b)}>
                {blocks ? "Blokni yashirish" : "Promptlarga qo'shiladigan blok"}
              </Btn>
              {blocks ? (
                <div style={{ display: "grid", gap: 8 }}>
                  <span className="label">Ssenariy (Gemini) — {"{channel_context}"} o&apos;rniga</span>
                  <div className="pre">{st.blocks.script}</div>
                  <span className="label">Montaj (Claude) — «Channel style» bo&apos;limi</span>
                  <div className="pre">{st.blocks.montage}</div>
                </div>
              ) : null}
            </>
          ) : null}
        </>
      ) : null}
      {more ? (
        <Btn tone="ghost" size="sm" className={s.more} icon={full ? ChevronUp : ChevronDown} aria-expanded={full} onClick={() => { setFull((f) => !f); if (full) setBlocks(false); }}>
          {full ? "Yig'ish" : "Subtitr, sarlavha, qochish kerak…"}
        </Btn>
      ) : null}
    </section>
  );
}

function Snippet({ label, items }: { label: string; items: string[] }) {
  if (!items?.length) return null;
  return (
    <div className={s.snippet}>
      <span className="label">{label}</span>
      <ul>{items.map((x, i) => <li key={i}>{x}</li>)}</ul>
    </div>
  );
}

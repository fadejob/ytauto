"use client";

/**
 * «Videolar» ekrani (dizayn: ytauto.dsai → Screens → Videolar). Fokus - navbatdagi videolar soni va keyingi chiqish; e'tibor
 * kerak bo'lgan, tayyor yoki jarayondagi videolar to'liq karta (bosqichlar chizig'i, xato, bitta asosiy amal), oddiy navbat va
 * chiqqanlar - ixcham qatorlar. Karta yoki qator bosilsa video tafsiloti ochiladi (u yerda barcha amallar ham bor).
 */
import { useEffect, useMemo, useState } from "react";
import {
  CalendarClock, ChevronRight, ChevronsUpDown, CircleX, CloudUpload, Ellipsis, ExternalLink, ListVideo, Pencil, Plus, RotateCcw, RotateCw,
  Trash2, Undo2, Upload, Zap, CircleCheck, LoaderCircle, Sparkles,
} from "lucide-react";
import { confirmBox, haptic, openExternal } from "@/lib/client";
import { StatusText, type Action, type State, type Topic } from "@/lib/types";
import type { Run } from "./App";
import type { SettingsPage } from "./settings/types";
import { Alert, Btn, Empty, HookLine, Segmented, Sheet, Skel, SkelCard, StageStrip, Status, useBusy, type IconT, type StageState } from "./ui";
import TopicSheet, { RescheduleSheet, MONTHS, fmtWhen, formatShort, isLate, todayIn } from "./TopicSheet";
import s from "./Topics.module.css";

type Group = "queue" | "work" | "done";
const groupOf = (t: Topic): Group => (t.status === 11 ? "done" : [0, 12, 13, 14].includes(t.status) && !t.claimed ? "queue" : "work");

// ------------------------------------------------------------------ bosqichlar chizig'i (holatdan)
const STAGES = ["Ssenariy", "Ovoz", "Montaj", "Render", "YouTube"] as const;
/** Ish holati → hozirgi bosqich: 1 ssenariy; 2-3 ovoz va subtitr vaqtlari; 4 montaj; 5-6 render va sifat; 7-10 arxiv, yuklash, YouTube. */
const STAGE_OF: Record<number, number> = { 1: 0, 2: 1, 3: 1, 4: 2, 5: 3, 6: 3, 7: 4, 8: 4, 9: 4, 10: 4 };

export function stagesOf(t: Topic): { name: string; state: StageState }[] {
  const make = (fn: (i: number) => StageState) => STAGES.map((name, i) => ({ name, state: fn(i) }));
  const st = t.status;
  if (st === 11) return make(() => "done");
  // Video tayyor (arxivda yoki YouTube'da): Render'gacha hammasi bajarilgan.
  const made = !!t.youtubeUrl || t.canUploadReady || t.canForceUpload || t.uploadRequested;
  if (st === 15) return make((i) => (i < 4 ? "done" : "todo"));
  if (st in STAGE_OF) { const at = STAGE_OF[st]; return make((i) => (i < at ? "done" : i === at ? "now" : "todo")); }
  if (st === 12 || st === 13) {
    // Qaysi bosqichda to'xtagani mavzuda yo'q: video tayyor bo'lsa - YouTube, aks holda birinchi bajarilmagan bosqich.
    const at = made ? 4 : 0;
    return make((i) => (i < at ? "done" : i === at ? "fail" : "todo"));
  }
  if (st === 14) return make((i) => (made && i < 4 ? "done" : "todo"));
  // Navbatda: worker endi olgan bo'lsa ssenariy boshlanmoqda.
  return make((i) => (t.claimed && i === 0 ? "now" : "todo"));
}

function statusTone(t: Topic): "neutral" | "accent" | "ok" | "bad" {
  if (t.status === 11) return "ok";
  if (t.status === 12 || t.status === 13) return "bad";
  if (t.status === 14 || (t.status === 0 && !t.claimed)) return "neutral";
  return "accent";
}

const sourceText = (t: Topic) => (t.hasAudio ? "tayyor audio" : t.manualScript ? "o'z matni" : null);

function musicText(t: Topic, state: State | null) {
  if (t.music === "none") return "musiqasiz";
  if (t.music) return state?.catalog.music?.tracks.find((m) => m.id === t.music)?.title ?? "tanlangan musiqa";
  return null;
}

/** «bugun, 18:00» / «ertaga, 18:00» / «24 noy, 18:00» - kanal vaqt zonasi bo'yicha. */
function relWhen(d: { date: string; time: string }, tz: string) {
  const today = todayIn(tz);
  const [y, m, dd] = today.split("-").map(Number);
  const tomorrow = new Date(Date.UTC(y, m - 1, dd + 1)).toISOString().slice(0, 10);
  if (d.date === today) return `bugun, ${d.time}`;
  if (d.date === tomorrow) return `ertaga, ${d.time}`;
  return fmtWhen(d);
}

type Kind = "publish" | "unpublish" | "upload" | "unupload" | "delete" | "retry" | "retry_settings" | "cancel" | "force_upload";
type Item = { key: string; label: string; icon: IconT; danger?: boolean; kind?: Kind; onClick?: () => void };

type Props = {
  state: State | null; loading: boolean; refresh: () => Promise<void>; run: Run; locked?: boolean;
  openVideo?: (id: number) => void; openSettings?: (p: SettingsPage | null) => void;
  /** «AI yordamchi» tabiga o'tish (bo'sh navbat kartasidagi tugma). */
  openAi?: () => void;
};

export default function Topics({ state, loading, refresh, run, locked, openVideo, openSettings, openAi }: Props) {
  const [group, setGroup] = useState<Group>("queue");
  const [sheet, setSheet] = useState<{ mode: "new" } | { mode: "edit"; topic: Topic } | null>(null);
  const [resched, setResched] = useState<Topic | null>(null);
  const [more, setMore] = useState<number | null>(null);
  const busy = useBusy();

  const topics = useMemo(() => state?.topics ?? [], [state]);
  const counts = useMemo(() => {
    const c = { queue: 0, work: 0, done: 0 };
    for (const t of topics) c[groupOf(t)]++;
    return c;
  }, [topics]);
  const tz = state?.catalog.youtube.timeZone ?? "Asia/Tashkent";


  // Shablon serverdan kompyuterga tushayotgan bo'lsa, ro'yxat o'zi yangilanadi: o'rnatilishi bilan tanlash mumkin bo'ladi.
  const waitingTemplates = (state?.catalog.pendingTemplates?.length ?? 0) > 0;
  useEffect(() => {
    if (!waitingTemplates) return;
    const t = window.setInterval(() => document.visibilityState === "visible" && void refresh(), 8000);
    return () => window.clearInterval(t);
  }, [waitingTemplates, refresh]);

  const items = topics.filter((t) => groupOf(t) === group);
  if (group === "done") items.sort((a, b) => b.publishAt.localeCompare(a.publishAt));

  // Fokus: navbatdagi videolar, keyingi chiqish va e'tibor kerak bo'lganlar.
  const serverNow = state?.now;
  const summary = useMemo(() => {
    // Server vaqti (/api/state): render toza qolishi uchun Date.now() ishlatilmaydi.
    const now = serverNow ? new Date(serverNow).getTime() : 0;
    const next = topics
      .filter((t) => t.status !== 11 && t.status !== 14 && new Date(t.publishAt).getTime() >= now)
      .sort((a, b) => a.publishAt.localeCompare(b.publishAt))[0];
    const attention = topics.filter((t) => t.status === 12 || t.status === 13).length;
    return { next, attention };
  }, [topics, serverNow]);

  async function act(t: Topic, kind: Kind): Promise<boolean> {
    if (kind === "retry_settings" && !(await confirmBox("Vazifa joriy sozlamalar va promptlar bilan qayta snapshot qilinadi, o'zgargan bosqichlar qayta bajariladi. Davom etilsinmi?"))) return false;
    if (kind === "cancel" && !(await confirmBox(`"${t.topic}" bekor qilinsinmi? YouTube'ga yuklangan video o'chirilmaydi.`))) return false;
    if (kind === "force_upload" && !(await confirmBox("YouTube Studio'da bu video YO'QLIGINI tekshirdingizmi? Aks holda ikki marta yuklanadi.\n\nYangi upload boshlansinmi?"))) return false;
    if (kind === "publish" && !(await confirmBox(t.youtubeUrl
      ? `"${t.topic}" YouTube'da hozir public qilinsinmi?`
      : `"${t.topic}" hozir tayyorlanib, YouTube'ga darhol chiqarilsinmi?`))) return false;
    if (kind === "upload" && !(await confirmBox(
      `"${t.title || t.topic}" tayyor videosi YouTube'ga yuklansinmi?\n\nVideo qayta tayyorlanmaydi. Chiqish vaqti: ${isLate(t.publishAt) ? "o'tib ketgan — yuklangandan 1 soat keyin" : fmtWhen(t.publishLocal)}.`))) return false;
    if (kind === "delete" && !(await confirmBox(`"${t.topic}" o'chirilsinmi? Qaytarib bo'lmaydi.`))) return false;
    const action: Action =
      kind === "publish" ? { type: "publish_now", id: t.id }
      : kind === "unpublish" ? { type: "cancel_publish_now", id: t.id }
      : kind === "upload" ? { type: "upload_ready", id: t.id }
      : kind === "unupload" ? { type: "cancel_upload_ready", id: t.id }
      : kind === "retry" ? { type: "retry", id: t.id }
      : kind === "retry_settings" ? { type: "retry_settings", id: t.id }
      : kind === "cancel" ? { type: "cancel", id: t.id }
      : kind === "force_upload" ? { type: "force_new_upload", id: t.id }
      : { type: "delete_topic", id: t.id };
    // Natija xabari (toast) App'da; xato bo'lsa tugmada «✓» chiqmasin.
    const ok = await busy.run(`${kind}:${t.id}`, async () => {
      const [r] = await run([action]);
      if (!r?.ok) throw new Error(r?.message ?? "Xato");
      return true;
    });
    return ok === true;
  }

  /** Kartaning asosiy amali: xatoda - qayta urinish, tayyorda - yuklash, navbatda - hozir chiqarish. */
  function primaryOf(t: Topic): { kind: Kind; label: string; icon: IconT; tone?: "primary" | "secondary" } | null {
    if (t.canRetry) return { kind: "retry", label: "Qayta urinish", icon: RotateCcw };
    if (t.canUploadReady) return { kind: "upload", label: "YouTube'ga yuklash", icon: Upload };
    if (t.canPublishNow) return { kind: "publish", label: "Hozir chiqarish", icon: Zap };
    if (t.uploadRequested) return { kind: "unupload", label: "Yuklashni bekor qilish", icon: Undo2, tone: "secondary" };
    if (t.publishNow) return { kind: "unpublish", label: "Bekor qilish", icon: Undo2, tone: "secondary" };
    return null;
  }

  /** Qolgan amallar («⋯» oynasida). */
  function secondaryOf(t: Topic): Item[] {
    const p = primaryOf(t)?.kind;
    const list: Item[] = [];
    // Asosiy amal takrorlanmaydi. kind'siz bandlar (tahrirlash, vaqt, YouTube) doim qoladi - asosiy amal yo'q bo'lsa ham.
    const add = (cond: boolean, it: Item) => { if (cond && (!it.kind || it.kind !== p)) list.push(it); };
    add(t.canPublishNow, { key: "publish", kind: "publish", label: "Hozir chiqarish", icon: Zap });
    add(t.canUploadReady, { key: "upload", kind: "upload", label: "YouTube'ga yuklash", icon: Upload });
    add(t.uploadRequested, { key: "unupload", kind: "unupload", label: "Yuklashni bekor qilish", icon: Undo2 });
    add(t.publishNow, { key: "unpublish", kind: "unpublish", label: "Hozir chiqarishni bekor qilish", icon: Undo2 });
    add(t.editable, { key: "edit", label: "Tahrirlash", icon: Pencil, onClick: () => setSheet({ mode: "edit", topic: t }) });
    add(t.canRetry, { key: "retry_settings", kind: "retry_settings", label: "Joriy sozlamalar bilan qayta", icon: RotateCw });
    add(t.canForceUpload, { key: "force_upload", kind: "force_upload", label: "Yangi upload", icon: CloudUpload });
    add(t.canReschedule, { key: "resched", label: "Vaqtni o'zgartirish", icon: CalendarClock, onClick: () => setResched(t) });
    add(!!t.youtubeUrl, { key: "yt", label: "YouTube'da ochish", icon: ExternalLink, onClick: () => t.youtubeUrl && openExternal(t.youtubeUrl) });
    add(t.canCancel && !t.deletable, { key: "cancel", kind: "cancel", label: "Videoni bekor qilish", icon: CircleX, danger: true });
    add(t.deletable, { key: "delete", kind: "delete", label: "O'chirish", icon: Trash2, danger: true });
    return list;
  }

  // Bir video «to'liq karta» bo'ladimi: qilinadigan ish yoki jarayon bor. Oddiy navbat va chiqqanlar - ixcham qator.
  const featured = (t: Topic) =>
    groupOf(t) === "work" || !!t.error || t.canRetry || t.canUploadReady || t.uploadRequested || t.publishNow || t.canForceUpload || t.status === 15;
  const cards = items.filter(featured);
  const rows = items.filter((t) => !featured(t));

  const open = (t: Topic) => { haptic.tap(); openVideo?.(t.id); };
  const moreTopic = more !== null ? topics.find((t) => t.id === more) ?? null : null;
  const channel = state?.channel;

  return (
    <div className="page">
      <div className={s.top}>
        {state ? (
          <button type="button" className={s.chip} onClick={() => openSettings?.("channels")}
            aria-label={channel?.title ? `Kanal: ${channel.title}. Almashtirish` : "Kanal tanlash"}>
            <span className={s.avatar} aria-hidden>{(channel?.title || "?").trim().charAt(0) || "?"}</span>
            <span className={s.name}>{channel?.title || "Kanal tanlanmagan"}</span>
            {channel && channel.total > 1 ? <span className={s.total}>{channel.total} ta</span> : null}
            <ChevronsUpDown size={16} />
          </button>
        ) : <Skel h={44} w={200} r={999} />}
        <button type="button" className={`round ${s.refresh}`} aria-label="Yangilash" disabled={loading} onClick={() => { haptic.tap(); void refresh(); }}>
          <RotateCw size={18} className={loading && state ? "spin" : undefined} />
        </button>
      </div>

      <div className={s.summary}>
        {state ? (
          <>
            <h1 className="focal">
              {counts.queue > 0 ? <>{counts.queue} <span className="dim">video navbatda</span></> : <>Navbat <span className="dim">bo&apos;sh</span></>}
            </h1>
            <div className="sub">
              {[
                summary.next ? `Keyingisi ${relWhen(summary.next.publishLocal, tz)}` : counts.work > 0 ? `${counts.work} ta video tayyorlanmoqda` : null,
                summary.attention > 0 ? `${summary.attention} tasiga e'tibor kerak` : null,
              ].filter(Boolean).join(" · ") || "Yangi video qo'shing — qolganini kompyuter bajaradi."}
            </div>
            <div className={s.tz}>{tz} vaqti · videolar ingliz tilida</div>
          </>
        ) : (
          <><Skel h={42} w="70%" /><Skel h={14} w="55%" /></>
        )}
      </div>

      <Segmented<Group> label="Videolar guruhi" value={group}
        onChange={(g) => { haptic.tap(); setGroup(g); }}
        items={[
          { key: "queue", label: "Navbatda", count: state ? counts.queue : undefined },
          { key: "work", label: "Jarayonda", count: state ? counts.work : undefined },
          { key: "done", label: "Chiqqan", count: state ? counts.done : undefined },
        ]} />

      {!state && loading ? (
        <div className={`cards ${s.list}`}><SkelCard lines={4} /><SkelCard lines={2} /><SkelCard lines={2} /></div>
      ) : null}

      {state && items.length === 0 ? (
        group === "queue" ? (
          <section className={s.emptyCard} aria-label="Navbat bo'sh">
            <div className="strip" aria-hidden>
              <div className="bars">{STAGES.map((n) => <i key={n} />)}</div>
              <div className="names">{STAGES.map((n) => <span key={n}>{n}</span>)}</div>
            </div>
            <div className={s.emptyText}>
              <b>Navbat bo&apos;sh</b>
              <span>Mavzu va boshlang&apos;ich savol qo&apos;shing — tunda Gemini ssenariy yozadi, ovoz o&apos;qiydi, montaj qilinib YouTube&apos;ga chiqadi.</span>
            </div>
            <div className={s.emptyActs}>
              <Btn block icon={Plus} disabled={locked} title={locked ? "Avval Gemini kalitini qo'shing" : undefined}
                onClick={() => { haptic.tap(); setSheet({ mode: "new" }); }}>Yangi video</Btn>
              <Btn block icon={Sparkles} disabled={locked} title={locked ? "Avval Gemini kalitini qo'shing" : undefined}
                onClick={() => { haptic.tap(); openAi?.(); }}>AI yordamchi</Btn>
            </div>
          </section>
        ) : group === "work" ? (
          <Empty icon={ListVideo} title="Hozir hech narsa tayyorlanmayapti">Navbatdagi video vaqti kelganda kompyuter uni shu yerda tayyorlaydi.</Empty>
        ) : (
          <Empty icon={CircleCheck} title="Hali chiqqan video yo'q">YouTube&apos;ga chiqqan videolar shu yerda ko&apos;rinadi.</Empty>
        )
      ) : null}

      {cards.length > 0 ? (
        <div className={`cards ${s.list}`}>
          {cards.map((t) => {
            const p = primaryOf(t);
            const extra = secondaryOf(t);
            const meta = [formatShort(t.format), sourceText(t), musicText(t, state), relWhen(t.publishLocal, tz)].filter(Boolean).join(" · ");
            return (
              <article key={t.id} className={`card ${s.card}`} onClick={() => open(t)}>
                <div className={s.head}>
                  <button type="button" className={s.title} onClick={(e) => { e.stopPropagation(); open(t); }}>{t.title || t.topic}</button>
                  <Status tone={statusTone(t)}>{StatusText[t.status]}</Status>
                </div>
                {t.hook ? <HookLine>{t.hook}</HookLine> : null}
                <StageStrip stages={stagesOf(t)} />
                {t.error ? <Alert>{t.error}</Alert> : null}
                {t.publishNow ? <Alert tone="accent" icon={Zap}>Hozir chiqarish kutilmoqda — kompyuter navbatdan oldin tayyorlaydi.</Alert> : null}
                {t.uploadRequested ? <Alert tone="accent" icon={Upload}>YouTube&apos;ga yuklash kutilmoqda.</Alert> : null}
                <div className={s.foot} onClick={(e) => e.stopPropagation()}>
                  <span className={`${s.meta} tnum`}>{meta}</span>
                  <div className={s.acts}>
                    {extra.length > 0 ? (
                      <button type="button" className={`round ${s.moreBtn}`} aria-label="Boshqa amallar" onClick={() => { haptic.tap(); setMore(t.id); }}>
                        <Ellipsis size={18} />
                      </button>
                    ) : null}
                    {p ? (
                      <Btn size="sm" tone={p.tone ?? "primary"} icon={p.icon} loading={busy.is(`${p.kind}:${t.id}`)} done={busy.done(`${p.kind}:${t.id}`)}
                        onClick={() => void act(t, p.kind)}>{p.label}</Btn>
                    ) : null}
                  </div>
                </div>
              </article>
            );
          })}
        </div>
      ) : null}

      {rows.length > 0 ? (
        <div className={`group ${s.rows}`}>
          {rows.map((t) => {
            const meta = (group === "done"
              ? ["Chiqdi", t.publishLocal.time, formatShort(t.format)]
              : [t.publishLocal.time, formatShort(t.format), sourceText(t), musicText(t, state)]).filter(Boolean).join(" · ");
            return (
              <div key={t.id} className={s.row}>
                <button type="button" className={s.rowMain} onClick={() => open(t)}>
                  <span className={s.date}>
                    <b className="tnum">{Number(t.publishLocal.date.slice(8, 10))}</b>
                    <span>{MONTHS[Number(t.publishLocal.date.slice(5, 7)) - 1]}</span>
                  </span>
                  <span className={s.texts}>
                    <span className={s.rowTitle}>{t.title || t.topic}</span>
                    {t.hook ? <HookLine>{t.hook}</HookLine> : null}
                    <span className={`${s.meta} tnum`}>{meta}</span>
                    {t.status === 14 || t.claimed ? <span><Status tone={statusTone(t)}>{StatusText[t.status]}</Status></span> : null}
                  </span>
                  <ChevronRight className={s.chev} size={18} />
                </button>
                {t.youtubeUrl ? (
                  <button type="button" className={`round ${s.yt}`} aria-label="YouTube'da ochish" onClick={() => { haptic.tap(); openExternal(t.youtubeUrl!); }}>
                    <ExternalLink size={17} />
                  </button>
                ) : null}
              </div>
            );
          })}
        </div>
      ) : null}

      {group !== "done" && !(state && group === "queue" && items.length === 0) ? (
        <>
          {/* Oxirgi karta/qator «Yangi video» tugmasi ostida qolib ketmasin. */}
          <div className={s.fabSpace} aria-hidden />
          <div className="fade-bottom" aria-hidden />
          <Btn className="fab" icon={Plus} disabled={locked} title={locked ? "Avval Gemini kalitini qo'shing" : undefined}
            onClick={() => { haptic.tap(); setSheet({ mode: "new" }); }}>Yangi video</Btn>
        </>
      ) : null}

      {moreTopic ? (
        <Sheet title={moreTopic.title || moreTopic.topic} sub={`#${moreTopic.id} · ${StatusText[moreTopic.status]}`} onClose={() => setMore(null)} label="Video amallari">
          <div className={`group ${s.menu}`}>
            {secondaryOf(moreTopic).map((it) => {
              const key = it.kind ? `${it.kind}:${moreTopic.id}` : "";
              const spinning = !!it.kind && busy.is(key);
              return (
                <button key={it.key} type="button" className={`${s.menuItem}${it.danger ? " " + s.danger : ""}`} disabled={spinning}
                  onClick={async () => {
                    haptic.tap();
                    if (it.onClick) { setMore(null); it.onClick(); return; }
                    if (it.kind && (await act(moreTopic, it.kind))) setMore(null);
                  }}>
                  <span className={s.menuIc}>{spinning ? <LoaderCircle size={18} className="spin" /> : <it.icon size={18} />}</span>
                  <span>{it.label}</span>
                </button>
              );
            })}
          </div>
          <Btn tone="secondary" block onClick={() => { setMore(null); openVideo?.(moreTopic.id); }}>Tafsilotni ochish</Btn>
        </Sheet>
      ) : null}

      {sheet && state ? <TopicSheet sheet={sheet} catalog={state.catalog} channelTitle={(state.channel?.total ?? 0) > 1 ? state.channel.title : ""} close={() => setSheet(null)} run={run} /> : null}
      {resched ? <RescheduleSheet topic={resched} close={() => setResched(null)} run={run} /> : null}
    </div>
  );
}

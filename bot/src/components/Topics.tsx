"use client";

import { useEffect, useMemo, useState } from "react";
import { api, confirmBox, haptic, putFile } from "@/lib/client";
import { FormatText, StatusText, type Action, type Catalog, type PreparedAudio, type ScriptSource, type State, type Topic } from "@/lib/types";
import type { Run } from "./App";
import { IconBolt, IconEdit, IconPlay, IconPlus, IconRefresh, IconTrash, IconUpload } from "./icons";

type Group = "queue" | "work" | "done";
const groupOf = (t: Topic): Group => (t.status === 11 ? "done" : [0, 12, 13, 14].includes(t.status) && !t.claimed ? "queue" : "work");

function chipClass(s: number) {
  if (s === 11) return "chip ok";
  if (s === 13 || s === 12) return "chip bad";
  if (s === 14) return "chip mute";
  if (s === 10 || s === 9) return "chip warn";
  return "chip";
}

const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
/** Desktop ilovadagi qoida: vaqt o'tib ketgan yoki 30 daqiqadan yaqin bo'lsa, video yuklangandan 1 soat keyin chiqadi. */
const isLate = (iso: string) => new Date(iso).getTime() <= Date.now() + 30 * 60000;
const fmt = (d: { date: string; time: string }) => `${Number(d.date.slice(8, 10))} ${MONTHS[Number(d.date.slice(5, 7)) - 1]}, ${d.time}`;

export default function Topics({ state, loading, refresh, run, locked }: { state: State | null; loading: boolean; refresh: () => Promise<void>; run: Run; locked?: boolean }) {
  const [group, setGroup] = useState<Group>("queue");
  const [sheet, setSheet] = useState<{ mode: "new" } | { mode: "edit"; topic: Topic } | null>(null);
  const [open, setOpen] = useState<number | null>(null);
  const [busy, setBusy] = useState<number | null>(null);
  const [resched, setResched] = useState<Topic | null>(null);

  const counts = useMemo(() => {
    const c = { queue: 0, work: 0, done: 0 };
    for (const t of state?.topics ?? []) c[groupOf(t)]++;
    return c;
  }, [state]);
  // Shablon serverdan kompyuterga tushayotgan bo'lsa, ro'yxat o'zi yangilanadi: o'rnatilishi bilan tanlash mumkin bo'ladi.
  const waitingTemplates = (state?.catalog.pendingTemplates?.length ?? 0) > 0;
  useEffect(() => {
    if (!waitingTemplates) return;
    const t = window.setInterval(() => document.visibilityState === "visible" && void refresh(), 8000);
    return () => window.clearInterval(t);
  }, [waitingTemplates, refresh]);
  const items = (state?.topics ?? []).filter((t) => groupOf(t) === group);
  if (group === "done") items.sort((a, b) => b.publishAt.localeCompare(a.publishAt));

  async function act(t: Topic, kind: "publish" | "unpublish" | "upload" | "unupload" | "delete" | "retry" | "retry_settings" | "cancel" | "force_upload") {
    if (kind === "retry_settings" && !(await confirmBox("Vazifa joriy sozlamalar va promptlar bilan qayta snapshot qilinadi, o'zgargan bosqichlar qayta bajariladi. Davom etilsinmi?"))) return;
    if (kind === "cancel" && !(await confirmBox(`"${t.topic}" bekor qilinsinmi? YouTube'ga yuklangan video o'chirilmaydi.`))) return;
    if (kind === "force_upload" && !(await confirmBox("YouTube Studio'da bu video YO'QLIGINI tekshirdingizmi? Aks holda ikki marta yuklanadi.\n\nYangi upload boshlansinmi?"))) return;
    if (kind === "publish" && !(await confirmBox(t.youtubeUrl
      ? `"${t.topic}" YouTube'da hozir public qilinsinmi?`
      : `"${t.topic}" hozir tayyorlanib, YouTube'ga darhol chiqarilsinmi?`))) return;
    if (kind === "upload" && !(await confirmBox(
      `"${t.title || t.topic}" tayyor videosi YouTube'ga yuklansinmi?\n\nVideo qayta tayyorlanmaydi. Chiqish vaqti: ${isLate(t.publishAt) ? "o'tib ketgan — yuklangandan 1 soat keyin" : fmt(t.publishLocal)}.`))) return;
    if (kind === "delete" && !(await confirmBox(`"${t.topic}" o'chirilsinmi? Qaytarib bo'lmaydi.`))) return;
    setBusy(t.id);
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
    await run([action]);
    setBusy(null);
  }

  return (
    <>
      <header className="head">
        <div>
          <h1>Mavzular</h1>
          <div className="sub">{state ? `${state.catalog.youtube.timeZone} · videolar ingliz tilida` : "Yuklanmoqda…"}</div>
        </div>
        <button className="iconbtn" aria-label="Yangilash" onClick={() => { haptic.tap(); void refresh(); }} disabled={loading}><IconRefresh /></button>
      </header>

      <div className="seg" role="tablist">
        {([["queue", "Navbatda"], ["work", "Jarayonda"], ["done", "Chiqqan"]] as const).map(([g, label]) => (
          <button key={g} className={group === g ? "on" : ""} onClick={() => { haptic.tap(); setGroup(g); }}>
            {label}<span className="n">{counts[g]}</span>
          </button>
        ))}
      </div>

      <div className="list cards" style={{ paddingBottom: group !== "done" ? 64 : 0 }}>
        {!state && loading && [0, 1, 2].map((i) => <div key={i} className="skeleton" />)}
        {state && items.length === 0 && <div className="empty">{group === "queue" ? "Navbat bo'sh. Pastdagi “Mavzu” tugmasi yoki AI yordamchi orqali qo'shing." : "Bu yerda hozircha hech narsa yo'q."}</div>}
        {items.map((t) => (
          <article key={t.id} className="card topic">
            <div className="row">
              <div className="title" onClick={() => setOpen(open === t.id ? null : t.id)}>{t.title || t.topic}</div>
              <span className="id">#{t.id}</span>
            </div>
            {t.notes ? <div className={`notes${open === t.id ? " open" : ""}`} onClick={() => setOpen(open === t.id ? null : t.id)}>{t.notes}</div> : null}
            <div className="meta">
              <span className={chipClass(t.status)}>{t.claimed && t.status < 9 ? "⏳ " : ""}{StatusText[t.status]}</span>
              {t.publishNow ? <span className="chip warn">⚡ hozir chiqarish kutilmoqda</span> : null}
              {t.uploadRequested ? <span className="chip warn">⬆ YouTube'ga yuklash kutilmoqda</span> : null}
              {t.hasAudio ? <span className="chip mute">🎙 tayyor audio</span> : t.manualScript ? <span className="chip mute">✍ o&apos;z matni</span> : null}
              <span>📅 {fmt(t.publishLocal)}</span>
            </div>
            {t.error ? <div className="err">{t.error}</div> : null}
            <div className="actions">
              {t.canUploadReady ? <button className="btn primary" disabled={busy === t.id} onClick={() => act(t, "upload")}><IconUpload />YouTube'ga yuklash</button> : null}
              {t.uploadRequested ? <button className="btn ghost" disabled={busy === t.id} onClick={() => act(t, "unupload")}>Yuklashni bekor qilish</button> : null}
              {t.canPublishNow ? <button className="btn primary" disabled={busy === t.id} onClick={() => act(t, "publish")}><IconBolt />Hozir chiqarish</button> : null}
              {t.publishNow ? <button className="btn ghost" disabled={busy === t.id} onClick={() => act(t, "unpublish")}>Bekor qilish</button> : null}
              {t.editable ? <button className="btn" onClick={() => { haptic.tap(); setSheet({ mode: "edit", topic: t }); }}><IconEdit />Tahrirlash</button> : null}
              {t.canRetry ? <button className="btn primary" disabled={busy === t.id} onClick={() => act(t, "retry")}><IconRefresh />Qayta urinish</button> : null}
              {t.canRetry ? <button className="btn" disabled={busy === t.id} onClick={() => act(t, "retry_settings")}>Joriy sozlamalar bilan qayta</button> : null}
              {t.canForceUpload ? <button className="btn" disabled={busy === t.id} onClick={() => act(t, "force_upload")}><IconUpload />Yangi upload</button> : null}
              {t.canReschedule ? <button className="btn" disabled={busy === t.id} onClick={() => { haptic.tap(); setResched(t); }}>Vaqtni o&apos;zgartirish</button> : null}
              {t.canCancel && !t.deletable ? <button className="btn danger" disabled={busy === t.id} onClick={() => act(t, "cancel")}>Bekor qilish</button> : null}
              {t.deletable ? <button className="btn danger" disabled={busy === t.id} onClick={() => act(t, "delete")}><IconTrash /></button> : null}
              {t.youtubeUrl ? <a className="btn" href={t.youtubeUrl} target="_blank" rel="noreferrer"><IconPlay />YouTube</a> : null}
            </div>
          </article>
        ))}
      </div>

      {group !== "done" && (
        <button className="fab" disabled={locked} title={locked ? "Avval Gemini kalitini qo'shing" : undefined}
          onClick={() => { haptic.tap(); setSheet({ mode: "new" }); }}><IconPlus />Mavzu</button>
      )}
      {sheet && state && <TopicSheet sheet={sheet} catalog={state.catalog} close={() => setSheet(null)} run={run} />}
      {resched && <RescheduleSheet topic={resched} close={() => setResched(null)} run={run} />}
    </>
  );
}

/** Tayyor (yoki tayyorlanayotgan) vazifaning chiqish vaqtini o'zgartirish. */
function RescheduleSheet({ topic, close, run }: { topic: Topic; close: () => void; run: Run }) {
  const [date, setDate] = useState(topic.publishLocal.date);
  const [time, setTime] = useState(topic.publishLocal.time);
  const [saving, setSaving] = useState(false);
  async function save() {
    setSaving(true);
    const [r] = await run([{ type: "reschedule", id: topic.id, publishAt: `${date} ${time || "18:00"}` }]);
    setSaving(false);
    if (r?.ok) close();
  }
  return (
    <>
      <div className="backdrop" onClick={close} />
      <section className="sheet" role="dialog" aria-label="Chiqish vaqtini o'zgartirish">
        <div className="grab" />
        <h2>#{topic.id} — chiqish vaqti</h2>
        <div className="card formcard">
          <div className="grid2">
            <div className="field"><label>Sana</label><input className="input" type="date" value={date} onChange={(e) => setDate(e.target.value)} /></div>
            <div className="field"><label>Soat</label><input className="input" type="time" value={time} onChange={(e) => setTime(e.target.value)} /></div>
          </div>
          <div className="hint">Kamida 30 daqiqa kelajakda bo&apos;lsin. Video qayta yaratilmaydi.</div>
        </div>
        <button className="btn primary block" style={{ marginTop: 12 }} disabled={saving || !date} onClick={save}>{saving ? "Saqlanmoqda…" : "Saqlash"}</button>
      </section>
    </>
  );
}

type Row = { topic: string; notes: string; date: string; time: string; source: ScriptSource; title: string; script: string; file: File | null };

const emptyRow = (): Row => ({ topic: "", notes: "", date: "", time: "", source: "gemini", title: "", script: "", file: null });
const AUDIO_ACCEPT = ".mp3,.wav,.m4a,.aac,.ogg,.opus,.flac,.webm,audio/*";
const AUDIO_MAX_MB = 200;
const MB = 1048576;

const SOURCES: [ScriptSource, string][] = [["gemini", "Gemini yozadi"], ["manual", "O'z matnim"], ["audio", "Tayyor audio"]];

/** Qatorni saqlashdan oldin tekshiradi: qo'lda rejimda subtitr va title, audio rejimda fayl ham kerak. */
function rowProblem(r: Row, hasAudio: boolean): string | null {
  if (r.source === "gemini") return r.topic.trim() ? null : "";
  if (!r.title.trim() || !r.script.trim()) return "Subtitr matni va title majburiy.";
  if (r.title.trim().length > 100) return "Title 100 belgidan oshmasin.";
  if (/[<>]/.test(r.title)) return "Title ichida < yoki > bo'lmasin.";
  if (r.source === "audio" && !hasAudio && !r.file) return "Audio faylni tanlang.";
  if (r.file && r.file.size > AUDIO_MAX_MB * MB) return `Audio ${AUDIO_MAX_MB} MB dan katta.`;
  return null;
}

function TopicSheet({ sheet, catalog, close, run }: { sheet: { mode: "new" } | { mode: "edit"; topic: Topic }; catalog: Catalog; close: () => void; run: Run }) {
  const edit = sheet.mode === "edit" ? sheet.topic : null;
  // Shablon va format: yangi mavzuda standartdan boshlanadi, tahrirda mavzuning hozirgi qiymatidan.
  const [template, setTemplate] = useState(edit ? edit.template : catalog.defaults.template);
  const [format, setFormat] = useState(edit ? edit.format : catalog.defaults.format);
  const [voice, setVoice] = useState(edit ? edit.voice : catalog.defaults.voiceProfile || catalog.voiceProfiles[0] || "");
  const [rows, setRows] = useState<Row[]>(
    edit
      ? [{
          topic: edit.topic, notes: edit.notes, date: edit.publishLocal.date, time: edit.publishLocal.time,
          source: edit.hasAudio ? "audio" : edit.manualScript ? "manual" : "gemini", title: edit.manualTitle ?? "", script: edit.manualScript ?? "", file: null,
        }]
      : [emptyRow()],
  );
  const [saving, setSaving] = useState<string | null>(null);
  const set = (i: number, patch: Partial<Row>) => setRows((r) => r.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const at = (r: Row) => (r.date ? `${r.date} ${r.time || "18:00"}` : undefined);
  const hasAudio = !!edit?.hasAudio;
  // Qo'lda/audio rejimda alohida mavzu yo'q: mavzu nomi title bo'ladi.
  const filled = rows.filter((r) => (r.source === "gemini" ? r.topic.trim() : r.title.trim() || r.script.trim() || r.file));
  const topicOf = (r: Row) => (r.source === "gemini" ? r.topic : r.title);
  const problems = filled.map((r) => rowProblem(r, hasAudio));
  // Hali kompyuterga tushmagan shablon tanlanmaydi (tahrirdagi eski mavzuda tanlangan bo'lsa, saqlash ham to'xtaydi).
  const pending = catalog.pendingTemplates ?? [];
  const templatePending = !!template && pending.some((p) => p.name === template);
  const canSave = filled.length > 0 && problems.every((p) => p === null) && !templatePending;
  const allAudio = rows.every((r) => r.source === "audio");

  async function save() {
    if (!canSave) return;
    try {
      // Tayyor audio avval Storage'ga yuboriladi (Vercel so'rov chegarasini chetlab), mavzu esa faqat fayl yo'li bilan yaratiladi.
      const audioPaths: (string | undefined)[] = [];
      for (let i = 0; i < filled.length; i++) {
        const r = filled[i];
        if (r.source !== "audio" || !r.file) { audioPaths.push(undefined); continue; }
        const label = filled.length > 1 ? `Audio ${i + 1}/${filled.length}` : "Audio";
        setSaving(`${label} yuklanmoqda… 0%`);
        const prep = await api<PreparedAudio>("/api/audio", { filename: r.file.name, size: r.file.size });
        if (prep.uploadUrl) await putFile(prep.uploadUrl, r.file, (p) => setSaving(`${label} yuklanmoqda… ${p}%`));
        audioPaths.push(prep.path);
      }
      setSaving("Saqlanmoqda…");
      const manual = (r: Row) => (r.source === "gemini" ? { script: "", title: "" } : { script: r.script, title: r.title });
      const results = await run(edit
        ? [{ type: "update_topic", id: edit.id, topic: topicOf(filled[0]), notes: filled[0].notes, publishAt: at(filled[0]), template, format, voice, ...manual(filled[0]) }]
        : [{
            type: "create_topics",
            topics: filled.map((r, i) => ({
              topic: topicOf(r), notes: r.source === "gemini" ? r.notes : "", publishAt: at(r), template, format, voice,
              ...(r.source === "gemini" ? {} : { script: r.script, title: r.title }), audioPath: audioPaths[i],
            })),
          }]);
      if (results.every((r) => r.ok)) close();
    } catch (e) {
      haptic.fail();
      alert(e instanceof Error ? e.message : String(e));
    }
    setSaving(null);
  }

  return (
    <>
      <div className="backdrop" onClick={close} />
      <section className="sheet" role="dialog" aria-label={edit ? "Mavzuni tahrirlash" : "Yangi mavzu"}>
        <div className="grab" />
        <h2>{edit ? `#${edit.id} — tahrirlash` : "Yangi mavzular"}</h2>
        <div className="list" style={{ padding: 0 }}>
          {rows.map((r, i) => {
            const manual = r.source !== "gemini";
            const problem = filled.includes(r) ? problems[filled.indexOf(r)] : null;
            return (
              <div key={i} className="card formcard">
                <div className="seg" role="tablist" aria-label="Matn manbai" style={{ margin: 0 }}>
                  {SOURCES.map(([s, label]) => (
                    <button key={s} className={r.source === s ? "on" : ""} disabled={hasAudio && s !== "audio"}
                      onClick={() => { haptic.tap(); set(i, { source: s }); }}>{label}</button>
                  ))}
                </div>
                {manual ? null : (
                  <div className="field">
                    <label>{rows.length > 1 ? `Mavzu ${i + 1}` : "Mavzu"}</label>
                    <input className="input" value={r.topic} maxLength={500} placeholder="Masalan: How black holes form"
                      onChange={(e) => set(i, { topic: e.target.value })} autoFocus={i === rows.length - 1} />
                  </div>
                )}
                {manual ? (
                  <>
                    <div className="field">
                      <label>{rows.length > 1 ? `Title ${i + 1}` : "Title"} (YouTube) · {r.title.trim().length}/100</label>
                      <input className="input" value={r.title} maxLength={120} placeholder="Masalan: How Black Holes Are Born" onChange={(e) => set(i, { title: e.target.value })} />
                    </div>
                    <div className="field">
                      <label>Subtitr matni</label>
                      <textarea className="textarea" style={{ minHeight: 160 }} value={r.script} maxLength={30000}
                        placeholder={r.source === "audio" ? "Audioda aytilgan matn — so'zma-so'z." : "Ovozga o'qiladigan va subtitr bo'ladigan aniq matn."}
                        onChange={(e) => set(i, { script: e.target.value })} />
                    </div>
                    {r.source === "audio" ? (
                      <div className="field">
                        <label>Audio fayl{r.file ? ` · ${(r.file.size / MB).toFixed(1)} MB` : ""}</label>
                        {hasAudio
                          ? <div className="hint">Audio yuklangan ✓ (almashtirish uchun mavzuni o&apos;chirib, qaytadan qo&apos;shing).</div>
                          : <input className="input" type="file" accept={AUDIO_ACCEPT} onChange={(e) => set(i, { file: e.target.files?.[0] ?? null })} />}
                      </div>
                    ) : null}
                    <div className="hint">
                      {r.source === "audio"
                        ? "Gemini va VoiceStudio ishlatilmaydi: audio kompyuterga tushadi, subtitr vaqtlari shu audiodan olinadi. Matn audio bilan mos bo'lishi shart."
                        : "Gemini ishlatilmaydi: ovoz shu matndan yaratiladi va aynan shu matn subtitr bo'ladi."}
                    </div>
                  </>
                ) : (
                  <div className="field">
                    <label>Tushuntirish</label>
                    <textarea className="textarea" value={r.notes} maxLength={4000} placeholder="Nimalarni yoritish kerak, auditoriya, ohang…" onChange={(e) => set(i, { notes: e.target.value })} />
                  </div>
                )}
                <div className="grid2">
                  <div className="field"><label>Sana</label><input className="input" type="date" value={r.date} onChange={(e) => set(i, { date: e.target.value })} /></div>
                  <div className="field"><label>Soat</label><input className="input" type="time" value={r.time} onChange={(e) => set(i, { time: e.target.value })} /></div>
                </div>
                {!edit && !r.date ? <div className="hint">Sana bo'sh bo'lsa avtomatik: oxirgi rejadan +2 kun.</div> : null}
                {problem ? <div className="hint" style={{ color: "var(--danger)" }}>{problem}</div> : null}
              </div>
            );
          })}
        </div>
        <div className="card formcard" style={{ marginTop: 10 }}>
          <div className="grid2">
            <div className="field">
              <label>Shablon</label>
              <select className="input" value={template} onChange={(e) => setTemplate(e.target.value)}>
                <option value="">Shablonsiz</option>
                {catalog.templates.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
                {pending.map((p) => (
                  <option key={`pending:${p.name}`} value={p.name} disabled>⏳ {p.name} — serverga yuklanmoqda{p.progress > 0 ? ` ${p.progress}%` : "…"}</option>
                ))}
                {template && !templatePending && !catalog.templates.some((t) => t.name === template) ? <option value={template}>{template}</option> : null}
              </select>
            </div>
            <div className="field">
              <label>Format</label>
              <select className="input" value={format} onChange={(e) => setFormat(Number(e.target.value))}>
                {[0, 1, 2].map((f) => <option key={f} value={f}>{FormatText[f]}</option>)}
              </select>
            </div>
          </div>
          {allAudio ? null : (
            <div className="field">
              <label>Ovoz</label>
              <select className="input" value={voice} onChange={(e) => setVoice(e.target.value)}>
                {catalog.voiceProfiles.length === 0 ? <option value="">Kompyuterdagi standart</option> : null}
                {catalog.voiceProfiles.map((v) => <option key={v} value={v}>{v}</option>)}
                {voice && !catalog.voiceProfiles.includes(voice) ? <option value={voice}>{voice}</option> : null}
              </select>
            </div>
          )}
          {templatePending ? (
            <div className="hint" style={{ color: "var(--danger)" }}>«{template}» shabloni hali serverga yuklanmoqda: kompyuterga tushib o&apos;rnatilgach tanlash mumkin bo&apos;ladi.</div>
          ) : pending.length > 0 ? (
            <div className="hint">⏳ {pending.map((p) => `«${p.name}»`).join(", ")} serverga yuklanmoqda: kompyuterga tushib o&apos;rnatilgach shu ro&apos;yxatda tanlash mumkin bo&apos;ladi.</div>
          ) : null}
          {!catalog.known ? <div className="hint">Shablonlar ro&apos;yxati desktop ilova katalog yuborgach chiqadi.</div> : null}
        </div>
        <div style={{ display: "grid", gap: 8, marginTop: 12 }}>
          {!edit && rows.length < 5 ? <button className="btn block" onClick={() => setRows((r) => [...r, emptyRow()])}><IconPlus />Yana mavzu ({rows.length}/5)</button> : null}
          <div className="hint" style={{ textAlign: "center" }}>Video, subtitr va title har doim ingliz tilida tayyorlanadi.</div>
          <button className="btn primary block" disabled={saving !== null || !canSave} onClick={save}>{saving ?? (edit ? "Saqlash" : "Qo'shish")}</button>
        </div>
      </section>
    </>
  );
}

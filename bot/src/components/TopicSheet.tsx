"use client";

/**
 * «Yangi video» / tahrirlash oynasi va «Chiqish vaqti» oynasi (dizayn: ytauto.dsai → Screens → Yangi video, Yangi video · O'z matnim,
 * Yangi video · Tayyor audio, Vaqtni o'zgartirish). Format plitkalari hamma videolarga; har video uchun matn manbai (Gemini / o'z
 * matnim / tayyor audio), boshlang'ich savol, sana-soat. Bir nechta video bo'lsa faqat bittasi ochiq, qolganlari qisqa qator.
 * Shablon, ovoz va musiqa - «ko'proq» bo'limida. Audio yuklanish foizi saqlash tugmasi ichida.
 */
import { useEffect, useState } from "react";
import { ArrowRight, Calendar, ChevronRight, CircleAlert, Clock, FileAudio, Info, Plus, RectangleHorizontal, RectangleVertical, SlidersHorizontal, Square, Trash2 } from "lucide-react";
import { api, confirmBox, haptic, putFile } from "@/lib/client";
import { FormatText, type Catalog, type PreparedAudio, type ScriptSource, type Topic } from "@/lib/types";
import type { Run } from "./App";
import { checkHook, countWords, FormatLength, HOOK_MAX_WORDS, manualLengthProblem, TTS_WORDS_PER_SEC } from "@/lib/video-rules";
import { Alert, Btn, Field, HookSlab, Segmented, Sheet, Status, type IconT } from "./ui";
import st from "./TopicSheet.module.css";

// ------------------------------------------------------------------ umumiy yordamchilar (Topics ham ishlatadi)
export const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
/** Desktop ilovadagi qoida: vaqt o'tib ketgan yoki 30 daqiqadan yaqin bo'lsa, video yuklangandan 1 soat keyin chiqadi. */
export const isLate = (iso: string) => new Date(iso).getTime() <= Date.now() + 30 * 60000;
export const fmtWhen = (d: { date: string; time: string }) => `${Number(d.date.slice(8, 10))} ${MONTHS[Number(d.date.slice(5, 7)) - 1]}, ${d.time}`;
export const formatShort = (f: number) => (f === 1 ? "Shorts" : f === 2 ? "1:1" : "16:9");
/** Kanal vaqt zonasidagi bugungi sana (YYYY-MM-DD). */
export function todayIn(tz: string) {
  try {
    return new Intl.DateTimeFormat("en-CA", { timeZone: tz, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  } catch {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  }
}
/** Format uzunligi qoidasi (lib/video-rules FormatLength): «2–3 daqiqa» yoki «55 s gacha». */
export function lengthLabel(f: number) {
  const r = FormatLength[f] ?? FormatLength[0];
  return r.minSec >= 60 ? `${r.minSec / 60}–${r.maxSec / 60} daqiqa` : `${r.maxSec} s gacha`;
}

const FORMAT_ICON: Record<number, IconT> = { 0: RectangleHorizontal, 1: RectangleVertical, 2: Square };
const formatParts = (f: number) => {
  const words = (FormatText[f] ?? "").split(" ");
  return { ratio: words.pop() ?? "", name: words.join(" ") };
};
const mb = (bytes: number) => (bytes / MB).toFixed(1).replace(".", ",");
const mmss = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.round(sec % 60)).padStart(2, "0")}`;

// ------------------------------------------------------------------ chiqish vaqti
/** Tayyor (yoki tayyorlanayotgan) vazifaning chiqish vaqtini o'zgartirish. */
export function RescheduleSheet({ topic, close, run }: { topic: Topic; close: () => void; run: Run }) {
  const [date, setDate] = useState(topic.publishLocal.date);
  const [time, setTime] = useState(topic.publishLocal.time);
  const [saving, setSaving] = useState(false);
  const changed = date !== topic.publishLocal.date || time !== topic.publishLocal.time;
  async function save() {
    setSaving(true);
    const [r] = await run([{ type: "reschedule", id: topic.id, publishAt: `${date} ${time || "18:00"}` }]);
    setSaving(false);
    if (r?.ok) close();
  }
  return (
    <Sheet title="Chiqish vaqti" sub={`#${topic.id} · ${topic.title || topic.topic}`} onClose={close} label="Chiqish vaqtini o'zgartirish"
      footer={<Btn block loading={saving} disabled={!date} onClick={() => void save()}>{saving ? "Saqlanmoqda…" : "Saqlash"}</Btn>}>
      <div className={`${st.when} tnum`}>
        <span>{fmtWhen(topic.publishLocal)}</span>
        {changed && date ? <><ArrowRight size={16} aria-hidden /><b>{fmtWhen({ date, time: time || "18:00" })}</b></> : null}
      </div>
      <div className="grid2">
        <Field label="Sana"><input className="inp" type="date" aria-label="Sana" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        <Field label="Soat"><input className="inp" type="time" aria-label="Soat" value={time} onChange={(e) => setTime(e.target.value)} /></Field>
      </div>
      <Alert tone="note" icon={Info}>Kamida 30 daqiqa kelajakda bo&apos;lsin. Video qayta yaratilmaydi.</Alert>
    </Sheet>
  );
}

// ------------------------------------------------------------------ yangi video / tahrirlash
type Row = { topic: string; notes: string; hook: string; date: string; time: string; source: ScriptSource; title: string; script: string; file: File | null };

const emptyRow = (): Row => ({ topic: "", notes: "", hook: "", date: "", time: "", source: "gemini", title: "", script: "", file: null });
const AUDIO_ACCEPT = ".mp3,.wav,.m4a,.aac,.ogg,.opus,.flac,.webm,audio/*";
const AUDIO_MAX_MB = 200;
const MB = 1048576;
const MAX_ROWS = 5;

const SOURCES: { key: ScriptSource; label: string }[] = [{ key: "gemini", label: "Gemini yozadi" }, { key: "manual", label: "O'z matnim" }, { key: "audio", label: "Tayyor audio" }];

/**
 * Qatorni saqlashdan oldin tekshiradi: Gemini rejimida mavzu va boshlang'ich savol (hook), qo'lda rejimda subtitr va title (matn
 * formatga sig'ishi kerak), audio rejimda fayl ham kerak. legacyHook - hook'siz qo'shilgan eski mavzu: hook bo'sh qolishi mumkin.
 */
function rowProblem(r: Row, hasAudio: boolean, format: number, legacyHook: boolean): string | null {
  if (r.source === "gemini") {
    if (!r.topic.trim()) return "";
    if (legacyHook && !r.hook.trim()) return null;
    const h = checkHook(r.hook);
    return "error" in h ? h.error : null;
  }
  if (!r.title.trim() || !r.script.trim()) return "Subtitr matni va title majburiy.";
  if (r.title.trim().length > 100) return "Title 100 belgidan oshmasin.";
  if (/[<>]/.test(r.title)) return "Title ichida < yoki > bo'lmasin.";
  if (r.source === "audio" && !hasAudio && !r.file) return "Audio faylni tanlang.";
  if (r.file && r.file.size > AUDIO_MAX_MB * MB) return `Audio ${AUDIO_MAX_MB} MB dan katta.`;
  if (r.source === "manual") return manualLengthProblem(r.script, format);
  return null;
}

/** Qatorda nimadir kiritilganmi (bo'sh qatorlar saqlanmaydi). Qo'lda/audio rejimda alohida mavzu yo'q: mavzu nomi title bo'ladi. */
const isFilled = (r: Row) => (r.source === "gemini" ? !!r.topic.trim() : !!(r.title.trim() || r.script.trim() || r.file));
const topicOf = (r: Row) => (r.source === "gemini" ? r.topic : r.title);

type SheetMode = { mode: "new" } | { mode: "edit"; topic: Topic };

export default function TopicSheet({ sheet, catalog, channelTitle, close, run }: { sheet: SheetMode; catalog: Catalog; channelTitle: string; close: () => void; run: Run }) {
  const edit = sheet.mode === "edit" ? sheet.topic : null;
  // Shablon va format: yangi mavzuda standartdan boshlanadi, tahrirda mavzuning hozirgi qiymatidan.
  const [template, setTemplate] = useState(edit ? edit.template : catalog.defaults.template);
  const [format, setFormat] = useState(edit ? edit.format : catalog.defaults.format);
  const [voice, setVoice] = useState(edit ? edit.voice : catalog.defaults.voiceProfile || catalog.voiceProfiles[0] || "");
  // Fon musiqasi: "" - avtomatik (kutubxonadan navbat bilan), "none" - musiqasiz, aks holda tanlangan trek.
  const lib = catalog.music ?? { enabled: false, tracks: [] };
  const [music, setMusic] = useState(edit?.music ?? "");
  const [rows, setRows] = useState<Row[]>(
    edit
      ? [{
          topic: edit.topic, notes: edit.notes, hook: edit.hook ?? "", date: edit.publishLocal.date, time: edit.publishLocal.time,
          source: edit.hasAudio ? "audio" : edit.manualScript ? "manual" : "gemini", title: edit.manualTitle ?? "", script: edit.manualScript ?? "", file: null,
        }]
      : [emptyRow()],
  );
  const [active, setActive] = useState(0);
  const [saving, setSaving] = useState<string | null>(null);
  const [progress, setProgress] = useState<{ i: number; pct: number } | null>(null);
  const [uploaded, setUploaded] = useState<number[]>([]);
  const [error, setError] = useState<string | null>(null);

  const set = (i: number, patch: Partial<Row>) => { setError(null); setRows((r) => r.map((x, j) => (j === i ? { ...x, ...patch } : x))); };
  const at = (r: Row) => (r.date ? `${r.date} ${r.time || "18:00"}` : undefined);
  const hasAudio = !!edit?.hasAudio;
  const legacyHook = !!edit && edit.hook === null;
  const problemAt = (i: number) => (isFilled(rows[i]) ? rowProblem(rows[i], hasAudio, format, legacyHook) : null);
  const filledIdx = rows.map((_, i) => i).filter((i) => isFilled(rows[i]));
  // Hali kompyuterga tushmagan shablon tanlanmaydi (tahrirdagi eski mavzuda tanlangan bo'lsa, saqlash ham to'xtaydi).
  const pending = catalog.pendingTemplates ?? [];
  const templatePending = !!template && pending.some((p) => p.name === template);
  const canSave = filledIdx.length > 0 && filledIdx.every((i) => problemAt(i) === null) && !templatePending;
  const allAudio = rows.every((r) => r.source === "audio");
  const [moreOpen, setMoreOpen] = useState(() => templatePending);

  async function save() {
    if (!canSave) return;
    setError(null);
    const filled = filledIdx.map((i) => rows[i]);
    try {
      // Tayyor audio avval Storage'ga yuboriladi (Vercel so'rov chegarasini chetlab), mavzu esa faqat fayl yo'li bilan yaratiladi.
      const audioPaths: (string | undefined)[] = [];
      for (let k = 0; k < filled.length; k++) {
        const r = filled[k];
        if (r.source !== "audio" || !r.file) { audioPaths.push(undefined); continue; }
        const label = filled.length > 1 ? `Audio ${k + 1}/${filled.length}` : "Audio";
        setSaving(`${label} yuklanmoqda… 0%`);
        setProgress({ i: filledIdx[k], pct: 0 });
        const prep = await api<PreparedAudio>("/api/audio", { filename: r.file.name, size: r.file.size });
        if (prep.uploadUrl) await putFile(prep.uploadUrl, r.file, (p) => { setSaving(`${label} yuklanmoqda… ${p}%`); setProgress({ i: filledIdx[k], pct: p }); });
        setUploaded((u) => [...u, filledIdx[k]]);
        audioPaths.push(prep.path);
      }
      setProgress(null);
      setSaving("Saqlanmoqda…");
      const manual = (r: Row) => (r.source === "gemini" ? { script: "", title: "" } : { script: r.script, title: r.title });
      const results = await run(edit
        ? [{
            type: "update_topic", id: edit.id, topic: topicOf(filled[0]), notes: filled[0].notes, publishAt: at(filled[0]), template, format, voice, music, ...manual(filled[0]),
            hook: filled[0].source === "gemini" && (filled[0].hook.trim() || !legacyHook) ? filled[0].hook : undefined,
          }]
        : [{
            type: "create_topics",
            topics: filled.map((r, i) => ({
              topic: topicOf(r), notes: r.source === "gemini" ? r.notes : "", hook: r.source === "gemini" ? r.hook : undefined, publishAt: at(r), template, format, voice, music,
              ...(r.source === "gemini" ? {} : { script: r.script, title: r.title }), audioPath: audioPaths[i],
            })),
          }]);
      if (results.every((r) => r.ok)) { close(); return; }
    } catch (e) {
      haptic.fail();
      setError(e instanceof Error ? e.message : String(e));
    }
    setSaving(null);
    setProgress(null);
    setUploaded([]);
  }

  function addRow() {
    haptic.tap();
    setRows((r) => [...r, emptyRow()]);
    setActive(rows.length);
  }
  function removeRow(i: number) {
    haptic.tap();
    setRows((r) => r.filter((_, j) => j !== i));
    setActive((a) => Math.max(0, a > i ? a - 1 : a === i ? i - 1 : a));
  }

  const musicLabel = music === "none" ? "musiqasiz" : music ? lib.tracks.find((m) => m.id === music)?.title ?? "o'chirilgan trek" : "avtomatik";
  const moreSummary = [template || "Shablonsiz", allAudio ? null : voice || "kompyuterdagi standart ovoz", musicLabel].filter(Boolean).join(" · ");
  const isSaving = saving !== null;

  return (
    <Sheet
      title={edit ? "Tahrirlash" : "Yangi video"}
      sub={`${edit ? `#${edit.id} · ` : ""}${channelTitle ? `${channelTitle} kanaliga · ` : ""}matn inglizcha`}
      label={edit ? "Videoni tahrirlash" : "Yangi video"}
      onClose={async () => {
        if (isSaving) return;
        if (!edit && rows.some(isFilled) && !(await confirmBox("Kiritilgan ma'lumot o'chib ketadi. Yopilsinmi?"))) return;
        close();
      }}
      footer={
        <>
          {error ? <Alert>{error}</Alert> : null}
          {templatePending ? <Alert>«{template}» shabloni hali serverga yuklanmoqda: kompyuterga tushib o&apos;rnatilgach tanlash mumkin bo&apos;ladi.</Alert> : null}
          <div className={st.footRow}>
            {!edit && rows.length < MAX_ROWS && !isSaving ? (
              <Btn tone="secondary" icon={Plus} onClick={addRow}>Yana mavzu ({rows.length}/{MAX_ROWS})</Btn>
            ) : null}
            <Btn loading={isSaving} disabled={!canSave} onClick={() => void save()}>{saving ?? (edit ? "Saqlash" : "Navbatga qo'shish")}</Btn>
          </div>
        </>
      }>
      {/* format - hamma videolarga */}
      <div className={st.section}>
        <span className="label" id="fmt-label">Format · hamma videolarga</span>
        <div className={st.tiles} role="radiogroup" aria-labelledby="fmt-label">
          {[0, 1, 2].map((f) => {
            const Icon = FORMAT_ICON[f];
            const { ratio, name } = formatParts(f);
            return (
              <button key={f} type="button" role="radio" aria-checked={format === f} className={`${st.tile}${format === f ? " " + st.on : ""}`} disabled={isSaving}
                onClick={() => { haptic.tap(); setFormat(f); }}>
                <span className={st.tileTop}><Icon size={16} /><b>{ratio}</b></span>
                <span className={st.tileName}>{name}</span>
                <span className={st.tileLen}>{lengthLabel(f)}</span>
              </button>
            );
          })}
        </div>
        <div className="hint">
          {format === 0 ? "Ssenariy shu oraliqqa yoziladi." : "55 soniyadan oshsa ovoz biroz tezlashtiriladi yoki video to'xtatiladi."}
        </div>
      </div>

      {/* videolar: bittasi ochiq, qolganlari qisqa qator */}
      {rows.map((r, i) => {
        const problem = problemAt(i);
        if (i !== active) {
          const len = r.source === "manual" && r.script.trim() ? scriptLength(r.script, format) : null;
          return (
            <button key={i} type="button" className={st.collapsed} aria-expanded={false} onClick={() => { haptic.tap(); setActive(i); }}>
              <span className={st.cHead}>
                <span className={`${st.no} tnum`}>{i + 1}</span>
                <span className={st.cTitle}>{topicOf(r).trim() || (r.source === "gemini" ? "Mavzu kiritilmagan" : "Title kiritilmagan")}</span>
                {uploaded.includes(i) ? <Status tone="ok">Yuklandi</Status>
                  : progress?.i === i ? <Status tone="accent">{progress.pct}%</Status>
                  : len ? <span className={`${st.cLen}${len.over ? " " + st.bad : ""} tnum`}>~{len.sec} s <span>/ {len.max} s</span></span>
                  : <ChevronRight size={18} className={st.cChev} />}
              </span>
              {len?.over ? <span className={st.cBar}><i /></span> : null}
              {problem ? <span className="field-err"><CircleAlert size={15} />{problem}</span> : null}
            </button>
          );
        }
        const manual = r.source !== "gemini";
        return (
          <div key={i} className={st.rowCard}>
            {rows.length > 1 ? (
              <div className={st.rowHead}>
                <span className="label">Video {i + 1}</span>
                {!isSaving ? <button type="button" className="linkbtn" onClick={() => removeRow(i)}><Trash2 size={14} />Olib tashlash</button> : null}
              </div>
            ) : null}
            <Segmented<ScriptSource> label="Matn manbai" value={r.source}
              items={SOURCES.map((s) => ({ ...s, disabled: isSaving || (hasAudio && s.key !== "audio") }))}
              onChange={(v) => { haptic.tap(); set(i, { source: v }); }} />

            {!manual ? (
              <>
                <Field label={rows.length > 1 ? `Mavzu ${i + 1}` : "Mavzu"}>
                  <input className="inp" aria-label="Mavzu" value={r.topic} maxLength={500} placeholder="Masalan: How black holes form"
                    onChange={(e) => set(i, { topic: e.target.value })} autoFocus={i === rows.length - 1} />
                </Field>
                <div className="field">
                  <HookSlab right={`${countWords(r.hook)}/${HOOK_MAX_WORDS} so'z`}
                    hint="Video aynan shu savol bilan boshlanadi: montaj ham shundan quriladi, qolgani — javob. Inglizcha, «?» bilan.">
                    <textarea className={st.hookInput} rows={2} aria-label="Boshlang'ich savol (hook)" value={r.hook} maxLength={200}
                      placeholder="What really happens inside a black hole?" onChange={(e) => set(i, { hook: e.target.value })} />
                  </HookSlab>
                  {problem ? <div className="field-err"><CircleAlert size={15} />{problem}</div> : null}
                </div>
                <Field label="Tushuntirish (ixtiyoriy)">
                  <textarea className="inp" aria-label="Tushuntirish" value={r.notes} maxLength={4000} placeholder="Nimalarni yoritish kerak, auditoriya, ohang…"
                    onChange={(e) => set(i, { notes: e.target.value })} />
                </Field>
              </>
            ) : (
              <>
                <Field label={`${rows.length > 1 ? `Title ${i + 1}` : "Title"} (YouTube)`} right={`${r.title.trim().length}/100`}>
                  <input className="inp" aria-label="Title (YouTube)" value={r.title} maxLength={120} placeholder="Masalan: How Black Holes Are Born"
                    onChange={(e) => set(i, { title: e.target.value })} />
                </Field>
                <Field label="Subtitr matni">
                  <textarea className={`inp ${st.script}`} aria-label="Subtitr matni" value={r.script} maxLength={30000}
                    placeholder={r.source === "audio" ? "Audioda aytilgan matn — so'zma-so'z." : "Ovozga o'qiladigan va subtitr bo'ladigan aniq matn."}
                    onChange={(e) => set(i, { script: e.target.value })} />
                </Field>
                {r.source === "manual" ? <LengthMeter script={r.script} format={format} /> : null}
                {r.source === "audio" ? (
                  <Field label="Audio fayl" right={`${AUDIO_MAX_MB} MB gacha`}>
                    {hasAudio
                      ? <Alert tone="note" icon={FileAudio}>Audio yuklangan. Almashtirish uchun videoni o&apos;chirib, qaytadan qo&apos;shing.</Alert>
                      : <AudioPick file={r.file} disabled={isSaving} pct={progress?.i === i ? progress.pct : uploaded.includes(i) ? 100 : null}
                          onPick={(file) => set(i, { file })} />}
                  </Field>
                ) : null}
                <div className="hint">
                  {r.source === "audio"
                    ? "Gemini va VoiceStudio ishlatilmaydi: subtitr vaqtlari shu audiodan olinadi. Matn audio bilan so'zma-so'z mos bo'lsin."
                    : "Gemini ishlatilmaydi: ovoz va subtitr shu matndan."}
                </div>
                {problem ? <div className="field-err"><CircleAlert size={15} />{problem}</div> : null}
              </>
            )}

            <div className="grid2">
              <label className={st.iconInp}>
                <Calendar size={18} aria-hidden />
                <input className="inp" type="date" aria-label="Sana" data-empty={!r.date || undefined} value={r.date} onChange={(e) => set(i, { date: e.target.value })} />
                {!r.date ? <span className={st.ph} aria-hidden>Avtomatik</span> : null}
              </label>
              <label className={st.iconInp}>
                <Clock size={18} aria-hidden />
                <input className="inp" type="time" aria-label="Soat" data-empty={!r.time || undefined} value={r.time} placeholder="18:00" onChange={(e) => set(i, { time: e.target.value })} />
                {!r.time ? <span className={st.ph} aria-hidden>18:00</span> : null}
              </label>
            </div>
            {!edit && !r.date ? <div className="hint">Sana bo&apos;sh: oxirgi rejadan +2 kun keyin chiqadi.</div> : null}
          </div>
        );
      })}

      {/* shablon, ovoz va musiqa */}
      <div className={st.moreWrap}>
        <button type="button" className={st.more} aria-expanded={moreOpen} onClick={() => { haptic.tap(); setMoreOpen((o) => !o); }}>
          <span className={st.moreIc}><SlidersHorizontal size={18} /></span>
          <span className={st.moreT}><b>{allAudio ? "Shablon va musiqa" : "Shablon, ovoz va musiqa"}</b><span>{moreSummary}</span></span>
          <ChevronRight size={18} className={`${st.moreChev}${moreOpen ? " " + st.open : ""}`} />
        </button>
        {moreOpen ? (
          <div className={st.moreBody}>
            <Field label="Shablon"
              hint={!catalog.known ? "Shablonlar ro'yxati desktop ilova katalog yuborgach chiqadi."
                : pending.length > 0 && !templatePending ? `${pending.map((p) => `«${p.name}»`).join(", ")} serverga yuklanmoqda: kompyuterga tushib o'rnatilgach shu ro'yxatda tanlash mumkin bo'ladi.` : undefined}
              error={templatePending ? `«${template}» shabloni hali serverga yuklanmoqda: kompyuterga tushib o'rnatilgach tanlash mumkin bo'ladi.` : undefined}>
              <select className="inp" aria-label="Shablon" value={template} onChange={(e) => setTemplate(e.target.value)}>
                <option value="">Shablonsiz</option>
                {catalog.templates.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
                {pending.map((p) => (
                  <option key={`pending:${p.name}`} value={p.name} disabled>{p.name} — serverga yuklanmoqda{p.progress > 0 ? ` ${p.progress}%` : "…"}</option>
                ))}
                {template && !templatePending && !catalog.templates.some((t) => t.name === template) ? <option value={template}>{template}</option> : null}
              </select>
            </Field>
            {allAudio ? null : (
              <Field label="Ovoz">
                <select className="inp" aria-label="Ovoz" value={voice} onChange={(e) => setVoice(e.target.value)}>
                  {catalog.voiceProfiles.length === 0 ? <option value="">Kompyuterdagi standart</option> : null}
                  {catalog.voiceProfiles.map((v) => <option key={v} value={v}>{v}</option>)}
                  {voice && !catalog.voiceProfiles.includes(voice) ? <option value={voice}>{voice}</option> : null}
                </select>
              </Field>
            )}
            <Field label="Fon musiqasi"
              hint={lib.tracks.length === 0 ? "Kutubxona bo'sh: Sozlamalar → Shablon va musiqa'dan trek qo'shing."
                : music && music !== "none" && !lib.enabled ? "Umumiy fon musiqasi o'chiq, lekin shu videoga tanlangan trek baribir qo'yiladi." : undefined}>
              <select className="inp" aria-label="Fon musiqasi" value={music} onChange={(e) => setMusic(e.target.value)}>
                <option value="">{lib.enabled && lib.tracks.length > 0 ? "Avtomatik — kutubxonadan navbat bilan" : "Avtomatik (hozir musiqasiz)"}</option>
                <option value="none">Musiqasiz</option>
                {lib.tracks.map((m) => <option key={m.id} value={m.id}>{m.title}{m.creator ? ` — ${m.creator}` : ""}</option>)}
                {music && music !== "none" && !lib.tracks.some((m) => m.id === music) ? <option value={music}>O&apos;chirilgan trek</option> : null}
              </select>
            </Field>
          </div>
        ) : null}
      </div>
    </Sheet>
  );
}

// ------------------------------------------------------------------ matn uzunligi va audio fayl
function scriptLength(script: string, format: number) {
  const rule = FormatLength[format] ?? FormatLength[0];
  const words = countWords(script);
  const sec = Math.round(words / TTS_WORDS_PER_SEC);
  // Ovoz tezligi taxminiy: chegaradan 10% gacha oshsa worker ovozni biroz tezlashtirib sig'diradi (manualLengthProblem bilan bir xil).
  return { words, sec, max: rule.maxSec, over: sec > rule.maxSec * 1.1 };
}

function LengthMeter({ script, format }: { script: string; format: number }) {
  const { words, sec, max, over } = scriptLength(script, format);
  return (
    <div className={st.meter}>
      <div className={st.bar} role="meter" aria-valuemin={0} aria-valuemax={max} aria-valuenow={sec} aria-label="Ovoz uzunligi (soniya)">
        <i className={over ? st.bad : undefined} style={{ width: `${Math.min(100, (sec / max) * 100)}%` }} />
      </div>
      <div className={`${st.meterRow} tnum`}>
        <span>{words} so&apos;z · ovozda taxminan</span>
        <b className={over ? st.bad : undefined}>~{sec} s <span>/ {max} s</span></b>
      </div>
    </div>
  );
}

function AudioPick({ file, pct, disabled, onPick }: { file: File | null; pct: number | null; disabled?: boolean; onPick: (f: File | null) => void }) {
  // Audio davomiyligi faqat ko'rsatish uchun (brauzer metama'lumotidan).
  const [dur, setDur] = useState<{ file: File; sec: number } | null>(null);
  useEffect(() => {
    if (!file) return;
    const url = URL.createObjectURL(file);
    const a = new Audio();
    a.preload = "metadata";
    a.onloadedmetadata = () => { if (Number.isFinite(a.duration)) setDur({ file, sec: a.duration }); };
    a.src = url;
    return () => { a.onloadedmetadata = null; a.removeAttribute("src"); URL.revokeObjectURL(url); };
  }, [file]);
  const sec = dur && dur.file === file ? dur.sec : null;
  return (
    <label className={`${st.file}${disabled ? " " + st.fileOff : ""}`}>
      <input className={st.fileInput} type="file" accept={AUDIO_ACCEPT} disabled={disabled} aria-label="Audio fayl"
        onChange={(e) => onPick(e.target.files?.[0] ?? null)} />
      <span className={st.fileIc}><FileAudio size={20} /></span>
      <span className={st.fileT}>
        <b>{file ? file.name : "Audio faylni tanlang"}</b>
        <span className="tnum">
          {!file ? "mp3, wav, m4a, ogg, flac…"
            : pct !== null ? <><em>{mb((file.size * pct) / 100)} / {mb(file.size)} MB</em>{sec !== null ? ` · ${mmss(sec)}` : ""}</>
            : `${mb(file.size)} MB${sec !== null ? ` · ${mmss(sec)}` : ""}`}
        </span>
      </span>
      {pct !== null ? <span className={st.fileBar} aria-hidden><i style={{ width: `${pct}%` }} /></span> : null}
    </label>
  );
}

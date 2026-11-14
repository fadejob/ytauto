"use client";

import { useMemo, useState } from "react";
import { confirmBox, haptic } from "@/lib/client";
import { StatusText, type Action, type State, type Topic } from "@/lib/types";
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
/** Desktop ilovadagi qoida: vaqt o'tib ketgan yoki 30 daqiqadan yaqin bo'lsa, video yuklangandan 35 daqiqa keyin chiqadi. */
const isLate = (iso: string) => new Date(iso).getTime() <= Date.now() + 30 * 60000;
const fmt = (d: { date: string; time: string }) => `${Number(d.date.slice(8, 10))} ${MONTHS[Number(d.date.slice(5, 7)) - 1]}, ${d.time}`;

export default function Topics({ state, loading, refresh, run }: { state: State | null; loading: boolean; refresh: () => Promise<void>; run: Run }) {
  const [group, setGroup] = useState<Group>("queue");
  const [sheet, setSheet] = useState<{ mode: "new" } | { mode: "edit"; topic: Topic } | null>(null);
  const [open, setOpen] = useState<number | null>(null);
  const [busy, setBusy] = useState<number | null>(null);

  const counts = useMemo(() => {
    const c = { queue: 0, work: 0, done: 0 };
    for (const t of state?.topics ?? []) c[groupOf(t)]++;
    return c;
  }, [state]);
  const items = (state?.topics ?? []).filter((t) => groupOf(t) === group);
  if (group === "done") items.sort((a, b) => b.publishAt.localeCompare(a.publishAt));

  async function act(t: Topic, kind: "publish" | "unpublish" | "upload" | "unupload" | "delete") {
    if (kind === "publish" && !(await confirmBox(t.youtubeUrl
      ? `"${t.topic}" YouTube'da hozir public qilinsinmi?`
      : `"${t.topic}" hozir tayyorlanib, YouTube'ga darhol chiqarilsinmi?`))) return;
    if (kind === "upload" && !(await confirmBox(
      `"${t.title || t.topic}" tayyor videosi YouTube'ga yuklansinmi?\n\nVideo qayta tayyorlanmaydi. Chiqish vaqti: ${isLate(t.publishAt) ? "o'tib ketgan — yuklangandan 35 daqiqa keyin" : fmt(t.publishLocal)}.`))) return;
    if (kind === "delete" && !(await confirmBox(`"${t.topic}" o'chirilsinmi? Qaytarib bo'lmaydi.`))) return;
    setBusy(t.id);
    const action: Action =
      kind === "publish" ? { type: "publish_now", id: t.id }
      : kind === "unpublish" ? { type: "cancel_publish_now", id: t.id }
      : kind === "upload" ? { type: "upload_ready", id: t.id }
      : kind === "unupload" ? { type: "cancel_upload_ready", id: t.id }
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

      <div className="list" style={{ paddingBottom: group !== "done" ? 64 : 0 }}>
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
              <span>📅 {fmt(t.publishLocal)}</span>
            </div>
            {t.error ? <div className="err">{t.error}</div> : null}
            <div className="actions">
              {t.canUploadReady ? <button className="btn primary" disabled={busy === t.id} onClick={() => act(t, "upload")}><IconUpload />YouTube'ga yuklash</button> : null}
              {t.uploadRequested ? <button className="btn ghost" disabled={busy === t.id} onClick={() => act(t, "unupload")}>Yuklashni bekor qilish</button> : null}
              {t.canPublishNow ? <button className="btn primary" disabled={busy === t.id} onClick={() => act(t, "publish")}><IconBolt />Hozir chiqarish</button> : null}
              {t.publishNow ? <button className="btn ghost" disabled={busy === t.id} onClick={() => act(t, "unpublish")}>Bekor qilish</button> : null}
              {t.editable ? <button className="btn" onClick={() => { haptic.tap(); setSheet({ mode: "edit", topic: t }); }}><IconEdit />Tahrirlash</button> : null}
              {t.deletable ? <button className="btn danger" disabled={busy === t.id} onClick={() => act(t, "delete")}><IconTrash /></button> : null}
              {t.youtubeUrl ? <a className="btn" href={t.youtubeUrl} target="_blank" rel="noreferrer"><IconPlay />YouTube</a> : null}
            </div>
          </article>
        ))}
      </div>

      {group !== "done" && (
        <button className="fab" onClick={() => { haptic.tap(); setSheet({ mode: "new" }); }}><IconPlus />Mavzu</button>
      )}
      {sheet && <TopicSheet sheet={sheet} close={() => setSheet(null)} run={run} />}
    </>
  );
}

type Row = { topic: string; notes: string; date: string; time: string };

function TopicSheet({ sheet, close, run }: { sheet: { mode: "new" } | { mode: "edit"; topic: Topic }; close: () => void; run: Run }) {
  const edit = sheet.mode === "edit" ? sheet.topic : null;
  const [rows, setRows] = useState<Row[]>(
    edit ? [{ topic: edit.topic, notes: edit.notes, date: edit.publishLocal.date, time: edit.publishLocal.time }] : [{ topic: "", notes: "", date: "", time: "" }],
  );
  const [saving, setSaving] = useState(false);
  const set = (i: number, patch: Partial<Row>) => setRows((r) => r.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const at = (r: Row) => (r.date ? `${r.date} ${r.time || "18:00"}` : undefined);

  async function save() {
    const filled = rows.filter((r) => r.topic.trim());
    if (!filled.length) return;
    setSaving(true);
    const results = await run(edit
      ? [{ type: "update_topic", id: edit.id, topic: filled[0].topic, notes: filled[0].notes, publishAt: at(filled[0]) }]
      : [{ type: "create_topics", topics: filled.map((r) => ({ topic: r.topic, notes: r.notes, publishAt: at(r) })) }]);
    setSaving(false);
    if (results.every((r) => r.ok)) close();
  }

  return (
    <>
      <div className="backdrop" onClick={close} />
      <section className="sheet" role="dialog" aria-label={edit ? "Mavzuni tahrirlash" : "Yangi mavzu"}>
        <div className="grab" />
        <h2>{edit ? `#${edit.id} — tahrirlash` : "Yangi mavzular"}</h2>
        <div className="list" style={{ padding: 0 }}>
          {rows.map((r, i) => (
            <div key={i} className="card formcard">
              <div className="field">
                <label>{rows.length > 1 ? `Mavzu ${i + 1}` : "Mavzu"}</label>
                <input className="input" value={r.topic} maxLength={500} placeholder="Masalan: How black holes form" onChange={(e) => set(i, { topic: e.target.value })} autoFocus={i === rows.length - 1} />
              </div>
              <div className="field">
                <label>Tushuntirish</label>
                <textarea className="textarea" value={r.notes} maxLength={4000} placeholder="Nimalarni yoritish kerak, auditoriya, ohang…" onChange={(e) => set(i, { notes: e.target.value })} />
              </div>
              <div className="grid2">
                <div className="field"><label>Sana</label><input className="input" type="date" value={r.date} onChange={(e) => set(i, { date: e.target.value })} /></div>
                <div className="field"><label>Soat</label><input className="input" type="time" value={r.time} onChange={(e) => set(i, { time: e.target.value })} /></div>
              </div>
              {!edit && !r.date ? <div className="hint">Sana bo'sh bo'lsa avtomatik: oxirgi rejadan +2 kun.</div> : null}
            </div>
          ))}
        </div>
        <div style={{ display: "grid", gap: 8, marginTop: 12 }}>
          {!edit && rows.length < 5 ? <button className="btn block" onClick={() => setRows((r) => [...r, { topic: "", notes: "", date: "", time: "" }])}><IconPlus />Yana mavzu ({rows.length}/5)</button> : null}
          <div className="hint" style={{ textAlign: "center" }}>Video, subtitr va title har doim ingliz tilida tayyorlanadi.</div>
          <button className="btn primary block" disabled={saving || !rows.some((r) => r.topic.trim())} onClick={save}>{saving ? "Saqlanmoqda…" : edit ? "Saqlash" : "Qo'shish"}</button>
        </div>
      </section>
    </>
  );
}

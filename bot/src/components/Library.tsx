"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { CircleCheck, Clapperboard, Film, LayoutTemplate, LoaderCircle, Package, Plus, Trash2, TriangleAlert, Upload as UploadIcon } from "lucide-react";
import { api, confirmBox } from "@/lib/client";
import type { TemplateItem, TemplateUpload as Upload } from "@/lib/types";
import type { Toast } from "./App";
import TemplateUpload, { MB, useTemplateUploads, type LocalUpload } from "./TemplateUpload";
import { Alert, Empty, Skel, Status, useBusy } from "./ui";
import st from "./Library.module.css";

const KIND: Record<string, string> = { Mp4Reference: "video namuna", OpenFilmProject: "OpenFilm loyihasi" };
const size = (b: number) => (b >= 1024 * MB ? `${(b / 1024 / MB).toFixed(2)} GB` : b >= MB ? `${(b / MB).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const day = (iso: string | null) => { if (!iso) return null; const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}`; };
function KindIcon({ kind }: { kind: string }) {
  return kind === "OpenFilmProject" ? <Package size={20} /> : <Film size={20} />;
}
const key = (machine: string, name: string) => machine + "\u0000" + name;

/** Kompyuterdagi yuklash bosqichi (plitkadagi yozuv). */
const STAGE: Record<Upload["status"], string> = {
  pending: "kompyuter kutilmoqda",
  downloading: "kompyuter olmoqda",
  importing: "o'rnatilmoqda",
  done: "o'rnatildi",
  error: "xato",
};

/**
 * Shablonlar (dizayn: Shablonlar va musiqa Fk4Jp6): kompyuterlar bazaga yozgan shablonlar plitkalarda + yuklanayotganlari foiz bilan
 * + «Shablon yuklash» plitkasi (sheet ochadi).
 * canUpload=false - qo'shimcha token (mehmon): shablon kompyuter egasi tomonidan yuklanadi, mehmon faqat tanlaydi.
 */
export default function Library({ toast, canUpload = true, onCount }: { toast: Toast; canUpload?: boolean; onCount?: (n: number) => void }) {
  const [templates, setTemplates] = useState<TemplateItem[] | null>(null);
  const [loadErr, setLoadErr] = useState<string | null>(null);
  /** O'chirish so'rovi yuborilganlar: kompyuter (~2 daqiqada) bajargach ro'yxatdan yo'qoladi. */
  const [deleting, setDeleting] = useState<Set<string>>(() => new Set());
  const [sheet, setSheet] = useState(false);
  const busy = useBusy((m) => toast(m, true));
  const up = useTemplateUploads(toast, canUpload);

  const load = useCallback(() => {
    return api<{ templates: TemplateItem[] }>("/api/library")
      .then((d) => { setTemplates(d.templates); setLoadErr(null); })
      .catch((e) => { const m = e instanceof Error ? e.message : String(e); setLoadErr(m); toast(m, true); });
  }, [toast]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (templates) onCount?.(templates.length); }, [templates, onCount]);

  // Yuklash kompyuterda tugaganda (yangi "done") shablonlar ro'yxati qayta olinadi - yangi shablon darhol ko'rinsin.
  const doneIds = up.uploads ? up.uploads.filter((u) => u.status === "done").map((u) => u.id).join(",") : null;
  const prevDone = useRef<string | null>(null);
  useEffect(() => {
    if (doneIds === null) return;
    if (prevDone.current !== null && prevDone.current !== doneIds) void load();
    prevDone.current = doneIds;
  }, [doneIds, load]);

  async function del(t: TemplateItem) {
    const note = t.source === "video" ? "Arxivdagi video O'CHIRILMAYDI, faqat shablon ro'yxatidan olib tashlanadi." : "Shablon fayllari kompyuterdan o'chiriladi.";
    if (!(await confirmBox(`"${t.name}" shabloni o'chirilsinmi?\n\n${note}\nShu shablonni ishlatadigan navbatdagi mavzular "e'tibor kerak" holatiga tushishi mumkin.`))) return;
    const k = key(t.machine, t.name);
    await busy.run("del:" + k, async () => {
      const r = await api<{ ok: boolean; message: string }>("/api/library", { action: "delete_template", machine: t.machine, name: t.name });
      toast(r.message, !r.ok);
      if (r.ok) setDeleting((s) => new Set(s).add(k));
    });
  }

  const fromVideo = templates?.filter((t) => t.source === "video").length ?? 0;
  const have = new Set((templates ?? []).map((t) => key(t.machine, t.name)));
  // Kompyuterda: jarayondagilar va xatolar; tugaganlari shablon ro'yxatiga tushguncha «o'rnatildi» bo'lib turadi.
  const uploads = canUpload ? (up.uploads ?? []).filter((u) => u.status !== "done" || !have.has(key(u.machine, u.name))) : [];

  return (
    <section className={st.wrap} aria-label="Shablonlar">
      {!canUpload ? <Alert tone="note">Shablonni kompyuter egasi yuklaydi: siz tayyor shablonlardan tanlaysiz.</Alert> : null}

      {templates ? (
        <div className="section-label"><span>{fromVideo} tasi videodan · {templates.length - fromVideo} tasi yuklangan</span></div>
      ) : null}

      {loadErr && !templates ? (
        <Alert>
          {loadErr}{" "}
          <button type="button" className="linkbtn" onClick={() => { setLoadErr(null); void load(); }}>Qayta urinish</button>
        </Alert>
      ) : null}

      <div className={st.grid}>
        {canUpload ? (
          <button type="button" className={`${st.tile} ${st.add}`} onClick={() => setSheet(true)}>
            <span className={st.plus}><Plus size={22} /></span>
            <b className={st.name}>Shablon yuklash</b>
            <span className={st.meta}>MP4 yoki OpenFilm .zip</span>
          </button>
        ) : null}

        {up.local ? <LocalTile u={up.local} /> : null}
        {uploads.map((u) => <UploadTile key={u.id} u={u} />)}

        {!templates && !loadErr ? [0, 1, 2].map((i) => <Skel key={i} h={164} r={24} />) : null}

        {templates?.map((t) => {
          const k = key(t.machine, t.name);
          const gone = deleting.has(k);
          const when = day(t.addedAt);
          return (
            <article key={k} className={`${st.tile}${gone ? " " + st.dim : ""}`} title={t.notes || undefined}>
              <span className={st.kind} title={KIND[t.kind] ?? t.kind} aria-label={KIND[t.kind] ?? t.kind} role="img"><KindIcon kind={t.kind} /></span>
              {gone ? (
                <span className={st.corner}><Status>o'chirilmoqda</Status></span>
              ) : (
                <button type="button" className={st.del} aria-label={`«${t.name}» shablonini o'chirish`} disabled={busy.is("del:" + k)} onClick={() => void del(t)}>
                  {busy.is("del:" + k) ? <LoaderCircle size={18} className="spin" /> : <Trash2 size={18} />}
                </button>
              )}
              <b className={st.name}>{t.name}</b>
              <span className={st.grow} />
              <span className={st.src}>
                {t.source === "video" ? <Clapperboard size={14} /> : <UploadIcon size={14} />}
                <span>{t.source === "video" ? "tayyor video" : "yuklangan"}{when ? ` · ${when}` : ""}</span>
              </span>
              {t.notes ? <span className={st.note}>{t.notes}</span> : null}
              <span className={`${st.meta} tnum`}>{size(t.size)} · {t.machine}</span>
            </article>
          );
        })}
      </div>

      {templates && templates.length === 0 && uploads.length === 0 && !up.local ? (
        <Empty icon={LayoutTemplate} title="Hali shablon yo'q">Birinchi video tayyor bo'lgach shu yerda paydo bo'ladi.</Empty>
      ) : null}

      <div className="hint">
        Har tayyor video o'zi shablon bo'lib qoladi («tayyor video»): fayl arxivda qoladi, qayta nusxalanmaydi va internetga yuklanmaydi.
        Qo'lda yuklanganlar «yuklangan» deb belgilanadi.
      </div>

      {sheet && canUpload ? <TemplateUpload machines={up.machines} local={up.local} start={up.start} onClose={() => setSheet(false)} /> : null}
    </section>
  );
}

/** Brauzerdan serverga yuklanayotgan shablon (foiz bilan). */
function LocalTile({ u }: { u: LocalUpload }) {
  return (
    <article className={`${st.tile} ${st.busy}`} aria-busy="true">
      <span className={st.kind}><KindIcon kind={u.kind} /></span>
      <span className={`${st.pct} tnum`}>{u.pct}%</span>
      <b className={st.name}>{u.name}</b>
      <span className={st.grow} />
      <span className={st.stage}>serverga yuklanmoqda</span>
      <span className={`${st.meta} tnum`}>{size(u.size)} · {u.machines} kompyuter</span>
      <Bar pct={u.pct} label="Serverga yuklanmoqda" />
    </article>
  );
}

/** Kompyuterga tushayotgan / o'rnatilayotgan / xato bo'lgan yuklash (har kompyuter alohida). */
function UploadTile({ u }: { u: Upload }) {
  if (u.status === "error") {
    return (
      <article className={`${st.tile} ${st.bad}`}>
        <span className={st.kind}><TriangleAlert size={20} /></span>
        <b className={`${st.name} ${st.one}`}>{u.name}</b>
        <span className={st.err}>{u.message || "Yuklashda xato"}</span>
        <span className={st.grow} />
        <span className={st.meta}>→ {u.machine}</span>
      </article>
    );
  }
  const done = u.status === "done";
  const showPct = u.status === "downloading" || u.status === "importing";
  return (
    <article className={`${st.tile}${done ? "" : " " + st.busy}`} aria-busy={!done || undefined} title={u.message || undefined}>
      <span className={st.kind}><KindIcon kind={u.kind} /></span>
      {showPct ? <span className={`${st.pct} tnum`}>{u.progress}%</span> : done ? <span className={st.corner}><CircleCheck size={20} className={st.ok} /></span> : null}
      <b className={st.name}>{u.name}</b>
      <span className={st.grow} />
      <span className={`${st.stage}${done ? " " + st.stageOk : ""}`}>{STAGE[u.status]}{u.status === "pending" ? " (~2 daqiqa)" : ""}</span>
      {u.message ? <span className={st.note}>{u.message}</span> : null}
      <span className={`${st.meta} tnum`}>{size(u.size)} → {u.machine}</span>
      {done ? null : <Bar pct={showPct ? u.progress : 0} label={STAGE[u.status]} />}
    </article>
  );
}

function Bar({ pct, label }: { pct: number; label: string }) {
  return (
    <span className={st.bar} role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct}>
      <i style={{ width: `${Math.max(0, Math.min(100, pct))}%` }} />
    </span>
  );
}


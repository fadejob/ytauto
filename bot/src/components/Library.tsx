"use client";

import { useEffect, useState } from "react";
import { api, confirmBox } from "@/lib/client";
import type { TemplateItem } from "@/lib/types";
import type { Toast } from "./App";
import TemplateUpload from "./TemplateUpload";
import Music from "./Music";

const KIND: Record<string, string> = { Mp4Reference: "video namuna", OpenFilmProject: "OpenFilm loyihasi" };
const MB = 1048576;
const size = (b: number) => (b >= 1024 * MB ? `${(b / 1024 / MB).toFixed(2)} GB` : b >= MB ? `${(b / MB).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const day = (iso: string | null) => { if (!iso) return "—"; const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}`; };

/** Shablonlar va ovoz: kompyuterlar bazaga yozgan ma'lumotlar (faqat ko'rsatiladi) + qo'lda shablon yuklash. */
/** canUpload=false - qo'shimcha token: shablon kompyuter egasi tomonidan yuklanadi (mehmon faqat tanlaydi). */
export default function Library({ toast, canUpload = true }: { toast: Toast; canUpload?: boolean }) {
  const [templates, setTemplates] = useState<TemplateItem[] | null>(null);

  useEffect(() => {
    api<{ templates: TemplateItem[] }>("/api/library")
      .then((d) => setTemplates(d.templates))
      .catch((e) => toast(e instanceof Error ? e.message : String(e), true));
  }, [toast]);

  const fromVideo = templates?.filter((t) => t.source === "video").length ?? 0;
  const [busy, setBusy] = useState<string | null>(null);

  async function del(t: TemplateItem) {
    const note = t.source === "video" ? "Arxivdagi video O'CHIRILMAYDI, faqat shablon ro'yxatidan olib tashlanadi." : "Shablon fayllari kompyuterdan o'chiriladi.";
    if (!(await confirmBox(`"${t.name}" shabloni o'chirilsinmi?\n\n${note}\nShu shablonni ishlatadigan navbatdagi mavzular "e'tibor kerak" holatiga tushishi mumkin.`))) return;
    setBusy(t.machine + t.name);
    try {
      const r = await api<{ ok: boolean; message: string }>("/api/library", { action: "delete_template", machine: t.machine, name: t.name });
      toast(r.message, !r.ok);
      // Kompyuter (ilova ochiq bo'lsa ~30 soniya, aks holda ≤10 daqiqa) bajargach ro'yxatdan yo'qoladi.
    } catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  return (
    <>
      <div className="sect">Shablonlar {templates ? `(${templates.length})` : ""}</div>
      <div className="card formcard">
        <div className="hint">
          Har tayyor bo&apos;lgan video avtomatik shablon bo&apos;lib qoladi («video»): fayl arxivda qoladi, qayta nusxalanmaydi va internetga yuklanmaydi,
          faqat ma&apos;lumot shu yerga yoziladi. Qo&apos;lda yuklanganlar «yuklangan» deb belgilanadi.
        </div>
        {!templates ? <div className="skeleton" /> : templates.length === 0 ? <div className="hint">Hali shablon yo&apos;q: birinchi video tayyor bo&apos;lgach shu yerda paydo bo&apos;ladi.</div> : (
          <>
            <div className="hint">{fromVideo} ta tayyor videodan, {templates.length - fromVideo} ta yuklangan.</div>
            {templates.map((t) => (
              <div key={t.machine + t.name} className="logline">
                <span className="t">{size(t.size)}</span><b>{t.name}</b>
                <div className="hint">{KIND[t.kind] ?? t.kind} · {t.source === "video" ? "tayyor video" : "yuklangan"} · 💻 {t.machine} · {day(t.addedAt)}{t.notes ? ` · ${t.notes}` : ""}</div>
                <button className="btn danger" disabled={busy === t.machine + t.name} onClick={() => del(t)}>O&apos;chirish</button>
              </div>
            ))}
          </>
        )}
      </div>
      {canUpload ? <TemplateUpload toast={toast} /> : null}

      <Music toast={toast} />
    </>
  );
}

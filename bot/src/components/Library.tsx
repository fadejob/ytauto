"use client";

import { useEffect, useState } from "react";
import { api, confirmBox } from "@/lib/client";
import type { TemplateItem, VoiceInfo } from "@/lib/types";
import type { Toast } from "./App";
import TemplateUpload from "./TemplateUpload";

const KIND: Record<string, string> = { Mp4Reference: "video namuna", OpenFilmProject: "OpenFilm loyihasi" };
const MB = 1048576;
const size = (b: number) => (b >= 1024 * MB ? `${(b / 1024 / MB).toFixed(2)} GB` : b >= MB ? `${(b / MB).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const day = (iso: string | null) => { if (!iso) return "—"; const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}`; };

/** Shablonlar va ovoz: kompyuterlar bazaga yozgan ma'lumotlar (faqat ko'rsatiladi) + qo'lda shablon yuklash. */
export default function Library({ toast }: { toast: Toast }) {
  const [templates, setTemplates] = useState<TemplateItem[] | null>(null);
  const [voice, setVoice] = useState<VoiceInfo[] | null>(null);

  useEffect(() => {
    api<{ templates: TemplateItem[]; voice: VoiceInfo[] }>("/api/library")
      .then((d) => { setTemplates(d.templates); setVoice(d.voice); })
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
      <TemplateUpload toast={toast} />

      <div className="sect">Ovoz</div>
      {!voice ? <div className="card formcard"><div className="skeleton" /></div> : voice.length === 0 ? (
        <div className="card formcard"><div className="hint">Ovoz ma&apos;lumoti hali yo&apos;q: kompyuterdagi YtAuto ilovasi VoiceStudio modellarini o&apos;qigach shu yerda ko&apos;rinadi.</div></div>
      ) : voice.map((v) => (
        <div key={v.machine} className="card formcard">
          <div className="logline"><b>💻 {v.machine}</b> · VoiceStudio {v.voiceStudioVersion ? `v${v.voiceStudioVersion}` : "—"}</div>
          <div className="hint">Ovoz profillari (mavzu yaratishda tanlanadi):</div>
          {v.profiles.length === 0 ? <div className="hint">Profil yo&apos;q — ilovada «Ayol/Erkak ovozi» ni yuklab qo&apos;shing.</div> : v.profiles.map((p) => (
            <div key={p.name} className="logline">
              <b>{p.name}</b>{p.name === v.defaultProfile ? " ★ standart" : ""}
              <div className="hint">{p.model} · {p.voice || "—"} · {p.language} · tezlik {p.speed}</div>
            </div>
          ))}
          <div className="hint">Dvigatellar ({v.engines.filter((e) => e.usable).length} ta ishlatish mumkin / {v.engines.length}):</div>
          {v.engines.filter((e) => e.usable).map((e) => (
            <div key={e.id} className="logline">
              <b>{e.displayName}</b> <span className="chip ok">ishlaydi</span>
              <div className="hint">Modellar: {e.installedModels.length ? e.installedModels.join(", ") : "—"} · ovozlar: {e.voices.length ? e.voices.join(", ") : e.supportsCloning ? "klonlash" : "—"}</div>
            </div>
          ))}
          <details>
            <summary className="hint">Ishlatib bo&apos;lmaydigan dvigatellar ({v.engines.filter((e) => !e.usable).length})</summary>
            {v.engines.filter((e) => !e.usable).map((e) => (
              <div key={e.id} className="logline"><b>{e.displayName}</b><div className="hint">{e.reason ?? "Model o'rnatilmagan"}</div></div>
            ))}
          </details>
        </div>
      ))}
    </>
  );
}

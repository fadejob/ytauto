"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api, haptic, putFile } from "@/lib/client";
import type { Machine, PreparedUpload, TemplateUpload as Upload } from "@/lib/types";
import type { Toast } from "./App";

const STATUS: Record<Upload["status"], { text: string; cls: string }> = {
  pending: { text: "kompyuter kutilmoqda (~2 daqiqa)", cls: "chip warn" },
  downloading: { text: "kompyuterga yuklanmoqda", cls: "chip warn" },
  importing: { text: "o'rnatilmoqda", cls: "chip warn" },
  done: { text: "tayyor ✓", cls: "chip ok" },
  error: { text: "xato", cls: "chip bad" },
};

const MB = 1048576;

/** Shablonni botdan kompyuter(lar)ga yuklash: fayl → Supabase Storage → ilova yuklab oladi va o'rnatadi. */
export default function TemplateUpload({ toast }: { toast: Toast }) {
  const [name, setName] = useState("");
  const [kind, setKind] = useState<Upload["kind"]>("Mp4Reference");
  const [file, setFile] = useState<File | null>(null);
  const [machines, setMachines] = useState<Machine[]>([]);
  const [picked, setPicked] = useState<string[]>([]);
  const [pct, setPct] = useState<number | null>(null);
  const [uploads, setUploads] = useState<Upload[]>([]);
  const fileRef = useRef<HTMLInputElement>(null);

  const loadUploads = useCallback(() => api<Upload[]>("/api/templates").then(setUploads).catch(() => {}), []);

  useEffect(() => {
    api<Machine[]>("/api/machines").then((m) => { setMachines(m); setPicked((p) => (p.length ? p : m.map((x) => x.name))); }).catch(() => {});
    void loadUploads();
  }, [loadUploads]);

  // Faol yuklashlar bor ekan, har 4 soniyada holat yangilanadi.
  const active = uploads.some((u) => u.status === "pending" || u.status === "downloading" || u.status === "importing");
  useEffect(() => {
    if (!active) return;
    const t = window.setInterval(() => void loadUploads(), 4000);
    return () => window.clearInterval(t);
  }, [active, loadUploads]);

  async function start() {
    if (!file || !name.trim() || picked.length === 0) return;
    haptic.tap();
    setPct(0);
    try {
      const prep = await api<PreparedUpload>("/api/templates", { action: "prepare", name, kind, filename: file.name, size: file.size });
      if (prep.uploadUrl) await putFile(prep.uploadUrl, file, setPct);
      await api("/api/templates", { action: "commit", groupId: prep.groupId, path: prep.path, name, kind, size: file.size, machines: picked });
      toast("Shablon yuborildi. Kompyuter yuklab olib o'rnatadi.");
      setName(""); setFile(null); if (fileRef.current) fileRef.current.value = "";
      await loadUploads();
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setPct(null);
  }

  const accept = kind === "OpenFilmProject" ? ".zip" : ".mp4,.mov,.webm,.mkv,.m4v";
  const tooBig = !!file && file.size > 600 * MB;

  return (
    <>
      <div className="sect">Shablon yuklash</div>
      <div className="card formcard">
        <div className="grid2">
          <div className="field"><label>Nomi</label><input className="input" value={name} maxLength={60} placeholder="Masalan: Clean explainer" onChange={(e) => setName(e.target.value)} /></div>
          <div className="field"><label>Turi</label>
            <select className="input" value={kind} onChange={(e) => { setKind(e.target.value as Upload["kind"]); setFile(null); if (fileRef.current) fileRef.current.value = ""; }}>
              <option value="Mp4Reference">Video namuna (MP4…)</option>
              <option value="OpenFilmProject">OpenFilm loyihasi (.zip)</option>
            </select></div>
        </div>
        <div className="field"><label>Fayl {file ? `· ${(file.size / MB).toFixed(1)} MB` : ""}</label>
          <input ref={fileRef} className="input" type="file" accept={accept} onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></div>
        {tooBig ? <div className="hint" style={{ color: "var(--danger)" }}>Fayl 600 MB dan katta.</div> : null}
        <div className="field"><label>Qaysi kompyuter(lar)ga</label>
          {machines.length === 0 ? <div className="hint">Kompyuterlar hali xabar bermagan.</div> : machines.map((m) => (
            <label key={m.name} className="chk"><input type="checkbox" checked={picked.includes(m.name)} onChange={(e) => setPicked((p) => (e.target.checked ? [...p, m.name] : p.filter((x) => x !== m.name)))} />
              <span>{m.name} {m.online ? "● online" : "○ offline"}{m.app.open ? "" : " · ilova yopiq"}</span></label>
          ))}
        </div>
        <div className="hint">Kompyuter ~2 daqiqada (ilova yopiq bo&apos;lsa ham) yuklab oladi va o&apos;rnatadi. Bepul Supabase tarifida bitta fayl 50 MB gacha.</div>
        <button className="btn primary block" disabled={pct !== null || !file || tooBig || !name.trim() || picked.length === 0} onClick={start}>
          {pct !== null ? `Yuklanmoqda… ${pct}%` : "Yuklash"}
        </button>
      </div>

      {uploads.length > 0 ? (
        <div className="card formcard">
          {uploads.map((u) => (
            <div key={u.id} className="logline">
              <b>{u.name}</b> → {u.machine} <span className={STATUS[u.status].cls}>{STATUS[u.status].text}</span>
              {u.status === "downloading" || u.status === "importing" ? ` ${u.progress}%` : ""}
              {u.message ? <div className={u.status === "error" ? "err" : "hint"}>{u.message}</div> : null}
            </div>
          ))}
        </div>
      ) : null}
    </>
  );
}

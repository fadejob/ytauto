"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Check, FileArchive, FileVideo, Film, Monitor, Package, Upload as UploadIcon } from "lucide-react";
import { api, haptic, putFile } from "@/lib/client";
import { machinesSnapshot, reloadMachines } from "@/lib/machines-store";
import type { Machine, PreparedUpload, TemplateUpload as Upload } from "@/lib/types";
import type { Toast } from "./App";
import { Btn, Field, Sheet, Skel } from "./ui";
import st from "./TemplateUpload.module.css";

export const MB = 1048576;
const MAX_MB = 600;

/** Brauzerdan serverga yuklanayotgan fayl (hali kompyuterlarga topshirilmagan) - plitkada «serverga yuklanmoqda». */
export type LocalUpload = { name: string; kind: Upload["kind"]; size: number; machines: number; pct: number };
export type UploadInput = { name: string; kind: Upload["kind"]; file: File; machines: string[] };

/**
 * Shablon yuklashlar holati: kompyuterlar ro'yxati, serverdagi yuklashlar (har 4 soniyada, faol bo'lsa) va hozirgi brauzer yuklashi.
 * Yuklash sheet yopilsa ham davom etadi (holat shu hook'da - sahifada turadi).
 * enabled=false (mehmon) - hech narsa so'ralmaydi.
 */
export function useTemplateUploads(toast: Toast, enabled = true) {
  const [uploads, setUploads] = useState<Upload[] | null>(null);
  const [machines, setMachines] = useState<Machine[] | null>(null);
  const [local, setLocal] = useState<LocalUpload | null>(null);

  const load = useCallback(() => api<Upload[]>("/api/templates").then(setUploads).catch(() => setUploads((u) => u ?? [])), []);

  useEffect(() => {
    if (!enabled) return;
    void reloadMachines().then(() => setMachines(machinesSnapshot() ?? []));
    void load();
  }, [enabled, load]);

  // Faol yuklashlar bor ekan, har 4 soniyada holat yangilanadi.
  const active = !!uploads?.some((u) => u.status === "pending" || u.status === "downloading" || u.status === "importing");
  useEffect(() => {
    if (!enabled || !active) return;
    const t = window.setInterval(() => { if (document.visibilityState === "visible") void load(); }, 4000);
    return () => window.clearInterval(t);
  }, [enabled, active, load]);

  /** Fayl → Supabase Storage → commit (kompyuterlar darhol yuklab olib o'rnatadi). Muvaffaqiyatda true. */
  const start = useCallback(async ({ name, kind, file, machines: picked }: UploadInput): Promise<boolean> => {
    if (!file || !name.trim() || picked.length === 0) return false;
    haptic.tap();
    setLocal({ name: name.trim(), kind, size: file.size, machines: picked.length, pct: 0 });
    try {
      const prep = await api<PreparedUpload>("/api/templates", { action: "prepare", name, kind, filename: file.name, size: file.size });
      if (prep.uploadUrl) await putFile(prep.uploadUrl, file, (p) => setLocal((l) => (l ? { ...l, pct: p } : l)));
      await api("/api/templates", { action: "commit", groupId: prep.groupId, path: prep.path, name, kind, size: file.size, machines: picked });
      haptic.ok();
      toast("Shablon yuborildi. Kompyuter yuklab olib o'rnatadi.");
      await load();
      return true;
    } catch (e) {
      haptic.fail();
      toast(e instanceof Error ? e.message : String(e), true);
      return false;
    } finally {
      setLocal(null);
    }
  }, [load, toast]);

  return { uploads, machines, local, start, reload: load };
}

const KINDS: { key: Upload["kind"]; title: string; sub: string; icon: typeof Film; accept: string }[] = [
  { key: "Mp4Reference", title: "Video namuna", sub: "MP4, MOV, WEBM", icon: Film, accept: ".mp4,.mov,.webm,.mkv,.m4v" },
  { key: "OpenFilmProject", title: "OpenFilm loyihasi", sub: ".zip arxiv", icon: Package, accept: ".zip" },
];

/** «Shablon yuklash» sheet'i (dizayn: Shablonlar va musiqa · Yuklash). Yuklash o'zi useTemplateUploads'da - sheet yopilsa ham davom etadi. */
export default function TemplateUpload({ machines, local, start, onClose }: {
  machines: Machine[] | null;
  local: LocalUpload | null;
  start: (u: UploadInput) => Promise<boolean>;
  onClose: () => void;
}) {
  const [name, setName] = useState("");
  const [kind, setKind] = useState<Upload["kind"]>("Mp4Reference");
  const [file, setFile] = useState<File | null>(null);
  /** null - foydalanuvchi hali tegmagan: hamma kompyuter tanlangan. */
  const [picked, setPicked] = useState<string[] | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const chosen = picked ?? (machines ?? []).map((m) => m.name);
  const accept = KINDS.find((k) => k.key === kind)!.accept;
  const tooBig = !!file && file.size > MAX_MB * MB;
  const ready = !!file && !tooBig && !!name.trim() && chosen.length > 0;

  function pickKind(k: Upload["kind"]) {
    if (k === kind) return;
    setKind(k);
    setFile(null);
    if (fileRef.current) fileRef.current.value = "";
  }

  function toggleMachine(n: string) {
    setPicked(chosen.includes(n) ? chosen.filter((x) => x !== n) : [...chosen, n]);
  }

  async function submit() {
    if (!file || !ready || local) return;
    if (await start({ name, kind, file, machines: chosen })) onClose();
  }

  const FileIc = file && /\.zip$/i.test(file.name) ? FileArchive : FileVideo;

  return (
    <Sheet
      title="Shablon yuklash"
      sub="Kompyuter darhol yuklab olib o'rnatadi"
      onClose={onClose}
      footer={
        <>
          {local ? (
            <div className={st.progress} role="progressbar" aria-label="Serverga yuklanmoqda" aria-valuemin={0} aria-valuemax={100} aria-valuenow={local.pct}>
              <i style={{ width: `${local.pct}%` }} />
            </div>
          ) : null}
          <Btn block icon={UploadIcon} loading={!!local} disabled={!ready} onClick={() => void submit()}>
            {local ? `Yuklanmoqda… ${local.pct}%` : "Yuklash"}
          </Btn>
        </>
      }
    >
      <Field label={<label htmlFor="tpl-name">Nomi</label>}>
        <input id="tpl-name" className="inp" value={name} maxLength={60} placeholder="Masalan: Clean explainer" autoComplete="off" onChange={(e) => setName(e.target.value)} />
      </Field>

      <Field label="Turi">
        <div className={st.kinds} role="radiogroup" aria-label="Shablon turi">
          {KINDS.map((k) => {
            const on = kind === k.key;
            const Ic = k.icon;
            return (
              <button key={k.key} type="button" role="radio" aria-checked={on} className={st.kindTile} onClick={() => pickKind(k.key)}>
                <span className={st.kindTop}><Ic size={22} /><span className={st.tick}>{on ? <Check size={14} strokeWidth={3} /> : null}</span></span>
                <b>{k.title}</b>
                <span className={st.kindSub}>{k.sub}</span>
              </button>
            );
          })}
        </div>
      </Field>

      <Field label="Fayl" right={`${MAX_MB} MB gacha`} error={tooBig ? `Fayl ${MAX_MB} MB dan katta.` : undefined}>
        <input ref={fileRef} className="sr-only" type="file" accept={accept} tabIndex={-1} aria-hidden onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        {file ? (
          <div className={`${st.file}${tooBig ? " " + st.fileBad : ""}`}>
            <span className={st.fileIc}><FileIc size={20} /></span>
            <span className={st.fileT}><b>{file.name}</b><span className="tnum">{(file.size / MB).toFixed(1)} MB</span></span>
            <Btn tone="ghost" size="sm" onClick={() => fileRef.current?.click()}>Almashtirish</Btn>
          </div>
        ) : (
          <button type="button" className={st.pick} onClick={() => fileRef.current?.click()}>
            <span className={st.fileIc}><UploadIcon size={20} /></span>
            <span className={st.fileT}><b>Fayl tanlash</b><span>{kind === "OpenFilmProject" ? "OpenFilm loyihasi (.zip)" : "Video namuna: MP4, MOV, WEBM, MKV"}</span></span>
          </button>
        )}
      </Field>

      <Field label="Qaysi kompyuter(lar)ga" hint="Ilova yopiq bo'lsa ham kompyuter faylni oladi. Bepul Supabase tarifida bitta fayl 50 MB gacha.">
        {machines === null ? (
          <div className={st.machines}><Skel h={60} r={16} /><Skel h={60} r={16} /></div>
        ) : machines.length === 0 ? (
          <div className="hint">Kompyuterlar hali xabar bermagan.</div>
        ) : (
          <div className={st.machines}>
            {machines.map((m) => {
              const on = chosen.includes(m.name);
              return (
                <button key={m.name} type="button" role="checkbox" aria-checked={on} className={`${st.machine}${m.online ? " " + st.online : ""}`} onClick={() => toggleMachine(m.name)}>
                  <Monitor size={20} />
                  <span className={st.mT}>
                    <b>{m.name}</b>
                    <span className={st.state}><i className={st.dot} />{m.online ? "online" : "offline"}{m.app.open ? "" : " · ilova yopiq"}</span>
                  </span>
                  <span className={st.tick}>{on ? <Check size={14} strokeWidth={3} /> : null}</span>
                </button>
              );
            })}
          </div>
        )}
      </Field>
    </Sheet>
  );
}

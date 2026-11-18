"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api, confirmBox, haptic, putFile } from "@/lib/client";
import type { VoiceClone as VC } from "@/lib/types";
import type { Toast } from "./App";

type State = (VC & { listenUrl: string | null }) | null;
const MIN_SEC = 5, MAX_SEC = 60;

/** Fayl davomiyligi (brauzerda o'qiladi): yozib olingan webm'da Infinity bo'lishi mumkin - shunda yozish vaqti ishlatiladi. */
function audioDuration(file: Blob): Promise<number> {
  return new Promise((resolve) => {
    const a = document.createElement("audio");
    const url = URL.createObjectURL(file);
    a.preload = "metadata";
    a.onloadedmetadata = () => { URL.revokeObjectURL(url); resolve(a.duration); };
    a.onerror = () => { URL.revokeObjectURL(url); resolve(NaN); };
    a.src = url;
  });
}

const STATUS: Record<VC["status"], { text: string; cls: string }> = {
  pending: { text: "kompyuterga qo'shilmoqda", cls: "chip warn" },
  ready: { text: "tayyor — videolar shu ovozda", cls: "chip ok" },
  error: { text: "xato", cls: "chip bad" },
};

/**
 * «Mening ovozim»: hisobning ovoz namunasi. Model kompyuterda bitta (MOSS-TTS-Nano), har foydalanuvchi o'z namunasini beradi —
 * uning videolari shu ovozda o'qiladi. Namuna hisob papkasida saqlanadi, boshqa foydalanuvchiga ko'rinmaydi.
 */
export default function VoiceClone({ toast, onChanged }: { toast: Toast; onChanged?: () => void }) {
  const [st, setSt] = useState<State | undefined>(undefined);
  const [file, setFile] = useState<{ blob: Blob; name: string; sec: number } | null>(null);
  const [consent, setConsent] = useState(false);
  const [makeDefault, setMakeDefault] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [rec, setRec] = useState<{ r: MediaRecorder; started: number } | null>(null);
  const [recSec, setRecSec] = useState(0);
  const chunks = useRef<Blob[]>([]);
  const canRecord = typeof window !== "undefined" && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== "undefined";
  // Tanlangan/yozilgan faylni tinglash uchun bitta havola (har renderda yangisi yaratilmaydi, almashganda bo'shatiladi).
  const previewUrl = useMemo(() => (file ? URL.createObjectURL(file.blob) : null), [file]);
  useEffect(() => () => { if (previewUrl) URL.revokeObjectURL(previewUrl); }, [previewUrl]);

  const load = useCallback(async () => {
    try { setSt(await api<State>("/api/voice-clone")); } catch (e) { toast(e instanceof Error ? e.message : String(e), true); setSt(null); }
  }, [toast]);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- mount'da bir marta serverdan yuklanadi
  useEffect(() => { void load(); }, [load]);

  // Kompyuter namunani qo'shayotganda holat 15 s da yangilanadi.
  useEffect(() => {
    if (st?.status !== "pending") return;
    const t = window.setInterval(() => void load(), 15000);
    return () => window.clearInterval(t);
  }, [st?.status, load]);

  useEffect(() => {
    if (!rec) return;
    const t = window.setInterval(() => {
      const s = (Date.now() - rec.started) / 1000;
      setRecSec(s);
      if (s >= MAX_SEC) rec.r.stop();
    }, 250);
    return () => window.clearInterval(t);
  }, [rec]);

  async function pick(f: File | undefined) {
    if (!f) return;
    const sec = await audioDuration(f);
    if (Number.isFinite(sec) && (sec < MIN_SEC || sec > MAX_SEC)) { toast(`Namuna ${MIN_SEC}–${MAX_SEC} soniya bo'lsin (hozir ${Math.round(sec)} s).`, true); return; }
    setFile({ blob: f, name: f.name, sec: Number.isFinite(sec) ? sec : 0 });
  }

  async function startRec() {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: true, autoGainControl: true } });
      const type = ["audio/webm;codecs=opus", "audio/ogg;codecs=opus", "audio/mp4"].find((t) => MediaRecorder.isTypeSupported(t)) ?? "";
      const r = new MediaRecorder(stream, type ? { mimeType: type } : undefined);
      chunks.current = [];
      const started = Date.now();
      r.ondataavailable = (e) => { if (e.data.size) chunks.current.push(e.data); };
      r.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        const sec = (Date.now() - started) / 1000;
        setRec(null);
        if (sec < MIN_SEC) { toast(`Juda qisqa (${Math.round(sec)} s). Kamida ${MIN_SEC} soniya, eng yaxshisi 15–30 s gapiring.`, true); return; }
        const mime = r.mimeType || "audio/webm";
        const ext = mime.includes("ogg") ? ".ogg" : mime.includes("mp4") ? ".m4a" : ".webm";
        setFile({ blob: new Blob(chunks.current, { type: mime }), name: `yozuv${ext}`, sec: Math.min(sec, MAX_SEC) });
      };
      r.start(500);
      setRecSec(0);
      setRec({ r, started });
      haptic.tap();
    } catch (e) {
      toast(`Mikrofon ochilmadi: ${e instanceof Error ? e.message : String(e)}. Telefonda yozib, faylni yuklang.`, true);
    }
  }

  async function save() {
    if (!file) return;
    if (!consent) { toast("Rozilikni belgilang.", true); return; }
    setBusy("save");
    try {
      const prep = await api<{ path: string; uploadUrl: string }>("/api/voice-clone", { action: "prepare", filename: file.name, size: file.blob.size });
      if (prep.uploadUrl) await putFile(prep.uploadUrl, new File([file.blob], file.name, { type: file.blob.type }), () => {});
      setSt(await api<State>("/api/voice-clone", { action: "save", path: prep.path, durationSec: file.sec || 20, consent, makeDefault }));
      setFile(null); setConsent(false);
      haptic.ok();
      toast("Namuna saqlandi. Kompyuter uni VoiceStudio'ga qo'shgach, videolaringiz shu ovozda o'qiladi.");
      onChanged?.();
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  async function remove() {
    if (!(await confirmBox("Ovoz namunasi o'chirilsinmi? Yangi videolar standart ovozda o'qiladi. Namuna bulutdan darhol, kompyuterdan (VoiceStudio) keyingi tekshiruvda o'chiriladi."))) return;
    setBusy("delete");
    try { await api("/api/voice-clone", { action: "delete" }); setSt(null); toast("Namuna o'chirildi."); onChanged?.(); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  return (
    <>
      <div className="sect">Mening ovozim</div>
      <div className="card formcard">
        <div className="hint">
          Videolaringiz sizning ovozingizda o&apos;qilishi uchun 10–30 soniyalik toza yozuv yuklang. Model kompyuterda bitta (MOSS-TTS-Nano),
          har foydalanuvchi o&apos;z namunasini beradi — o&apos;qitish kerak emas. Namuna faqat sizning hisobingizda saqlanadi.
        </div>
        {st === undefined ? <div className="skeleton" style={{ height: 48 }} /> : null}

        {st ? (
          <div className="ctx">
            <div className="meta">
              <span className={STATUS[st.status].cls}>{STATUS[st.status].text}</span>
              <span>{st.durationSec} s</span>
              <span>profil: {st.profileName}</span>
            </div>
            {st.message && st.status !== "ready" ? <span className={st.status === "error" ? "logline error" : "hint"}>{st.message}</span> : null}
            {st.listenUrl ? <audio controls preload="none" src={st.listenUrl} style={{ width: "100%" }} /> : null}
            <span className="hint">Yangi mavzularda «Mening ovozim» profilini tanlang (yoki standart ovoz qilib qo&apos;ying). Eskisini almashtirish uchun pastdan yangi namuna yuklang.</span>
          </div>
        ) : null}

        <div className="grid2">
          <label className="btn block" style={{ display: "grid", placeItems: "center" }}>
            Fayl tanlash
            <input type="file" accept="audio/*" hidden onChange={(e) => void pick(e.target.files?.[0])} />
          </label>
          {canRecord ? (
            rec ? <button className="btn danger block" onClick={() => rec.r.stop()}>■ To&apos;xtatish ({Math.round(recSec)} s)</button>
              : <button className="btn block" onClick={startRec}>● Yozib olish</button>
          ) : <button className="btn block" disabled>Yozib olish yo&apos;q</button>}
        </div>
        {rec ? <div className="hint pulse">Yozilmoqda… Inglizcha, tabiiy ohangda, tinch xonada gapiring (15–30 s).</div> : null}

        {file ? (
          <div className="ctx">
            <span><b>Tanlangan:</b> {file.name}{file.sec ? ` · ${Math.round(file.sec)} s` : ""}</span>
            {previewUrl ? <audio controls src={previewUrl} style={{ width: "100%" }} /> : null}
            <label className="chk"><input type="checkbox" checked={consent} onChange={(e) => setConsent(e.target.checked)} /><span>Bu mening ovozim yoki egasi uni ishlatishga ruxsat bergan.</span></label>
            <label className="chk"><input type="checkbox" checked={makeDefault} onChange={(e) => setMakeDefault(e.target.checked)} /><span>Yangi mavzular uchun standart ovoz qilish</span></label>
            <button className="btn primary block" disabled={!consent || busy === "save"} onClick={save}>{busy === "save" ? "Yuklanmoqda…" : "Namunani saqlash"}</button>
          </div>
        ) : null}

        {st ? <button className="btn danger block" disabled={busy === "delete"} onClick={remove}>Namunani o&apos;chirish</button> : null}
        <div className="hint">Maslahat: shovqinsiz xona, musiqasiz, bir tekis ohang. Videolar inglizcha — namunani ham inglizcha gapirsangiz talaffuz tabiiyroq chiqadi.</div>
      </div>
    </>
  );
}

"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api, confirmBox, haptic, putFile } from "@/lib/client";
import type { VoiceClone as VC } from "@/lib/types";
import { VOICE_PASSAGES } from "@/lib/voice-passages";
import type { Toast } from "./App";

type State = (VC & { listenUrl: string | null }) | null;
/** Matn ~15–20 s o'qiladi: juda qisqa yozuv - o'qib tugatilmagan, juda uzun - ortiqcha sukut/shovqin. */
const MIN_SEC = 10, MAX_SEC = 40;

const STATUS: Record<VC["status"], { text: string; cls: string }> = {
  pending: { text: "kompyuterga qo'shilmoqda", cls: "chip warn" },
  ready: { text: "tayyor — videolar shu ovozda", cls: "chip ok" },
  error: { text: "xato", cls: "chip bad" },
};

/**
 * «Mening ovozim»: foydalanuvchi ekrandagi oson inglizcha matnni (15–20 s) ovoz chiqarib o'qiydi, bot o'zi yozib oladi va yuboradi.
 * Model kompyuterda bitta (MOSS-TTS-Nano); har foydalanuvchining videolari o'z namunasi bilan o'qiladi. O'qilgan matn ham saqlanadi —
 * model namunada nima aytilganini bilsa ovozni aniqroq ko'chiradi.
 */
export default function VoiceClone({ toast, onChanged }: { toast: Toast; onChanged?: () => void }) {
  const [st, setSt] = useState<State | undefined>(undefined);
  const [passage, setPassage] = useState(0);
  const [take, setTake] = useState<{ blob: Blob; name: string; sec: number; passage: number } | null>(null);
  const [consent, setConsent] = useState(false);
  const [makeDefault, setMakeDefault] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [rec, setRec] = useState<{ r: MediaRecorder; started: number } | null>(null);
  const [recSec, setRecSec] = useState(0);
  const [level, setLevel] = useState(0);
  const chunks = useRef<Blob[]>([]);
  const meter = useRef<{ ctx: AudioContext; raf: number } | null>(null);
  const canRecord = typeof window !== "undefined" && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== "undefined";
  const previewUrl = useMemo(() => (take ? URL.createObjectURL(take.blob) : null), [take]);
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

  // Yozish vaqti va avtomatik to'xtash.
  useEffect(() => {
    if (!rec) return;
    const t = window.setInterval(() => {
      const s = (Date.now() - rec.started) / 1000;
      setRecSec(s);
      if (s >= MAX_SEC && rec.r.state === "recording") rec.r.stop();
    }, 200);
    return () => window.clearInterval(t);
  }, [rec]);

  function stopMeter() {
    if (!meter.current) return;
    cancelAnimationFrame(meter.current.raf);
    void meter.current.ctx.close().catch(() => {});
    meter.current = null;
    setLevel(0);
  }

  /** Ovoz balandligi ko'rsatkichi: foydalanuvchi mikrofon ishlayotganini va juda past gapirmayotganini ko'radi. */
  function startMeter(stream: MediaStream) {
    try {
      const ctx = new AudioContext();
      const an = ctx.createAnalyser(); an.fftSize = 1024;
      ctx.createMediaStreamSource(stream).connect(an);
      const data = new Uint8Array(an.fftSize);
      const tick = () => {
        an.getByteTimeDomainData(data);
        let peak = 0; for (const v of data) peak = Math.max(peak, Math.abs(v - 128));
        setLevel(Math.min(1, peak / 90));
        if (meter.current) meter.current.raf = requestAnimationFrame(tick);
      };
      meter.current = { ctx, raf: requestAnimationFrame(tick) };
    } catch { /* ko'rsatkichsiz ham yoziladi */ }
  }

  async function startRec() {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: true, autoGainControl: true } });
      const type = ["audio/webm;codecs=opus", "audio/ogg;codecs=opus", "audio/mp4"].find((t) => MediaRecorder.isTypeSupported(t)) ?? "";
      const r = new MediaRecorder(stream, type ? { mimeType: type } : undefined);
      chunks.current = [];
      const started = Date.now();
      const p = passage;
      r.ondataavailable = (e) => { if (e.data.size) chunks.current.push(e.data); };
      r.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        stopMeter();
        const sec = (Date.now() - started) / 1000;
        setRec(null);
        if (sec < MIN_SEC) { haptic.fail(); toast(`Juda qisqa (${Math.round(sec)} s). Matnni oxirigacha o'qing — odatda 15–20 soniya.`, true); return; }
        const mime = r.mimeType || "audio/webm";
        const ext = mime.includes("ogg") ? ".ogg" : mime.includes("mp4") ? ".m4a" : ".webm";
        setTake({ blob: new Blob(chunks.current, { type: mime }), name: `ovoz${ext}`, sec: Math.min(sec, MAX_SEC), passage: p });
        haptic.ok();
      };
      r.start(500);
      startMeter(stream);
      setTake(null); setRecSec(0);
      setRec({ r, started });
      haptic.tap();
    } catch (e) {
      const denied = e instanceof DOMException && (e.name === "NotAllowedError" || e.name === "SecurityError");
      toast(denied ? "Mikrofonga ruxsat berilmadi. Telegram (telefon sozlamalari) → Mikrofon ruxsatini yoqing va qayta urining."
        : `Mikrofon ochilmadi: ${e instanceof Error ? e.message : String(e)}`, true);
    }
  }

  async function send() {
    if (!take) return;
    if (!consent) { toast("Rozilikni belgilang.", true); return; }
    setBusy("save");
    try {
      const prep = await api<{ path: string; uploadUrl: string }>("/api/voice-clone", { action: "prepare", filename: take.name, size: take.blob.size });
      if (prep.uploadUrl) await putFile(prep.uploadUrl, new File([take.blob], take.name, { type: take.blob.type }), () => {});
      setSt(await api<State>("/api/voice-clone", { action: "save", path: prep.path, durationSec: take.sec, passage: take.passage, consent, makeDefault }));
      setTake(null); setConsent(false);
      haptic.ok();
      toast("Ovozingiz yuborildi. Kompyuter uni qo'shgach, videolaringiz shu ovozda o'qiladi.");
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

  const recording = !!rec;
  const nextPassage = () => { haptic.tap(); setTake(null); setPassage((p) => (p + 1) % VOICE_PASSAGES.length); };

  return (
    <>
      <div className="sect">Mening ovozim</div>
      <div className="card formcard">
        <div className="hint">
          Videolaringiz sizning ovozingizda o&apos;qilishi uchun pastdagi matnni ovoz chiqarib o&apos;qing — bot o&apos;zi yozib oladi (15–20 soniya).
          Ovoz faqat sizning hisobingizda saqlanadi.
        </div>
        {st === undefined ? <div className="skeleton" style={{ height: 48 }} /> : null}

        {st ? (
          <div className="ctx">
            <div className="meta">
              <span className={STATUS[st.status].cls}>{STATUS[st.status].text}</span>
              <span>{st.durationSec} s</span>
            </div>
            {st.message && st.status !== "ready" ? <span className={st.status === "error" ? "logline error" : "hint"}>{st.message}</span> : null}
            {st.listenUrl ? <audio controls preload="none" src={st.listenUrl} style={{ width: "100%" }} /> : null}
            <span className="hint">Qayta yozish uchun matnni yana o&apos;qing — yangi ovoz eskisining o&apos;rnini egallaydi.</span>
          </div>
        ) : null}

        {/* O'qiladigan matn */}
        <div className="pre" style={{ fontFamily: "inherit", fontSize: 17, lineHeight: 1.55, maxHeight: "none", padding: 14, outline: recording ? "2px solid var(--danger)" : undefined }}>
          {VOICE_PASSAGES[passage]}
        </div>
        {!recording ? <button className="btn ghost" onClick={nextPassage}>Boshqa matn</button> : null}

        {canRecord ? (
          recording ? (
            <>
              <div style={{ height: 8, borderRadius: 4, background: "color-mix(in srgb, var(--hint) 20%, transparent)", overflow: "hidden" }}>
                <div style={{ width: `${Math.round(level * 100)}%`, height: "100%", background: level > 0.85 ? "var(--danger)" : "var(--ok)", transition: "width 80ms linear" }} />
              </div>
              <button className="btn danger block" onClick={() => rec!.r.stop()}>■ Tugatdim ({Math.round(recSec)} s)</button>
              <div className="hint pulse">Yozilmoqda… Matnni oxirigacha, tabiiy ohangda o&apos;qing. {MAX_SEC} soniyada o&apos;zi to&apos;xtaydi.</div>
            </>
          ) : (
            <button className="btn primary block" disabled={busy === "save"} onClick={startRec}>● {take ? "Qayta yozish" : "Yozishni boshlash"}</button>
          )
        ) : (
          <div className="logline error">Bu qurilmada mikrofon bilan yozib bo&apos;lmaydi. Telegram&apos;ni yangilang yoki boshqa telefondan oching.</div>
        )}

        {take && !recording ? (
          <div className="ctx">
            <span><b>Yozuv:</b> {Math.round(take.sec)} s — tinglab ko&apos;ring</span>
            {previewUrl ? <audio controls src={previewUrl} style={{ width: "100%" }} /> : null}
            <label className="chk"><input type="checkbox" checked={consent} onChange={(e) => setConsent(e.target.checked)} /><span>Bu mening ovozim va uni videolarim uchun ishlatishga roziman.</span></label>
            <label className="chk"><input type="checkbox" checked={makeDefault} onChange={(e) => setMakeDefault(e.target.checked)} /><span>Yangi mavzular uchun standart ovoz qilish</span></label>
            <button className="btn primary block" disabled={!consent || busy === "save"} onClick={send}>{busy === "save" ? "Yuborilmoqda…" : "Ovozni yuborish"}</button>
          </div>
        ) : null}

        {st ? <button className="btn danger block" disabled={busy === "delete" || recording} onClick={remove}>Namunani o&apos;chirish</button> : null}
        <div className="hint">Maslahat: tinch xona, telefonni og&apos;izdan ~20 sm uzoqda tuting, shoshmasdan va bir tekis o&apos;qing.</div>
      </div>
    </>
  );
}

"use client";

/**
 * «Mening ovozim» (dizayn: Ovoz · yozish vecihh, Ovoz · yuborish wGmITJ).
 * Foydalanuvchi ekrandagi oson inglizcha matnni (15–20 s) ovoz chiqarib o'qiydi, bot o'zi yozib oladi va yuboradi.
 * Model kompyuterda bitta (MOSS-TTS-Nano); har foydalanuvchining videolari o'z namunasi bilan o'qiladi. O'qilgan matn ham saqlanadi —
 * model namunada nima aytilganini bilsa ovozni aniqroq ko'chiradi.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AudioLines, ChevronRight, Mic, Pause, Play, RefreshCw, Square, Trash2, Upload, VolumeX, X } from "lucide-react";
import { api, confirmBox, haptic, putFile } from "@/lib/client";
import type { VoiceClone as VC } from "@/lib/types";
import { VOICE_PASSAGES } from "@/lib/voice-passages";
import type { Toast } from "./App";
import { Alert, Btn, PageHead, Skel, SkelCard, Status, ToggleRow, useBusy } from "./ui";
import s from "./VoiceClone.module.css";

export type CloneState = (VC & { listenUrl: string | null }) | null;
/** Matn ~15–20 s o'qiladi: juda qisqa yozuv - o'qib tugatilmagan, juda uzun - ortiqcha sukut/shovqin. */
const MIN_SEC = 10, MAX_SEC = 40;

/** Namuna holati: to'liq matn (namuna kartasida) va qisqa so'z (plitkada). */
export const CLONE_STATUS: Record<VC["status"], { text: string; short: string; tone: "accent" | "ok" | "bad" }> = {
  pending: { text: "kompyuterga qo'shilmoqda", short: "qo'shilmoqda", tone: "accent" },
  ready: { text: "tayyor — videolar shu ovozda", short: "tayyor", tone: "ok" },
  error: { text: "xato", short: "xato", tone: "bad" },
};

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));
const clock = (sec: number) => {
  const t = Math.max(0, Math.floor(Number.isFinite(sec) ? sec : 0));
  return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, "0")}`;
};

/**
 * Namuna holati (/api/voice-clone): Ovoz sahifasi (plitka, kirish kartasi) va «Mening ovozim» ekrani shu bitta manbadan o'qiydi.
 * undefined - yuklanmoqda, null - namuna yo'q. Kompyuter namunani qo'shayotganda holat 15 s da yangilanadi.
 */
export function useVoiceClone(toast: Toast) {
  const [st, setSt] = useState<CloneState | undefined>(undefined);
  const load = useCallback(async () => {
    try { setSt(await api<CloneState>("/api/voice-clone")); } catch (e) { toast(errText(e), true); setSt((x) => (x === undefined ? null : x)); }
  }, [toast]);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- mount'da bir marta serverdan yuklanadi
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (st?.status !== "pending") return;
    const t = window.setInterval(() => void load(), 15000);
    return () => window.clearInterval(t);
  }, [st?.status, load]);
  return { st, setSt, load };
}
export type VoiceCloneData = ReturnType<typeof useVoiceClone>;

// ------------------------------------------------------------------ pleyer (yozuv va joriy namuna)
/** Bezak to'lqini: ustunlar balandligi o'zgarmas naqsh (yozuvning haqiqiy shakli emas) - faqat tinglash joyini ko'rsatadi. */
const WAVE = [0.35, 0.6, 0.85, 0.5, 0.3, 0.7, 1, 0.55, 0.4, 0.8, 0.95, 0.45, 0.65, 0.9, 0.5, 0.35, 0.75, 0.6, 0.3, 0.55, 0.85, 0.45, 0.7, 0.4];

/** Ixcham audio pleyer: o'ynash/pauza, to'lqin bo'ylab joy (sudrab o'tkazish mumkin), vaqt. MediaRecorder yozuvlarida
 * davomiylik ko'pincha Infinity bo'ladi - shuning uchun ma'lum davomiylik (durationSec) ham beriladi. */
export function AudioPlayer({ src, durationSec, preload = "metadata", label = "Yozuv" }: { src: string; durationSec: number; preload?: "none" | "metadata"; label?: string }) {
  const ref = useRef<HTMLAudioElement>(null);
  const [playing, setPlaying] = useState(false);
  const [pos, setPos] = useState(0);
  const [dur, setDur] = useState(durationSec);
  const total = Number.isFinite(dur) && dur > 0 ? dur : Math.max(durationSec, 1);
  const p = Math.min(1, pos / total);

  function toggle() {
    const a = ref.current;
    if (!a) return;
    if (a.paused) void a.play().catch(() => setPlaying(false));
    else a.pause();
  }
  function seek(v: number) {
    const a = ref.current;
    if (!a) return;
    try { a.currentTime = v; setPos(v); } catch { /* hali yuklanmagan */ }
  }

  return (
    <div className={s.player}>
      <audio ref={ref} src={src} preload={preload}
        onPlay={() => setPlaying(true)} onPause={() => setPlaying(false)} onEnded={() => { setPlaying(false); setPos(0); }}
        onTimeUpdate={(e) => setPos(e.currentTarget.currentTime)}
        onLoadedMetadata={(e) => { const d = e.currentTarget.duration; if (Number.isFinite(d) && d > 0) setDur(d); }} />
      <button type="button" className={s.playBtn} aria-label={playing ? "Pauza" : "Tinglash"} onClick={toggle}>
        {playing ? <Pause size={18} /> : <Play size={18} />}
      </button>
      <div className={s.wave}>
        {WAVE.map((h, i) => <i key={i} className={(i + 0.5) / WAVE.length <= p ? s.on : undefined} style={{ height: `${Math.round(h * 100)}%` }} />)}
        <input type="range" className={s.seek} min={0} max={total} step={0.1} value={Math.min(pos, total)} aria-label={`${label}: tinglash joyi`}
          onChange={(e) => seek(Number(e.target.value))} />
      </div>
      <span className={`${s.time} tnum`}>{clock(pos)} / {clock(total)}</span>
    </div>
  );
}

// ------------------------------------------------------------------ Ovoz sahifasidagi kirish kartasi
/** Ovoz sahifasida: namuna holati va «Mening ovozim» ekranini ochish. */
export function VoiceCloneEntry({ st, onOpen }: { st: CloneState | undefined; onOpen: () => void }) {
  if (st === undefined) return <SkelCard lines={1} />;
  const status = st ? CLONE_STATUS[st.status] : null;
  return (
    <button type="button" className={`card ${s.entry}`} onClick={onOpen}>
      <span className={s.iconTile}><Mic size={20} /></span>
      <span className={s.entryText}>
        <b>Mening ovozim</b>
        <span>{st ? `Namuna · ${st.durationSec} s` : "Videolaringiz sizning ovozingizda o'qilsin — 15–20 soniyalik yozuv"}</span>
      </span>
      {status ? <Status tone={status.tone}>{status.short}</Status> : <span className={s.entryCta}>Yozish</span>}
      <ChevronRight size={18} className={s.chev} />
    </button>
  );
}

// ------------------------------------------------------------------ «Mening ovozim» ekrani
export function VoiceCloneScreen({ clone, toast, back, onChanged }: { clone: VoiceCloneData; toast: Toast; back: () => void; onChanged?: () => void }) {
  const { st, setSt } = clone;
  const busy = useBusy((m) => { haptic.fail(); toast(m, true); });
  const [passage, setPassage] = useState(0);
  const [take, setTake] = useState<{ blob: Blob; name: string; sec: number; passage: number } | null>(null);
  const [consent, setConsent] = useState(false);
  const [makeDefault, setMakeDefault] = useState(true);
  const [progress, setProgress] = useState<number | null>(null);
  const [rec, setRec] = useState<{ r: MediaRecorder; started: number } | null>(null);
  const [recSec, setRecSec] = useState(0);
  const [level, setLevel] = useState(0);
  const chunks = useRef<Blob[]>([]);
  const meter = useRef<{ ctx: AudioContext; raf: number } | null>(null);
  const live = useRef<{ r: MediaRecorder; stream: MediaStream } | null>(null);
  const mounted = useRef(true);
  const canRecord = typeof window !== "undefined" && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== "undefined";
  const previewUrl = useMemo(() => (take ? URL.createObjectURL(take.blob) : null), [take]);
  useEffect(() => () => { if (previewUrl) URL.revokeObjectURL(previewUrl); }, [previewUrl]);

  // Ekrandan chiqilsa (orqaga, boshqa tab) yozish to'xtatiladi va mikrofon yopiladi.
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      const l = live.current;
      if (l) { try { if (l.r.state === "recording") l.r.stop(); } catch { /* allaqachon to'xtagan */ } l.stream.getTracks().forEach((t) => t.stop()); }
      if (meter.current) { cancelAnimationFrame(meter.current.raf); void meter.current.ctx.close().catch(() => {}); meter.current = null; }
    };
  }, []);

  // Yozish vaqti va avtomatik to'xtash.
  useEffect(() => {
    if (!rec) return;
    const t = window.setInterval(() => {
      const sec = (Date.now() - rec.started) / 1000;
      setRecSec(sec);
      if (sec >= MAX_SEC && rec.r.state === "recording") rec.r.stop();
    }, 200);
    return () => window.clearInterval(t);
  }, [rec]);

  function stopMeter() {
    if (!meter.current) return;
    cancelAnimationFrame(meter.current.raf);
    void meter.current.ctx.close().catch(() => {});
    meter.current = null;
    if (mounted.current) setLevel(0);
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
      if (!mounted.current) { stream.getTracks().forEach((t) => t.stop()); return; }
      const type = ["audio/webm;codecs=opus", "audio/ogg;codecs=opus", "audio/mp4"].find((t) => MediaRecorder.isTypeSupported(t)) ?? "";
      const r = new MediaRecorder(stream, type ? { mimeType: type } : undefined);
      chunks.current = [];
      const started = Date.now();
      const p = passage;
      r.ondataavailable = (e) => { if (e.data.size) chunks.current.push(e.data); };
      r.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        live.current = null;
        stopMeter();
        if (!mounted.current) return;
        const sec = (Date.now() - started) / 1000;
        setRec(null);
        if (sec < MIN_SEC) { haptic.fail(); toast(`Juda qisqa (${Math.round(sec)} s). Matnni oxirigacha o'qing — odatda 15–20 soniya.`, true); return; }
        const mime = r.mimeType || "audio/webm";
        const ext = mime.includes("ogg") ? ".ogg" : mime.includes("mp4") ? ".m4a" : ".webm";
        setTake({ blob: new Blob(chunks.current, { type: mime }), name: `ovoz${ext}`, sec: Math.min(sec, MAX_SEC), passage: p });
        haptic.ok();
      };
      r.start(500);
      live.current = { r, stream };
      startMeter(stream);
      setTake(null); setRecSec(0);
      setRec({ r, started });
      haptic.tap();
    } catch (e) {
      const denied = e instanceof DOMException && (e.name === "NotAllowedError" || e.name === "SecurityError");
      toast(denied ? "Mikrofonga ruxsat berilmadi. Telegram (telefon sozlamalari) → Mikrofon ruxsatini yoqing va qayta urining."
        : `Mikrofon ochilmadi: ${errText(e)}`, true);
    }
  }

  async function send() {
    if (!take) return;
    if (!consent) { toast("Rozilikni belgilang.", true); return; }
    const t = take;
    const ok = await busy.run("save", async () => {
      try {
        const prep = await api<{ path: string; uploadUrl: string }>("/api/voice-clone", { action: "prepare", filename: t.name, size: t.blob.size });
        if (prep.uploadUrl) { setProgress(0); await putFile(prep.uploadUrl, new File([t.blob], t.name, { type: t.blob.type }), setProgress); }
        return await api<CloneState>("/api/voice-clone", { action: "save", path: prep.path, durationSec: t.sec, passage: t.passage, consent, makeDefault });
      } finally { setProgress(null); }
    });
    if (ok === false) return;
    setSt(ok);
    setTake(null); setConsent(false);
    haptic.ok();
    toast("Ovozingiz yuborildi. Kompyuter uni qo'shgach, videolaringiz shu ovozda o'qiladi.");
    onChanged?.();
  }

  async function remove() {
    if (!(await confirmBox("Ovoz namunasi o'chirilsinmi? Yangi videolar standart ovozda o'qiladi. Namuna bulutdan darhol, kompyuterdan (VoiceStudio) keyingi tekshiruvda o'chiriladi."))) return;
    const ok = await busy.run("delete", () => api("/api/voice-clone", { action: "delete" }));
    if (ok === false) return;
    setSt(null);
    toast("Namuna o'chirildi.");
    onChanged?.();
  }

  const recording = !!rec;
  const nextPassage = () => { haptic.tap(); setTake(null); setPassage((p) => (p + 1) % VOICE_PASSAGES.length); };
  const status = st ? CLONE_STATUS[st.status] : null;
  const sub = recording ? "Matnni ovoz chiqarib o'qing — bot o'zi yozib oladi"
    : take ? "Yozuvni tinglab ko'ring va yuboring"
    : "Videolaringiz sizning ovozingizda o'qilishi uchun matnni ovoz chiqarib o'qing — bot o'zi yozib oladi (15–20 soniya)";

  return (
    <div className="page narrow">
      <PageHead back={{ label: "Ovoz", onClick: back }} title="Mening ovozim" sub={sub} />

      {/* Joriy namuna: holat, xabar, tinglash, o'chirish */}
      {st === undefined ? (
        <div className="card" aria-busy="true" aria-label="Yuklanmoqda">
          <div className={s.head}><Skel h={40} w={40} r={10} /><div style={{ flex: 1, display: "grid", gap: 6 }}><Skel h={16} w="60%" /><Skel h={12} w="35%" /></div></div>
        </div>
      ) : st && status ? (
        <section className="card" aria-label="Joriy namuna">
          <div className={s.head}>
            <span className={s.iconTile}><AudioLines size={20} /></span>
            <div className={s.nameCol}>
              <b>Joriy namuna · {st.durationSec} s</b>
              <Status tone={status.tone}>{status.text}</Status>
            </div>
            <Btn tone="ghost" size="sm" icon={Trash2} loading={busy.is("delete")} disabled={recording || busy.is("save")} onClick={remove}>O&apos;chirish</Btn>
          </div>
          {st.message && st.status !== "ready" ? <div className={st.status === "error" ? s.err : "hint"}>{st.message}</div> : null}
          {st.listenUrl ? <AudioPlayer key={st.listenUrl} src={st.listenUrl} durationSec={st.durationSec} preload="none" label="Joriy namuna" /> : null}
          {!take && !recording ? <div className="hint">Qayta yozish uchun matnni yana o&apos;qing — yangi ovoz eskisining o&apos;rnini egallaydi.</div> : null}
        </section>
      ) : null}

      {!canRecord ? (
        <Alert>Bu qurilmada mikrofon bilan yozib bo&apos;lmaydi. Telegram&apos;ni yangilang yoki boshqa telefondan oching.</Alert>
      ) : recording ? (
        /* Yozilmoqda */
        <section className="card" aria-live="polite">
          <div className={s.recHead}>
            <span className={s.recLabel}><i className={s.recDot} />Yozilmoqda</span>
            <span className="sub">{MAX_SEC} s da o&apos;zi to&apos;xtaydi</span>
          </div>
          <div className={s.timerBlock}>
            <p className="focal tnum" aria-label={`${Math.round(recSec)} soniya`}>{clock(recSec)}</p>
            <div className={s.level}>
              <Mic size={16} />
              <div className={s.meter} role="meter" aria-label="Ovoz balandligi" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(level * 100)}>
                <i className={level > 0.85 ? s.loud : undefined} style={{ width: `${Math.round(level * 100)}%` }} />
              </div>
            </div>
          </div>
          <div className={`${s.passage} ${s.live}`}>{VOICE_PASSAGES[passage]}</div>
          <div className={s.actions}>
            <Btn tone="danger" block icon={Square} onClick={() => rec!.r.stop()}>Tugatdim</Btn>
            <div className="hint">Matnni oxirigacha, tabiiy ohangda o&apos;qing</div>
          </div>
        </section>
      ) : take ? (
        /* Yangi yozuv: tinglash, rozilik, yuborish */
        <section className={`card ${s.takeCard}`} aria-label="Yangi yozuv">
          <div className={s.takeHead}>
            <span className={s.takeLabel}>Yangi yozuv · {Math.round(take.sec)} s</span>
            <div className={s.takeActions}>
              <Btn tone="ghost" size="sm" icon={Mic} disabled={busy.is("save")} onClick={startRec}>Qayta yozish</Btn>
              <button type="button" className={s.iconBtn} aria-label="Yozuvni bekor qilish" disabled={busy.is("save")} onClick={() => { haptic.tap(); setTake(null); }}><X size={18} /></button>
            </div>
          </div>
          {previewUrl ? <AudioPlayer key={previewUrl} src={previewUrl} durationSec={take.sec} label="Yangi yozuv" /> : null}
          <div className={s.toggles}>
            <ToggleRow label="Bu mening ovozim va uni videolarim uchun ishlatishga roziman" title={<span className={s.toggleText}>Bu mening ovozim va uni videolarim uchun ishlatishga roziman</span>} on={consent} onChange={setConsent} disabled={busy.is("save")} />
            <ToggleRow label="Yangi mavzular uchun standart ovoz qilish" title={<span className={s.toggleText}>Yangi mavzular uchun standart ovoz qilish</span>} on={makeDefault} onChange={setMakeDefault} disabled={busy.is("save")} />
          </div>
          <Btn block icon={Upload} loading={busy.is("save")} disabled={!consent} onClick={send}>
            {busy.is("save") ? (progress !== null && progress < 100 ? `Yuborilmoqda… ${progress}%` : "Yuborilmoqda…") : "Ovozni yuborish"}
          </Btn>
          {!consent ? <div className="hint">Yuborish uchun rozilikni yoqing.</div> : null}
        </section>
      ) : (
        /* O'qiladigan matn va yozishni boshlash */
        <section className="card" aria-label="O'qiladigan matn">
          <div className={s.recHead}>
            <span className="label">O&apos;qiladigan matn</span>
            <button type="button" className="linkbtn" onClick={nextPassage}><RefreshCw size={14} />Boshqa matn</button>
          </div>
          <div className={s.passage}>{VOICE_PASSAGES[passage]}</div>
          <Btn block icon={Mic} disabled={busy.is("save") || busy.is("delete")} onClick={startRec}>{st ? "Qayta yozish" : "Yozishni boshlash"}</Btn>
          <div className="hint">Ovoz faqat sizning hisobingizda saqlanadi.</div>
        </section>
      )}

      <div className={s.tip}><VolumeX size={16} /><span>Tinch xona, telefon og&apos;izdan ~20 sm uzoqda — shoshmasdan, bir tekis o&apos;qing</span></div>
    </div>
  );
}

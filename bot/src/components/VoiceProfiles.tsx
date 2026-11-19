"use client";

import { useState } from "react";
import type { VoiceInfo, VoiceProfileCfg } from "@/lib/types";

/**
 * Ovoz profillari (qo'shish, tezlik/til, standart, o'chirish). Ro'yxat botda asosiy manba.
 * VoiceStudio texnik sozlamalari botda ko'rsatilmaydi: saqlangan qiymatlar o'zgarmaydi.
 */
export default function VoiceProfiles({ profiles, defaultProfile, info, onProfiles, onDefault }: {
  profiles: VoiceProfileCfg[]; defaultProfile: string; info: VoiceInfo[];
  onProfiles: (p: VoiceProfileCfg[]) => void; onDefault: (name: string) => void;
}) {
  // Ishlatsa bo'ladigan dvigatellar (barcha kompyuterlar birlashmasi, takrorsiz).
  const engines = new Map<string, { id: string; displayName: string; voices: string[] }>();
  // Faqat MOSS-TTS-Nano ishlatiladi (worker boshqa modelli profilni rad etadi).
  for (const v of info) for (const e of v.engines) if (e.usable && e.id === "moss-tts-nano" && !engines.has(e.id)) engines.set(e.id, { id: e.id, displayName: e.displayName, voices: e.voices });
  const [engineId, setEngineId] = useState("");
  const eng = engines.get(engineId) ?? [...engines.values()][0];
  const [voice, setVoice] = useState("");
  const [name, setName] = useState("");
  const [lang, setLang] = useState("en");

  const localProfiles = info[0]?.profiles ?? [];
  const voiceValue = eng && eng.voices.length ? (eng.voices.includes(voice) ? voice : eng.voices[0]) : voice;

  function add() {
    if (!eng) return;
    const n = (name.trim() || `${eng.id}${voiceValue ? " / " + voiceValue : ""}`).slice(0, 60);
    if (profiles.some((p) => p.name.toLowerCase() === n.toLowerCase())) return;
    onProfiles([...profiles, { name: n, model: eng.id, voice: voiceValue, language: lang || "en", speed: 1 }]);
    setName("");
  }

  return (
    <>
      <div className="sect">Ovoz profillari</div>
      <div className="card formcard">
        {profiles.length === 0 ? (
          <>
            <div className="hint">Botda profil ro&apos;yxati hali yo&apos;q: kompyuterdagi lokal ro&apos;yxat ishlatilmoqda ({localProfiles.map((p) => p.name).join(", ") || "bo'sh"}).</div>
            {localProfiles.length > 0 ? <button className="btn block" onClick={() => onProfiles(localProfiles.map((p) => ({ ...p })))}>Shu ro&apos;yxatni botga olish (keyin tahrirlash mumkin)</button> : null}
          </>
        ) : profiles.map((p, i) => (
          <div key={p.name} className="logline">
            <label className="chk"><input type="radio" name="defvoice" checked={defaultProfile === p.name} onChange={() => onDefault(p.name)} /><b>{p.name}</b>{defaultProfile === p.name ? " ★ standart" : ""}</label>
            <div className="hint">{p.model} · {p.voice || "—"}</div>
            <div className="grid2">
              <div className="field"><label>Til</label><input className="input" value={p.language} onChange={(e) => onProfiles(profiles.map((x, j) => (j === i ? { ...x, language: e.target.value } : x)))} /></div>
            </div>
            <SpeedControl value={p.speed} onChange={(v) => onProfiles(profiles.map((x, j) => (j === i ? { ...x, speed: v } : x)))} />
            <button className="btn danger" disabled={profiles.length <= 1} onClick={() => { onProfiles(profiles.filter((_, j) => j !== i)); if (defaultProfile === p.name) onDefault(""); }}>O&apos;chirish</button>
          </div>
        ))}

        <div className="sect" style={{ marginTop: 6 }}>Profil qo&apos;shish</div>
        {engines.size === 0 ? <div className="hint">Dvigatellar ro&apos;yxati yo&apos;q: kompyuterdagi ilova VoiceStudio modellarini o&apos;qigach chiqadi.</div> : (
          <>
            <div className="grid2">
              <div className="field"><label>Dvigatel</label>
                <select className="input" value={eng?.id ?? ""} onChange={(e) => { setEngineId(e.target.value); setVoice(""); }}>
                  {[...engines.values()].map((e) => <option key={e.id} value={e.id}>{e.displayName}</option>)}
                </select></div>
              <div className="field"><label>Ovoz</label>
                {eng && eng.voices.length ? (
                  <select className="input" value={voiceValue} onChange={(e) => setVoice(e.target.value)}>{eng.voices.map((v) => <option key={v} value={v}>{v}</option>)}</select>
                ) : <input className="input" value={voice} placeholder="(ixtiyoriy)" onChange={(e) => setVoice(e.target.value)} />}
              </div>
              <div className="field"><label>Profil nomi</label><input className="input" value={name} placeholder="Masalan: MOSS erkak" onChange={(e) => setName(e.target.value)} /></div>
              <div className="field"><label>Til</label><input className="input" value={lang} onChange={(e) => setLang(e.target.value)} /></div>
            </div>
            <button className="btn block" onClick={add}>Qo&apos;shish</button>
          </>
        )}
      </div>

    </>
  );
}

const SPEED_MIN = 0.5, SPEED_MAX = 2, SPEED_STEP = 0.05;
const SPEED_PRESETS = [0.8, 0.9, 1, 1.1, 1.25, 1.5];
const clampSpeed = (v: number) => Math.min(SPEED_MAX, Math.max(SPEED_MIN, Math.round(v / SPEED_STEP) * SPEED_STEP));
const fmtSpeed = (v: number) => (Number.isFinite(v) ? v : 1).toFixed(2).replace(/0$/, "");

/**
 * Ovoz tezligi: slider + tayyor qiymatlar. Oldingi matn maydoni har harfda songa aylantirardi, shuning uchun "1." yoki "0,9"
 * yozib bo'lmasdi (nuqta o'chib ketardi) — tezlikni o'zgartirib bo'lmasdi.
 */
function SpeedControl({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const v = Number.isFinite(value) && value > 0 ? value : 1;
  return (
    <div className="field">
      <label>Ovoz tezligi: <b>{fmtSpeed(v)}×</b>{Math.abs(v - 1) < 0.001 ? " (oddiy)" : v < 1 ? " (sekinroq)" : " (tezroq)"}</label>
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        <button type="button" className="btn" aria-label="Sekinroq" disabled={v <= SPEED_MIN} onClick={() => onChange(clampSpeed(v - SPEED_STEP))}>−</button>
        <input type="range" min={SPEED_MIN} max={SPEED_MAX} step={SPEED_STEP} value={v} style={{ flex: 1, accentColor: "var(--accent)" }}
          aria-label="Ovoz tezligi" onChange={(e) => onChange(clampSpeed(Number(e.target.value)))} />
        <button type="button" className="btn" aria-label="Tezroq" disabled={v >= SPEED_MAX} onClick={() => onChange(clampSpeed(v + SPEED_STEP))}>+</button>
      </div>
      <div className="actions">
        {SPEED_PRESETS.map((x) => (
          <button key={x} type="button" className={Math.abs(v - x) < 0.001 ? "btn primary" : "btn"} style={{ height: 28, padding: "0 10px" }} onClick={() => onChange(x)}>{fmtSpeed(x)}×</button>
        ))}
      </div>
      <div className="hint">Yangi tezlik keyingi tayyorlanadigan videolarga qo&apos;llanadi (ovozi tayyor bo&apos;lgan videolar o&apos;zgarmaydi).</div>
    </div>
  );
}

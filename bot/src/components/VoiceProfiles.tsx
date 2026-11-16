"use client";

import { useState } from "react";
import type { BotConfig, VoiceInfo, VoiceProfileCfg } from "@/lib/types";

type VSettings = BotConfig["voice"]["settings"];

/** Ovoz profillari (qo'shish, tezlik/til, standart, o'chirish) va VoiceStudio sozlamalari. Ro'yxat botda asosiy manba. */
export default function VoiceProfiles({ profiles, settings, defaultProfile, info, onProfiles, onSettings, onDefault }: {
  profiles: VoiceProfileCfg[]; settings: VSettings; defaultProfile: string; info: VoiceInfo[];
  onProfiles: (p: VoiceProfileCfg[]) => void; onSettings: (s: VSettings) => void; onDefault: (name: string) => void;
}) {
  // Ishlatsa bo'ladigan dvigatellar (barcha kompyuterlar birlashmasi, takrorsiz).
  const engines = new Map<string, { id: string; displayName: string; voices: string[] }>();
  for (const v of info) for (const e of v.engines) if (e.usable && !engines.has(e.id)) engines.set(e.id, { id: e.id, displayName: e.displayName, voices: e.voices });
  const [engineId, setEngineId] = useState("");
  const eng = engines.get(engineId) ?? [...engines.values()][0];
  const [voice, setVoice] = useState("");
  const [name, setName] = useState("");
  const [lang, setLang] = useState("en");
  const set = (k: keyof VSettings, v: string | number) => onSettings({ ...settings, [k]: v } as VSettings);
  const num = (v: string) => (v === "" ? 0 : Number(v));

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
              <div className="field"><label>Tezlik (0.5–2)</label><input className="input" inputMode="decimal" value={p.speed} onChange={(e) => onProfiles(profiles.map((x, j) => (j === i ? { ...x, speed: num(e.target.value) } : x)))} /></div>
            </div>
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
              <div className="field"><label>Profil nomi</label><input className="input" value={name} placeholder="Masalan: Kitten EN erkak" onChange={(e) => setName(e.target.value)} /></div>
              <div className="field"><label>Til</label><input className="input" value={lang} onChange={(e) => setLang(e.target.value)} /></div>
            </div>
            <button className="btn block" onClick={add}>Qo&apos;shish</button>
          </>
        )}
      </div>

      <div className="sect">VoiceStudio sozlamalari</div>
      <div className="card formcard">
        <div className="field"><label>Lokal API manzili</label><input className="input" value={settings.baseUrl} onChange={(e) => set("baseUrl", e.target.value)} /></div>
        <div className="grid2">
          <div className="field"><label>Ishga tushish kutish (s)</label><input className="input" inputMode="numeric" value={settings.startupTimeoutSec} onChange={(e) => set("startupTimeoutSec", num(e.target.value))} /></div>
          <div className="field"><label>TTS so&apos;rov vaqti (s)</label><input className="input" inputMode="numeric" value={settings.requestTimeoutSec} onChange={(e) => set("requestTimeoutSec", num(e.target.value))} /></div>
          <div className="field"><label>TTS bo&apos;lagi (belgi)</label><input className="input" inputMode="numeric" value={settings.chunkChars} onChange={(e) => set("chunkChars", num(e.target.value))} /></div>
          <div className="field"><label>Bo&apos;laklar orasi pauza (s)</label><input className="input" inputMode="decimal" value={settings.pauseBetweenChunksSec} onChange={(e) => set("pauseBetweenChunksSec", num(e.target.value))} /></div>
          <div className="field"><label>Subtitr vaqti timeouti (s)</label><input className="input" inputMode="numeric" value={settings.alignTimeoutSec} onChange={(e) => set("alignTimeoutSec", num(e.target.value))} /></div>
        </div>
        <div className="field"><label>Whisper modeli (subtitr vaqtlari)</label><input className="input" value={settings.whisperModel} onChange={(e) => set("whisperModel", e.target.value)} /></div>
      </div>
    </>
  );
}

"use client";

/**
 * Ovoz profillari (dizayn: Ovoz MJiFi5): standart ovoz plitkalari, har profil kartasi (til + tezlik, o'z «Saqlash» tugmasi),
 * o'chirish va profil qo'shish oynasi. Ro'yxat botda asosiy manba.
 * VoiceStudio texnik sozlamalari botda ko'rsatilmaydi: saqlangan qiymatlar o'zgarmaydi.
 */
import { useState } from "react";
import { AudioLines, Check, ChevronDown, Languages, LoaderCircle, Mic, Minus, Monitor, Plus, Trash2 } from "lucide-react";
import { confirmBox, haptic } from "@/lib/client";
import { differs } from "@/lib/settings-data";
import type { VoiceInfo, VoiceProfileCfg } from "@/lib/types";
import type { Toast } from "./App";
import { Btn, Field, SaveBtn, Sheet, Status, useBusy } from "./ui";
import { CLONE_STATUS, type CloneState } from "./VoiceClone";
import s from "./VoiceProfiles.module.css";

/** «Mening ovozim» profili: ovoz nomi server yasaydi (lib/config.ts isCloneProfile bilan bir xil). */
export const isCloneProfile = (p: { voice?: string }) => /^YtAuto u-[0-9a-f]{8}$/.test(p.voice ?? "");

// ------------------------------------------------------------------ tezlik
const SPEED_MIN = 0.5, SPEED_MAX = 2, SPEED_STEP = 0.05;
const SPEED_PRESETS = [0.8, 0.9, 1, 1.1, 1.25, 1.5];
const clampSpeed = (v: number) => Math.min(SPEED_MAX, Math.max(SPEED_MIN, Number((Math.round(v / SPEED_STEP) * SPEED_STEP).toFixed(2))));
const safeSpeed = (v: number) => (Number.isFinite(v) && v > 0 ? v : 1);
/** 1 → "1", 1.1 → "1.1", 1.25 → "1.25" (dizayndagidek). */
export const fmtSpeed = (v: number) => String(Number(safeSpeed(v).toFixed(2)));
const isOne = (v: number) => Math.abs(v - 1) < 0.001;
const speedWord = (v: number) => (isOne(v) ? "oddiy" : `${v < 1 ? "sekinroq" : "tezroq"} · oddiy 1×`);

/**
 * Ovoz tezligi: katta qiymat, slider (−/+ bilan) va tayyor qiymatlar. Matn maydoni yo'q: oldingi maydon har harfda songa aylantirardi,
 * shuning uchun "1." yoki "0,9" yozib bo'lmasdi (nuqta o'chib ketardi) — tezlikni o'zgartirib bo'lmasdi.
 */
function SpeedControl({ value, onChange, disabled }: { value: number; onChange: (v: number) => void; disabled?: boolean }) {
  const v = safeSpeed(value);
  const pct = ((v - SPEED_MIN) / (SPEED_MAX - SPEED_MIN)) * 100;
  const onePct = ((1 - SPEED_MIN) / (SPEED_MAX - SPEED_MIN)) * 100;
  return (
    <div className={s.speed}>
      <div className={s.readout}>
        <span className={`${s.value} tnum`}>{fmtSpeed(v)}×</span>
        <span className={s.words}><span className="label">Ovoz tezligi</span><span className={s.word}>{speedWord(v)}</span></span>
      </div>
      <div className={s.sliderRow}>
        <button type="button" className={s.step} aria-label="Sekinroq" disabled={disabled || v <= SPEED_MIN} onClick={() => onChange(clampSpeed(v - SPEED_STEP))}><Minus size={18} /></button>
        <div className={s.slider} style={{ "--p": `${pct}%`, "--one": `${onePct}%` } as React.CSSProperties}>
          <i className={s.oneMark} aria-hidden />
          <input type="range" min={SPEED_MIN} max={SPEED_MAX} step={SPEED_STEP} value={v} disabled={disabled}
            aria-label="Ovoz tezligi" aria-valuetext={`${fmtSpeed(v)}×, ${speedWord(v)}`} onChange={(e) => onChange(clampSpeed(Number(e.target.value)))} />
        </div>
        <button type="button" className={s.step} aria-label="Tezroq" disabled={disabled || v >= SPEED_MAX} onClick={() => onChange(clampSpeed(v + SPEED_STEP))}><Plus size={18} /></button>
      </div>
      <div className={s.presets} role="group" aria-label="Tayyor tezliklar">
        {SPEED_PRESETS.map((x) => (
          <button key={x} type="button" className={Math.abs(v - x) < 0.001 ? s.on : undefined} aria-pressed={Math.abs(v - x) < 0.001} disabled={disabled}
            onClick={() => { haptic.tap(); onChange(x); }}>{fmtSpeed(x)}×</button>
        ))}
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ til
const LANGS: [string, string][] = [
  ["en", "English"], ["ru", "Русский"], ["uz", "O'zbek"], ["tr", "Türkçe"], ["de", "Deutsch"], ["fr", "Français"], ["es", "Español"],
  ["it", "Italiano"], ["pt", "Português"], ["ar", "العربية"], ["hi", "हिन्दी"], ["ja", "日本語"], ["ko", "한국어"], ["zh", "中文"],
];
const OTHER = "__other";

/** Til tanlovi (chip ko'rinishida). Ro'yxatda bo'lmagan kodni «Boshqa…» orqali qo'lda yozish mumkin (oldingi erkin maydon kabi). */
function LangPicker({ value, onChange, disabled }: { value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const [custom, setCustom] = useState(false);
  if (custom) {
    return (
      <input className={`inp ${s.langInput}`} autoFocus value={value} maxLength={16} placeholder="masalan: en" aria-label="Til kodi" disabled={disabled}
        onChange={(e) => onChange(e.target.value)} onBlur={() => { if (!value.trim()) onChange("en"); setCustom(false); }} />
    );
  }
  const known = LANGS.some(([k]) => k === value);
  return (
    <label className={s.lang}>
      <Languages size={16} className={s.muted} />
      <span>Til: {value || "en"}</span>
      <ChevronDown size={16} className={s.muted} />
      <select value={value} aria-label="Til" disabled={disabled} onChange={(e) => (e.target.value === OTHER ? setCustom(true) : onChange(e.target.value))}>
        {!known ? <option value={value}>{value || "en"}</option> : null}
        {LANGS.map(([k, name]) => <option key={k} value={k}>{k} — {name}</option>)}
        <option value={OTHER}>Boshqa…</option>
      </select>
    </label>
  );
}

// ------------------------------------------------------------------ standart ovoz plitkalari
export type Tile = { name: string; meta: string; kind: "profile" | "clone" | "machine"; dot?: "ok" | "accent" | "bad" };

/** Plitkalar ro'yxati: botdagi profillar, so'ng faqat kompyuterda bor profillar (standart qilsa bo'ladi). */
export function buildTiles(profiles: VoiceProfileCfg[], catalogNames: string[], clone: CloneState | undefined): Tile[] {
  const tiles: Tile[] = profiles.map((p) => {
    if (isCloneProfile(p)) {
      const st = clone ? CLONE_STATUS[clone.status] : null;
      return { name: p.name, kind: "clone", meta: `${p.language || "en"}${st ? ` · ${st.short}` : ` · ${fmtSpeed(p.speed)}×`}`, dot: st?.tone };
    }
    return { name: p.name, kind: "profile", meta: `${p.language || "en"} · ${fmtSpeed(p.speed)}×` };
  });
  for (const n of catalogNames) if (n && !tiles.some((t) => t.name === n)) tiles.push({ name: n, kind: "machine", meta: "kompyuterda" });
  return tiles;
}

/** Standart ovoz: bosilgan plitka darhol saqlanadi (defaults.voiceProfile). Tanlangani - to'liq urg'u rangida. */
export function DefaultTiles({ tiles, value, pending, disabled, onPick }: {
  tiles: Tile[]; value: string; pending: string | null; disabled?: boolean; onPick: (name: string) => void;
}) {
  return (
    <div className={s.tiles} role="radiogroup" aria-label="Standart ovoz">
      {tiles.map((t) => {
        const on = t.name === value;
        const Icon = t.kind === "clone" ? Mic : t.kind === "machine" ? Monitor : AudioLines;
        return (
          <button key={t.name} type="button" role="radio" aria-checked={on} className={`${s.tile}${on ? " " + s.tileOn : ""}`}
            disabled={disabled || pending !== null} aria-busy={pending === t.name || undefined} onClick={() => { if (!on) onPick(t.name); }}>
            <span className={s.tileTop}>
              <span className={s.tileIcon}><Icon size={20} /></span>
              {pending === t.name ? <LoaderCircle size={22} className={`spin ${s.tileSpin}`} />
                : <span className={s.radio}>{on ? <Check size={14} strokeWidth={3} /> : null}</span>}
            </span>
            <span className={s.tileText}>
              <b>{t.name}</b>
              <span className={s.tileMeta}>{t.dot ? <i className={`${s.dot} ${s[t.dot]}`} /> : null}{t.meta}</span>
            </span>
          </button>
        );
      })}
    </div>
  );
}

// ------------------------------------------------------------------ profil kartasi
/**
 * Bitta profil: tezlik va til shu kartada o'zgartiriladi va shu kartaning «Saqlash» tugmasi bilan saqlanadi.
 * «Mening ovozim» profilida ham tezlik/til saqlanadi (server nomi/modeli/ovozini bazadagidek qoldiradi); o'chirish o'rniga namuna ekrani ochiladi.
 */
export function ProfileCard({ profile, isDefault, canDelete, expanded, onToggle, toast, onSave, onDelete, onOpenClone }: {
  profile: VoiceProfileCfg; isDefault: boolean; canDelete: boolean; expanded: boolean; onToggle: () => void; toast: Toast;
  onSave: (patch: { language: string; speed: number }) => Promise<unknown>; onDelete: () => Promise<unknown>; onOpenClone: () => void;
}) {
  const busy = useBusy((m) => toast(m, true));
  const saved = { language: profile.language || "en", speed: safeSpeed(profile.speed) };
  const [base, setBase] = useState(saved);
  const [draft, setDraft] = useState(saved);
  // Saqlangan qiymat tashqaridan o'zgarsa (qayta yuklash, boshqa karta), tahrirlanmagan qoralama yangilanadi.
  if (differs(base, saved)) { setBase(saved); if (!differs(draft, base)) setDraft(saved); }
  const dirty = differs(draft, saved);
  const clone = isCloneProfile(profile);
  const working = busy.is("save") || busy.is("delete");

  async function save() {
    const language = draft.language.trim() || "en";
    if (await busy.run("save", () => onSave({ language, speed: draft.speed })) !== false) {
      setDraft({ language, speed: draft.speed });
      haptic.ok();
      toast(`«${profile.name}» saqlandi. Yangi tezlik keyingi videolarga qo'llanadi.`);
    }
  }
  async function remove() {
    if (!(await confirmBox(`«${profile.name}» ovoz profili o'chirilsinmi?${isDefault ? " U standart ovoz edi — yangi mavzular kompyuterdagi standart ovozda o'qiladi." : ""}`))) return;
    if (await busy.run("delete", onDelete) !== false) toast(`«${profile.name}» o'chirildi.`);
  }

  return (
    <section className={`card ${s.pcard}`}>
      <button type="button" className={s.phead} aria-expanded={expanded} onClick={onToggle}>
        <span className={s.pname}>
          <b>{profile.name}</b>
          <span>{clone ? "Mening ovozim · " : ""}{profile.model}{profile.voice && !clone ? ` · ${profile.voice}` : ""}</span>
        </span>
        {expanded
          ? (isDefault ? <Status>Standart</Status> : dirty ? <Status tone="accent">o&apos;zgardi</Status> : null)
          : <span className={`${s.short} tnum`}>{fmtSpeed(draft.speed)}× <span>{isOne(draft.speed) ? "oddiy" : draft.speed < 1 ? "sekinroq" : "tezroq"}</span>{dirty ? <i className={s.dirtyDot} aria-label="saqlanmagan" /> : null}</span>}
      </button>
      {expanded ? (
        <>
          <SpeedControl value={draft.speed} disabled={working} onChange={(speed) => setDraft((d) => ({ ...d, speed }))} />
          <div className={s.foot}>
            <LangPicker value={draft.language} disabled={working} onChange={(language) => setDraft((d) => ({ ...d, language }))} />
            {clone
              ? <Btn tone="ghost" size="sm" icon={Mic} disabled={working} onClick={onOpenClone}>Namuna</Btn>
              : <Btn tone="ghost" size="sm" icon={Trash2} loading={busy.is("delete")} disabled={!canDelete || busy.is("save")}
                  title={canDelete ? undefined : "Oxirgi profilni o'chirib bo'lmaydi"} onClick={remove}>O&apos;chirish</Btn>}
            <span className={s.grow} />
            <SaveBtn dirty={dirty} saving={busy.is("save")} done={busy.done("save")} onClick={save} />
          </div>
          <div className="hint">Yangi tezlik keyingi videolarga qo&apos;llanadi. Ovozi tayyor videolar o&apos;zgarmaydi.</div>
        </>
      ) : null}
    </section>
  );
}

// ------------------------------------------------------------------ profil qo'shish
type Engine = { id: string; displayName: string; voices: string[] };

/** Ishlatsa bo'ladigan dvigatellar (barcha kompyuterlar birlashmasi, takrorsiz). Faqat MOSS-TTS-Nano (worker boshqa modelli profilni rad etadi). */
export function usableEngines(info: VoiceInfo[]): Engine[] {
  const engines = new Map<string, Engine>();
  for (const v of info) for (const e of v.engines) if (e.usable && e.id === "moss-tts-nano" && !engines.has(e.id)) engines.set(e.id, { id: e.id, displayName: e.displayName, voices: e.voices });
  return [...engines.values()];
}

/** Profil qo'shish oynasi: dvigatel, ovoz, nom, til. Nom bo'sh bo'lsa "dvigatel / ovoz" bo'ladi; bir xil nom qabul qilinmaydi. */
export function AddProfileSheet({ info, profiles, toast, onClose, onAdd }: {
  info: VoiceInfo[] | null; profiles: VoiceProfileCfg[]; toast: Toast; onClose: () => void; onAdd: (p: VoiceProfileCfg) => Promise<unknown>;
}) {
  const busy = useBusy((m) => toast(m, true));
  const engines = usableEngines(info ?? []);
  const [engineId, setEngineId] = useState("");
  const eng = engines.find((e) => e.id === engineId) ?? engines[0];
  const [voice, setVoice] = useState("");
  const [name, setName] = useState("");
  const [lang, setLang] = useState("en");
  const voiceValue = eng && eng.voices.length ? (eng.voices.includes(voice) ? voice : eng.voices[0]) : voice;
  const finalName = eng ? (name.trim() || `${eng.id}${voiceValue ? " / " + voiceValue : ""}`).slice(0, 60) : "";
  const taken = !!finalName && profiles.some((p) => p.name.toLowerCase() === finalName.toLowerCase());

  async function add() {
    if (!eng || taken) return;
    const p: VoiceProfileCfg = { name: finalName, model: eng.id, voice: voiceValue, language: lang.trim() || "en", speed: 1 };
    if (await busy.run("add", () => onAdd(p)) !== false) { haptic.ok(); toast(`«${p.name}» profili qo'shildi.`); onClose(); }
  }

  return (
    <Sheet title="Profil qo'shish" sub="Yangi ovoz profili — tezlik va tilni keyin kartada sozlaysiz" onClose={onClose}
      footer={engines.length ? <Btn block icon={Plus} loading={busy.is("add")} disabled={taken} onClick={add}>Qo&apos;shish</Btn> : undefined}>
      {info === null ? (
        <div className={s.sheetBody} aria-busy="true"><span className="skel" style={{ height: 46 }} /><span className="skel" style={{ height: 46 }} /></div>
      ) : engines.length === 0 ? (
        <div className="hint">Dvigatellar ro&apos;yxati yo&apos;q: kompyuterdagi ilova VoiceStudio modellarini o&apos;qigach chiqadi.</div>
      ) : (
        <div className={s.sheetBody}>
          <div className="grid2">
            <Field label="Dvigatel">
              <select className="inp" value={eng?.id ?? ""} onChange={(e) => { setEngineId(e.target.value); setVoice(""); }}>
                {engines.map((e) => <option key={e.id} value={e.id}>{e.displayName}</option>)}
              </select>
            </Field>
            <Field label="Ovoz">
              {eng && eng.voices.length ? (
                <select className="inp" value={voiceValue} onChange={(e) => setVoice(e.target.value)}>{eng.voices.map((v) => <option key={v} value={v}>{v}</option>)}</select>
              ) : <input className="inp" value={voice} placeholder="(ixtiyoriy)" onChange={(e) => setVoice(e.target.value)} />}
            </Field>
          </div>
          <Field label="Profil nomi" error={taken ? `«${finalName}» nomli profil bor — boshqa nom kiriting.` : undefined}
            hint={name.trim() ? undefined : `Bo'sh qolsa: «${finalName}»`}>
            <input className={`inp${taken ? " err" : ""}`} value={name} maxLength={60} placeholder="Masalan: MOSS erkak" onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label="Til">
            <input className="inp" value={lang} maxLength={16} placeholder="en" onChange={(e) => setLang(e.target.value)} />
          </Field>
        </div>
      )}
    </Sheet>
  );
}

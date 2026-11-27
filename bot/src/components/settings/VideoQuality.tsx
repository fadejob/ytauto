"use client";

/**
 * «Video va sifat» (dizayn: Sozlamalar → Video va sifat euFqPV, to'liq sahifa yX7XZY).
 * Ikki karta, har biri o'z qismini o'zi saqlaydi (saveConfig): yangi video standartlari va real rasm/ikonlar.
 * Shablonga moslik va render sozlamalari botdan olib tashlandi - kompyuterdagi standart qiymatlar ishlatiladi.
 */
import { useCallback, useState, type ReactNode } from "react";
import { AudioLines, Check, ChevronDown, ClipboardPaste, KeyRound, LayoutTemplate, Lock, Minus, Plus, RotateCcw, Trash2 } from "lucide-react";
import { confirmBox, haptic } from "@/lib/client";
import { differs, useBotConfig, type ConfigDraft } from "@/lib/settings-data";
import { FormatText, type BotConfig, type Catalog, type SubtitleConfig, type SubtitleStyle } from "@/lib/types";
import { FormatLength } from "@/lib/video-rules";
import { Alert, Btn, Card, PageHead, SaveBtn, SkelCard, Status, Toggle, ToggleRow, useBusy } from "../ui";
import type { SettingsPageProps } from "./types";
import s from "./VideoQuality.module.css";

type CardProps = { config: BotConfig; save: (title: string, patch: (d: ConfigDraft) => ConfigDraft) => Promise<BotConfig | false>; busy: ReturnType<typeof useBusy> };

const CONFIRM = "Saqlansinmi? Yangi videolardan boshlab qo'llanadi.";

export default function VideoQuality({ state, toast, back, onChanged }: SettingsPageProps) {
  const { config, error, reload, saveConfig } = useBotConfig();
  const fail = useCallback((m: string) => { haptic.fail(); toast(m, true); }, [toast]);
  const busy = useBusy(fail);

  /** Bitta kartani saqlash: tasdiq (eski «Saqlash» kabi), saqlash, toast va umumiy holatni yangilash. */
  const save = useCallback(async (title: string, patch: (d: ConfigDraft) => ConfigDraft): Promise<BotConfig | false> => {
    if (!(await confirmBox(`«${title}» saqlansinmi? ${CONFIRM}`))) return false;
    const r = await busy.run(title, () => saveConfig(patch));
    if (r) { haptic.ok(); toast(`${title} saqlandi`); onChanged(); }
    return r;
  }, [busy, saveConfig, toast, onChanged]);

  const props = config ? { config, save, busy } : null;

  return (
    <div className="page narrow">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Video va sifat"
        sub="Yangi videolar qanday ko'rinishda chiqadi: format, ovoz, shablon va rasmlar" />

      {!config && error ? (
        <Card>
          <Alert>Sozlamalarni yuklab bo&apos;lmadi: {error}</Alert>
          <Btn tone="secondary" icon={RotateCcw} loading={busy.is("reload")} onClick={() => busy.run("reload", reload)}>Qayta urinish</Btn>
        </Card>
      ) : null}

      <div className="cards">
        {props ? <>
          <DefaultsCard {...props} catalog={state?.catalog ?? null} />
          <MediaCard {...props} />
          <SubtitleCard {...props} />
        </> : !error ? <>
          <SkelCard lines={6} /><SkelCard lines={6} /><SkelCard lines={6} />
        </> : null}
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ umumiy qismlar

/** Karta qoralamasi: saqlangan config o'zgarsa (boshqa karta saqlandi, qayta yuklandi) va qoralama toza bo'lsa - yangilanadi. */
function useDraft<T>(config: BotConfig, pick: (c: BotConfig) => T) {
  const [draft, setDraft] = useState<T>(() => pick(config));
  const [prev, setPrev] = useState(config);
  if (prev !== config) {
    setPrev(config);
    if (!differs(draft, pick(prev))) setDraft(pick(config));
  }
  const saved = pick(config);
  return { draft, setDraft, saved, dirty: differs(draft, saved) };
}

/** O'zgargan maydonlar nomi: «Shablon o'zgardi», «Format, ovoz profili o'zgardi». */
function changed<T extends object>(draft: T, saved: T, names: Partial<Record<keyof T, string>>): string {
  const list = [...new Set((Object.keys(names) as (keyof T)[]).filter((k) => differs(draft[k], saved[k])).map((k) => names[k] as string))];
  if (!list.length) return "";
  const text = list.join(", ").toLowerCase();
  return text.charAt(0).toUpperCase() + text.slice(1) + " o'zgardi";
}

/** Karta oxiri: chapda izoh (toza bo'lsa doimiy izoh, o'zgargan bo'lsa nima o'zgargani), o'ngda «Saqlash». */
function Foot({ hint, dirtyHint, dirty, saving, done, onSave, onReset }: {
  hint: ReactNode; dirtyHint: string; dirty: boolean; saving: boolean; done: boolean; onSave: () => void; onReset: () => void;
}) {
  return (
    <div className={s.foot}>
      <span className={`hint ${dirty ? s.dirtyHint : ""}`}>{dirty ? dirtyHint : hint}</span>
      {dirty || saving || done ? (
        <span className={s.footActions}>
          {dirty && !saving ? <Btn size="sm" tone="ghost" onClick={onReset}>Qaytarish</Btn> : null}
          <SaveBtn dirty={dirty} saving={saving} done={done} onClick={onSave} />
        </span>
      ) : null}
    </div>
  );
}

/** −/+ va qo'lda yoziladigan qiymat (chegaradan chiqmaydi). */
function Stepper({ value, min, max, step = 1, onChange, label, suffix }: {
  value: number; min: number; max: number; step?: number; onChange: (v: number) => void; label: string; suffix?: string;
}) {
  const [text, setText] = useState<string | null>(null);
  const clamp = (v: number) => Math.min(max, Math.max(min, v));
  const commit = () => {
    if (text === null) return;
    const n = Number(text.replace(",", "."));
    if (text.trim() !== "" && Number.isFinite(n)) onChange(clamp(Math.round(n)));
    setText(null);
  };
  return (
    <div className={`${s.stepper} `} role="group" aria-label={label}>
      <button type="button" className={s.stepBtn} aria-label={`${label}: kamaytirish`} disabled={value <= min} onClick={() => onChange(clamp(value - step))}><Minus size={18} /></button>
      <span className={s.stepVal}>
        <input className="tnum" inputMode="numeric" aria-label={label} value={text ?? String(value)} size={Math.max(2, String(max).length)}
          onChange={(e) => setText(e.target.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === "Enter") (e.target as HTMLInputElement).blur(); }} />
        {suffix ? <span className={s.suffix}>{suffix}</span> : null}
      </span>
      <button type="button" className={s.stepBtn} aria-label={`${label}: oshirish`} disabled={value >= max} onClick={() => onChange(clamp(value + step))}><Plus size={18} /></button>
    </div>
  );
}

function Row({ title, desc, children }: { title: ReactNode; desc?: ReactNode; children: ReactNode }) {
  return (
    <div className={s.row}>
      <div className="t"><span className={s.rowTitle}>{title}</span>{desc ? <span className="sub">{desc}</span> : null}</div>
      {children}
    </div>
  );
}

/** Tanlov (radio) tugmalari: faol qiymat urg'u rangida. Saqlangan qiymat ro'yxatda bo'lmasa u ham qo'shiladi. */
/** Belgili va o'qli tanlov qutisi (asl <select>). */
function SelectBox({ icon: Icon, label, value, onChange, children }: { icon: typeof LayoutTemplate; label: string; value: string; onChange: (v: string) => void; children: ReactNode }) {
  return (
    <span className={s.select}>
      <Icon className={s.selIcon} size={18} aria-hidden />
      <select className="inp" aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>{children}</select>
      <ChevronDown className={s.selChev} size={18} aria-hidden />
    </span>
  );
}

// ------------------------------------------------------------------ 1. Yangi video standartlari

type DefaultsDraft = { format: number; template: string; voiceProfile: string };
const pickDefaults = (c: BotConfig): DefaultsDraft => ({ format: c.defaults.format, template: c.defaults.template, voiceProfile: c.defaults.voiceProfile });

/** Format nomi va nisbati FormatText'dan («Gorizontal 16:9»), uzunligi FormatLength'dan («2–3 daqiqa», «55 s gacha»). */
function formatParts(f: number) {
  const words = (FormatText[f] ?? "").split(" ");
  const ratio = words.pop() ?? "";
  const rule = FormatLength[f] ?? FormatLength[0];
  return { ratio, name: words.join(" "), length: rule.minSec >= 60 ? rule.label : `${rule.maxSec} s gacha` };
}

function DefaultsCard({ config, save, busy, catalog }: CardProps & { catalog: Catalog | null }) {
  const TITLE = "Yangi video standartlari";
  const { draft, setDraft, saved, dirty } = useDraft(config, pickDefaults);
  const set = (p: Partial<DefaultsDraft>) => setDraft((d) => ({ ...d, ...p }));
  const templates = catalog?.templates ?? [];
  const pending = (catalog?.pendingTemplates ?? []).filter((p) => !templates.some((t) => t.name === p.name));
  const profiles = catalog?.voiceProfiles ?? [];
  /** Profil tafsiloti (til, tezlik) bot config'dagi ovoz profillaridan. */
  const voiceLabel = (name: string) => {
    const p = config.voice.profiles.find((x) => x.name === name);
    return p ? `${name} · ${p.language || "en"} · ${p.speed}×` : name;
  };

  return (
    <Card title={TITLE} sub="Har yangi mavzuga avtomatik qo'yiladi">
      <div className="field">
        <span className="label" id="vq-format">Format va uzunlik</span>
        <div className={s.formats} role="radiogroup" aria-labelledby="vq-format">
          {[0, 1, 2].map((f) => {
            const { ratio, name, length } = formatParts(f);
            const on = draft.format === f;
            return (
              <button key={f} type="button" role="radio" aria-checked={on} className={`${s.format} ${on ? s.on : ""}`} onClick={() => set({ format: f })}>
                <span className={s.formatTop}>
                  <span className={s.aspect}><i className={s[`shape${f}`]} /></span>
                  <span className={s.pick}>{on ? <Check size={14} strokeWidth={3} /> : null}</span>
                </span>
                <b className="tnum">{ratio}</b>
                <span className={s.formatName}>{name}</span>
                <span className={s.formatSub}>{length}</span>
              </button>
            );
          })}
        </div>
      </div>

      <div className="field">
        <span className="label">Shablon</span>
        <SelectBox icon={LayoutTemplate} label="Shablon" value={draft.template} onChange={(v) => set({ template: v })}>
          <option value="">Shablonsiz</option>
          {templates.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
          {draft.template && !templates.some((t) => t.name === draft.template) ? <option value={draft.template}>{draft.template}</option> : null}
          {pending.map((p) => <option key={`p-${p.name}`} value={`pending:${p.name}`} disabled>{p.name} — serverga yuklanmoqda {Math.round(p.progress)}%</option>)}
        </SelectBox>
        {pending.length ? (
          <div className="hint">
            {pending.map((p) => `«${p.name}»`).join(", ")} hali kompyuterga tushmagan ({pending.length === 1 ? `${Math.round(pending[0].progress)}%` : "yuklanmoqda"}) —
            o&apos;rnatilgach tanlash mumkin bo&apos;ladi.
          </div>
        ) : null}
      </div>

      <div className="field">
        <span className="label">Ovoz profili</span>
        <SelectBox icon={AudioLines} label="Ovoz profili" value={draft.voiceProfile} onChange={(v) => set({ voiceProfile: v })}>
          <option value="">Kompyuterdagi standart</option>
          {profiles.map((v) => <option key={v} value={v}>{voiceLabel(v)}</option>)}
          {draft.voiceProfile && !profiles.includes(draft.voiceProfile) ? <option value={draft.voiceProfile}>{voiceLabel(draft.voiceProfile)}</option> : null}
        </SelectBox>
      </div>

      <div className={s.locked}><Lock size={16} aria-hidden />Til: ingliz (en) — har doim shu</div>

      <Foot hint="Faqat yangi mavzularga" dirty={dirty} saving={busy.is(TITLE)} done={busy.done(TITLE)}
        dirtyHint={changed(draft, saved, { format: "Format", template: "Shablon", voiceProfile: "Ovoz profili" })}
        onReset={() => setDraft(saved)}
        onSave={async () => {
          const r = await save(TITLE, (d) => ({ ...d, defaults: { ...d.defaults, ...draft } }));
          if (r) setDraft(pickDefaults(r));
        }} />
    </Card>
  );
}

// ------------------------------------------------------------------ 2. Real rasm va ikonlar

type MediaDraft = {
  realMedia: boolean; maxMedia: number; stockImages: number; stockClips: number; liveAssets: boolean; max3dModels: number;
  pexelsKeySet: boolean; pixabayKeySet: boolean; pexelsApiKey: string; pixabayApiKey: string;
};
const pickMedia = (c: BotConfig): MediaDraft => ({
  realMedia: c.visuals.realMedia, maxMedia: c.visuals.maxMedia, stockImages: c.visuals.stockImages, stockClips: c.visuals.stockClips,
  liveAssets: c.visuals.liveAssets, max3dModels: c.visuals.max3dModels,
  pexelsKeySet: c.visuals.pexelsKeySet, pixabayKeySet: c.visuals.pixabayKeySet, pexelsApiKey: "", pixabayApiKey: "",
});
/** Server tekshiruvi bilan bir xil (lib/config.ts stockKey). */
const KEY_RE = /^[A-Za-z0-9_-]{10,120}$/;

function MediaCard({ config, save, busy }: CardProps) {
  const TITLE = "Real rasm va ikonlar";
  const { draft, setDraft, saved, dirty } = useDraft(config, pickMedia);
  const set = (p: Partial<MediaDraft>) => setDraft((d) => ({ ...d, ...p }));
  const badKey = (k: string) => !!k.trim() && !KEY_RE.test(k.trim());
  const invalid = badKey(draft.pexelsApiKey) || badKey(draft.pixabayApiKey);

  return (
    <Card title={TITLE} sub="Bepul, tijoratda ishlatsa bo'ladigan rasm, logo va ikonlar"
      action={<Toggle on={draft.realMedia} onChange={(v) => set({ realMedia: v })} label="Real rasm, logo va ikonlar" />}>
      <Row title="Bir videoda ko'pi bilan" desc="0–40 ta rasm">
        <Stepper label="Bir videoga rasmlar" value={draft.maxMedia} min={0} max={40} onChange={(v) => set({ maxMedia: v })} />
      </Row>

      <hr className={s.divider} />

      <ToggleRow title="Jonli manbalar va 3D" sub="Video paytida: Wikimedia, NASA fotolari, 3D ikonlar (3dicons, Icons8), aylanadigan 3D modellar (Poly Haven, Objaverse)"
        on={draft.liveAssets} onChange={(v) => set({ liveAssets: v })} />
      <Row title="Aylanadigan 3D modellar" desc="bitta videoga, 0 – o'chiq">
        <Stepper label="3D modellar soni" value={draft.max3dModels} min={0} max={8} onChange={(v) => set({ max3dModels: v })} />
      </Row>

      <hr className={s.divider} />

      <ToggleRow title="OpenFilm stok qidiruvi" sub="Claude har sahnaga mosini tanlaydi · doim yoqiq" on disabled onChange={() => {}} />
      <div className="grid2">
        <div className={s.counter}>
          <span className="label">Rasm / sahna</span>
          <Stepper label="Sahnaga rasm nomzodlari" value={draft.stockImages} min={1} max={8} onChange={(v) => set({ stockImages: v })} />
        </div>
        <div className={s.counter}>
          <span className="label">Klip / sahna</span>
          <Stepper label="Sahnaga video nomzodlari" value={draft.stockClips} min={0} max={4} onChange={(v) => set({ stockClips: v })} />
        </div>
      </div>

      <hr className={s.divider} />

      <div className={s.keys}>
        <div className={s.groupHead}><span className="label">Stok foto kalitlari</span><span className="sub">ixtiyoriy</span></div>
        <StockKey name="Pexels" placeholderNew="Bepul: pexels.com/api" stored={saved.pexelsKeySet} keep={draft.pexelsKeySet} value={draft.pexelsApiKey}
          error={badKey(draft.pexelsApiKey)} onKeep={(v) => set({ pexelsKeySet: v, pexelsApiKey: "" })} onValue={(v) => set({ pexelsApiKey: v })} />
        <StockKey name="Pixabay" placeholderNew="Bepul: pixabay.com/api/docs" stored={saved.pixabayKeySet} keep={draft.pixabayKeySet} value={draft.pixabayApiKey}
          error={badKey(draft.pixabayApiKey)} onKeep={(v) => set({ pixabayKeySet: v, pixabayApiKey: "" })} onValue={(v) => set({ pixabayApiKey: v })} />
      </div>

      <Foot hint="Kalit bo'lmasa — faqat ochiq manbalar" dirty={dirty} saving={busy.is(TITLE)} done={busy.done(TITLE)}
        dirtyHint={invalid ? "Kalitni tekshiring" : changed(draft, saved, {
          realMedia: "Real rasmlar", maxMedia: "Rasmlar soni", stockImages: "Rasm nomzodlari", stockClips: "Klip nomzodlari",
          liveAssets: "Jonli manbalar", max3dModels: "3D modellar",
          pexelsKeySet: "Pexels kaliti", pexelsApiKey: "Pexels kaliti", pixabayKeySet: "Pixabay kaliti", pixabayApiKey: "Pixabay kaliti",
        })}
        onReset={() => setDraft(saved)}
        onSave={async () => {
          if (invalid) { haptic.fail(); return; }
          const r = await save(TITLE, (d) => ({
            ...d,
            visuals: {
              ...d.visuals, realMedia: draft.realMedia, maxMedia: draft.maxMedia, stockImages: draft.stockImages, stockClips: draft.stockClips,
              liveAssets: draft.liveAssets, max3dModels: draft.max3dModels,
              pexelsKeySet: draft.pexelsKeySet, pixabayKeySet: draft.pixabayKeySet,
              pexelsApiKey: draft.pexelsApiKey.trim(), pixabayApiKey: draft.pixabayApiKey.trim(),
            },
          }));
          if (r) setDraft(pickMedia(r));
        }} />
    </Card>
  );
}

// ------------------------------------------------------------------ 3. Subtitr

const STYLES: { id: SubtitleStyle; name: string; sub: string }[] = [
  { id: "bold", name: "Qalin", sub: "Oq matn, qora kontur, so'z rangli" },
  { id: "box", name: "Plashka", sub: "Qorong'i fon, so'z ajratilgan" },
  { id: "clean", name: "Ixcham", sub: "Oddiy registr, yumshoq soya" },
  { id: "karaoke", name: "Karaoke", sub: "So'zlar aytilishi bilan chiqadi" },
];

/** Subtitr ko'rinishining jonli namunasi (worker'dagi captions.html uslubining soddalashtirilgani). */
function CaptionPreview({ v }: { v: SubtitleConfig }) {
  const words = ["Bees", "see", "colours", "we", "can't"];
  const on = 2;
  const caps = v.uppercase && v.style !== "clean";
  const size = Math.round((v.style === "clean" ? 15 : 19) * v.fontScale);
  const top = `${Math.round((v.position > 0 ? v.position : 0.8) * 100)}%`;
  return (
    <div className={s.capPreview} aria-hidden>
      <div className={s.capLine} style={{
        top, fontSize: size, fontWeight: v.style === "clean" ? 700 : 900, fontFamily: v.font || undefined,
        background: v.style === "box" ? `${v.boxColor}d9` : undefined, padding: v.style === "box" ? "3px 8px" : undefined, borderRadius: 8,
      }}>
        {words.slice(0, v.maxWords > 0 ? v.maxWords : 3).map((w, i) => {
          const active = i === on;
          const hidden = v.style === "karaoke" && i > on;
          return (
            <span key={w} style={{
              margin: "0 3px", display: "inline-block", opacity: hidden ? 0 : v.style === "clean" && !active ? 0.72 : 1,
              color: v.style === "box" && active ? v.boxColor : active ? v.highlightColor : v.textColor,
              background: v.style === "box" && active ? v.highlightColor : undefined, borderRadius: 4, padding: v.style === "box" && active ? "0 3px" : undefined,
              WebkitTextStroke: v.style === "bold" || v.style === "karaoke" ? "1.2px #000" : undefined, paintOrder: "stroke fill",
              textShadow: v.style === "clean" ? "0 1px 6px #000c" : "0 2px 6px #000b", transform: active && v.style !== "clean" ? "scale(1.12)" : undefined,
            }}>{caps ? w.toUpperCase() : w}</span>
          );
        })}
      </div>
    </div>
  );
}

function ColorPick({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <label className={s.color}>
      <input type="color" value={value} aria-label={label} onChange={(e) => onChange(e.target.value.toUpperCase())} />
      <span><b>{label}</b><span className="sub tnum">{value}</span></span>
    </label>
  );
}

function SubtitleCard({ config, save, busy }: CardProps) {
  const TITLE = "Subtitr";
  const { draft, setDraft, saved, dirty } = useDraft(config, (c) => ({ ...c.subtitles }));
  const set = (p: Partial<SubtitleConfig>) => setDraft((d) => ({ ...d, ...p }));
  return (
    <Card title={TITLE} sub="Videoga qo'shiladigan subtitr: so'zma-so'z ovoz bilan yonadi"
      action={<Toggle on={draft.designed} onChange={(v) => set({ designed: v })} label="Dizaynli subtitr" />}>
      <div className={s.capWrap}>
        <CaptionPreview v={draft} />
        <div className={s.capStyles} role="radiogroup" aria-label="Subtitr uslubi">
          {STYLES.map((x) => (
            <button key={x.id} type="button" role="radio" aria-checked={draft.style === x.id} disabled={!draft.designed}
              className={`${s.capStyle} ${draft.style === x.id ? s.on : ""}`} onClick={() => set({ style: x.id })}>
              <b>{x.name}</b><span>{x.sub}</span>
            </button>
          ))}
        </div>
      </div>
      <Row title="Shrift o'lchami" desc="50–200%">
        <Stepper label="Subtitr o'lchami" value={Math.round(draft.fontScale * 100)} min={50} max={200} step={10} suffix="%" onChange={(v) => set({ fontScale: v / 100 })} />
      </Row>
      <Row title="Iboradagi so'zlar" desc="0 – avtomatik (vertikal 3, gorizontal 5)">
        <Stepper label="Iboradagi so'zlar" value={draft.maxWords} min={0} max={8} onChange={(v) => set({ maxWords: v })} />
      </Row>
      <Row title="Joylashuv (balandlik)" desc="0 – avtomatik; 50–92% pastdan">
        <Stepper label="Subtitr joyi" value={Math.round(draft.position * 100)} min={0} max={92} step={2} suffix="%"
          onChange={(v) => set({ position: v === 0 ? 0 : Math.max(50, v) / 100 })} />
      </Row>
      <ToggleRow title="KATTA HARFLAR" sub="Ixcham uslubda doim oddiy registr" on={draft.uppercase} onChange={(v) => set({ uppercase: v })} />
      <div className={s.colors}>
        <ColorPick label="Matn" value={draft.textColor} onChange={(v) => set({ textColor: v })} />
        <ColorPick label="Aytilayotgan so'z" value={draft.highlightColor} onChange={(v) => set({ highlightColor: v })} />
        <ColorPick label="Plashka" value={draft.boxColor} onChange={(v) => set({ boxColor: v })} />
      </div>
      <Foot hint={draft.designed ? "Yangi videolardan boshlab" : "O'chiq: OpenFilm'ning oddiy subtitri"} dirty={dirty} saving={busy.is(TITLE)} done={busy.done(TITLE)}
        dirtyHint={changed(draft, saved, {
          designed: "Dizaynli subtitr", style: "Uslub", fontScale: "O'lcham", maxWords: "So'zlar soni", position: "Joylashuv",
          uppercase: "Katta harflar", textColor: "Rang", highlightColor: "Rang", boxColor: "Rang",
        })}
        onReset={() => setDraft(saved)}
        onSave={async () => {
          const r = await save(TITLE, (d) => ({ ...d, subtitles: { ...draft } }));
          if (r) setDraft({ ...r.subtitles });
        }} />
    </Card>
  );
}

/**
 * Bitta stok kaliti: saqlangan bo'lsa «Saqlangan» + «Almashtirish» / «O'chirish» (saqlanganda o'chadi),
 * saqlanmagan bo'lsa kiritish maydoni. Kalitning o'zi serverdan hech qachon qaytmaydi.
 */
function StockKey({ name, placeholderNew, stored, keep, value, error, onKeep, onValue }: {
  name: string; placeholderNew: string; stored: boolean; keep: boolean; value: string; error: boolean; onKeep: (v: boolean) => void; onValue: (v: string) => void;
}) {
  const [replacing, setReplacing] = useState(false);
  const removing = stored && !keep;
  const showInput = !stored || removing || replacing || !!value;
  const status = value.trim() ? <Status tone="accent">Yangi kalit</Status>
    : removing ? <Status tone="bad">O&apos;chiriladi</Status>
    : stored ? <Status tone="ok">Saqlangan</Status> : <Status>Kiritilmagan</Status>;

  async function paste() {
    try {
      const t = (await navigator.clipboard.readText()).trim();
      if (t) onValue(t);
    } catch { haptic.fail(); }
  }

  return (
    <div className={s.key}>
      <div className={s.keyHead}>
        <span className={s.keyTile}><KeyRound size={18} aria-hidden /></span>
        <span className={s.keyText}><b>{name}</b>{status}</span>
        {stored && keep && !replacing && !value ? <>
          <Btn size="sm" tone="ghost" onClick={() => setReplacing(true)}>Almashtirish</Btn>
          <Btn size="sm" tone="ghost" icon={Trash2} className={s.remove} onClick={() => onKeep(false)}>O&apos;chirish</Btn>
        </> : stored && (removing || replacing || value) ? (
          <Btn size="sm" tone="ghost" icon={RotateCcw} onClick={() => { setReplacing(false); onKeep(true); }}>Qaytarish</Btn>
        ) : null}
      </div>
      {showInput ? (
        <div className="field">
          <span className={s.keyInput}>
            <input className={`inp ${error ? "err" : ""}`} type="password" autoComplete="off" spellCheck={false} aria-label={`${name} API kaliti`} aria-invalid={error || undefined}
              value={value} placeholder={stored ? "O'zgartirish uchun yangisini kiriting" : placeholderNew}
              onChange={(e) => onValue(e.target.value)} />
            <button type="button" className={s.paste} aria-label="Buferdan qo'yish" onClick={paste}><ClipboardPaste size={18} /></button>
          </span>
          {error ? <div className="field-err">{name} kaliti noto&apos;g&apos;ri ko&apos;rinishda: bo&apos;sh joysiz harf/raqam satri (10–120 belgi).</div>
            : removing ? <div className="hint">Saqlanganda kalit o&apos;chiriladi.</div> : null}
        </div>
      ) : null}
    </div>
  );
}

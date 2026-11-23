"use client";

/**
 * «Video va sifat» (dizayn: Sozlamalar → Video va sifat euFqPV, to'liq sahifa yX7XZY).
 * To'rt karta, har biri o'z qismini o'zi saqlaydi (saveConfig): yangi video standartlari, real rasm va ikonlar,
 * shablonga moslik, render. Render - kompyuter darajasidagi sozlama: qo'shimcha token (mehmon) uchun ko'rsatilmaydi.
 */
import { useCallback, useState, type ReactNode } from "react";
import { AudioLines, Check, ChevronDown, ClipboardPaste, KeyRound, LayoutTemplate, Lock, Minus, Plus, RotateCcw, Trash2 } from "lucide-react";
import { confirmBox, haptic } from "@/lib/client";
import { differs, useBotConfig, type ConfigDraft } from "@/lib/settings-data";
import { FormatText, type BotConfig, type Catalog } from "@/lib/types";
import { FormatLength } from "@/lib/video-rules";
import { Alert, Btn, Card, PageHead, SaveBtn, SkelCard, Status, Toggle, ToggleRow, useBusy } from "../ui";
import type { SettingsPageProps } from "./types";
import s from "./VideoQuality.module.css";

type CardProps = { config: BotConfig; save: (title: string, patch: (d: ConfigDraft) => ConfigDraft) => Promise<BotConfig | false>; busy: ReturnType<typeof useBusy> };

const CONFIRM = "Kompyuterlar ~2 daqiqada yangilanadi, yangi videolardan boshlab qo'llanadi.";

export default function VideoQuality({ state, toast, back, onChanged }: SettingsPageProps) {
  const guest = state?.account.guest === true;
  const host = state?.account.hostLabel;
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
        sub="Yangi videolar qanday ko'rinishda chiqadi: format, rasmlar, shablon va render" />

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
          <StyleCard {...props} catalog={state?.catalog ?? null} />
          {guest ? (
            <Card title="Render" sub="OpenFilm tayyor videoni shunday chiqaradi" action={<Status>Kompyuter egasida</Status>}>
              <div className="sub">
                Videolaringiz {host ? <b>«{host}»</b> : "token bergan hisob"} kompyuterida render qilinadi: kadr tezligi, ovoz balandligi,
                subtitr va vaqt chegarasini kompyuter egasi sozlaydi.
              </div>
            </Card>
          ) : <RenderCard {...props} />}
        </> : !error ? <>
          <SkelCard lines={6} /><SkelCard lines={6} /><SkelCard lines={5} /><SkelCard lines={5} />
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
function Stepper({ value, min, max, step = 1, onChange, label, suffix, compact }: {
  value: number; min: number; max: number; step?: number; onChange: (v: number) => void; label: string; suffix?: string; compact?: boolean;
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
    <div className={`${s.stepper} ${compact ? s.compact : ""}`} role="group" aria-label={label}>
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
function Pills({ label, options, value, onChange, fmt }: { label: string; options: number[]; value: number; onChange: (v: number) => void; fmt: (v: number) => string }) {
  const all = options.includes(value) ? options : [...options, value].sort((a, b) => a - b);
  return (
    <div className={s.pills} role="radiogroup" aria-label={label}>
      {all.map((v) => (
        <button key={v} type="button" role="radio" aria-checked={v === value} className={v === value ? s.on : ""} onClick={() => onChange(v)}>{fmt(v)}</button>
      ))}
    </div>
  );
}

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
  realMedia: boolean; maxMedia: number; stockImages: number; stockClips: number;
  pexelsKeySet: boolean; pixabayKeySet: boolean; pexelsApiKey: string; pixabayApiKey: string;
};
const pickMedia = (c: BotConfig): MediaDraft => ({
  realMedia: c.visuals.realMedia, maxMedia: c.visuals.maxMedia, stockImages: c.visuals.stockImages, stockClips: c.visuals.stockClips,
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

      <ToggleRow title="OpenFilm stok qidiruvi" sub="Claude har sahnaga mosini tanlaydi · doim yoqiq" on disabled onChange={() => {}} />
      <div className="grid2">
        <div className={s.counter}>
          <span className="label">Rasm / sahna</span>
          <Stepper compact label="Sahnaga rasm nomzodlari" value={draft.stockImages} min={1} max={8} onChange={(v) => set({ stockImages: v })} />
        </div>
        <div className={s.counter}>
          <span className="label">Klip / sahna</span>
          <Stepper compact label="Sahnaga video nomzodlari" value={draft.stockClips} min={0} max={4} onChange={(v) => set({ stockClips: v })} />
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
          pexelsKeySet: "Pexels kaliti", pexelsApiKey: "Pexels kaliti", pixabayKeySet: "Pixabay kaliti", pixabayApiKey: "Pixabay kaliti",
        })}
        onReset={() => setDraft(saved)}
        onSave={async () => {
          if (invalid) { haptic.fail(); return; }
          const r = await save(TITLE, (d) => ({
            ...d,
            visuals: {
              ...d.visuals, realMedia: draft.realMedia, maxMedia: draft.maxMedia, stockImages: draft.stockImages, stockClips: draft.stockClips,
              pexelsKeySet: draft.pexelsKeySet, pixabayKeySet: draft.pixabayKeySet,
              pexelsApiKey: draft.pexelsApiKey.trim(), pixabayApiKey: draft.pixabayApiKey.trim(),
            },
          }));
          if (r) setDraft(pickMedia(r));
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
          <span className={`${s.keyInput} ${error ? s.err : ""}`}>
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

// ------------------------------------------------------------------ 3. Shablonga moslik

type StyleDraft = { templateStyleGuide: boolean; templateMotion: boolean; styleReview: boolean; minStyleScore: number };
const pickStyle = (c: BotConfig): StyleDraft => ({
  templateStyleGuide: c.visuals.templateStyleGuide, templateMotion: c.visuals.templateMotion, styleReview: c.visuals.styleReview, minStyleScore: c.visuals.minStyleScore,
});
const TEMPLATE_KIND: Record<string, string> = { Mp4Reference: "video namuna", OpenFilmProject: "OpenFilm loyihasi" };

function StyleCard({ config, save, busy, catalog }: CardProps & { catalog: Catalog | null }) {
  const TITLE = "Shablonga moslik";
  const { draft, setDraft, saved, dirty } = useDraft(config, pickStyle);
  const set = (p: Partial<StyleDraft>) => setDraft((d) => ({ ...d, ...p }));
  const tpl = config.defaults.template;
  const kind = catalog?.templates.find((t) => t.name === tpl)?.kind;
  const pct = (draft.minStyleScore / 10) * 100;

  return (
    <Card title={TITLE} sub="Video tanlangan shablon uslubida chiqadi">
      <div className={s.tpl}>
        <span className={`${s.thumb} ${tpl ? "" : s.none}`} aria-hidden><i /><i /><i /></span>
        <span className={s.tplText}>
          <b>{tpl || "Shablonsiz"}</b>
          <span className="sub">{tpl ? `Standart shablon${kind ? ` · ${TEMPLATE_KIND[kind] ?? kind}` : ""}` : "Yangi video standartlarida shablon tanlanmagan"}</span>
        </span>
      </div>

      <div className={s.toggles}>
        <ToggleRow title="Uslub qo'llanmasi" sub="Rang va shrift shablondan olinadi · doim yoqiq" on={draft.templateStyleGuide} disabled onChange={() => {}} />
        <ToggleRow title="Harakat va o'tishlar" sub="Shablondagi animatsiyalar takrorlanadi · doim yoqiq" on={draft.templateMotion} disabled onChange={() => {}} />
        <ToggleRow title="Tayyor videoni solishtirish" sub="Mos kelmasa Claude tuzattiradi" on={draft.styleReview} onChange={(v) => set({ styleReview: v })} />
      </div>

      <hr className={s.divider} />

      <div className={`${s.score} ${draft.styleReview ? "" : s.dim}`}>
        <div className={s.readout}>
          <span className={`${s.big} tnum`}>{draft.minStyleScore.toFixed(1).replace(/\.0$/, "")}<span>/10</span></span>
          <span className={s.scoreText}><span className="label">Minimal moslik</span><span className="sub">Bundan past chiqsa — qayta tuzatiladi</span></span>
        </div>
        <input type="range" className={s.range} min={0} max={10} step={0.5} value={draft.minStyleScore} aria-label="Minimal moslik bali (0–10)"
          style={{ ["--p" as string]: `${pct}%` }} onChange={(e) => set({ minStyleScore: Number(e.target.value) })} />
        <div className={`${s.scale} tnum`} aria-hidden><span>0</span><span>5</span><span>10</span></div>
      </div>

      <Foot hint="Tekshiruv yoqilganda ishlaydi" dirty={dirty} saving={busy.is(TITLE)} done={busy.done(TITLE)}
        dirtyHint={changed(draft, saved, { styleReview: "Solishtirish", minStyleScore: "Minimal moslik" })}
        onReset={() => setDraft(saved)}
        onSave={async () => {
          const r = await save(TITLE, (d) => ({ ...d, visuals: { ...d.visuals, ...draft } }));
          if (r) setDraft(pickStyle(r));
        }} />
    </Card>
  );
}

// ------------------------------------------------------------------ 4. Render (faqat kompyuter egasi)

const pickRender = (c: BotConfig): BotConfig["openFilm"] => ({ ...c.openFilm });
const lufs = (v: number) => `${v < 0 ? "−" : ""}${Math.abs(v)} LUFS`;

function RenderCard({ config, save, busy }: CardProps) {
  const TITLE = "Render";
  const { draft, setDraft, saved, dirty } = useDraft(config, pickRender);
  const set = (p: Partial<BotConfig["openFilm"]>) => setDraft((d) => ({ ...d, ...p }));

  return (
    <Card title={TITLE} sub="OpenFilm tayyor videoni shunday chiqaradi">
      <div className="field">
        <div className={s.groupHead}><span className="label">Kadr tezligi</span></div>
        <Pills label="Kadr tezligi" options={[24, 30, 60]} value={draft.fps} onChange={(v) => set({ fps: v })} fmt={(v) => `${v} fps`} />
      </div>
      <div className="field">
        <div className={s.groupHead}><span className="label">Ovoz balandligi</span><span className="sub">YouTube: −14</span></div>
        <Pills label="Ovoz balandligi" options={[-16, -14, -12]} value={draft.loudnessLufs} onChange={(v) => set({ loudnessLufs: v })} fmt={lufs} />
      </div>

      <div className={s.toggles}>
        <ToggleRow title="Subtitrni kuydirish" sub="Matn kadrning o'ziga yoziladi" on={draft.burnSubtitles} onChange={(v) => set({ burnSubtitles: v })} />
        <ToggleRow title="Renderdan keyin to'liq ko'rish" sub="Claude videoni oxirigacha tekshiradi" on={draft.lookFullAfterRender} onChange={(v) => set({ lookFullAfterRender: v })} />
      </div>

      <hr className={s.divider} />

      <Row title="Parallel render" desc="1–4 ta video bir vaqtda · 8 GB RAM: 1">
        <Stepper label="Parallel render" value={draft.renderWorkers} min={1} max={4} onChange={(v) => set({ renderWorkers: v })} />
      </Row>
      <Row title="Vaqt chegarasi" desc="Oshsa — render to'xtatiladi · 5–600 daq">
        <Stepper label="Render vaqt chegarasi (daqiqa)" value={draft.renderTimeoutMinutes} min={5} max={600} step={15} suffix="daq" onChange={(v) => set({ renderTimeoutMinutes: v })} />
      </Row>

      <Foot hint="Keyingi renderdan qo'llanadi" dirty={dirty} saving={busy.is(TITLE)} done={busy.done(TITLE)}
        dirtyHint={changed(draft, saved, {
          fps: "Kadr tezligi", loudnessLufs: "Ovoz balandligi", burnSubtitles: "Subtitr", lookFullAfterRender: "To'liq ko'rish",
          renderWorkers: "Parallel render", renderTimeoutMinutes: "Vaqt chegarasi",
        })}
        onReset={() => setDraft(saved)}
        onSave={async () => {
          const r = await save(TITLE, (d) => ({ ...d, openFilm: { ...draft } }));
          if (r) setDraft(pickRender(r));
        }} />
    </Card>
  );
}

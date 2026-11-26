"use client";

/**
 * Sozlamalar → «AI va promptlar» (dizayn: jODCJH, «· pastroq» rZoqDA, «· Ssenariy prompti» wy2G7R).
 * Har karta o'z qismini o'zi saqlaydi: Model zanjiri (GeminiLimits.tsx), Gemini kalitlari, Claude (montaj), Promptlar (Prompts.tsx).
 * Gemini kaliti yo'q bo'lsa (banner'dan kelinganda ham) kalitlar kartasi birinchi turadi va maydon fokuslanadi.
 */
import { useEffect, useRef, useState } from "react";
import { ArrowUp, ChevronsUpDown, ExternalLink, KeyRound, Plus, X } from "lucide-react";
import { confirmBox, haptic, openExternal } from "@/lib/client";
import { useBotConfig, type ConfigDraft } from "@/lib/settings-data";
import type { BotConfig, GeminiKeyInfo, PromptKey } from "@/lib/types";
import type { Toast } from "../App";
import { ModelChainCard } from "../GeminiLimits";
import PromptEditor, { PromptRows, usePrompts } from "../Prompts";
import { Alert, Btn, Card, Field, PageHead, SaveBtn, SkelCard, Status, useBusy } from "../ui";
import type { SettingsPageProps } from "./types";
import s from "./AiPrompts.module.css";

/**
 * Claude Code (montaj) modeli: `claude --model <id>` ga uzatiladi. Bo'sh - Claude Code'ning o'z standarti.
 * Fable - eng kuchli va eng qimmat; obunangizda bo'lmasa Claude Code xato beradi.
 */
const CLAUDE_MODELS: [string, string][] = [
  ["", "Claude Code standarti"],
  ["claude-opus-5-5", "Claude Opus 5.5 — tavsiya"],
  ["claude-sonnet-5-5", "Claude Sonnet 5.5 — tezroq, arzonroq"],
  ["claude-haiku-5-5", "Claude Haiku 5.5 — eng tez va arzon"],
  ["claude-fable-5-2", "Claude Fable 5.2 — eng kuchli, eng qimmat"],
];

type SaveConfig = (patch: (d: ConfigDraft) => ConfigDraft) => Promise<BotConfig>;
type CardProps = { config: BotConfig | null; saveConfig: SaveConfig; toast: Toast; onSaved: () => void };

/** Server kalitning boshi va oxirini beradi ("AIza…Qx7f"): dizayndagidek yashirilgan ko'rinish. */
const mask = (hint: string) => hint.replace("…", " •••• •••• ");

const savedMsg = "Saqlandi. Yangi videolardan boshlab qo'llanadi.";

export default function AiPrompts({ state, toast, run, back, onChanged, setSubBack }: SettingsPageProps) {
  const guest = state?.account?.guest === true;
  const { config, error, reload, saveConfig } = useBotConfig();
  const prompts = usePrompts(toast);
  const [editing, setEditing] = useState<PromptKey | null>(null);
  const busy = useBusy((m) => toast(m, true));
  // Gemini kaliti yo'q: holatdan (banner shu bilan ko'rinadi) yoki config'dan.
  const noKeys = state?.gemini?.ready === false || (!!config && config.gemini.apiKeys.length === 0);

  const openEditor = (k: PromptKey | null) => { setEditing(k); window.scrollTo({ top: 0 }); };

  // Prompt muharriri ochiq: Telegram'ning «orqaga» tugmasi ro'yxatga qaytaradi (saqlanmagan qoralama usePrompts'da qoladi).
  useEffect(() => {
    if (!editing) return;
    setSubBack(() => { setEditing(null); window.scrollTo({ top: 0 }); });
    return () => setSubBack(null);
  }, [editing, setSubBack]);

  if (editing) return <PromptEditor k={editing} prompts={prompts} run={run} toast={toast} back={() => openEditor(null)} />;

  const cardProps: CardProps = { config, saveConfig, toast, onSaved: onChanged };
  const keysCard = <KeysCard {...cardProps} missing={noKeys} />;

  return (
    <div className="page">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="AI va promptlar" sub="Ssenariyni yozadigan va montajni qiladigan AI uzilmay ishlashi uchun" />

      {error && !config ? (
        <Alert>
          <div className={s.errRow}>
            <span>Sozlamalarni yuklab bo&apos;lmadi: {error}</span>
            <Btn size="sm" tone="secondary" loading={busy.is("reload")} onClick={() => void busy.run("reload", reload)}>Qayta urinish</Btn>
          </div>
        </Alert>
      ) : null}

      <div className={s.cards}>
        {noKeys ? keysCard : null}
        <ModelChainCard config={config} saveConfig={saveConfig} guest={guest} toast={toast} onSaved={onChanged} />
        {!noKeys ? keysCard : null}
        {guest ? (
          <Alert tone="note">Claude (montaj) sozlamalari va model limitlari kompyuter egasida. Gemini kalitlari va promptlar — o&apos;zingizniki.</Alert>
        ) : (
          <ClaudeCard {...cardProps} />
        )}
      </div>

      <PromptRows prompts={prompts} open={openEditor} />
    </div>
  );
}

// ------------------------------------------------------------------ Gemini kalitlari
function KeysCard({ config, saveConfig, toast, onSaved, missing }: CardProps & { missing: boolean }) {
  const busy = useBusy((m) => toast(m, true));
  /** Saqlangan kalitlar tartibi/o'chirilganlari (saqlanmagan); null - saqlangandek. */
  const [order, setOrder] = useState<GeminiKeyInfo[] | null>(null);
  const [text, setText] = useState("");
  const inputRef = useRef<HTMLTextAreaElement>(null);

  // Banner'dan kelinganda (kalit yo'q) - maydon darhol fokusda.
  useEffect(() => { if (missing && config) inputRef.current?.focus({ preventScroll: true }); }, [missing, config]);

  if (!config) return <SkelCard lines={3} />;
  const saved = config.gemini.apiKeys;
  const keys = order ?? saved;
  const listDirty = order !== null && order.map((k) => k.id).join() !== saved.map((k) => k.id).join();
  const removed = saved.filter((k) => !keys.some((x) => x.id === k.id));
  const fresh = text.split(/[\s,;]+/).map((k) => k.trim()).filter(Boolean);
  const freshErr = fresh.some((k) => k.length < 20) ? "Kalit noto'g'ri ko'rinishda (AIza… bilan boshlanadigan uzun satr bo'lishi kerak)." : null;

  async function save(withNew: boolean) {
    if (withNew && (!fresh.length || freshErr)) return;
    if (removed.length && !(await confirmBox(`${removed.length} ta Gemini kaliti o'chirilsinmi? ${removed.length === saved.length && !fresh.length ? "Kalitsiz mavzu qo'shish va AI yordamchi ishlamaydi." : "Qolganlari navbat bilan ishlatiladi."}`))) return;
    const ok = await busy.run(withNew ? "add" : "save", () => saveConfig((d) => ({
      // Ro'yxat o'zgartirilmagan bo'lsa serverdagi (yangi olingan) kalitlar qoladi - boshqa joyda qo'shilgani yo'qolmasin.
      ...d, gemini: { ...d.gemini, apiKeys: order ? keys.map((k) => ({ ...k })) : d.gemini.apiKeys, newApiKeys: withNew ? fresh : [] },
    })));
    if (ok === false) return;
    haptic.ok();
    setOrder(null);
    if (withNew) setText("");
    toast(withNew ? `${fresh.length} ta kalit qo'shildi.` : savedMsg);
    onSaved(); // Gemini kaliti qo'shilgan bo'lsa mavzular va AI yordamchi ochiladi
  }

  const moveUp = (i: number) => { const n = [...keys]; [n[i - 1], n[i]] = [n[i], n[i - 1]]; setOrder(n); };
  const remove = (id: string) => setOrder(keys.filter((k) => k.id !== id));

  const head = listDirty
    ? <SaveBtn dirty saving={busy.is("save")} onClick={() => void save(false)} />
    : keys.length ? <Status tone={busy.done("save") ? "ok" : "neutral"}>{keys.length} ta · navbat bilan</Status>
    : <Status tone="bad">Kiritilmagan</Status>;

  return (
    <Card title="Gemini kalitlari" action={head} className={missing ? s.missing : undefined}>
      {missing ? (
        <Alert tone="accent" icon={KeyRound}>
          Gemini kaliti yo&apos;q: mavzu qo&apos;shish va AI yordamchi o&apos;chiq, mavjud mavzular kutib turibdi. Google AI Studio&apos;da bepul kalit oling va shu yerga qo&apos;shing.
        </Alert>
      ) : null}

      {keys.length ? (
        <ul className={s.keys} aria-label="Saqlangan kalitlar, ishlatilish tartibida">
          {keys.map((k, i) => (
            <li key={k.id} className={s.key}>
              <span className={`${s.dot}${i === 0 ? " " + s.dotOn : ""}`} aria-hidden />
              <code className={s.hint}>{mask(k.hint)}</code>
              {i === 0 ? <span className="sub">ishlatilmoqda</span> : null}
              {i > 0 ? (
                <button type="button" className={s.iconBtn} aria-label={`${k.hint} kalitini bir pog'ona yuqoriga`} onClick={() => moveUp(i)}><ArrowUp size={18} /></button>
              ) : null}
              <button type="button" className={s.iconBtn} aria-label={`${k.hint} kalitini o'chirish`} onClick={() => remove(k.id)}><X size={18} /></button>
            </li>
          ))}
        </ul>
      ) : null}
      {removed.length ? <div className="hint">{removed.length} ta kalit o&apos;chiriladi — «Saqlash» ni bosing.</div> : null}

      <Field label={<label htmlFor="gemini-new-key">Yangi kalit</label>} error={freshErr}
        hint={fresh.length > 1 ? `${fresh.length} ta kalit qo'shiladi` : "Bir nechta kalit bo'lsa - har qatorga bittadan"}>
        <div className={s.addRow}>
          <textarea
            id="gemini-new-key" ref={inputRef} className={`inp ${s.keyInput}`} rows={1} autoComplete="off" autoCapitalize="off" autoCorrect="off" spellCheck={false}
            placeholder="AIza…" value={text} aria-invalid={!!freshErr} onChange={(e) => setText(e.target.value)}
          />
          <Btn icon={Plus} loading={busy.is("add")} disabled={!fresh.length || !!freshErr || busy.is("save")} onClick={() => void save(true)}>Qo&apos;shish</Btn>
        </div>
      </Field>

      <div className="hint">
        Kalitlar tartib bilan ishlatiladi: biri limitga (429) tushsa keyingisi bilan davom etiladi, hammasi tugasa ish to&apos;xtaydi. AI yordamchi ham shu kalitlardan foydalanadi.
      </div>
      {missing ? (
        <button type="button" className="linkbtn" onClick={() => openExternal("https://aistudio.google.com/apikey")}>Google AI Studio&apos;da kalit olish<ExternalLink size={14} /></button>
      ) : null}
    </Card>
  );
}

// ------------------------------------------------------------------ Claude (montaj)
type ClaudeEdit = { model: string; timeoutMinutes: string; maxBudgetUsd: string };
const claudeFrom = (c: BotConfig): ClaudeEdit => ({ model: c.claude.model, timeoutMinutes: String(c.claude.timeoutMinutes), maxBudgetUsd: String(c.claude.maxBudgetUsd) });

function ClaudeCard({ config, saveConfig, toast, onSaved }: CardProps) {
  const busy = useBusy((m) => toast(m, true));
  const [edit, setEdit] = useState<ClaudeEdit | null>(null);
  if (!config) return <SkelCard lines={4} />;
  const base = claudeFrom(config);
  const cur = edit ?? base;
  const dirty = JSON.stringify(cur) !== JSON.stringify(base);
  const set = (p: Partial<ClaudeEdit>) => setEdit({ ...cur, ...p });

  const tout = Number(cur.timeoutMinutes);
  const toutErr = !Number.isInteger(tout) || tout < 5 || tout > 600 ? "5 dan 600 gacha (butun son)" : null;
  const budget = cur.maxBudgetUsd.trim() === "" ? 0 : Number(cur.maxBudgetUsd.replace(",", "."));
  const budgetErr = !Number.isFinite(budget) || budget < 0 || budget > 1000 ? "0 dan 1000 gacha" : null;

  const known = CLAUDE_MODELS.find(([id]) => id === cur.model);
  const [name, tag] = (known?.[1] ?? cur.model).split(" — ");

  async function save() {
    if (toutErr || budgetErr) return;
    const ok = await busy.run("save", () => saveConfig((d) => ({ ...d, claude: { model: cur.model, timeoutMinutes: tout, maxBudgetUsd: budget } })));
    if (ok === false) return;
    setEdit(null);
    toast(savedMsg);
    onSaved();
  }

  return (
    <Card title="Claude (montaj)" action={<SaveBtn dirty={dirty && !toutErr && !budgetErr} saving={busy.is("save")} done={busy.done("save")} onClick={() => void save()} />}>
      <Field label={<label htmlFor="claude-model">Model</label>} hint="Sonnet tezroq va arzonroq · Fable eng kuchli">
        <div className={s.select}>
          <span className={s.selVal}><b>{name}</b>{tag ? <span className={tag === "tavsiya" ? s.tagAccent : s.tag}>{tag}</span> : null}</span>
          <ChevronsUpDown size={18} className={s.selIcon} />
          <select id="claude-model" value={cur.model} onChange={(e) => set({ model: e.target.value })}>
            {CLAUDE_MODELS.map(([id, label]) => <option key={id || "default"} value={id}>{label}</option>)}
            {cur.model && !known ? <option value={cur.model}>{cur.model}</option> : null}
          </select>
        </div>
      </Field>
      <div className="grid2">
        <Field label={<label htmlFor="claude-timeout">Timeout, daqiqa</label>} error={toutErr} hint="standart 45">
          <input id="claude-timeout" className="inp" inputMode="numeric" value={cur.timeoutMinutes} aria-invalid={!!toutErr} onChange={(e) => set({ timeoutMinutes: e.target.value })} />
        </Field>
        <Field label={<label htmlFor="claude-budget">Maks. byudjet, $</label>} error={budgetErr} hint="0 — cheklovsiz">
          <input id="claude-budget" className="inp" inputMode="decimal" value={cur.maxBudgetUsd} aria-invalid={!!budgetErr} onChange={(e) => set({ maxBudgetUsd: e.target.value })} />
        </Field>
      </div>
    </Card>
  );
}

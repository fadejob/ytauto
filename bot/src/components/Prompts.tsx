"use client";

/**
 * Promptlar (dizayn: Sozlamalar → «AI va promptlar · Ssenariy prompti» wy2G7R). Barcha kompyuterlar uchun umumiy (online baza).
 * - usePrompts: saqlangan matn va qoralama (sahifa ichida ro'yxat ↔ muharrir almashganda qoralama yo'qolmaydi).
 * - PromptRows: «Promptlar» guruhi - ikki qator, har biri muharrirni ochadi.
 * - default PromptEditor: placeholder'lari ajratib ko'rsatiladigan muharrir, «Saqlash», «Bekor», «Standartga qaytarish».
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { Clapperboard, FileText, MessageCircleQuestion, RotateCcw } from "lucide-react";
import { api, cache, confirmBox, haptic } from "@/lib/client";
import type { PromptKey, Prompts as PromptsT } from "@/lib/types";
import type { Run, Toast } from "./App";
import { Btn, PageHead, SettingsRow, Skel, Status, useBusy } from "./ui";
import s from "./Prompts.module.css";

export const PROMPT_LABEL: Record<PromptKey, { title: string; short: string; row: string; sub: string; confirmName: string }> = {
  script: { title: "Ssenariy prompti", short: "Ssenariy", row: "Gemini · matn, subtitr, sarlavha", sub: "Gemini ssenariyni shu ko'rsatma bo'yicha yozadi", confirmName: "Ssenariy" },
  montage: { title: "Montaj prompti", short: "Montaj", row: "Claude Code · sahnalar va dizayn", sub: "Claude Code va OpenFilm montajni shu ko'rsatma bo'yicha qiladi", confirmName: "Montaj" },
};

/** Ssenariy promptidagi placeholder'lar va ularning o'rniga nima qo'yilishi (worker: GeminiScriptGenerator.Fill, ChannelPrompt). */
const SCRIPT_PLACEHOLDERS: [string, string][] = [
  ["{topic}", "Mavzu sarlavhasi"],
  ["{notes}", "Mavzuga yozilgan izoh"],
  ["{language_name}", "Til nomi (English)"],
  ["{language}", "Til kodi (en)"],
  ["{length_rule}", "16:9 — 2–3 daqiqa\nShorts — 55 s gacha"],
  ["{format}", "Video formati"],
  ["{fact_sheet}", "Google qidiruvidan yig'ilgan faktlar"],
  ["{channel_context}", "«Kanal» bo'limidagi uslub (promptda bo'lmasa oxiriga qo'shiladi)"],
];
const PH_RX = /(\{[a-z_]+\})/g;

// ------------------------------------------------------------------ ma'lumot
export function usePrompts(toast: Toast) {
  const [saved, setSaved] = useState<PromptsT | null>(null);
  const [draft, setDraft] = useState<PromptsT | null>(null);
  useEffect(() => {
    const cached = cache.get<PromptsT>("prompts");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi
    if (cached) { setSaved(cached); setDraft(cached); } // darhol, keyin bazadagi yangisi
    api<PromptsT>("/api/prompts")
      .then((p) => { setSaved(p); setDraft(p); cache.set("prompts", p); })
      .catch((e) => toast(e instanceof Error ? e.message : String(e), true));
  }, [toast]);
  const dirty = useCallback((k: PromptKey) => !!saved && !!draft && draft[k] !== saved[k], [saved, draft]);
  return { saved, setSaved, draft, setDraft, dirty };
}
export type PromptsState = ReturnType<typeof usePrompts>;

// ------------------------------------------------------------------ ro'yxat
export function PromptRows({ prompts, open }: { prompts: PromptsState; open: (k: PromptKey) => void }) {
  return (
    <section className={s.section} aria-labelledby="prompts-label">
      <div className="section-label"><span id="prompts-label">Promptlar</span><span className={s.common}>barcha kompyuterlar uchun umumiy</span></div>
      <div className="group">
        {(["script", "montage"] as PromptKey[]).map((k) => (
          <SettingsRow key={k} icon={k === "script" ? FileText : Clapperboard} title={PROMPT_LABEL[k].title}
            summary={prompts.dirty(k) ? "Saqlanmagan o'zgarish bor" : PROMPT_LABEL[k].row} onClick={() => { haptic.tap(); open(k); }} />
        ))}
      </div>
    </section>
  );
}

// ------------------------------------------------------------------ muharrir
/** Matn ostidagi nusxa: placeholder'lar ajratiladi (textarea shaffof, kursor ko'rinadi; balandlik matnga qarab o'sadi). */
function Highlight({ text }: { text: string }) {
  const parts = text.split(PH_RX);
  return (
    <div className={s.mirror} aria-hidden>
      {parts.map((p, i) => (i % 2 === 1 ? <mark key={i} className={s.ph}>{p}</mark> : <span key={i}>{p}</span>))}
      {"\n "}
    </div>
  );
}

export default function PromptEditor({ k, prompts, run, toast, back }: { k: PromptKey; prompts: PromptsState; run: Run; toast: Toast; back: () => void }) {
  const { saved, setSaved, draft, setDraft } = prompts;
  // Bo'sh xabar - run() xatoni allaqachon toast qilgan.
  const busy = useBusy((m) => { if (m) toast(m, true); });
  const L = PROMPT_LABEL[k];
  const dirty = prompts.dirty(k);
  const text = draft?.[k] ?? "";
  const used = useMemo(() => new Set(text.match(PH_RX) ?? []), [text]);

  async function save() {
    if (!draft || !dirty) return;
    if (!(await confirmBox(`${L.confirmName} prompti saqlansinmi? Keyingi yangi videolardan boshlab ishlaydi.`))) return;
    const value = draft[k];
    await busy.run("save", async () => {
      const [r] = await run([{ type: "update_prompt", key: k, text: value }]);
      if (!r?.ok) throw new Error(""); // xabarni run() allaqachon ko'rsatdi
      setSaved((cur) => { const next = { ...(cur ?? draft), [k]: value }; cache.set("prompts", next); return next; });
    });
  }

  /** Ilova bilan keladigan standart promptni qaytaradi (hozirgi matn almashtiriladi, darhol saqlanadi). */
  async function resetDefault() {
    if (!(await confirmBox(`${L.confirmName} prompti standart holatga qaytarilsinmi? Hozirgi matn almashtiriladi.`))) return;
    await busy.run("reset", async () => {
      const [r] = await run([{ type: "update_prompt", key: k, reset: true }]);
      if (!r?.ok) throw new Error("");
      const fresh = await api<PromptsT>("/api/prompts").catch(() => null);
      if (fresh) { setSaved(fresh); setDraft(fresh); cache.set("prompts", fresh); }
    });
  }

  const working = busy.is("save") || busy.is("reset");
  return (
    <div className="page">
      <PageHead back={{ label: "AI va promptlar", onClick: back }} title={L.title} sub={L.sub} />

      <section className="card" aria-label={L.title}>
        <div className={s.head}>
          {!draft ? <Skel h={24} w={110} r={999} /> : dirty ? <Status tone="accent">Saqlanmagan</Status> : <Status>Saqlangan</Status>}
          <Btn tone="ghost" size="sm" icon={RotateCcw} loading={busy.is("reset")} disabled={!draft || busy.is("save")} onClick={() => void resetDefault()}>
            Standartga qaytarish
          </Btn>
        </div>

        {draft ? (
          <div className={`${s.editor}${dirty ? " " + s.editorDirty : ""}`}>
            <Highlight text={text} />
            <textarea
              className={s.area} spellCheck={false} autoCapitalize="off" autoCorrect="off" aria-label={L.title} value={text} readOnly={working}
              onChange={(e) => setDraft({ ...draft, [k]: e.target.value })}
            />
          </div>
        ) : (
          <div className={s.skel} aria-busy="true" aria-label="Yuklanmoqda">
            {[100, 92, 96, 70, 88, 60].map((w, i) => <Skel key={i} h={13} w={`${w}%`} />)}
          </div>
        )}

        <div className={s.foot}>
          <span className="sub">Yangi videolardan boshlab ishlaydi</span>
          <Btn tone="secondary" disabled={!dirty || working} onClick={() => saved && draft && setDraft({ ...draft, [k]: saved[k] })}>Bekor</Btn>
          <Btn disabled={!dirty} loading={busy.is("save")} done={busy.done("save") && !dirty} onClick={() => void save()}>
            {busy.done("save") && !dirty ? "Saqlandi" : "Saqlash"}
          </Btn>
        </div>
      </section>

      {k === "script" ? (
        <section className="card" aria-labelledby="ph-label">
          <span className="label" id="ph-label">O&apos;rniga nima qo&apos;yiladi</span>
          <dl className={s.phList}>
            {SCRIPT_PLACEHOLDERS.map(([ph, what]) => (
              <div key={ph} className={`${s.phRow}${draft && !used.has(ph) ? " " + s.phMissing : ""}`}>
                <dt><code className={s.chip}>{ph}</code></dt>
                <dd>{what}{draft && !used.has(ph) ? <span className={s.missing}>promptda yo&apos;q</span> : null}</dd>
              </div>
            ))}
          </dl>
          <div className={s.note}>
            <MessageCircleQuestion size={18} />
            <span><b>Boshlang&apos;ich savol</b> promptga o&apos;zi qo&apos;shiladi: ovoz har doim shu savol bilan boshlanadi.</span>
          </div>
        </section>
      ) : (
        <section className="card" aria-label="Montaj prompti haqida">
          <div className={s.note}>
            <Clapperboard size={18} />
            <span>Claude Code + OpenFilm: sahnalar va dizayn bo&apos;yicha ko&apos;rsatmalar. Kanal tahlilidagi montaj uslubi avtomatik <b>«Channel style»</b> bo&apos;limi bo&apos;lib qo&apos;shiladi.</span>
          </div>
        </section>
      )}
    </div>
  );
}

"use client";

import { useEffect, useState } from "react";
import { api, cache, confirmBox, haptic } from "@/lib/client";
import type { PromptKey, Prompts as PromptsT } from "@/lib/types";
import type { Run, Toast } from "./App";

const LABEL: Record<PromptKey, { title: string; hint: string }> = {
  script: { title: "Ssenariy", hint: "Gemini: matn, subtitr va YouTube title. {topic} {notes} {language} … placeholder'larini saqlang." },
  montage: { title: "Montaj", hint: "Claude Code + OpenFilm: sahnalar va dizayn bo'yicha ko'rsatmalar." },
};

export default function Prompts({ run, toast }: { run: Run; toast: Toast }) {
  const [key, setKey] = useState<PromptKey>("script");
  const [saved, setSaved] = useState<PromptsT | null>(null);
  const [draft, setDraft] = useState<PromptsT | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    const cached = cache.get<PromptsT>("prompts");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi
    if (cached) { setSaved(cached); setDraft(cached); } // darhol, keyin bazadagi yangisi
    api<PromptsT>("/api/prompts")
      .then((p) => { setSaved(p); setDraft(p); cache.set("prompts", p); })
      .catch((e) => toast(e instanceof Error ? e.message : String(e), true));
  }, [toast]);

  const dirty = !!saved && !!draft && draft[key] !== saved[key];

  async function save() {
    if (!draft || !dirty) return;
    if (!(await confirmBox(`${LABEL[key].title} prompti saqlansinmi? Keyingi yangi videolardan boshlab ishlaydi.`))) return;
    setSaving(true);
    const [r] = await run([{ type: "update_prompt", key, text: draft[key] }]);
    setSaving(false);
    if (r?.ok) { const next = { ...saved!, [key]: draft[key] }; setSaved(next); cache.set("prompts", next); }
  }

  /** Ilova bilan keladigan standart promptni qaytaradi (hozirgi matn almashtiriladi, darhol saqlanadi). */
  async function resetDefault() {
    if (!(await confirmBox(`${LABEL[key].title} prompti standart holatga qaytarilsinmi? Hozirgi matn almashtiriladi.`))) return;
    setSaving(true);
    const [r] = await run([{ type: "update_prompt", key, reset: true }]);
    setSaving(false);
    if (r?.ok) {
      const fresh = await api<PromptsT>("/api/prompts").catch(() => null);
      if (fresh) { setSaved(fresh); setDraft(fresh); cache.set("prompts", fresh); }
    }
  }

  return (
    <>
      <header className="head">
        <div>
          <h1>Promptlar</h1>
          <div className="sub">Barcha kompyuterlar uchun umumiy (online baza)</div>
        </div>
      </header>
      <div className="seg">
        {(Object.keys(LABEL) as PromptKey[]).map((k) => (
          <button key={k} className={key === k ? "on" : ""} onClick={() => { haptic.tap(); setKey(k); }}>
            {LABEL[k].title}{saved && draft && draft[k] !== saved[k] ? " •" : ""}
          </button>
        ))}
      </div>
      <div className="list">
        <div className="card formcard">
          <div className="hint">{LABEL[key].hint}</div>
          {draft ? (
            <textarea className="textarea code" spellCheck={false} value={draft[key]} onChange={(e) => setDraft({ ...draft, [key]: e.target.value })} />
          ) : (
            <div className="skeleton" style={{ height: 320 }} />
          )}
          <div className="grid2">
            <button className="btn block" disabled={!dirty || saving} onClick={() => saved && setDraft({ ...draft!, [key]: saved[key] })}>Qaytarish</button>
            <button className="btn primary block" disabled={!dirty || saving} onClick={save}>{saving ? "Saqlanmoqda…" : "Saqlash"}</button>
          </div>
          <button className="btn block" disabled={saving || !draft} onClick={resetDefault}>Standart promptga qaytarish</button>
        </div>
      </div>
    </>
  );
}

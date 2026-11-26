"use client";

import { useRef, type KeyboardEvent } from "react";
import { X } from "lucide-react";
import s from "./TagInput.module.css";

/** YouTube cheklovi: barcha teglar birga 500 belgigacha (vergullar bilan). */
export const TAGS_MAX_CHARS = 500;
const TAG_MAX = 100;

/** "a, b ,c" → ["a","b","c"] (bo'shlari va takrorlar - katta-kichik harf farqisiz - olib tashlanadi). */
export function parseTags(v: string): string[] {
  return addTags([], v.split(","));
}

export function joinTags(tags: string[]): string {
  return tags.join(", ");
}

function addTags(list: string[], items: string[]): string[] {
  const out = [...list];
  for (const raw of items) {
    const t = raw.replace(/\s+/g, " ").trim().replace(/^#/, "").slice(0, TAG_MAX);
    if (t && !out.some((x) => x.toLowerCase() === t.toLowerCase())) out.push(t);
  }
  return out;
}

/**
 * YouTube Studio'dagidek teglar maydoni: yozib vergul (yoki Enter) bosilsa teg tepada chip bo'lib chiqadi, × bilan o'chadi,
 * bo'sh maydonda Backspace oxirgi tegni tahrirga qaytaradi. Saqlash - kartaning o'z «Saqlash» tugmasida (pending matn ham qo'shiladi).
 * Vergul onChange'da ajratiladi: telefon klaviaturalarida keydown "," kelmasa ham ishlaydi, nusxa-joylash ham bo'linadi.
 */
export default function TagInput({ id, tags, pending, onTags, onPending, disabled, placeholder }: {
  id?: string; tags: string[]; pending: string; onTags: (t: string[]) => void; onPending: (v: string) => void;
  disabled?: boolean; placeholder?: string;
}) {
  const inp = useRef<HTMLInputElement>(null);
  const chars = joinTags(pending.trim() ? addTags(tags, [pending]) : tags).length;

  function change(v: string) {
    if (!v.includes(",")) { onPending(v); return; }
    const parts = v.split(",");
    const rest = parts.pop() ?? "";
    onTags(addTags(tags, parts));
    onPending(rest.trimStart());
  }
  function commit() {
    if (!pending.trim()) { if (pending) onPending(""); return; }
    onTags(addTags(tags, [pending]));
    onPending("");
  }
  function key(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter") { e.preventDefault(); commit(); return; }
    if (e.key === "Backspace" && !pending && tags.length) {
      e.preventDefault();
      onPending(tags[tags.length - 1]);
      onTags(tags.slice(0, -1));
    }
  }
  const remove = (t: string) => { onTags(tags.filter((x) => x !== t)); inp.current?.focus(); };

  return (
    <div className={`${s.box}${disabled ? " " + s.disabled : ""}`} onClick={() => inp.current?.focus()}>
      {tags.length ? (
        <ul className={s.chips} aria-label="Teglar">
          {tags.map((t) => (
            <li key={t} className={s.chip}>
              <span>{t}</span>
              <button type="button" className={s.x} aria-label={`«${t}» tegini o'chirish`} disabled={disabled}
                onClick={(e) => { e.stopPropagation(); remove(t); }}><X size={14} strokeWidth={2.5} /></button>
            </li>
          ))}
        </ul>
      ) : null}
      <div className={s.line}>
        <input id={id} ref={inp} className={s.input} value={pending} disabled={disabled} autoComplete="off" enterKeyHint="enter"
          placeholder={tags.length ? "Yana teg…" : placeholder} onChange={(e) => change(e.target.value)} onKeyDown={key} onBlur={commit} />
        <span className={`${s.count} tnum${chars > TAGS_MAX_CHARS ? " " + s.over : ""}`} aria-live="polite">{chars}/{TAGS_MAX_CHARS}</span>
      </div>
    </div>
  );
}

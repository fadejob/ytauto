"use client";

import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Check, ChevronsUpDown, LoaderCircle, Settings2 } from "lucide-react";
import { api, haptic } from "@/lib/client";
import type { YtStatus } from "./YouTubeChannels";
import ys from "./YouTubeChannels.module.css";
import s from "./ChannelMenu.module.css";

/**
 * Videolar ekranining tepasidagi kanal tugmasi: bosilsa shu yerning o'zida kanallar ro'yxati ochiladi va tanlangan kanal faol bo'ladi
 * (sozlamalarga o'tish shart emas). Ro'yxat ochilganda /api/youtube dan olinadi; oxirida «Kanallarni boshqarish».
 */
export default function ChannelMenu({ title, total, onSwitched, onManage }: {
  title: string | null; total: number; onSwitched: () => void; onManage: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [yt, setYt] = useState<YtStatus | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [switching, setSwitching] = useState<string | null>(null);
  const wrap = useRef<HTMLDivElement>(null);
  const btn = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const away = (e: PointerEvent) => { if (!wrap.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("pointerdown", away);
    api<YtStatus>("/api/youtube").then((r) => { setYt(r); setErr(null); }).catch((e) => setErr(e instanceof Error ? e.message : String(e)));
    return () => document.removeEventListener("pointerdown", away);
  }, [open]);

  useEffect(() => { if (open && yt) menu.current?.querySelector<HTMLElement>("[aria-selected=true]")?.focus(); }, [open, yt]);

  async function pick(id: string, active: boolean) {
    if (active) { setOpen(false); btn.current?.focus(); return; }
    haptic.tap();
    setSwitching(id);
    try {
      const r = await api<YtStatus>("/api/youtube", { action: "switch", id });
      setYt(r);
      haptic.ok();
      setOpen(false);
      onSwitched();
    } catch (e) { haptic.fail(); setErr(e instanceof Error ? e.message : String(e)); }
    finally { setSwitching(null); }
  }

  function key(e: KeyboardEvent<HTMLDivElement>) {
    const items = Array.from(menu.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
    const at = items.indexOf(document.activeElement as HTMLButtonElement);
    if (e.key === "Escape") { e.preventDefault(); setOpen(false); btn.current?.focus(); }
    else if (e.key === "ArrowDown") { e.preventDefault(); items[(at + 1) % items.length]?.focus(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); items[(at - 1 + items.length) % items.length]?.focus(); }
    else if (e.key === "Tab") setOpen(false);
  }

  const name = title || "Kanal tanlanmagan";
  return (
    <div className={s.wrap} ref={wrap}>
      <button type="button" ref={btn} className={`${s.chip}${open ? " " + s.open : ""}`} aria-haspopup="listbox" aria-expanded={open}
        aria-label={title ? `Kanal: ${title}. Almashtirish` : "Kanal tanlash"} onClick={() => { haptic.tap(); setOpen((o) => !o); }}>
        <span className={s.avatar} aria-hidden>{(title || "?").trim().charAt(0).toUpperCase() || "?"}</span>
        <span className={s.name}>{name}</span>
        {total > 1 ? <span className={s.total}>{total} ta</span> : null}
        {switching ? <LoaderCircle size={16} className="spin" /> : <ChevronsUpDown size={16} />}
      </button>

      {open ? (
        <div className={`${ys.menu} ${s.menu}`} ref={menu} role="listbox" aria-label="Kanalni tanlang" onKeyDown={key}>
          {!yt && !err ? <div className={s.loading}><LoaderCircle size={16} className="spin" />Kanallar yuklanmoqda…</div> : null}
          {err ? <div className={s.err}>{err}</div> : null}
          {yt?.channels.map((c) => {
            const n = c.title || c.id;
            return (
              <button key={c.id} type="button" role="option" aria-selected={c.active} disabled={!!switching}
                className={`${ys.option}${c.active ? " " + ys.on : ""}`} onClick={() => void pick(c.id, c.active)}>
                <span className={ys.oAvatar} aria-hidden>{n.trim().charAt(0).toUpperCase()}</span>
                <span className={ys.oNames}>
                  <span className={ys.oTitle}>{n}</span>
                  <span className={c.expired ? ys.oBad : ys.oMeta}>{c.expired ? "Token eskirgan" : c.pending ? `${c.pending} ta video navbatda` : "Navbat bo'sh"}</span>
                </span>
                {switching === c.id ? <LoaderCircle className={`spin ${ys.oCheck}`} /> : c.active ? <Check className={ys.oCheck} strokeWidth={3} aria-hidden /> : null}
              </button>
            );
          })}
          {yt && yt.channels.length === 0 ? <div className={s.loading}>Hali kanal ulanmagan.</div> : null}
          <div className={ys.sep} role="separator" />
          <button type="button" className={ys.add} onClick={() => { setOpen(false); onManage(); }}>
            <span className={ys.addIc} aria-hidden><Settings2 /></span>
            <span className={ys.oNames}>
              <span className={ys.oTitle}>Kanallarni boshqarish</span>
              <span className={ys.oMeta}>Ulash, tekshirish, uzish</span>
            </span>
          </button>
        </div>
      ) : null}
    </div>
  );
}

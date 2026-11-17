"use client";

import { useEffect, useState } from "react";
import { api, confirmBox, haptic } from "@/lib/client";
import type { GuestToken } from "@/lib/types";
import type { Toast } from "./App";

const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const day = (iso: string) => { const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`; };

/**
 * Qo'shimcha tokenlar (faqat asosiy hisob): boshqalar shu token bilan botga kirib, sizning kompyuteringiz orqali video yaratadi.
 * Ular o'z mavzulari, sozlamalari va YouTube kanali bilan ishlaydi; «Hozir chiqarish» yo'q. Ularning videolari sizga ko'rinmaydi.
 */
export default function Guests({ toast }: { toast: Toast }) {
  const [guests, setGuests] = useState<GuestToken[] | null>(null);
  const [label, setLabel] = useState("");
  const [busy, setBusy] = useState(false);
  const [fresh, setFresh] = useState<{ label: string; token: string } | null>(null);

  useEffect(() => {
    api<{ guests: GuestToken[] }>("/api/guests").then((d) => setGuests(d.guests)).catch((e) => toast(e instanceof Error ? e.message : String(e), true));
  }, [toast]);

  async function create() {
    const name = label.trim();
    if (!name) { toast("Token uchun nom kiriting (masalan, kim uchun).", true); return; }
    setBusy(true);
    try {
      const r = await api<{ guests: GuestToken[]; token?: string; message: string }>("/api/guests", { action: "create", label: name });
      setGuests(r.guests); setLabel("");
      if (r.token) setFresh({ label: name, token: r.token });
      haptic.ok(); toast(r.message);
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(false);
  }

  async function remove(g: GuestToken) {
    if (!(await confirmBox(`"${g.label}" tokeni o'chirilsinmi?\n\nU bilan endi kirib bo'lmaydi. Shu token egasining barcha mavzulari va sozlamalari ham o'chadi (qaytarib bo'lmaydi). Kompyuterdagi tayyor video fayllari keyingi tozalashda o'chadi.`))) return;
    setBusy(true);
    try {
      const r = await api<{ guests: GuestToken[]; message: string }>("/api/guests", { action: "delete", id: g.id });
      setGuests(r.guests);
      if (fresh?.label === g.label) setFresh(null);
      haptic.ok(); toast(r.message);
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(false);
  }

  async function copy(token: string) {
    try { await navigator.clipboard.writeText(token); haptic.ok(); toast("Token nusxalandi."); }
    catch { toast("Nusxalab bo'lmadi — tokenni qo'lda belgilab nusxalang.", true); }
  }

  return (
    <>
      <div className="sect">Qo&apos;shimcha tokenlar</div>
      <div className="card formcard">
        <div className="hint">
          Token bergan kishi botga shu token bilan kirib, sizning kompyuteringiz orqali video yaratadi: o&apos;z mavzulari, Gemini kaliti,
          promptlari va YouTube kanali bilan. Ularda «Hozir chiqarish» yo&apos;q — videolar kompyuteringizda ish vaqtida, chiqish vaqti
          yaqinlari birinchi tayyorlanadi. Ularning videolari sizga ko&apos;rinmaydi, sizniki ularga ko&apos;rinmaydi.
        </div>

        {fresh ? (
          <div className="card formcard" style={{ border: "1px solid var(--accent)" }}>
            <div><b>{fresh.label}</b> uchun yangi token — faqat hozir ko&apos;rsatiladi, nusxalab yuboring:</div>
            <code style={{ display: "block", overflowWrap: "anywhere", userSelect: "all", fontSize: 13, padding: "8px 10px", borderRadius: 8, background: "var(--soft)" }}>{fresh.token}</code>
            <div className="grid2">
              <button className="btn primary" onClick={() => copy(fresh.token)}>Nusxalash</button>
              <button className="btn" onClick={() => setFresh(null)}>Yopish</button>
            </div>
          </div>
        ) : null}

        {!guests ? <div className="skeleton" /> : guests.length === 0 ? (
          <div className="hint">Hali qo&apos;shimcha token yo&apos;q.</div>
        ) : guests.map((g) => (
          <div key={g.id} className="logline" style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <b>{g.label}</b>
              <div className="hint">{day(g.createdAt)} · {g.telegramUsers > 0 ? `botga kirgan: ${g.telegramUsers}` : "hali hech kim kirmagan"}</div>
            </div>
            <button className="btn danger" disabled={busy} onClick={() => remove(g)}>O&apos;chirish</button>
          </div>
        ))}

        <div className="field"><label>Yangi token kim uchun</label>
          <input className="input" value={label} maxLength={60} placeholder="Masalan: Akmal kanali" onChange={(e) => setLabel(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") void create(); }} /></div>
        <button className="btn block" disabled={busy || !label.trim()} onClick={create}>Token yaratish</button>
      </div>
    </>
  );
}

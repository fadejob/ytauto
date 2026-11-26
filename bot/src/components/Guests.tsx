"use client";

import { useCallback, useEffect, useState } from "react";
import { Copy, Eye, KeyRound, LoaderCircle, Plus, Trash2, Users, X } from "lucide-react";
import { api, confirmBox, haptic } from "@/lib/client";
import type { GuestToken } from "@/lib/types";
import type { Toast } from "./App";
import { Alert, Btn, Card, Field, Skel, Status, useBusy } from "./ui";
import s from "./Account.module.css";

const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const day = (iso: string) => {
  const d = new Date(iso);
  if (d.toDateString() === new Date().toDateString()) return "bugun";
  return `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
};

/**
 * Qo'shimcha tokenlar (faqat asosiy hisob): boshqalar shu token bilan botga kirib, sizning kompyuteringiz orqali video yaratadi.
 * Ular o'z mavzulari, sozlamalari va YouTube kanali bilan ishlaydi; «Hozir chiqarish» yo'q. Ularning videolari sizga ko'rinmaydi.
 * Yangi token faqat yaratilgan paytda (bir marta) ko'rsatiladi.
 */
export default function Guests({ toast }: { toast: Toast }) {
  const [guests, setGuests] = useState<GuestToken[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [label, setLabel] = useState("");
  const [fresh, setFresh] = useState<{ label: string; token: string } | null>(null);
  const fail = useCallback((m: string) => { haptic.fail(); toast(m, true); }, [toast]);
  const busy = useBusy(fail);

  // Ro'yxat: setState faqat javob kelganda (effekt ichida sinxron emas). Xato - shu kartada «Qayta urinish» bilan.
  const fetchList = useCallback(() => api<{ guests: GuestToken[] }>("/api/guests").then((d) => setGuests(d.guests))
    .catch((e) => { const m = e instanceof Error ? e.message : String(e); setLoadError(m); toast(m, true); }), [toast]);
  useEffect(() => { void fetchList(); }, [fetchList]);
  const load = () => { setLoadError(null); void fetchList(); };

  async function create() {
    const name = label.trim();
    if (!name) { toast("Token uchun nom kiriting (masalan, kim uchun).", true); return; }
    if (busy.is("create")) return;
    await busy.run("create", async () => {
      const r = await api<{ guests: GuestToken[]; token?: string; message: string }>("/api/guests", { action: "create", label: name });
      setGuests(r.guests); setLabel("");
      if (r.token) setFresh({ label: name, token: r.token });
      haptic.ok(); toast(r.message);
    });
  }

  async function remove(g: GuestToken) {
    if (!(await confirmBox(`"${g.label}" tokeni o'chirilsinmi?\n\nU bilan endi kirib bo'lmaydi. Shu token egasining barcha mavzulari va sozlamalari ham o'chadi (qaytarib bo'lmaydi). Kompyuterdagi tayyor video fayllari keyingi tozalashda o'chadi.`))) return;
    await busy.run(`del:${g.id}`, async () => {
      const r = await api<{ guests: GuestToken[]; message: string }>("/api/guests", { action: "delete", id: g.id });
      setGuests(r.guests);
      setFresh((f) => (f?.label === g.label ? null : f));
      haptic.ok(); toast(r.message);
    });
  }

  async function copy(token: string) {
    await busy.run("copy", async () => {
      try { await navigator.clipboard.writeText(token); }
      catch { throw new Error("Nusxalab bo'lmadi — tokenni qo'lda belgilab nusxalang."); }
      haptic.ok(); toast("Token nusxalandi.");
    });
  }

  return (
    <Card title="Qo'shimcha tokenlar" action={guests ? <Status>{guests.length} ta</Status> : null}>
      <div className="sub">
        Token egasi botga kirib, kompyuteringizda o&apos;z mavzulari, Gemini kaliti va YouTube kanali bilan video yaratadi.
        Ularda «Hozir chiqarish» yo&apos;q — videolar ish vaqtida, chiqish vaqti yaqinlari birinchi tayyorlanadi. Videolaringiz bir-biriga ko&apos;rinmaydi.
      </div>

      {fresh ? (
        <div className={s.secret} role="region" aria-label="Yangi token">
          <div className={s.secretHead}>
            <span className={s.secretKey}><Eye />Faqat hozir ko&apos;rsatiladi</span>
            <button type="button" className={s.close} aria-label="Yopish" onClick={() => setFresh(null)}><X size={18} /></button>
          </div>
          <div className={s.secretTitle}>«{fresh.label}» uchun yangi token</div>
          <code className={s.secretValue}>{fresh.token}</code>
          <div className={s.secretActions}>
            <Btn icon={Copy} loading={busy.is("copy")} done={busy.done("copy")} onClick={() => copy(fresh.token)}>Nusxalash</Btn>
            <span className={s.secretNote}>Yuborib bo&apos;lgach yoping — qayta ko&apos;rinmaydi</span>
          </div>
        </div>
      ) : null}

      {guests === null ? (
        loadError ? (
          <Alert>
            Tokenlar ro&apos;yxatini yuklab bo&apos;lmadi: {loadError}{" "}
            <button type="button" className="linkbtn" onClick={load}>Qayta urinish</button>
          </Alert>
        ) : (
          <div className={s.list} aria-busy="true" aria-label="Yuklanmoqda">
            {[0, 1].map((i) => (
              <div key={i} className={s.item}>
                <Skel h={40} w={40} r={10} />
                <span className={s.itemText}><Skel h={14} w="55%" /><Skel h={12} w="40%" /></span>
              </div>
            ))}
          </div>
        )
      ) : guests.length === 0 ? (
        <div className="hint">Hali qo&apos;shimcha token yo&apos;q.</div>
      ) : (
        <ul className={s.list} style={{ listStyle: "none", margin: 0, padding: 0 }}>
          {guests.map((g) => {
            const deleting = busy.is(`del:${g.id}`);
            return (
              <li key={g.id} className={s.item}>
                <span className={`${s.tile}${g.telegramUsers > 0 ? " " + s.used : ""}`} aria-hidden>
                  {g.telegramUsers > 0 ? <Users size={18} /> : <KeyRound size={18} />}
                </span>
                <span className={s.itemText}>
                  <b>{g.label}</b>
                  <span>{day(g.createdAt)} · {g.telegramUsers > 0 ? `botga kirgan: ${g.telegramUsers}` : "hali hech kim kirmagan"}</span>
                </span>
                <button type="button" className={s.remove} aria-label={`«${g.label}» tokenini o'chirish`} disabled={deleting} aria-busy={deleting || undefined} onClick={() => remove(g)}>
                  {deleting ? <LoaderCircle className="spin" /> : <Trash2 />}
                </button>
              </li>
            );
          })}
        </ul>
      )}

      <div className={s.create}>
        <Field label={<label htmlFor="guest-label">Yangi token kim uchun</label>} hint="Nom faqat sizga ko'rinadi">
          <input id="guest-label" className="inp" value={label} maxLength={60} placeholder="Masalan: Akmal kanali" autoComplete="off"
            onChange={(e) => setLabel(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") void create(); }} />
        </Field>
        <Btn tone="secondary" block icon={Plus} loading={busy.is("create")} disabled={!label.trim()} onClick={create}>Token yaratish</Btn>
      </div>
    </Card>
  );
}

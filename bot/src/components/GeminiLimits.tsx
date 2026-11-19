"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/client";
import type { GeminiUsageView, ModelLimitRow } from "@/lib/gemini-usage";
import type { Toast } from "./App";

const fmt = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(n >= 10_000 ? 0 : 1)}K` : String(n));
const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

function status(r: ModelLimitRow): { cls: string; text: string } {
  switch (r.status) {
    case "day": return { cls: "bad", text: `Kunlik limit tugadi · ${time(r.until!)} da tiklanadi` };
    case "minute": return { cls: "warn", text: `Daqiqalik limit · ${time(r.until!)} gacha` };
    case "unavailable": return { cls: "mute", text: r.note || "Mavjud emas" };
    case "ok": return { cls: "ok", text: "Ishlayapti" };
    default: return { cls: "mute", text: "Bugun ishlatilmagan" };
  }
}

/**
 * Faqat asosiy hisob egasi uchun: har Gemini modeli bo'yicha bugungi sarf (so'rov, daqiqadagi so'rov, token) va bepul tarif limiti,
 * zanjir tartibida (birinchisi - asosiy). Kun Tinch okeani vaqti bilan yangilanadi (Google limitlari ham shunda tiklanadi).
 */
export default function GeminiLimits({ toast }: { toast: Toast }) {
  const [v, setV] = useState<GeminiUsageView | null>(null);
  const [busy, setBusy] = useState(false);
  const load = useCallback(async () => {
    setBusy(true);
    try { setV(await api<GeminiUsageView>("/api/gemini/usage")); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(false);
  }, [toast]);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- birinchi yuklash
  useEffect(() => { void load(); }, [load]);

  const keys = Math.max(1, v?.keys ?? 1);
  return (
    <>
      <div className="sect">Gemini modellari va limitlar</div>
      <div className="card formcard">
        {!v ? <div className="skeleton" style={{ height: 120 }} /> : v.rows.map((r) => {
          const st = status(r);
          const rpd = r.limits ? r.limits.rpd * keys : 0;
          const rpm = r.limits ? r.limits.rpm * keys : 0;
          const pct = rpd ? Math.min(100, Math.round((r.requestsToday / rpd) * 100)) : 0;
          return (
            <div key={r.model} style={{ padding: "8px 0", borderBottom: "1px solid var(--line, rgba(127,127,127,.15))" }}>
              <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
                <b style={{ flex: 1, minWidth: 140 }}>{r.display}</b>
                {r.primary ? <span className="chip">Asosiy</span> : null}
                <span className={`chip ${st.cls}`}>{st.text}</span>
              </div>
              <div style={{ display: "flex", gap: 14, flexWrap: "wrap", fontSize: 13, marginTop: 4 }}>
                <span>Bugun: <b>{r.requestsToday}</b>{rpd ? ` / ${fmt(rpd)}` : ""}</span>
                <span>Daqiqada: <b>{r.requestsLastMinute}</b>{rpm ? ` / ${rpm}` : ""}</span>
                <span>Token: <b>{fmt(r.tokensToday)}</b>{r.limits ? ` (${fmt(r.limits.tpm * keys)}/daq)` : ""}</span>
                {r.rateLimitedToday ? <span>429: <b>{r.rateLimitedToday}</b></span> : null}
              </div>
              {rpd ? (
                <div style={{ height: 4, borderRadius: 2, background: "var(--soft)", marginTop: 6, overflow: "hidden" }}>
                  <div style={{ width: `${pct}%`, height: "100%", background: pct >= 100 ? "var(--danger)" : pct >= 75 ? "var(--warn)" : "var(--ok)" }} />
                </div>
              ) : null}
            </div>
          );
        })}
        <div className="hint">
          Sarfni YtAuto o&apos;zi sanaydi (worker va bot chaqiruvlari, faqat sizning kalitlaringiz); limitlar — Google AI Studio bepul tarif jadvali
          {keys > 1 ? `, ${keys} ta kalit uchun (har kalit alohida loyihadan bo'lsa)` : ""}. Biror model limiti tugasa keyingisi ishlatiladi.
          Kun Tinch okeani vaqti bilan yangilanadi{v?.updatedAtUtc ? ` · oxirgi yozuv ${time(v.updatedAtUtc)}` : ""}.
        </div>
        <button className="btn block" type="button" disabled={busy} onClick={() => void load()}>{busy ? "Yangilanmoqda…" : "Yangilash"}</button>
      </div>
    </>
  );
}

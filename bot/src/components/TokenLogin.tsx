"use client";

import { useState } from "react";
import { api, haptic } from "@/lib/client";

/**
 * Birinchi kirish: YtAuto ilovasi (kompyuterda) sozlash tugagach ko'rsatgan tokenni kiritish. Token bilan Telegram hisobi shu
 * kompyuter hisobiga bog'lanadi - keyin barcha mavzular, sozlamalar va promptlar faqat shu hisobniki bo'ladi.
 */
export default function TokenLogin({ onDone }: { onDone: () => void }) {
  const [token, setToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function paste() {
    try { const t = (await navigator.clipboard.readText()).trim(); if (t) setToken(t); } catch { /* ruxsat yo'q - qo'lda kiritiladi */ }
  }

  async function submit() {
    const t = token.trim();
    if (!t) return;
    setBusy(true); setError(null);
    try {
      const r = await api<{ ok: boolean; message: string }>("/api/login", { token: t });
      if (!r.ok) { haptic.fail(); setError(r.message); return; }
      haptic.ok();
      onDone();
    } catch (e) {
      haptic.fail();
      setError(e instanceof Error ? e.message : String(e));
    } finally { setBusy(false); }
  }

  return (
    <main className="shell">
      <header className="head"><div><h1>YtAuto'ga kirish</h1><div className="sub">Bir marta — keyin token so&apos;ralmaydi</div></div></header>
      <div className="list">
        <div className="card formcard">
          <ol className="steps">
            <li>Kompyuterda <b>YtAuto</b> ilovasini oching (sozlash tugagan bo&apos;lsin).</li>
            <li>Ilovadagi <b>«Tokenni nusxalash»</b> tugmasini bosing.</li>
            <li>Tokenni shu yerga qo&apos;ying va <b>Kirish</b> ni bosing.</li>
          </ol>
          <div className="field">
            <label>Token</label>
            <input className="input" value={token} placeholder="yt_…" autoComplete="off" autoCapitalize="off" spellCheck={false}
              onChange={(e) => setToken(e.target.value)} onKeyDown={(e) => e.key === "Enter" && void submit()} />
          </div>
          {error ? <div className="err">{error}</div> : null}
          <div className="grid2">
            <button className="btn block" onClick={paste} disabled={busy}>Qo&apos;yish</button>
            <button className="btn primary block" onClick={submit} disabled={busy || !token.trim()}>{busy ? "Tekshirilmoqda…" : "Kirish"}</button>
          </div>
          <p className="hint">Token faqat sizning ma&apos;lumotlaringizga kalit: uni boshqalarga bermang. Ilovada tokenni yangilasangiz, bot uni qayta so&apos;raydi.</p>
          <p className="hint"><a href="/about">YtAuto haqida</a> · <a href="/privacy">Maxfiylik siyosati</a> · <a href="/terms">Foydalanish shartlari</a></p>
        </div>
      </div>
    </main>
  );
}

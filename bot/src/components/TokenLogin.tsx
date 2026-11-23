"use client";

import { useCallback, useState, type ReactNode } from "react";
import { Clapperboard, ClipboardPaste, KeyRound, Lock } from "lucide-react";
import { api, haptic } from "@/lib/client";
import { Btn, useBusy } from "./ui";
import s from "./TokenLogin.module.css";

/**
 * Birinchi kirish: YtAuto ilovasi (kompyuterda) sozlash tugagach ko'rsatgan tokenni kiritish. Token bilan Telegram hisobi shu
 * kompyuter hisobiga bog'lanadi - keyin barcha mavzular, sozlamalar va promptlar faqat shu hisobniki bo'ladi.
 */
export default function TokenLogin({ onDone }: { onDone: () => void }) {
  const [token, setToken] = useState("");
  const [error, setError] = useState<string | null>(null);
  const onError = useCallback((m: string) => { haptic.fail(); setError(m); }, []);
  const busy = useBusy(onError);
  const checking = busy.is("login");

  async function paste() {
    try { const t = (await navigator.clipboard.readText()).trim(); if (t) { setToken(t); setError(null); } } catch { /* ruxsat yo'q - qo'lda kiritiladi */ }
  }

  async function submit() {
    const t = token.trim();
    if (!t || checking) return;
    setError(null);
    const r = await busy.run("login", () => api<{ ok: boolean; message: string }>("/api/login", { token: t }));
    if (r === false) return; // xato - onError ko'rsatdi
    if (!r.ok) { haptic.fail(); setError(r.message); return; }
    haptic.ok();
    onDone();
  }

  const steps: [ReactNode, boolean][] = [
    [<>Kompyuterda <b>YtAuto</b> ilovasini oching — sozlash tugagan bo&apos;lsin.</>, false],
    [<>Ilovadagi <b>«Tokenni nusxalash»</b> tugmasini bosing.</>, false],
    [<>Tokenni pastga qo&apos;ying va <b>Kirish</b> ni bosing.</>, true],
  ];

  return (
    <main className="shell">
      <div className={`page ${s.wrap}`}>
        <header className="page-head">
          <span className={s.brand} aria-hidden><Clapperboard size={24} /></span>
          <h1>YtAuto&apos;ga kirish</h1>
          <div className="sub">Bir marta — keyin token so&apos;ralmaydi</div>
        </header>

        <section className="card" aria-labelledby="login-steps">
          <div className="label" id="login-steps">Qanday kiriladi</div>
          <ol className={s.steps}>
            {steps.map(([text, last], i) => (
              <li key={i}><span className={`${s.num}${last ? " " + s.now : ""}`} aria-hidden>{i + 1}</span><span>{text}</span></li>
            ))}
          </ol>
        </section>

        <section className="card">
          <div className={s.head}>
            <label className="label" htmlFor="login-token">Token</label>
            <span className="sub">kompyuterdagi ilovadan</span>
          </div>
          <div className={`${s.box}${error ? " " + s.err : ""}`}>
            <KeyRound className={s.key} size={20} aria-hidden />
            <input id="login-token" className={s.input} value={token} placeholder="yt_…" autoComplete="off" autoCapitalize="off" autoCorrect="off" spellCheck={false}
              aria-invalid={error ? true : undefined} aria-describedby={error ? "login-error" : undefined}
              onChange={(e) => { setToken(e.target.value); if (error) setError(null); }} onKeyDown={(e) => { if (e.key === "Enter") void submit(); }} />
            <Btn tone="secondary" size="sm" icon={ClipboardPaste} disabled={checking} onClick={paste}>Qo&apos;yish</Btn>
          </div>
          {error ? <div className="field-err" id="login-error" role="alert">{error}</div> : null}
          <Btn block loading={checking} disabled={!token.trim()} onClick={submit}>{checking ? "Tekshirilmoqda…" : "Kirish"}</Btn>
          <div className={s.safety}>
            <Lock size={15} aria-hidden />
            <span>Token — faqat sizning ma&apos;lumotlaringizga kalit, uni boshqalarga bermang. Ilovada tokenni yangilasangiz, bot uni qayta so&apos;raydi.</span>
          </div>
        </section>
      </div>
    </main>
  );
}

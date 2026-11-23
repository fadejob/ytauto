"use client";

/**
 * YtAuto UI to'plami (dizayn: ytauto.dsai). Hamma ekranlar shu qismlardan quriladi - ranglar, o'lchamlar va holatlar bir xil bo'lsin.
 * Qoidalar: asinxron amal tugmasi o'z ichida spinner ko'rsatadi (Btn loading), yuklanish faqat o'sha bo'lim ichida (Skel),
 * natija xabari - toast (App). Urg'u rangi faqat asosiy amal, faol holat va boshlang'ich savol uchun.
 */
import { useCallback, useEffect, useRef, useState, type ButtonHTMLAttributes, type ComponentType, type ReactNode } from "react";
import { setConfirmHost } from "@/lib/client";
import { Check, ChevronLeft, ChevronRight, CircleAlert, LoaderCircle, MessageCircleQuestion, X } from "lucide-react";

export type IconT = ComponentType<{ className?: string; size?: number | string; strokeWidth?: number | string }>;

// ------------------------------------------------------------------ tugma
type BtnProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> & {
  tone?: "primary" | "secondary" | "ghost" | "danger";
  size?: "md" | "sm";
  /** Amal bajarilmoqda: tugma ichida spinner, tugma bosilmaydi (boshqa joylar ishlayveradi). */
  loading?: boolean;
  /** Amal hozirgina muvaffaqiyatli tugadi: qisqa "✓" (useBusy.doneKey bilan). */
  done?: boolean;
  icon?: IconT;
  block?: boolean;
  children?: ReactNode;
};

export function Btn({ tone = "primary", size = "md", loading, done, icon: Icon, block, className, children, disabled, type, ...rest }: BtnProps) {
  const cls = ["btn", tone !== "primary" && tone, size === "sm" && "sm", block && "block", loading && "loading", done && "done", className].filter(Boolean).join(" ");
  return (
    <button type={type ?? "button"} className={cls} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading ? <LoaderCircle className="spin" /> : done ? <Check /> : Icon ? <Icon /> : null}
      {children !== undefined ? <span className="lbl">{children}</span> : null}
    </button>
  );
}

/**
 * Bir nechta tugmaning har biri o'z spinneri bilan: busy.run("save", fn) - shu kalitli tugma loading, tugagach 1.2 s "✓".
 * Xato bo'lsa onError (odatda toast) chaqiriladi va false qaytadi.
 */
export function useBusy(onError?: (message: string) => void) {
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [done, setDone] = useState<string | null>(null);
  const timer = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  const run = useCallback(async <T,>(key: string, fn: () => Promise<T>): Promise<T | false> => {
    setBusy((b) => ({ ...b, [key]: true }));
    try {
      const r = await fn();
      setDone(key);
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => setDone((d) => (d === key ? null : d)), 1200);
      return r;
    } catch (e) {
      onError?.(e instanceof Error ? e.message : String(e));
      return false;
    } finally {
      setBusy((b) => ({ ...b, [key]: false }));
    }
  }, [onError]);
  return { run, is: (key: string) => !!busy[key], done: (key: string) => done === key, any: Object.values(busy).some(Boolean) };
}

// ------------------------------------------------------------------ holat, switch, maydon
export function Status({ tone = "neutral", children }: { tone?: "neutral" | "accent" | "ok" | "bad"; children: ReactNode }) {
  return <span className={`status${tone === "neutral" ? "" : " " + tone}`}>{children}</span>;
}

export function Toggle({ on, onChange, disabled, label }: { on: boolean; onChange: (v: boolean) => void; disabled?: boolean; label?: string }) {
  return <button type="button" role="switch" aria-checked={on} aria-label={label} className={`toggle${on ? " on" : ""}`} disabled={disabled} onClick={() => onChange(!on)} />;
}

export function ToggleRow({ title, sub, on, onChange, disabled, label }: { title: ReactNode; label?: string; sub?: ReactNode; on: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <div className="toggle-row">
      <div className="t"><span className="tt">{title}</span>{sub ? <span className="sub">{sub}</span> : null}</div>
      <Toggle on={on} onChange={onChange} disabled={disabled} label={label ?? (typeof title === "string" ? title : undefined)} />
    </div>
  );
}

export function Field({ label, hint, error, children, right }: { label?: ReactNode; hint?: ReactNode; error?: ReactNode; children: ReactNode; right?: ReactNode }) {
  return (
    <div className="field">
      {label || right ? <div style={{ display: "flex", justifyContent: "space-between", gap: 8 }}><span className="label">{label}</span>{right ? <span className="sub tnum">{right}</span> : null}</div> : null}
      {children}
      {error ? <div className="field-err"><CircleAlert size={15} />{error}</div> : hint ? <div className="hint">{hint}</div> : null}
    </div>
  );
}

// ------------------------------------------------------------------ sahifa va karta
export function PageHead({ title, sub, back, right }: { title: ReactNode; sub?: ReactNode; back?: { label: string; onClick: () => void }; right?: ReactNode }) {
  return (
    <header className="page-head">
      {back ? <button type="button" className="back" onClick={back.onClick}><span className="circle"><ChevronLeft size={18} /></span>{back.label}</button> : null}
      <div className="row"><h1>{title}</h1>{right}</div>
      {sub ? <div className="sub">{sub}</div> : null}
    </header>
  );
}

export function Card({ title, sub, action, children, className, style }: { title?: ReactNode; sub?: ReactNode; action?: ReactNode; children?: ReactNode; className?: string; style?: React.CSSProperties }) {
  return (
    <section className={`card${className ? " " + className : ""}`} style={style}>
      {title || action ? <div className="card-head"><div className="t">{title ? <h2 className="h2">{title}</h2> : null}{sub ? <span className="sub">{sub}</span> : null}</div>{action}</div> : null}
      {children}
    </section>
  );
}

/** Kartaning o'z "Saqlash" tugmasi: faqat o'zgarish bo'lsa faol, saqlanayotganda spinner. size="md"/block - karta pastidagi katta tugma. */
export function SaveBtn({ dirty, saving, done, onClick, label = "Saqlash", size = "sm", block }: {
  dirty: boolean; saving: boolean; done?: boolean; onClick: () => void; label?: string; size?: "md" | "sm"; block?: boolean;
}) {
  return <Btn size={size} block={block} tone={dirty || saving ? "primary" : "secondary"} disabled={!dirty && !saving} loading={saving} done={done && !dirty} onClick={onClick}>{done && !dirty ? "Saqlandi" : label}</Btn>;
}

export function SettingsRow({ icon: Icon, title, summary, warn, onClick }: { icon: IconT; title: string; summary?: ReactNode; warn?: boolean; onClick: () => void }) {
  return (
    <button type="button" className={`srow${warn ? " warn" : ""}`} onClick={onClick}>
      <span className="ic"><Icon size={20} /></span>
      <span className="t"><b>{title}</b>{summary ? <span>{summary}</span> : null}</span>
      <ChevronRight className="chev" size={18} />
    </button>
  );
}

export function Segmented<K extends string>({ items, value, onChange, label }: { items: { key: K; label: string; count?: number; disabled?: boolean }[]; value: K; onChange: (k: K) => void; label?: string }) {
  return (
    <div className="seg" role="tablist" aria-label={label}>
      {items.map((it) => (
        <button key={it.key} role="tab" aria-selected={value === it.key} className={value === it.key ? "on" : ""} disabled={it.disabled} onClick={() => onChange(it.key)}>
          {it.label}{it.count !== undefined ? <span className="n">{it.count}</span> : null}
        </button>
      ))}
    </div>
  );
}

// ------------------------------------------------------------------ yuklanish, bo'sh holat, xabar qutisi
export function Skel({ h = 14, w = "100%", r }: { h?: number; w?: number | string; r?: number }) {
  return <span className="skel" style={{ height: h, width: w, borderRadius: r }} aria-hidden />;
}

/** Bo'lim yuklanayotganda: shu kartaning o'rnida skelet (butun sahifa emas). */
export function SkelCard({ lines = 3 }: { lines?: number }) {
  return (
    <div className="card" aria-busy="true" aria-label="Yuklanmoqda">
      <Skel h={18} w="55%" />
      {Array.from({ length: lines }, (_, i) => <Skel key={i} h={12} w={i === lines - 1 ? "70%" : "100%"} />)}
    </div>
  );
}

export function Empty({ icon: Icon, title, children, action }: { icon?: IconT; title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="empty">
      {Icon ? <span className="ic"><Icon size={24} /></span> : null}
      <b>{title}</b>
      {children ? <div>{children}</div> : null}
      {action}
    </div>
  );
}

export function Alert({ tone = "bad", children, icon: Icon = CircleAlert }: { tone?: "bad" | "note" | "accent"; children: ReactNode; icon?: IconT }) {
  return <div role={tone === "bad" ? "alert" : undefined} className={`alert${tone === "bad" ? "" : " " + tone}`}><Icon />{<div>{children}</div>}</div>;
}

// ------------------------------------------------------------------ video quvuri va boshlang'ich savol
export type StageState = "done" | "now" | "fail" | "todo";
export function StageStrip({ stages }: { stages: { name: string; state: StageState }[] }) {
  return (
    <div className="strip" role="img" aria-label={stages.map((s) => `${s.name}: ${({ done: "tayyor", now: "hozir", fail: "xato", todo: "kutilmoqda" })[s.state]}`).join(", ")}>
      <div className="bars">{stages.map((s) => <i key={s.name} className={s.state === "todo" ? "" : s.state} />)}</div>
      <div className="names">{stages.map((s) => <span key={s.name} className={s.state === "todo" ? "" : s.state}>{s.name}</span>)}</div>
    </div>
  );
}

export function HookLine({ children }: { children: ReactNode }) {
  return <div className="hook-line"><MessageCircleQuestion />{children}</div>;
}

export function HookSlab({ children, right, hint }: { children: ReactNode; right?: ReactNode; hint?: ReactNode }) {
  return (
    <div className="hook-slab">
      <div style={{ display: "flex", justifyContent: "space-between", gap: 8 }}><span className="k"><MessageCircleQuestion size={16} />Boshlang&apos;ich savol</span>{right ? <span className="h tnum">{right}</span> : null}</div>
      {children}
      {hint ? <div className="h">{hint}</div> : null}
    </div>
  );
}

// ------------------------------------------------------------------ sheet (pastdan chiqadigan oyna; kompyuterda markazda)
export function Sheet({ title, sub, onClose, children, footer, label }: { title: ReactNode; sub?: ReactNode; onClose: () => void; children: ReactNode; footer?: ReactNode; label?: string }) {
  useEffect(() => {
    const k = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", k);
    return () => window.removeEventListener("keydown", k);
  }, [onClose]);
  // Oyna ochiq: orqa fon aylanmaydi, yopilganda fokus oldingi elementga qaytadi (faqat bir marta, onClose o'zgarganda emas).
  useEffect(() => {
    const prevFocus = document.activeElement as HTMLElement | null;
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = prevOverflow;
      prevFocus?.focus?.();
    };
  }, []);
  return (
    <>
      <div className="backdrop" onClick={onClose} />
      <section className="sheet" role="dialog" aria-modal="true" aria-label={label ?? (typeof title === "string" ? title : undefined)}>
        <div className="grab" />
        <div className="sheet-head">
          <div style={{ display: "grid", gap: 2, minWidth: 0 }}><h2>{title}</h2>{sub ? <span className="sub">{sub}</span> : null}</div>
          <button type="button" className="round" style={{ background: "var(--sunken)", width: 36, height: 36 }} aria-label="Yopish" onClick={onClose}><X size={18} /></button>
        </div>
        {children}
        {footer ? <div className="sheet-foot">{footer}</div> : null}
      </section>
    </>
  );
}

// ------------------------------------------------------------------ tasdiq oynasi (confirmBox o'rniga; dizayn: Holatlar -> Xabarlar va tugmalar)
/** Birinchi qator - sarlavha, qolgani - izoh. Qaytarib bo'lmaydigan amallarda tasdiq tugmasi qizil. */
const DANGER = /o'chiril|uzil|bekor qilinsin|qaytarib bo'lmaydi|chiqasizmi/i;

export function ConfirmHost() {
  const [q, setQ] = useState<{ message: string; resolve: (v: boolean) => void } | null>(null);
  useEffect(() => {
    setConfirmHost((message) => new Promise<boolean>((resolve) => setQ((cur) => { cur?.resolve(false); return { message, resolve }; })));
    return () => setConfirmHost(null);
  }, []);
  const answer = useCallback((v: boolean) => setQ((cur) => { cur?.resolve(v); return null; }), []);
  useEffect(() => {
    if (!q) return;
    const k = (e: KeyboardEvent) => { if (e.key === "Escape") answer(false); };
    window.addEventListener("keydown", k);
    return () => window.removeEventListener("keydown", k);
  }, [q, answer]);
  if (!q) return null;
  const [title, ...rest] = q.message.split("\n");
  const body = rest.join("\n").trim();
  const danger = DANGER.test(q.message);
  return (
    <>
      <div className="backdrop confirm-backdrop" onClick={() => answer(false)} />
      <div className="confirm" role="alertdialog" aria-modal="true" aria-label={title}>
        <h2>{title}</h2>
        {body ? <p className="sub">{body}</p> : null}
        <div className="confirm-actions">
          <Btn tone="secondary" onClick={() => answer(false)}>Bekor qilish</Btn>
          <Btn tone={danger ? "danger" : "primary"} onClick={() => answer(true)} autoFocus>Tasdiqlash</Btn>
        </div>
      </div>
    </>
  );
}

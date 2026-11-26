"use client";

import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Check, ChevronDown, CircleCheck, CircleX, Link2Off, LoaderCircle, Lock, Plus, ShieldCheck, Tv } from "lucide-react";
import { api, confirmBox, openExternal } from "@/lib/client";
import type { Toast } from "./App";
import { Alert, Btn, SkelCard, useBusy } from "./ui";
import styles from "./YouTubeChannels.module.css";

export type YtChannelInfo = { id: string; title: string; connectedAt: string; active: boolean; pending: number; expired?: boolean };
export type YtStatus = {
  connected: boolean; channelId: string; channelTitle: string; connectedAt: string | null;
  channels: YtChannelInfo[]; maxChannels: number; clientConfigured: boolean; redirectUri: string;
  /** Server umumiy OAuth client'i bor: Client ID/Secret kiritish ixtiyoriy. */
  globalClient?: boolean;
};

type CheckResult = { ok: boolean; message: string };

/**
 * Ulangan YouTube kanallari: bir hisobga bir nechta kanal, bittasi faol. Faol kanal almashtirilsa mavzular, kanal tahlili va
 * yangi videolar shu kanal bo'yicha bo'ladi. Avval qo'shilgan videolar o'z kanaliga yuklanaveradi.
 * Ko'rinish: chiroyli tanlov (custom select) - faol kanal tugmada; bosilsa kanallar ro'yxati ochiladi, tanlangan kanal faol bo'ladi
 * va bot shu kanal bilan ishlaydi. Ro'yxat oxirida «Yana kanal ulash». Ostida faol kanal uchun tekshirish va uzish.
 */
export default function YouTubeChannels({ yt, setYt, toast, blocked, onSwitched, error, onRetry }: {
  yt: YtStatus | null; setYt: (s: YtStatus) => void; toast: Toast;
  /** Ulash tugmasini to'sadigan sabab (Client ID/Secret yo'q yoki saqlanmagan o'zgarish). */
  blocked: string | null;
  onSwitched?: () => void;
  /** Holatni yuklab bo'lmadi (yt null bo'lsa skelet o'rniga xato ko'rsatiladi). */
  error?: string | null;
  onRetry?: () => Promise<unknown>;
}) {
  const busy = useBusy((m) => toast(m, true));
  const [checks, setChecks] = useState<Record<string, CheckResult>>({});
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const channels = yt?.channels ?? [];
  const full = !!yt && channels.length >= yt.maxChannels;
  /** Kanal ro'yxatini o'zgartiradigan amal ketayotganda boshqa o'zgartiruvchi tugmalar kutadi (tekshirish bundan mustasno). */
  const mutating = busy.is("connect") || channels.some((c) => busy.is(`switch:${c.id}`) || busy.is(`remove:${c.id}`));

  // Ochiq ro'yxat: tashqariga bosilsa yopiladi; ochilganda tanlangan kanalga fokus (klaviatura bilan ham tanlash mumkin).
  useEffect(() => {
    if (!open) return;
    const away = (e: PointerEvent) => { if (!wrap.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("pointerdown", away);
    menu.current?.querySelector<HTMLElement>("[aria-selected=true]")?.focus();
    return () => document.removeEventListener("pointerdown", away);
  }, [open]);

  async function connect() {
    await busy.run("connect", async () => {
      const { url } = await api<{ url: string }>("/api/youtube", { action: "start" });
      openExternal(url);
      toast("Google oynasi ochildi. Kanalni tanlab ruxsat bergach shu yerga qayting.");
    });
  }
  async function makeActive(c: YtChannelInfo) {
    setOpen(false);
    trigger.current?.focus();
    if (c.active || mutating) return;
    const prev = channels.find((x) => x.active);
    const r = await busy.run(`switch:${c.id}`, () => api<YtStatus>("/api/youtube", { action: "switch", id: c.id }));
    if (r === false) return;
    setYt(r);
    onSwitched?.();
    // «Bekor qilish»: oldingi faol kanalga qaytariladi.
    toast(`Faol kanal: ${c.title || c.id}`, false, prev ? { label: "Bekor qilish", run: () => revert(prev) } : undefined);
  }
  async function revert(prev: YtChannelInfo) {
    try {
      const r = await api<YtStatus>("/api/youtube", { action: "switch", id: prev.id });
      setYt(r);
      onSwitched?.();
      toast(`Faol kanal: ${prev.title || prev.id}`);
    } catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
  }
  async function verify(c: YtChannelInfo) {
    setChecks((m) => without(m, c.id));
    await busy.run(`verify:${c.id}`, async () => {
      let res: CheckResult;
      try { res = await api<CheckResult>("/api/youtube", { action: "verify", id: c.id }); }
      catch (e) { res = { ok: false, message: e instanceof Error ? e.message : String(e) }; }
      setChecks((m) => ({ ...m, [c.id]: res }));
    });
  }
  async function remove(c: YtChannelInfo) {
    const name = c.title || c.id;
    const warn = c.pending ? ` Bu kanalga bog'langan ${c.pending} ta video hali YouTube'ga chiqmagan — ular kanal qayta ulanmaguncha yuklanmaydi.` : "";
    if (!(await confirmBox(`«${name}» kanali uziladimi?${warn}`))) return;
    const r = await busy.run(`remove:${c.id}`, () => api<YtStatus>("/api/youtube", { action: "remove", id: c.id }));
    if (r === false) return;
    setYt(r);
    setChecks((m) => without(m, c.id));
    toast(`«${name}» uzildi.`);
    onSwitched?.();
  }

  const head = (
    <div className="section-label">
      <span>Ulangan kanallar</span>
      {yt ? <span className="tnum" style={{ textTransform: "none" }}>{channels.length} ta · eng ko&apos;pi {yt.maxChannels}</span> : null}
    </div>
  );
  const lockHint = blocked ? <div className={styles.lock}><Lock />{blocked}</div>
    : full ? <div className={styles.lock}><Lock />Eng ko&apos;pi {yt?.maxChannels} ta kanal ulanadi.</div> : null;
  const connectDisabled = !yt || !!blocked || full || mutating;

  if (!yt) {
    return (
      <section className={styles.list} aria-label="YouTube kanallari">
        {head}
        {error ? (
          <div className="card">
            <Alert>YouTube holatini yuklab bo&apos;lmadi: {error}</Alert>
            {onRetry ? <Btn tone="secondary" size="sm" loading={busy.is("retry")} onClick={() => void busy.run("retry", onRetry)}>Qayta urinish</Btn> : null}
          </div>
        ) : <SkelCard lines={2} />}
      </section>
    );
  }

  if (channels.length === 0) {
    return (
      <section className={styles.list} aria-label="YouTube kanallari">
        {head}
        <div className="card">
          <div className={styles.emptyHead}>
            <span className={styles.emptyIc}><Tv size={22} /></span>
            <div style={{ display: "grid", gap: 2 }}>
              <b>Hali kanal ulanmagan</b>
              <span className="sub">Bir nechta kanal ulash mumkin — har birining mavzulari, tahlili va videolari alohida.</span>
            </div>
          </div>
          <Btn block icon={Plus} tone={connectDisabled && !busy.is("connect") ? "secondary" : "primary"} loading={busy.is("connect")} disabled={connectDisabled} onClick={() => void connect()}>
            YouTube kanalini ulash
          </Btn>
          {lockHint}
        </div>
        <div className={`hint ${styles.foot}`}>Google oynasida kerakli hisob yoki brend kanalini tanlang. Ulangan kanal darhol faol bo&apos;ladi.</div>
      </section>
    );
  }

  const active = channels.find((c) => c.active) ?? channels[0];
  const activeName = active.title || active.id;
  const switching = channels.find((c) => busy.is(`switch:${c.id}`));
  const res = checks[active.id];
  const checking = busy.is(`verify:${active.id}`);
  const removing = busy.is(`remove:${active.id}`);

  function menuKey(e: KeyboardEvent<HTMLDivElement>) {
    const items = Array.from(menu.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
    const at = items.indexOf(document.activeElement as HTMLButtonElement);
    if (e.key === "Escape") { e.preventDefault(); setOpen(false); trigger.current?.focus(); }
    else if (e.key === "ArrowDown") { e.preventDefault(); items[(at + 1) % items.length]?.focus(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); items[(at - 1 + items.length) % items.length]?.focus(); }
    else if (e.key === "Tab") setOpen(false);
  }

  return (
    <section className={styles.list} aria-label="YouTube kanallari">
      {head}
      <div className={styles.select} ref={wrap}>
        <button type="button" ref={trigger} className={`${styles.trigger}${open ? " " + styles.open : ""}`}
          aria-haspopup="listbox" aria-expanded={open} aria-label={`Faol kanal: ${activeName}. Almashtirish`}
          disabled={mutating && !switching} onClick={() => setOpen((o) => !o)}
          onKeyDown={(e) => { if (e.key === "ArrowDown" && !open) { e.preventDefault(); setOpen(true); } }}>
          <span className={styles.avatar} aria-hidden>{initial(switching ? switching.title || switching.id : activeName)}</span>
          <span className={styles.names}>
            <span className={styles.kicker}>{switching ? "Almashtirilmoqda" : "Faol kanal"}</span>
            <span className={styles.title}>{switching ? switching.title || switching.id : activeName}</span>
            {active.expired && !switching ? <span className={styles.expired}><CircleX size={14} />Token eskirgan — uzib, qayta ulang</span>
              : <span className={styles.meta}>{active.pending ? `${active.pending} ta video navbatda` : "Navbat bo'sh"}{channels.length > 1 ? ` · jami ${channels.length} ta kanal` : ""}</span>}
          </span>
          <span className={styles.chev} aria-hidden>{switching ? <LoaderCircle className="spin" /> : <ChevronDown />}</span>
        </button>

        {open ? (
          <div className={styles.menu} ref={menu} role="listbox" aria-label="Kanalni tanlang" onKeyDown={menuKey}>
            {channels.map((c) => {
              const name = c.title || c.id;
              return (
                <button key={c.id} type="button" role="option" aria-selected={c.active} className={`${styles.option}${c.active ? " " + styles.on : ""}`}
                  onClick={() => void makeActive(c)}>
                  <span className={styles.oAvatar} aria-hidden>{initial(name)}</span>
                  <span className={styles.oNames}>
                    <span className={styles.oTitle}>{name}</span>
                    <span className={c.expired ? styles.oBad : styles.oMeta}>
                      {c.expired ? "Token eskirgan" : c.pending ? `${c.pending} ta video navbatda` : "Navbat bo'sh"}
                    </span>
                  </span>
                  {c.active ? <Check className={styles.oCheck} strokeWidth={3} aria-hidden /> : null}
                </button>
              );
            })}
            <div className={styles.sep} role="separator" />
            <button type="button" className={styles.add} disabled={connectDisabled} onClick={() => { setOpen(false); void connect(); }}>
              <span className={styles.addIc} aria-hidden>{connectDisabled ? <Lock /> : <Plus />}</span>
              <span className={styles.oNames}>
                <span className={styles.oTitle}>Yana kanal ulash</span>
                <span className={styles.oMeta}>{blocked ?? (full ? `Eng ko'pi ${yt.maxChannels} ta kanal` : "Google orqali · darhol faol bo'ladi")}</span>
              </span>
            </button>
          </div>
        ) : null}
      </div>

      {res ? (
        <div className={`${styles.check} ${res.ok ? styles.ok : styles.bad}`} role="status">
          {res.ok ? <CircleCheck /> : <CircleX />}<span>{res.message}</span>
        </div>
      ) : null}
      <div className={styles.actions}>
        <Btn tone="secondary" size="sm" icon={ShieldCheck} loading={checking} onClick={() => void verify(active)}>
          {checking ? "Tekshirilmoqda" : "Ulanishni tekshirish"}
        </Btn>
        <Btn tone="ghost" size="sm" icon={Link2Off} loading={removing} disabled={mutating && !removing} onClick={() => void remove(active)}>
          Uzish
        </Btn>
        {busy.is("connect") ? <span className={styles.connecting}><LoaderCircle className="spin" />Google oynasi ochilmoqda…</span> : null}
      </div>
      <div className={`hint ${styles.foot}`}>
        Mavzular, kanal tahlili va yangi videolar faol kanal uchun. Avval qo&apos;shilgan videolar o&apos;z kanaliga yuklanaveradi —
        kanalni almashtirish ularga ta&apos;sir qilmaydi.
      </div>
    </section>
  );
}

/** Avatar uchun bosh harf (emoji yoki belgi bilan boshlansa ham bitta ko'rinadigan belgi). */
function initial(name: string) {
  const ch = Array.from(name.trim())[0] ?? "?";
  return ch.toLocaleUpperCase();
}

function without<T>(m: Record<string, T>, key: string): Record<string, T> {
  const next = { ...m };
  delete next[key];
  return next;
}

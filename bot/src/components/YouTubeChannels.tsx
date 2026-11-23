"use client";

import { useState } from "react";
import { Check, CircleCheck, CircleX, Link2Off, LoaderCircle, Lock, Plus, ShieldCheck, Tv } from "lucide-react";
import { api, confirmBox, openExternal } from "@/lib/client";
import type { Toast } from "./App";
import { Alert, Btn, SkelCard, useBusy } from "./ui";
import styles from "./YouTubeChannels.module.css";

export type YtChannelInfo = { id: string; title: string; connectedAt: string; active: boolean; pending: number };
export type YtStatus = {
  connected: boolean; channelId: string; channelTitle: string; connectedAt: string | null;
  channels: YtChannelInfo[]; maxChannels: number; clientConfigured: boolean; redirectUri: string;
};

type CheckResult = { ok: boolean; message: string };

/**
 * Ulangan YouTube kanallari: bir hisobga bir nechta kanal, bittasi faol. Faol kanal almashtirilsa mavzular, kanal tahlili va
 * yangi videolar shu kanal bo'yicha bo'ladi. Avval qo'shilgan videolar o'z kanaliga yuklanaveradi.
 * Dizayn: Kanallar va YouTube (0hBKBn) - faol kanal ko'tarilgan karta, boshqasini bosib faol qilinadi; Birinchi ulash (jvx45U).
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
  const channels = yt?.channels ?? [];
  const full = !!yt && channels.length >= yt.maxChannels;
  /** Kanal ro'yxatini o'zgartiradigan amal ketayotganda boshqa o'zgartiruvchi tugmalar kutadi (tekshirish bundan mustasno). */
  const mutating = busy.is("connect") || channels.some((c) => busy.is(`switch:${c.id}`) || busy.is(`remove:${c.id}`));

  async function connect() {
    await busy.run("connect", async () => {
      const { url } = await api<{ url: string }>("/api/youtube", { action: "start" });
      openExternal(url);
      toast("Google oynasi ochildi. Kanalni tanlab ruxsat bergach shu yerga qayting.");
    });
  }
  async function makeActive(c: YtChannelInfo) {
    if (c.active || mutating) return;
    const r = await busy.run(`switch:${c.id}`, () => api<YtStatus>("/api/youtube", { action: "switch", id: c.id }));
    if (r === false) return;
    setYt(r);
    toast(`Faol kanal: ${c.title || c.id}. Mavzular va kanal tahlili shu kanal bo'yicha.`);
    onSwitched?.();
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

  return (
    <section className={styles.list} aria-label="YouTube kanallari">
      {head}
      <div className={styles.list} role="radiogroup" aria-label="Faol kanal">
        {channels.map((c) => {
          const name = c.title || c.id;
          const switching = busy.is(`switch:${c.id}`);
          const checking = busy.is(`verify:${c.id}`);
          const removing = busy.is(`remove:${c.id}`);
          const res = checks[c.id];
          const result = res ? (
            <div className={`${styles.check} ${res.ok ? styles.ok : styles.bad}`} role="status">
              {res.ok ? <CircleCheck /> : <CircleX />}<span>{res.message}</span>
            </div>
          ) : null;
          return (
            <article key={c.id} className={`${styles.tile}${c.active ? " " + styles.active : ""}`}>
              <button type="button" role="radio" aria-checked={c.active} className={styles.pick}
                disabled={c.active || mutating} onClick={() => void makeActive(c)}
                aria-label={c.active ? `${name} — faol kanal` : `${name} — faol qilish`}>
                <span className={styles.avatar} aria-hidden>{initial(name)}</span>
                <span className={styles.names}>
                  {c.active ? <span className={styles.kicker}>Faol kanal</span> : null}
                  <span className={styles.title}>{name}</span>
                  {!c.active ? (
                    <span className={styles.meta}>
                      {switching ? "Faol qilinmoqda…" : `${c.pending ? `${c.pending} ta video navbatda · ` : ""}bosib faol qiling`}
                    </span>
                  ) : null}
                </span>
                <span className={styles.radio} aria-hidden>
                  {c.active ? <Check strokeWidth={3} /> : switching ? <LoaderCircle className="spin" /> : null}
                </span>
              </button>

              {c.active ? (
                <>
                  <div className={styles.queue}>
                    <span className={`focal ${styles.count}`}>{c.pending}</span>
                    <b>ta video navbatda</b>
                  </div>
                  <p className={styles.desc}>Mavzular, kanal tahlili va yangi videolar shu kanal uchun.</p>
                </>
              ) : null}
              {result}

              <div className={styles.actions}>
                <Btn tone="secondary" size="sm" icon={ShieldCheck} loading={checking} onClick={() => void verify(c)}>
                  {checking ? "Tekshirilmoqda" : "Tekshirish"}
                </Btn>
                <Btn tone="ghost" size="sm" icon={Link2Off} loading={removing} disabled={mutating && !removing} onClick={() => void remove(c)}>
                  Uzish
                </Btn>
              </div>
            </article>
          );
        })}
      </div>

      <Btn tone="secondary" block icon={Plus} loading={busy.is("connect")} disabled={connectDisabled} onClick={() => void connect()}>Yana kanal ulash</Btn>
      <div className={`hint ${styles.foot}`}>
        {lockHint}
        <span>
          Google oynasida kerakli hisob yoki brend kanalini tanlang. Ulangan kanal darhol faol bo&apos;ladi; avval qo&apos;shilgan videolar
          o&apos;z kanaliga yuklanaveradi — kanalni almashtirish ularga ta&apos;sir qilmaydi.
        </span>
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

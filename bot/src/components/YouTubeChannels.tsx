"use client";

import { useState } from "react";
import { api, confirmBox, openExternal } from "@/lib/client";
import type { Toast } from "./App";

export type YtChannelInfo = { id: string; title: string; connectedAt: string; active: boolean; pending: number };
export type YtStatus = {
  connected: boolean; channelId: string; channelTitle: string; connectedAt: string | null;
  channels: YtChannelInfo[]; maxChannels: number; clientConfigured: boolean; redirectUri: string;
};

/**
 * Ulangan YouTube kanallari: bir hisobga bir nechta kanal, bittasi faol. Faol kanal almashtirilsa mavzular, kanal tahlili va
 * yangi videolar shu kanal bo'yicha bo'ladi. Avval qo'shilgan videolar o'z kanaliga yuklanaveradi.
 */
export default function YouTubeChannels({ yt, setYt, toast, blocked, onSwitched }: {
  yt: YtStatus | null; setYt: (s: YtStatus) => void; toast: Toast;
  /** Ulash tugmasini to'sadigan sabab (Client ID/Secret yo'q yoki saqlanmagan o'zgarish). */
  blocked: string | null;
  onSwitched?: () => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [check, setCheck] = useState<{ id: string; ok: boolean; message: string } | null>(null);
  const err = (e: unknown) => toast(e instanceof Error ? e.message : String(e), true);
  const channels = yt?.channels ?? [];
  const full = !!yt && channels.length >= yt.maxChannels;

  async function connect() {
    setBusy("connect");
    try { const { url } = await api<{ url: string }>("/api/youtube", { action: "start" }); openExternal(url); toast("Google oynasi ochildi. Kanalni tanlab ruxsat bergach shu yerga qayting."); }
    catch (e) { err(e); }
    setBusy(null);
  }
  async function makeActive(c: YtChannelInfo) {
    if (c.active) return;
    setBusy(c.id);
    try {
      setYt(await api<YtStatus>("/api/youtube", { action: "switch", id: c.id }));
      toast(`Faol kanal: ${c.title}. Mavzular va kanal tahlili shu kanal bo'yicha.`);
      onSwitched?.();
    } catch (e) { err(e); }
    setBusy(null);
  }
  async function verify(c: YtChannelInfo) {
    setBusy(c.id); setCheck(null);
    try { setCheck({ id: c.id, ...(await api<{ ok: boolean; message: string }>("/api/youtube", { action: "verify", id: c.id })) }); }
    catch (e) { setCheck({ id: c.id, ok: false, message: e instanceof Error ? e.message : String(e) }); }
    setBusy(null);
  }
  async function remove(c: YtChannelInfo) {
    const warn = c.pending ? ` Bu kanalga bog'langan ${c.pending} ta video hali YouTube'ga chiqmagan — ular kanal qayta ulanmaguncha yuklanmaydi.` : "";
    if (!(await confirmBox(`«${c.title}» kanali uziladimi?${warn}`))) return;
    setBusy(c.id);
    try { setYt(await api<YtStatus>("/api/youtube", { action: "remove", id: c.id })); toast(`«${c.title}» uzildi.`); onSwitched?.(); }
    catch (e) { err(e); }
    setBusy(null);
  }

  return (
    <>
      <div className="sect">YouTube kanallari</div>
      <div className="card formcard">
        {!yt ? <div className="skeleton" style={{ height: 60 }} /> : channels.length === 0 ? (
          <div className="hint">Kanal ulanmagan. Bir nechta kanal ulash mumkin — har birining mavzulari, tahlili va videolari alohida.</div>
        ) : (
          <div>
            {channels.map((c) => (
              <div key={c.id} className="track">
                <label className="chk info" style={{ cursor: c.active ? "default" : "pointer" }}>
                  <input type="radio" name="yt-active" checked={c.active} disabled={!!busy} onChange={() => void makeActive(c)} />
                  <span>
                    <b>{c.title || c.id}</b>{" "}
                    {c.active ? <span className="chip ok">faol</span> : null}{" "}
                    {c.pending ? <span className="chip mute">{c.pending} ta video navbatda</span> : null}
                    {check?.id === c.id ? <span className={check.ok ? "hint" : "err"} style={{ display: "block" }}>{check.ok ? "✓ " : "✗ "}{check.message}</span> : null}
                  </span>
                </label>
                <button className="btn" disabled={!!busy} onClick={() => void verify(c)}>{busy === c.id ? "…" : "Tekshirish"}</button>
                <button className="btn danger" disabled={!!busy} onClick={() => void remove(c)}>Uzish</button>
              </div>
            ))}
          </div>
        )}
        <button className={`btn block${channels.length ? "" : " primary"}`} disabled={!!busy || !yt || !!blocked || full} onClick={() => void connect()}>
          {channels.length ? "+ Yana kanal ulash" : "YouTube kanalini ulash"}
        </button>
        {blocked ? <div className="hint">{blocked}</div> : full ? <div className="hint">Eng ko&apos;pi {yt?.maxChannels} ta kanal ulanadi.</div> : null}
        {channels.length > 1 ? (
          <div className="hint">
            Faol kanal: <b>{yt?.channelTitle}</b>. Mavzular ro&apos;yxati, kanal tahlili va yangi qo&apos;shiladigan mavzular shu kanal uchun.
            Avval qo&apos;shilgan videolar o&apos;z kanaliga yuklanadi — kanalni almashtirish ularga ta&apos;sir qilmaydi.
          </div>
        ) : null}
        <div className="hint">Ikkinchi kanalni ulashda Google oynasida kerakli hisob yoki brend kanalini tanlang. Ulangan kanal darhol faol bo&apos;ladi.</div>
      </div>
    </>
  );
}

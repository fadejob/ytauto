"use client";

import { useCallback, useEffect, useState } from "react";
import { Copy, Eye, Plus, RefreshCw, X } from "lucide-react";
import { api, confirmBox, haptic } from "@/lib/client";
import type { Toast } from "./App";
import { Alert, Btn, Card, Skel, Status, useBusy } from "./ui";
import s from "./Account.module.css";

type ChannelMcp = { id: string; title: string; name: string; url: string; command?: string };
type Info = { exists: boolean; createdAt: string | null; lastUsedAt: string | null; url: string; channels: ChannelMcp[]; token?: string; command?: string; message?: string };

const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const when = (iso: string) => { const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}, ${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`; };

/**
 * Claude Code ulash: alohida token yaratiladi va HAR BIR YouTube kanali uchun alohida `claude mcp add` buyrug'i chiqadi (ytauto-<kanal>):
 * har bir MCP faqat o'z kanali bilan ishlaydi. Kanal yo'q bo'lsa - bitta umumiy buyruq. Token faqat yaratilgan paytda ko'rinadi;
 * yangisi eskisini bekor qiladi (keyin ulangan kanal uchun ham yangi token - hamma buyruqlar qayta beriladi).
 */
export default function McpConnect({ toast }: { toast: Toast }) {
  const [info, setInfo] = useState<Info | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [fresh, setFresh] = useState<Info | null>(null);
  const fail = useCallback((m: string) => { haptic.fail(); toast(m, true); }, [toast]);
  const busy = useBusy(fail);

  // Holat: setState faqat javob kelganda (effekt ichida sinxron emas). Xato - shu kartada «Qayta urinish» bilan.
  const fetchInfo = useCallback(() => api<Info>("/api/mcp-token").then(setInfo)
    .catch((e) => { const m = e instanceof Error ? e.message : String(e); setLoadError(m); toast(m, true); }), [toast]);
  useEffect(() => { void fetchInfo(); }, [fetchInfo]);
  const load = () => { setLoadError(null); void fetchInfo(); };

  async function create() {
    if (info?.exists && !(await confirmBox("Yangi token yaratilsinmi?\n\nOldingi token darhol ishlamay qoladi — Claude Code'ni yangi matn bilan qayta ulashingiz kerak bo'ladi."))) return;
    await busy.run("create", async () => {
      const r = await api<Info>("/api/mcp-token", { action: "create" });
      setInfo(r); setFresh(r);
      haptic.ok(); if (r.message) toast(r.message);
    });
  }

  async function revoke() {
    if (!(await confirmBox("Claude Code tokeni bekor qilinsinmi?\n\nU bilan ulangan Claude Code endi YtAuto'ga murojaat qila olmaydi."))) return;
    await busy.run("revoke", async () => {
      const r = await api<Info>("/api/mcp-token", { action: "revoke" });
      setInfo(r); setFresh(null);
      haptic.ok(); if (r.message) toast(r.message);
    });
  }

  async function copy(text: string, key = "copy", what = "Buyruq nusxalandi.") {
    await busy.run(key, async () => {
      try { await navigator.clipboard.writeText(text); }
      catch { throw new Error("Nusxalab bo'lmadi."); }
      haptic.ok(); toast(what);
    });
  }

  const status = !info ? null
    : info.exists && info.lastUsedAt ? <Status tone="ok">Ulangan</Status>
    : info.exists ? <Status>Ishlatilmagan</Status>
    : <Status>Ulanmagan</Status>;
  const working = busy.is("create") || busy.is("revoke");

  return (
    <Card title="Claude Code (MCP)" action={status}>
      <div className="sub">
        Claude Code mavzularni qo&apos;shadi va boshqaradi, YouTube videolaringizni tahrirlaydi va loyihangizdan demo video yaratadi.
        Har bir kanal uchun alohida MCP — u faqat o&apos;z kanali bilan ishlaydi. YouTube&apos;ga faqat siz aniq buyursangiz chiqadi.
      </div>

      {fresh?.command ? (
        <div className={s.secret} role="region" aria-label="Claude Code ulash buyrug'i">
          <div className={s.secretHead}>
            <span className={s.secretKey}><Eye />Faqat hozir ko&apos;rsatiladi</span>
            <button type="button" className={s.close} aria-label="Yopish" onClick={() => setFresh(null)}><X size={18} /></button>
          </div>
          {fresh.channels.length > 1 ? (
            <>
              <div className={s.secretTitle}>Har bir kanal — alohida MCP. Buyruqlarni terminalda (yoki Claude Code&apos;da) bajaring</div>
              {fresh.channels.map((c) => (
                <div key={c.id} className={s.channelCmd}>
                  <div className={s.channelCmdHead}><b>{c.title || c.id}</b><span className={s.secretNote}>{c.name}</span></div>
                  <code className={`${s.secretValue} ${s.secretLine}`} title={c.command}>{c.command}</code>
                  <Btn tone="secondary" size="sm" icon={Copy} loading={busy.is("copy:" + c.id)} done={busy.done("copy:" + c.id)}
                    onClick={() => copy(c.command!, "copy:" + c.id, `«${c.title}» buyrug'i nusxalandi.`)} aria-label={`${c.title} buyrug'ini nusxalash`}>Nusxalash</Btn>
                </div>
              ))}
              <div className={s.secretActions}>
                <Btn icon={Copy} loading={busy.is("copy")} done={busy.done("copy")} onClick={() => copy(fresh.command!, "copy", "Hamma buyruqlar nusxalandi.")}>Hammasini nusxalash</Btn>
                <span className={s.secretNote}>So&apos;ng Claude&apos;ga qaysi kanal uchun ekanini ayting</span>
              </div>
            </>
          ) : (
            <>
              <div className={s.secretTitle}>Buyruqni terminalda (yoki Claude Code&apos;da) bajaring</div>
              {/* Bir qatorga sig'adigan boshi ko'rinadi, qolgani «…»; nusxa - to'liq buyruq. */}
              <code className={`${s.secretValue} ${s.secretLine}`} title={fresh.command}>{fresh.command}</code>
              <div className={s.secretActions}>
                <Btn icon={Copy} loading={busy.is("copy")} done={busy.done("copy")} onClick={() => copy(fresh.command!)} aria-label="Buyruqni nusxalash">Nusxalash</Btn>
                <span className={s.secretNote}>So&apos;ng «shu loyihaning demo videosini yarat» deng</span>
              </div>
            </>
          )}
        </div>
      ) : null}

      {!info ? (
        loadError ? (
          <Alert>
            Holatni yuklab bo&apos;lmadi: {loadError}{" "}
            <button type="button" className="linkbtn" onClick={load}>Qayta urinish</button>
          </Alert>
        ) : (
          <div className={s.info} aria-busy="true" aria-label="Yuklanmoqda">
            {[0, 1, 2].map((i) => <div key={i} className={s.infoRow}><Skel h={12} w={80} /><Skel h={12} w={110} /></div>)}
          </div>
        )
      ) : (
        <div className={s.info}>
          <div className={s.infoRow}><span>Server</span><span className={s.select}>{info.url}</span></div>
          {info.channels.map((c) => (
            <div key={c.id} className={s.infoRow}><span>{c.title || c.id}</span><span className={s.select}>{c.name}</span></div>
          ))}
          {info.exists ? (
            <>
              <div className={s.infoRow}><span>Yaratilgan</span><span>{info.createdAt ? when(info.createdAt) : "—"}</span></div>
              <div className={s.infoRow}><span>Oxirgi ulanish</span><span>{info.lastUsedAt ? when(info.lastUsedAt) : "hali ishlatilmagan"}</span></div>
            </>
          ) : (
            <div className={s.infoRow}><span>Token</span><span>hali yo&apos;q</span></div>
          )}
        </div>
      )}

      {info && !info.exists && !fresh ? (
        <div className="hint">Token yarating, chiqqan buyruqni nusxalab terminalda (yoki Claude Code&apos;da) bajaring.{info.channels.length > 1 ? " Har bir YouTube kanali uchun alohida MCP buyrug'i chiqadi — har biri faqat o'z kanali bilan ishlaydi." : " So'ng «shu loyihaning demo videosini yarat» deng."}</div>
      ) : null}

      <div className="grid2">
        {info?.exists
          ? <Btn tone="secondary" icon={RefreshCw} loading={busy.is("create")} disabled={working} onClick={create}>Yangi token</Btn>
          : <Btn icon={Plus} loading={busy.is("create")} disabled={!info || working} onClick={create}>Token yaratish</Btn>}
        <Btn tone="danger" loading={busy.is("revoke")} disabled={!info?.exists || working} onClick={revoke}>Bekor qilish</Btn>
      </div>
    </Card>
  );
}

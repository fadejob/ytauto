"use client";

import { useEffect, useState } from "react";
import { api, confirmBox, haptic } from "@/lib/client";
import type { Toast } from "./App";
import { IconCopy } from "./icons";

type Info = { exists: boolean; createdAt: string | null; lastUsedAt: string | null; url: string; token?: string; command?: string; message?: string };

const MONTHS = ["yan", "fev", "mar", "apr", "may", "iyn", "iyl", "avg", "sen", "okt", "noy", "dek"];
const when = (iso: string) => { const d = new Date(iso); return `${d.getDate()} ${MONTHS[d.getMonth()]}, ${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`; };

/**
 * Claude Code ulash: alohida token yaratiladi va bitta `claude mcp add` buyrug'i (bir qatorli ko'rinish + nusxalash ikonkasi) chiqadi.
 * Token faqat yaratilgan paytda ko'rinadi; yangisi eskisini bekor qiladi.
 */
export default function McpConnect({ toast }: { toast: Toast }) {
  const [info, setInfo] = useState<Info | null>(null);
  const [fresh, setFresh] = useState<Info | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api<Info>("/api/mcp-token").then(setInfo).catch((e) => toast(e instanceof Error ? e.message : String(e), true));
  }, [toast]);

  async function create() {
    if (info?.exists && !(await confirmBox("Yangi token yaratilsinmi?\n\nOldingi token darhol ishlamay qoladi — Claude Code'ni yangi matn bilan qayta ulashingiz kerak bo'ladi."))) return;
    setBusy(true);
    try {
      const r = await api<Info>("/api/mcp-token", { action: "create" });
      setInfo(r); setFresh(r);
      haptic.ok(); if (r.message) toast(r.message);
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(false);
  }

  async function revoke() {
    if (!(await confirmBox("Claude Code tokeni bekor qilinsinmi?\n\nU bilan ulangan Claude Code endi YtAuto'ga murojaat qila olmaydi."))) return;
    setBusy(true);
    try {
      const r = await api<Info>("/api/mcp-token", { action: "revoke" });
      setInfo(r); setFresh(null);
      haptic.ok(); if (r.message) toast(r.message);
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(false);
  }

  async function copy(text: string) {
    try { await navigator.clipboard.writeText(text); haptic.ok(); toast("Buyruq nusxalandi."); }
    catch { toast("Nusxalab bo'lmadi.", true); }
  }

  // Bir qatorga sig'adigan boshi ko'rinadi, qolgani «…»; nusxa - to'liq buyruq.
  const line = { flex: 1, minWidth: 0, whiteSpace: "nowrap" as const, overflow: "hidden", textOverflow: "ellipsis", fontSize: 12.5, padding: "8px 10px", borderRadius: 8, background: "var(--soft)" };

  return (
    <>
      <div className="sect">Claude Code ulash (MCP)</div>
      <div className="card formcard">
        <div className="hint">
          Claude Code loyihangizni o&apos;rganib, screenshot va qisqa videolarni YtAuto&apos;ga yuboradi — ssenariy, ovoz, montaj va render
          kompyuteringizda bajariladi. Token yarating, buyruqni nusxalab terminalda (yoki Claude Code&apos;da) bajaring. So&apos;ng
          «shu loyihaning demo videosini yarat» deng. YouTube&apos;ga faqat siz aniq buyursangiz chiqadi.
        </div>

        {fresh?.command ? (
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <code style={line}>{fresh.command}</code>
            <button className="iconbtn" aria-label="Buyruqni nusxalash" onClick={() => copy(fresh.command!)}><IconCopy /></button>
          </div>
        ) : null}

        {!info ? <div className="skeleton" /> : (
          <div className="logline">
            <div className="hint">Server: <code style={{ userSelect: "all" }}>{info.url}</code></div>
            <div className="hint">
              {info.exists
                ? <>Token bor · yaratilgan {when(info.createdAt!)} · {info.lastUsedAt ? `oxirgi ulanish ${when(info.lastUsedAt)}` : "hali ishlatilmagan"}</>
                : "Hali token yo'q."}
            </div>
          </div>
        )}

        <div className="grid2">
          <button className="btn primary" disabled={busy || !info} onClick={create}>{info?.exists ? "Yangi token va matn" : "Token yaratish"}</button>
          <button className="btn danger" disabled={busy || !info?.exists} onClick={revoke}>Bekor qilish</button>
        </div>
      </div>
    </>
  );
}

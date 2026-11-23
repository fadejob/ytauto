"use client";

import { useCallback } from "react";
import { LogOut } from "lucide-react";
import { confirmBox, haptic } from "@/lib/client";
import Guests from "../Guests";
import McpConnect from "../McpConnect";
import { Btn, Card, PageHead, Status, useBusy } from "../ui";
import type { SettingsPageProps } from "./types";
import s from "./Account.module.css";

/**
 * «Hisob»: kim va nima sizning YtAuto'ingizga kira oladi. Asosiy hisob - qo'shimcha tokenlar, Claude Code (MCP), chiqish.
 * Qo'shimcha token (mehmon) - tokenlarni boshqara olmaydi: o'rniga qaysi kompyuterda ishlashi haqida eslatma.
 */
export default function Account({ state, toast, back, onLogout }: SettingsPageProps) {
  const guest = state?.account.guest === true;
  const host = state?.account.hostLabel;
  const fail = useCallback((m: string) => { haptic.fail(); toast(m, true); }, [toast]);
  const busy = useBusy(fail);

  async function logout() {
    if (!(await confirmBox("Bu Telegram hisobi YtAuto hisobidan uzilsinmi? Qayta kirish uchun token kerak bo'ladi."))) return;
    await busy.run("logout", async () => { await onLogout(); });
  }

  return (
    <div className="page narrow">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Hisob" sub="Kim va nima sizning YtAuto'ingizga kira oladi" />

      {guest ? (
        <Card title="Qo'shimcha token" action={<Status>Mehmon</Status>}>
          <div className="sub">
            Videolaringiz {host ? <b>«{host}»</b> : "token bergan hisob"} kompyuterida, uning ish vaqtida tayyorlanadi —
            chiqish vaqti yaqinlari birinchi. Kech tayyor bo&apos;lsa, YouTube&apos;ga tayyor bo&apos;lgandan 1 soat keyin chiqadi.
            «Hozir chiqarish» va kompyuter sozlamalari (jadval, Claude, render, limitlar) kompyuter egasida.
            Gemini kaliti, promptlar, ovoz profillari va YouTube kanalingiz — o&apos;zingizniki.
          </div>
        </Card>
      ) : state ? <Guests toast={toast} /> : null}

      <McpConnect toast={toast} />

      <section className={`card ${s.logout}`} aria-label="Hisobdan chiqish">
        <Btn tone="ghost" block icon={LogOut} className={s.logoutBtn} loading={busy.is("logout")} onClick={logout}>Hisobdan chiqish</Btn>
        <div className="sub">Bu Telegram YtAuto hisobidan uziladi. Boshqa token bilan kirish uchun ham shu yerdan chiqing.</div>
      </section>
    </div>
  );
}

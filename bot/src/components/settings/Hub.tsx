"use client";

import { Clapperboard, Cpu, LibraryBig, Mic, Sparkles, Tv, UserRound } from "lucide-react";
import { SettingsRow } from "../ui";
import type { SettingsPage, SettingsPageProps } from "./types";

/** Sozlamalar markazi: kerakli bo'limni bir bosishda topish. Har qatorda joriy holat bir qatorda. */
export default function Hub({ state, open }: Pick<SettingsPageProps, "state" | "open">) {
  const guest = state?.account.guest === true;
  const ch = state?.channel;
  const geminiOff = state?.gemini.ready === false;
  const row = (p: SettingsPage, icon: Parameters<typeof SettingsRow>[0]["icon"], title: string, summary: string, warn?: boolean) =>
    <SettingsRow icon={icon} title={title} summary={summary} warn={warn} onClick={() => open(p)} />;
  return (
    <div className="page narrow">
      <header className="page-head"><h1>Sozlamalar</h1><div className="sub">Kerakli bo&apos;limni bir bosishda toping</div></header>
      <div className="group">
        {row("account", UserRound, "Hisob", guest ? `Qo'shimcha token · ${state?.account.hostLabel ?? "mezbon kompyuter"}` : "Qo'shimcha tokenlar · Claude Code (MCP) · chiqish")}
      </div>
      <div className="section-label">Video yaratish</div>
      <div className="group">
        {row("channels", Tv, "Kanallar va YouTube", ch?.total ? `${ch.total} ta kanal · faol: ${ch.title}` : "Kanal ulanmagan", !ch?.total)}
        {row("voice", Mic, "Ovoz", "Ovoz profillari, tezlik va «Mening ovozim»")}
        {row("video", Clapperboard, "Video va sifat", "Standart format va shablon · real rasmlar · render")}
        {row("library", LibraryBig, "Shablonlar va musiqa", "Shablon yuklash · fon musiqasi kutubxonasi")}
      </div>
      <div className="section-label">AI va kompyuter</div>
      <div className="group">
        {row("ai", Sparkles, "AI va promptlar", geminiOff ? "Gemini kaliti kiritilmagan — videolar to'xtagan" : "Gemini kalitlari va modellar · Claude · promptlar", geminiOff)}
        {!guest ? row("computer", Cpu, "Kompyuter va jadval", "Tungi ish vaqti · kompyuterlar · cheklovlar") : null}
      </div>
    </div>
  );
}

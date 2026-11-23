"use client";

import { useEffect, useState } from "react";
import { Clapperboard, Lock, Mic, Monitor, Music, Sparkles, Tv, UserRound } from "lucide-react";
import { api } from "@/lib/client";
import { useBotConfig, useSchedule } from "@/lib/settings-data";
import type { BotConfig, Machine, State } from "@/lib/types";
import { SettingsRow, Skel, Status, type IconT } from "../ui";
import type { SettingsPage, SettingsPageProps } from "./types";
import s from "./Hub.module.css";

/** Ro'yxatdagi qisqa format nomi (dizayn: «16:9 · Clean Explainer · real rasmlar»). */
const FORMAT_SHORT: Record<number, string> = { 0: "16:9", 1: "Shorts 9:16", 2: "1:1" };

/** "claude-opus-5-5" → "Claude Opus 5.5"; bo'sh - Claude Code'ning o'z standarti. */
function claudeName(id: string): string {
  if (!id) return "Claude standart";
  const m = /^claude-([a-z]+)-([\d-]+)$/i.exec(id);
  if (!m) return id;
  return `Claude ${m[1].charAt(0).toUpperCase()}${m[1].slice(1)} ${m[2].split("-").filter(Boolean).join(".")}`;
}

/** Faqat harf/raqamdan boshlanadigan so'zlar: «Brauzer (dev)» → «B», «Aziz Karimov» → «AK». */
const initials = (name: string) =>
  name.trim().split(/\s+/).map((w) => w.replace(/^[^\p{L}\p{N}]+/u, "")).filter(Boolean).slice(0, 2).map((w) => w.charAt(0).toUpperCase()).join("") || "?";

type Row = { page: SettingsPage; icon: IconT; title: string; summary: string; warn?: boolean };

function summaries(state: State | null, config: BotConfig | null, guest: boolean, schedule: ReturnType<typeof useSchedule>["schedule"]) {
  const ch = state?.channel;
  const cat = state?.catalog;
  const geminiOff = state?.gemini.ready === false;
  const noChannel = !!state && !ch?.total;

  const voiceName = config?.defaults.voiceProfile || cat?.defaults.voiceProfile || "";
  const voiceProfile = voiceName ? config?.voice.profiles.find((p) => p.name === voiceName) : undefined;
  const voice = !config && !state ? "Ovoz profillari, tezlik va «Mening ovozim»"
    : voiceName ? `${voiceName}${voiceProfile ? ` · ${voiceProfile.speed}×` : ""}` : "Kompyuterdagi standart ovoz";

  const fmt = config?.defaults.format ?? cat?.defaults.format;
  const tpl = config ? config.defaults.template : cat?.defaults.template;
  const video = fmt === undefined ? "Standart format va shablon · real rasmlar"
    : [FORMAT_SHORT[fmt] ?? `Format ${fmt}`, tpl || "Shablonsiz", config?.visuals.realMedia ? "real rasmlar" : null].filter(Boolean).join(" · ");

  const library = cat
    ? [`${cat.templates.length} shablon`, `${cat.music.tracks.length} trek`, guest ? "yuklash egasida" : null].filter(Boolean).join(" · ")
    : "Shablon yuklash · fon musiqasi kutubxonasi";

  const keys = config?.gemini.apiKeys.length;
  const ai = geminiOff ? "Gemini kaliti yo'q — ssenariy yozilmaydi"
    : keys === undefined ? "Gemini kalitlari va modellar · Claude · promptlar"
    : guest ? `Gemini · ${keys} kalit · promptlar`
    : `Gemini · ${keys} kalit · ${claudeName(config!.claude.model)}`;

  const computer = !schedule ? "Tungi ish vaqti · kompyuterlar · cheklovlar"
    : schedule.enabled ? `Har kuni ${schedule.startTime}–${schedule.endTime} · ${schedule.timeZoneId}`
    : "Jadval o'chiq · kompyuterlar · cheklovlar";

  const channels = noChannel ? "Kanal ulanmagan — videolar YouTube'ga chiqmaydi"
    : ch?.total ? `${ch.total} ta kanal · faol: ${ch.title}` : "YouTube kanallari va ulanish";

  return { geminiOff, noChannel, voice, video, library, ai, computer, channels };
}

/**
 * Sozlamalar markazi: tepada hisob kartasi, keyin bo'limlar. Har qatorda joriy holat bir qatorda (config, jadval, katalog).
 * E'tibor talab qiladigan bo'limlar (Gemini kaliti yo'q, kanal ulanmagan) alohida «Diqqat talab qiladi» guruhiga chiqadi.
 * Qo'shimcha token (mehmon): kompyuter sahifasi ochilmaydi - qator faqat ma'lumot (egasi boshqaradi).
 */
export default function Hub({ state, open }: Pick<SettingsPageProps, "state" | "open">) {
  const guest = state?.account.guest === true;
  const { config } = useBotConfig();
  const { schedule } = useSchedule();
  const [machines, setMachines] = useState<Machine[] | null>(null);

  // Hisob kartasidagi kompyuter nomi va onlayn holati (faqat asosiy hisob: mehmonda mezbon nomi state'da bor).
  const owner = !!state && !guest;
  useEffect(() => {
    if (!owner) return;
    let alive = true;
    api<Machine[]>("/api/machines").then((m) => { if (alive) setMachines(m); }).catch(() => { if (alive) setMachines([]); });
    return () => { alive = false; };
  }, [owner]);

  const sum = summaries(state, config, guest, guest ? null : schedule);
  const all: Row[] = [
    { page: "channels", icon: Tv, title: "Kanallar va YouTube", summary: sum.channels, warn: sum.noChannel },
    { page: "voice", icon: Mic, title: "Ovoz", summary: sum.voice },
    { page: "video", icon: Clapperboard, title: "Video va sifat", summary: sum.video },
    { page: "library", icon: Music, title: "Shablonlar va musiqa", summary: sum.library },
    { page: "ai", icon: Sparkles, title: "AI va promptlar", summary: sum.ai, warn: sum.geminiOff },
  ];
  const attention = all.filter((r) => r.warn);
  const create = all.filter((r) => !r.warn && r.page !== "ai");
  const aiRow = all.find((r) => r.page === "ai" && !r.warn);

  const row = (r: Row) => <SettingsRow key={r.page} icon={r.icon} title={r.title} summary={r.summary} warn={r.warn} onClick={() => open(r.page)} />;

  const machine = machines?.find((m) => m.online) ?? machines?.[0];
  const name = state?.viewer.name ?? "";
  const who = guest
    ? `${state?.account.hostLabel ?? "Mezbon"} kompyuterida`
    : machine ? `${machine.name} egasi` : machines ? "YtAuto hisobi egasi" : null;

  const computerRow = guest ? (
    <div className={s.info} key="computer" aria-label="Kompyuter va jadval: kompyuter egasi boshqaradi">
      <span className={s.ic}><Lock size={20} /></span>
      <span className={s.t}><b>Kompyuter va jadval</b><span>{state?.account.hostLabel ? `${state.account.hostLabel} · ` : ""}egasi boshqaradi</span></span>
    </div>
  ) : (
    <SettingsRow key="computer" icon={Monitor} title="Kompyuter va jadval" summary={sum.computer} onClick={() => open("computer")} />
  );

  return (
    <div className="page narrow">
      <header className="page-head"><h1>Sozlamalar</h1></header>

      <section className="group" aria-label="Hisob">
        <div className={s.account}>
          {state ? (
            <>
              <span className={`${s.avatar}${guest ? " " + s.guest : ""}`} aria-hidden>{initials(name)}</span>
              <span className={s.who}>
                <b>{name || "YtAuto"}</b>
                {who ? <span>{who}</span> : <Skel h={12} w={120} />}
              </span>
              {guest ? <Status>Mehmon</Status>
                : machine ? (machine.online ? <Status tone="ok">Onlayn</Status> : <Status>Oflayn</Status>) : null}
            </>
          ) : (
            <>
              <Skel h={48} w={48} r={999} />
              <span className={s.who}><Skel h={16} w="60%" /><Skel h={12} w="40%" /></span>
            </>
          )}
        </div>
        <SettingsRow icon={UserRound} title="Hisob" summary={guest ? "Claude Code · chiqish" : "Tokenlar · Claude Code · chiqish"} onClick={() => open("account")} />
      </section>

      {attention.length ? (
        <>
          <div className={`section-label ${s.alarm}`}>Diqqat talab qiladi</div>
          <div className="group">{attention.map(row)}</div>
        </>
      ) : null}

      {create.length ? (
        <>
          <div className="section-label">Video yaratish</div>
          <div className="group">{create.map(row)}</div>
        </>
      ) : null}

      <div className="section-label">{aiRow ? "AI va kompyuter" : "Kompyuter"}</div>
      <div className="group">
        {aiRow ? row(aiRow) : null}
        {computerRow}
      </div>
    </div>
  );
}

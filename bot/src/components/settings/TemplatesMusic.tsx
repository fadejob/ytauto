"use client";

import { useCallback, useState } from "react";
import { cache } from "@/lib/client";
import Library from "../Library";
import Music from "../Music";
import { PageHead, Segmented } from "../ui";
import type { SettingsPageProps } from "./types";

type Tab = "templates" | "music";
const TAB_KEY = "settings:library-tab";

/**
 * «Shablonlar va musiqa» (dizayn: Shablonlar va musiqa Fk4Jp6 / · Musiqa Trfuhk): ikki bo'lim segment bilan.
 * Ikkala bo'lim ham o'rnatilgan holda qoladi (yashiriladi) - bo'lim almashtirilganda shablon yuklash to'xtab qolmasin.
 * Mehmon (qo'shimcha token): shablon yuklash yo'q, faqat ko'rish/tanlash.
 */
export default function TemplatesMusic({ state, toast, back, onChanged }: SettingsPageProps) {
  const guest = state?.account?.guest === true;
  const [tab, setTab] = useState<Tab>(() => (cache.get<Tab>(TAB_KEY) === "music" ? "music" : "templates"));
  const [templates, setTemplates] = useState<number | undefined>(undefined);
  const [tracks, setTracks] = useState<number | undefined>(undefined);
  const onTemplates = useCallback((n: number) => setTemplates(n), []);
  const onTracks = useCallback((n: number) => setTracks(n), []);

  function pick(t: Tab) {
    setTab(t);
    cache.set(TAB_KEY, t);
  }

  return (
    <div className="page">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Shablonlar va musiqa" sub="Videolar qaysi ko'rinishda va qanday fon musiqasi bilan yig'iladi" />
      <Segmented<Tab>
        label="Bo'lim"
        value={tab}
        onChange={pick}
        items={[
          { key: "templates", label: "Shablonlar", count: templates ?? state?.catalog?.templates.length },
          { key: "music", label: "Musiqa", count: tracks ?? state?.catalog?.music?.tracks.length },
        ]}
      />
      <div role="tabpanel" aria-label="Shablonlar" hidden={tab !== "templates"}>
        <Library toast={toast} canUpload={!guest} onCount={onTemplates} assets={state?.catalog?.assets} onChanged={onChanged} />
      </div>
      <div role="tabpanel" aria-label="Musiqa" hidden={tab !== "music"}>
        <Music toast={toast} onCount={onTracks} onChanged={onChanged} active={tab === "music"} />
      </div>
    </div>
  );
}

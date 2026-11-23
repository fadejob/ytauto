"use client";

import Settings from "../Settings";
import { PageHead } from "../ui";
import type { SettingsPageProps } from "./types";

/** Vaqtinchalik: yangi dizayndagi «Video va sifat» sahifasi shu faylda quriladi (hozircha eski sozlamalar formasi). */
export default function VideoQuality({ state, toast, back, onChanged, onLogout }: SettingsPageProps) {
  return (
    <div className="page">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Video va sifat" />
      <Settings catalog={state?.catalog ?? null} toast={toast} onSaved={onChanged} onLogout={onLogout} account={state?.account} />
    </div>
  );
}

"use client";

import Settings from "../Settings";
import { PageHead } from "../ui";
import type { SettingsPageProps } from "./types";

/** Vaqtinchalik: yangi dizayndagi «Hisob» sahifasi shu faylda quriladi (hozircha eski sozlamalar formasi). */
export default function Account({ state, toast, back, onChanged, onLogout }: SettingsPageProps) {
  return (
    <div className="page">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Hisob" />
      <Settings catalog={state?.catalog ?? null} toast={toast} onSaved={onChanged} onLogout={onLogout} account={state?.account} />
    </div>
  );
}

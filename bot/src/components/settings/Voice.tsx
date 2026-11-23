"use client";

import Settings from "../Settings";
import { PageHead } from "../ui";
import type { SettingsPageProps } from "./types";

/** Vaqtinchalik: yangi dizayndagi «Ovoz» sahifasi shu faylda quriladi (hozircha eski sozlamalar formasi). */
export default function Voice({ state, toast, back, onChanged, onLogout }: SettingsPageProps) {
  return (
    <div className="page">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Ovoz" />
      <Settings catalog={state?.catalog ?? null} toast={toast} onSaved={onChanged} onLogout={onLogout} account={state?.account} />
    </div>
  );
}

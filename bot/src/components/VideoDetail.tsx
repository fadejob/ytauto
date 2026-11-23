"use client";

import type { Run, Toast } from "./App";
import type { State } from "@/lib/types";
import { PageHead } from "./ui";

/** Vaqtinchalik: «Video tafsiloti» (bosqichlar, amallar, loglar) shu faylda quriladi. */
export default function VideoDetail({ state, id, back }: { state: State | null; id: number; back: () => void; run: Run; toast: Toast; refresh: () => Promise<void> }) {
  const t = state?.topics.find((x) => x.id === id);
  return (
    <div className="page">
      <PageHead back={{ label: "Videolar", onClick: back }} title={t ? t.title || t.topic : `#${id}`} />
    </div>
  );
}

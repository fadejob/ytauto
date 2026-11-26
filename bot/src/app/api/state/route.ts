import { handler } from "@/lib/api";
import { buildState } from "@/lib/state";
import type { State } from "@/lib/types";

/** Ilova ochilganda kanal konteksti eskirgan bo'lsa fonda (after) yangilanadi - shu vaqt chegarasida. */
export const maxDuration = 300;

export const GET = handler(async (_req, viewer): Promise<State> => buildState(viewer, { refreshChannel: true }));

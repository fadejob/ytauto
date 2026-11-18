import { handler } from "@/lib/api";
import { getCatalog, listTopics } from "@/lib/topics";
import { isDemo } from "@/lib/demo";
import { geminiReady } from "@/lib/config";
import { maybeRefreshChannel } from "@/lib/channel";
import type { State } from "@/lib/types";

/** Ilova ochilganda kanal konteksti eskirgan bo'lsa fonda (after) yangilanadi - shu vaqt chegarasida. */
export const maxDuration = 300;

export const GET = handler(async (_req, viewer): Promise<State> => {
  const catalog = await getCatalog();
  const topics = await listTopics(catalog.youtube.timeZone);
  await maybeRefreshChannel();
  return {
    topics, catalog, viewer: { name: viewer.name, dev: viewer.dev }, now: new Date().toISOString(), demo: isDemo(), gemini: { ready: await geminiReady() },
    account: { guest: viewer.guest, hostLabel: viewer.hostLabel },
  };
});

import { handler } from "@/lib/api";
import { getCatalog, listTopics } from "@/lib/topics";
import { isDemo } from "@/lib/demo";
import type { State } from "@/lib/types";

export const GET = handler(async (_req, viewer): Promise<State> => {
  const catalog = await getCatalog();
  const topics = await listTopics(catalog.youtube.timeZone);
  return { topics, catalog, viewer: { name: viewer.name, dev: viewer.dev }, now: new Date().toISOString(), demo: isDemo() };
});

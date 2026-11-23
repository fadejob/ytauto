import "server-only";
import type { AccountViewer } from "./api";
import { getCatalog, listTopics } from "./topics";
import { isDemo } from "./demo";
import { geminiReady } from "./config";
import { maybeRefreshChannel } from "./channel";
import type { State } from "./types";
import { channelList, customThumbnails } from "./youtube";

/**
 * Ilovaning to'liq holati (mavzular, katalog, kanal). /api/state va /api/actions (amaldan keyin - shu so'rovning o'zida, alohida
 * so'rovsiz) ishlatadi: foydalanuvchi o'zgarishni darhol ko'radi. refreshChannel - kanal kontekstini fonda yangilash (faqat /api/state).
 */
export async function buildState(viewer: AccountViewer, opts?: { refreshChannel?: boolean }): Promise<State> {
  const catalog = await getCatalog();
  const ch = await channelList(); // eski bitta ulanish bo'lsa shu yerda ro'yxatga o'tkaziladi (mavzular ro'yxatidan oldin)
  const topics = await listTopics(catalog.youtube.timeZone);
  if (opts?.refreshChannel) await maybeRefreshChannel();
  return {
    topics, catalog, viewer: { name: viewer.name, dev: viewer.dev }, now: new Date().toISOString(), demo: isDemo(), gemini: { ready: await geminiReady() },
    account: { guest: viewer.guest, hostLabel: viewer.hostLabel },
    channel: { id: ch.active, title: ch.channels.find((c) => c.id === ch.active)?.title ?? "", total: ch.channels.length, customCover: ch.active ? await customThumbnails() : false },
  };
}

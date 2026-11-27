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
  // Mustaqil o'qishlar birga yuboriladi (postgres.js bitta ulanishda javobni kutmasdan ketma-ket yuboradi - pipelining):
  // ketma-ket await'lar o'rniga ~bitta borib-kelish. channelList birinchi - eski bitta ulanish bo'lsa ro'yxatga o'tkaziladi.
  const ch = await channelList();
  const [catalog, gemini, customCover] = await Promise.all([
    getCatalog(),
    geminiReady(),
    ch.active ? customThumbnails() : Promise.resolve(false),
    opts?.refreshChannel ? maybeRefreshChannel() : Promise.resolve(),
  ]);
  const topics = await listTopics(catalog.youtube.timeZone);
  return {
    topics, catalog, viewer: { name: viewer.name, dev: viewer.dev }, now: new Date().toISOString(), demo: isDemo(), gemini: { ready: gemini },
    account: { guest: viewer.guest, hostLabel: viewer.hostLabel },
    channel: { id: ch.active, title: ch.channels.find((c) => c.id === ch.active)?.title ?? "", total: ch.channels.length, customCover },
  };
}

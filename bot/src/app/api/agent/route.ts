import { accountHandler } from "@/lib/api";
import { liveSockets, mediaLinks } from "@/lib/agent";
import { isDemo } from "@/lib/demo";

/**
 * Kompyuter agenti bilan jonli aloqa (bot web app):
 *  GET /api/agent            → { sockets: [{ machine, url }] } - WebSocket manzillari (imzolangan, 10 daqiqa);
 *  GET /api/agent?media=ID   → { media: { video, download, cover, subtitles, machine } | null } - tayyor videoning public havolalari
 *                               (kompyuterdan to'g'ridan-to'g'ri, tunnel orqali; kompyuter o'chiq bo'lsa null).
 * Agent hisobni o'zi tekshiradi (vazifa boshqa hisobniki bo'lsa fayl berilmaydi).
 */
export const GET = accountHandler(async (req, viewer) => {
  const q = new URL(req.url).searchParams;
  if (isDemo()) return q.has("media") ? { media: null } : { sockets: [] };
  if (q.has("media")) {
    const id = Number(q.get("media"));
    if (!Number.isInteger(id) || id <= 0) return { media: null };
    return { media: await mediaLinks(viewer.accountId, id).catch(() => null) };
  }
  return { sockets: await liveSockets(viewer.accountId).catch(() => []) };
});

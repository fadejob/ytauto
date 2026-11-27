import { handler } from "@/lib/api";
import { buildState } from "@/lib/state";
import { purgeChannel } from "@/lib/channel-data";
import { disconnectYouTube, getYouTubeStatus, originOf, removeChannel, setChannelDefaults, startYouTube, switchChannel, verifyYouTube } from "@/lib/youtube";

export const GET = handler(async (req) => getYouTubeStatus(originOf(req)));

/**
 * switch/remove: javobda ilovaning yangi holati (state) ham - shu tranzaksiyada. Ilova alohida /api/state so'rovini kutmaydi
 * (har HTTP borib-kelish foydalanuvchidan serverga ~0,3 s).
 */
export const POST = handler(async (req, viewer) => {
  const { action, id, tags, description } = (await req.json()) as { action?: string; id?: string; tags?: string; description?: string };
  const channelId = typeof id === "string" ? id.trim() : "";
  if (action === "start") return startYouTube(originOf(req));
  if (action === "verify") return verifyYouTube(channelId || undefined);
  if (action === "switch") {
    if (!channelId) throw new Error("Kanal tanlanmagan.");
    await switchChannel(channelId);
    const [status, state] = await Promise.all([getYouTubeStatus(originOf(req)), buildState(viewer)]);
    return { ...status, state };
  }
  if (action === "remove") {
    if (!channelId) throw new Error("Kanal tanlanmagan.");
    await removeChannel(channelId);
    const [status, state] = await Promise.all([getYouTubeStatus(originOf(req)), buildState(viewer)]);
    return { ...status, state };
  }
  if (action === "defaults") {
    // Kanalning standart teglari va tavsifi (har kanalda alohida).
    if (!channelId) throw new Error("Kanal tanlanmagan.");
    await setChannelDefaults(channelId, { tags, description });
    return getYouTubeStatus(originOf(req));
  }
  if (action === "purge" || action === "purge_published") {
    // Kanal ma'lumotlarini tozalash (YouTube'dagi videolarga tegmaydi). Javobda yangi holat ham.
    if (!channelId) throw new Error("Kanal tanlanmagan.");
    const result = await purgeChannel(channelId, action === "purge" ? "all" : "published");
    const [status, state] = await Promise.all([getYouTubeStatus(originOf(req)), buildState(viewer)]);
    return { ...status, state, result };
  }
  if (action === "disconnect") { await disconnectYouTube(); return getYouTubeStatus(originOf(req)); }
  throw new Error("Noma'lum amal.");
});

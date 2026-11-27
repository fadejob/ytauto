import { handler } from "@/lib/api";
import { disconnectYouTube, getYouTubeStatus, originOf, removeChannel, setChannelDefaults, startYouTube, switchChannel, verifyYouTube } from "@/lib/youtube";

export const GET = handler(async (req) => getYouTubeStatus(originOf(req)));

export const POST = handler(async (req) => {
  const { action, id, tags, description } = (await req.json()) as { action?: string; id?: string; tags?: string; description?: string };
  const channelId = typeof id === "string" ? id.trim() : "";
  if (action === "start") return startYouTube(originOf(req));
  if (action === "verify") return verifyYouTube(channelId || undefined);
  if (action === "switch") {
    if (!channelId) throw new Error("Kanal tanlanmagan.");
    await switchChannel(channelId);
    return getYouTubeStatus(originOf(req));
  }
  if (action === "remove") {
    if (!channelId) throw new Error("Kanal tanlanmagan.");
    await removeChannel(channelId);
    return getYouTubeStatus(originOf(req));
  }
  if (action === "defaults") {
    // Kanalning standart teglari va tavsifi (har kanalda alohida).
    if (!channelId) throw new Error("Kanal tanlanmagan.");
    await setChannelDefaults(channelId, { tags, description });
    return getYouTubeStatus(originOf(req));
  }
  if (action === "disconnect") { await disconnectYouTube(); return getYouTubeStatus(originOf(req)); }
  throw new Error("Noma'lum amal.");
});

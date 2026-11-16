import { handler } from "@/lib/api";
import { disconnectYouTube, getYouTubeStatus, originOf, startYouTube, verifyYouTube } from "@/lib/youtube";

export const GET = handler(async (req) => getYouTubeStatus(originOf(req)));

export const POST = handler(async (req) => {
  const { action } = (await req.json()) as { action?: string };
  if (action === "start") return startYouTube(originOf(req));
  if (action === "verify") return verifyYouTube();
  if (action === "disconnect") { await disconnectYouTube(); return getYouTubeStatus(originOf(req)); }
  throw new Error("Noma'lum amal.");
});

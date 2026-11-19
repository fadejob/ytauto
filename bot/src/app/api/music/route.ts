import { handler } from "@/lib/api";
import { addTrack, getLibrary, removeTrack, setMusicSettings } from "@/lib/music";

/** Trek yuklab olinib Storage'ga qo'yiladi - bir necha soniya ketishi mumkin. */
export const maxDuration = 60;

export const GET = handler(async () => getLibrary());

export const POST = handler(async (req) => {
  const b = (await req.json().catch(() => ({}))) as { action?: string; id?: string; enabled?: unknown; volume?: unknown };
  if (b.action === "add") return addTrack(String(b.id ?? ""));
  if (b.action === "remove") return { library: await removeTrack(String(b.id ?? "")), message: "Kutubxonadan olib tashlandi." };
  if (b.action === "settings") return { library: await setMusicSettings(b), message: "Saqlandi." };
  throw new Error("Noma'lum amal.");
});

import { handler } from "@/lib/api";
import { prepareAudioUpload } from "@/lib/audio";

/** Tayyor ovoz uchun bir martalik yuklash havolasi (fayl brauzerdan to'g'ridan-to'g'ri Storage'ga boradi). */
export const POST = handler(async (req) => {
  const b = (await req.json()) as Record<string, unknown>;
  return prepareAudioUpload({ filename: String(b.filename ?? ""), size: Number(b.size) });
});

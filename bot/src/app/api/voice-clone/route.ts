import { handler } from "@/lib/api";
import { deleteSample, getVoiceClone, prepareSampleUpload, saveSample } from "@/lib/voice-clone";

/**
 * «Mening ovozim»: namuna holati, yuklash havolasi, saqlash (rozilik bilan) va o'chirish. Hisob o'z namunalarini boshqaradi.
 * channel: YouTube kanal id'si (har kanalning o'z namunasi), "" - umumiy namuna, berilmasa - faol kanal.
 */
const channelOf = (v: unknown) => (typeof v === "string" ? v.trim() : undefined);

export const GET = handler(async (req) => getVoiceClone(new URL(req.url).searchParams.get("channel")));

export const POST = handler(async (req, viewer) => {
  const b = (await req.json()) as Record<string, unknown>;
  switch (b.action) {
    case "prepare": return prepareSampleUpload({ filename: String(b.filename ?? ""), size: Number(b.size) });
    case "save":
      return saveSample({
        channel: channelOf(b.channel), path: String(b.path ?? ""), durationSec: Number(b.durationSec), passage: Number(b.passage),
        consent: b.consent === true, makeDefault: b.makeDefault === true, by: viewer.name,
      });
    case "delete": return deleteSample(channelOf(b.channel));
    default: throw new Error("Noma'lum amal.");
  }
});

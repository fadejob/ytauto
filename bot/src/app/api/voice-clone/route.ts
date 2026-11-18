import { handler } from "@/lib/api";
import { deleteSample, getVoiceClone, prepareSampleUpload, saveSample } from "@/lib/voice-clone";

/** «Mening ovozim»: namuna holati, yuklash havolasi, saqlash (rozilik bilan) va o'chirish. Hisob o'z namunasini boshqaradi. */
export const GET = handler(async () => getVoiceClone());

export const POST = handler(async (req, viewer) => {
  const b = (await req.json()) as Record<string, unknown>;
  switch (b.action) {
    case "prepare": return prepareSampleUpload({ filename: String(b.filename ?? ""), size: Number(b.size) });
    case "save":
      await saveSample({ path: String(b.path ?? ""), durationSec: Number(b.durationSec), passage: Number(b.passage), consent: b.consent === true, makeDefault: b.makeDefault === true, by: viewer.name });
      return getVoiceClone();
    case "delete": await deleteSample(); return null;
    default: throw new Error("Noma'lum amal.");
  }
});

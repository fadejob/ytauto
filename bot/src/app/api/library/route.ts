import { handler } from "@/lib/api";
import { queueCommand } from "@/lib/commands";
import { getTemplates, getVoice } from "@/lib/library";
import { isDemo } from "@/lib/demo";

export const GET = handler(async () => ({ templates: await getTemplates(), voice: await getVoice() }));

export const POST = handler(async (req) => {
  const b = (await req.json()) as { action?: string; machine?: string; name?: string };
  if (b.action === "delete_template") {
    if (!b.machine || !b.name) throw new Error("Kompyuter va shablon nomi kerak.");
    if (isDemo()) return { ok: true, message: "O'chirish so'rovi yuborildi. [demo]" };
    await queueCommand(b.machine, "delete_template", b.name);
    return { ok: true, message: "O'chirish so'rovi yuborildi: kompyuter ~2 daqiqada bajaradi." };
  }
  throw new Error("Noma'lum amal.");
});

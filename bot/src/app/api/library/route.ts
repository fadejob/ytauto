import { handler } from "@/lib/api";
import { forgetAssets } from "@/lib/channel-assets";
import { queueCommand } from "@/lib/commands";
import { getTemplates, getVoice } from "@/lib/library";
import { isDemo } from "@/lib/demo";

export const GET = handler(async () => ({ templates: await getTemplates(), voice: await getVoice() }));

export const POST = handler(async (req, viewer) => {
  if (viewer.guest) throw new Error("Qo'shimcha token shablonni o'chira olmaydi: uni kompyuter egasi boshqaradi.");
  const b = (await req.json()) as { action?: string; machine?: string; name?: string };
  if (b.action === "delete_template") {
    if (!b.machine || !b.name) throw new Error("Kompyuter va shablon nomi kerak.");
    if (isDemo()) return { ok: true, message: "O'chirish so'rovi yuborildi. [demo]" };
    await queueCommand(b.machine, "delete_template", b.name);
    // Boshqa kompyuterda shu nomli shablon qolmasa - kanal biriktirmasi ham olib tashlanadi.
    if (!(await getTemplates()).some((t) => t.name === b.name && t.machine !== b.machine)) await forgetAssets("template", [b.name]);
    return { ok: true, message: "O'chirish so'rovi yuborildi: kompyuter darhol bajaradi (o'chiq bo'lsa - yoqilganda)." };
  }
  throw new Error("Noma'lum amal.");
});

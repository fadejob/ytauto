import { handler } from "@/lib/api";
import { getConfig, saveConfig } from "@/lib/config";
import { applyPatch, toDraft, type ConfigPatch } from "@/lib/config-draft";
import type { BotConfigInput } from "@/lib/types";

export const GET = handler(async () => getConfig());

/**
 * {patch}: faqat o'zgargan maydonlar - shu tranzaksiyada bazadagi eng so'nggi config ustiga qo'yiladi (bitta so'rov, boshqa
 * qurilmadagi o'zgarishlar yo'qolmaydi). Eski klientlar to'liq config yuboradi - u ham qabul qilinadi.
 */
export const POST = handler(async (req) => {
  const body = (await req.json()) as BotConfigInput | { patch: ConfigPatch };
  if (body && typeof body === "object" && "patch" in body) return saveConfig(applyPatch(toDraft(await getConfig()), body.patch ?? {}));
  return saveConfig(body as BotConfigInput);
});

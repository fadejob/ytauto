import { handler } from "@/lib/api";
import { assignAsset } from "@/lib/channel-assets";
import { buildState } from "@/lib/state";

/**
 * Shablon yoki ovoz profilini kanalga biriktirish ({kind: "template" | "voice", name, channel: kanal id | ""}). "" - umumiy (hamma
 * kanalda). Javobda ilovaning yangi holati (katalog shu kanal bo'yicha qayta filtrlangan).
 */
export const POST = handler(async (req, viewer) => {
  if (viewer.guest) throw new Error("Qo'shimcha token shablon/ovozni kanalga biriktira olmaydi: buni hisob egasi qiladi.");
  const b = (await req.json()) as { kind?: string; name?: string; channel?: string };
  if (b.kind !== "template" && b.kind !== "voice") throw new Error("Tur noto'g'ri.");
  await assignAsset(b.kind, String(b.name ?? ""), String(b.channel ?? "").trim());
  return { ok: true, state: await buildState(viewer) };
});

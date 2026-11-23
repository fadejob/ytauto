import { handler } from "@/lib/api";
import { createGuest, deleteGuest, listGuests } from "@/lib/accounts";
import { isDemo } from "@/lib/demo";
import type { GuestToken } from "@/lib/types";

/**
 * Qo'shimcha (mehmon) tokenlar: faqat asosiy hisob egasi boshqaradi. Mehmon tokeni bilan kirgan kishi shu hisob kompyuteri orqali
 * video yaratadi (o'z mavzulari, sozlamalari, YouTube kanali), «Hozir chiqarish»siz. Mehmon videolari bu yerda ko'rsatilmaydi.
 */
export const GET = handler(async (_req, viewer): Promise<{ guests: GuestToken[] }> => {
  if (viewer.guest) throw new Error("Qo'shimcha tokenlarni faqat asosiy hisob egasi ko'radi.");
  if (isDemo()) return { guests: [] };
  return { guests: await listGuests(viewer.accountId) };
}, { ownerOnly: true });

export const POST = handler(async (req, viewer): Promise<{ guests: GuestToken[]; token?: string; message: string }> => {
  if (viewer.guest) throw new Error("Qo'shimcha token bilan tokenlarni boshqarib bo'lmaydi.");
  if (isDemo()) throw new Error("Demo rejimda token yaratilmaydi.");
  const body = (await req.json()) as { action?: string; label?: string; id?: string };
  if (body.action === "create") {
    const { token, guest } = await createGuest(viewer.accountId, String(body.label ?? ""));
    console.log(`[guests] ${viewer.name}: yangi qo'shimcha token "${guest.label}"`);
    return { guests: await listGuests(viewer.accountId), token, message: `"${guest.label}" uchun token yaratildi.` };
  }
  if (body.action === "delete") {
    const ok = await deleteGuest(viewer.accountId, String(body.id ?? ""));
    if (!ok) throw new Error("Token topilmadi.");
    console.log(`[guests] ${viewer.name}: qo'shimcha token o'chirildi`);
    return { guests: await listGuests(viewer.accountId), message: "Token o'chirildi: u bilan endi kirib bo'lmaydi." };
  }
  throw new Error("Noma'lum amal.");
}, { ownerOnly: true });

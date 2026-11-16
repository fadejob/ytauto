import { publicHandler } from "@/lib/api";
import { clientIp, registerDevice, type DeviceRequest } from "@/lib/accounts";

/** Desktop ilova sozlashi: yangi hisob va shu kompyuter uchun baza roli (token o'zi yuborilmaydi - faqat xeshi). */
export const POST = publicHandler(async (req) => registerDevice((await req.json().catch(() => ({}))) as DeviceRequest, clientIp(req)));

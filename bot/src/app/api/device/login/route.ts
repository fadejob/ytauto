import { publicHandler } from "@/lib/api";
import { clientIp, loginDevice, type DeviceRequest } from "@/lib/accounts";

/** Mavjud token bilan kompyuterni hisobga ulash (qayta o'rnatish yoki ikkinchi kompyuter). */
export const POST = publicHandler(async (req) => {
  const r = await loginDevice((await req.json().catch(() => ({}))) as DeviceRequest, clientIp(req));
  if (!r) throw new Error("Bunday token topilmadi. Botdagi emas, ilovadagi joriy tokenni kiriting.");
  return r;
});

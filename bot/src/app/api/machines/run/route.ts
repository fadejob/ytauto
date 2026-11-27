import { handler } from "@/lib/api";
import { getMachines, requestRun } from "@/lib/machines";

export const POST = handler(async (req, viewer) => {
  if (viewer.guest) throw new Error("Qo'shimcha token kompyuterni boshqarmaydi: videolar ish vaqtida tayyorlanadi.");
  const { machine } = (await req.json()) as { machine?: string };
  if (!machine) throw new Error("Kompyuter nomi kerak.");
  // Javobda yangilangan ro'yxat ham - ilova alohida GET /api/machines kutmaydi.
  const r = await requestRun(machine);
  return { ...r, machines: await getMachines() };
});

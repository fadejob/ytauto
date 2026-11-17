import { handler } from "@/lib/api";
import { requestRun } from "@/lib/machines";

export const POST = handler(async (req, viewer) => {
  if (viewer.guest) throw new Error("Qo'shimcha token kompyuterni boshqarmaydi: videolar ish vaqtida tayyorlanadi.");
  const { machine } = (await req.json()) as { machine?: string };
  if (!machine) throw new Error("Kompyuter nomi kerak.");
  return requestRun(machine);
});

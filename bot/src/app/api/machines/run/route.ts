import { handler } from "@/lib/api";
import { requestRun } from "@/lib/machines";

export const POST = handler(async (req) => {
  const { machine } = (await req.json()) as { machine?: string };
  if (!machine) throw new Error("Kompyuter nomi kerak.");
  return requestRun(machine);
});

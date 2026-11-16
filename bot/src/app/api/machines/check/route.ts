import { handler } from "@/lib/api";
import { requestCheck } from "@/lib/machines";

export const POST = handler(async (req) => {
  const { machine } = (await req.json()) as { machine?: string };
  if (!machine) throw new Error("Kompyuter nomi kerak.");
  return requestCheck(machine);
});

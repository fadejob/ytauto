import { handler } from "@/lib/api";
import { getMachines } from "@/lib/machines";

export const GET = handler(async () => getMachines());

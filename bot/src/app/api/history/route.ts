import { handler } from "@/lib/api";
import { getRuns } from "@/lib/history";

export const GET = handler(async () => getRuns());

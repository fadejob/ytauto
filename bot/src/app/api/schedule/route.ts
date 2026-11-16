import { handler } from "@/lib/api";
import { getSchedule, saveSchedule } from "@/lib/schedule";

export const GET = handler(async () => getSchedule());
export const POST = handler(async (req) => saveSchedule(await req.json()));

import { handler } from "@/lib/api";
import { getConfig, saveConfig } from "@/lib/config";

export const GET = handler(async () => getConfig());
export const POST = handler(async (req) => saveConfig(await req.json()));

import { handler } from "@/lib/api";
import { getPrompts } from "@/lib/topics";

export const GET = handler(async () => getPrompts());

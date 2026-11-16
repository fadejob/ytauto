import { handler } from "@/lib/api";
import { chat } from "@/lib/ai";
import { GEMINI_REQUIRED, geminiReady } from "@/lib/config";
import type { ChatMessage } from "@/lib/types";

export const POST = handler(async (req) => {
  const body = (await req.json()) as { messages?: ChatMessage[] };
  const messages = (body.messages ?? []).filter((m) => (m.role === "user" || m.role === "assistant") && typeof m.content === "string");
  if (!messages.length) throw new Error("Xabar bo'sh.");
  if (!(await geminiReady())) throw new Error(GEMINI_REQUIRED);
  return chat(messages);
});

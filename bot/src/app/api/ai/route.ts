import { accountHandler } from "@/lib/api";
import { chat, chatPrep } from "@/lib/ai";
import { GEMINI_REQUIRED, geminiReady } from "@/lib/config";
import { withAccount } from "@/lib/db";
import { isDemo } from "@/lib/demo";
import type { ChatMessage } from "@/lib/types";

/**
 * AI yordamchi: bazadan kerakli narsalar qisqa tranzaksiyada o'qiladi, Gemini chaqiruvlari esa tranzaksiyadan tashqarida -
 * uzoq suhbat baza ulanishini band qilmaydi (boshqa so'rovlar kutib qolmaydi).
 */
export const POST = accountHandler(async (req, viewer) => {
  const body = (await req.json()) as { messages?: ChatMessage[] };
  const messages = (body.messages ?? []).filter((m) => (m.role === "user" || m.role === "assistant") && typeof m.content === "string");
  if (!messages.length) throw new Error("Xabar bo'sh.");
  const run = <T,>(fn: () => Promise<T>): Promise<T> => (isDemo() ? fn() : withAccount(viewer.accountId, fn, { guest: viewer.guest }));
  const prep = await run(async () => {
    if (!(await geminiReady())) throw new Error(GEMINI_REQUIRED);
    return chatPrep();
  });
  return chat(messages, prep, run);
});

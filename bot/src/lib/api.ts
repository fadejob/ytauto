import "server-only";
import { connection } from "next/server";
import { AuthError, authenticate, type Viewer } from "./auth";

/** Har API so'rovi: Telegram auth → handler. Xatolar foydalanuvchiga tushunarli JSON bo'lib qaytadi. */
export function handler(fn: (req: Request, viewer: Viewer) => Promise<unknown>) {
  return async (req: Request) => {
    await connection(); // har doim so'rov vaqtida (prerender qilinmaydi)
    try {
      const viewer = authenticate(req);
      return Response.json(await fn(req, viewer), { headers: { "Cache-Control": "no-store" } });
    } catch (e) {
      if (e instanceof AuthError) return Response.json({ error: e.message, telegramId: e.telegramId }, { status: e.status });
      console.error(e);
      const msg = e instanceof Error ? e.message : String(e);
      return Response.json({ error: msg.split("\n")[0] }, { status: 500 });
    }
  };
}

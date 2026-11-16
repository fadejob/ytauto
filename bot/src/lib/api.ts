import "server-only";
import { connection } from "next/server";
import { accountForTelegram, LimitError } from "./accounts";
import { AuthError, authenticate, type Viewer } from "./auth";
import { withAccount } from "./db";
import { isDemo } from "./demo";

export type AccountViewer = Viewer & { accountId: string };

/**
 * Har API so'rovi: Telegram auth → token bilan bog'langan hisob → shu hisob kontekstida (RLS, bitta tranzaksiya) handler.
 * Hisob topilmasa 401 "token_required": ilova token kiritish oynasini ko'rsatadi.
 */
export function handler(fn: (req: Request, viewer: AccountViewer) => Promise<unknown>) {
  return async (req: Request) => {
    await connection(); // har doim so'rov vaqtida (prerender qilinmaydi)
    try {
      const viewer = authenticate(req);
      if (isDemo()) return json(await fn(req, { ...viewer, accountId: "00000000-0000-0000-0000-000000000000" }));
      const accountId = await accountForTelegram(viewer.id);
      if (!accountId) throw new AuthError("YtAuto ilovasidagi tokenni kiriting.", 401, viewer.id, "token_required");
      return json(await withAccount(accountId, () => fn(req, { ...viewer, accountId })));
    } catch (e) {
      return errorResponse(e);
    }
  };
}

/** Hisobsiz (lekin Telegram bilan tasdiqlangan) so'rov: token bilan kirish/chiqish. */
export function viewerHandler(fn: (req: Request, viewer: Viewer) => Promise<unknown>) {
  return async (req: Request) => {
    await connection();
    try { return json(await fn(req, authenticate(req))); }
    catch (e) { return errorResponse(e); }
  };
}

/** Telegram'siz ochiq so'rov (desktop ilova ro'yxatdan o'tishi). */
export function publicHandler(fn: (req: Request) => Promise<unknown>) {
  return async (req: Request) => {
    await connection();
    try { return json(await fn(req)); }
    catch (e) { return errorResponse(e); }
  };
}

const json = (v: unknown, status = 200) => Response.json(v, { status, headers: { "Cache-Control": "no-store" } });

function errorResponse(e: unknown) {
  if (e instanceof AuthError) return json({ error: e.message, telegramId: e.telegramId, code: e.code }, e.status);
  if (e instanceof LimitError) return json({ error: e.message }, 429);
  console.error(e);
  const msg = e instanceof Error ? e.message : String(e);
  return json({ error: msg.split("\n")[0] }, 500);
}

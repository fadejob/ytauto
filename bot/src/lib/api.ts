import "server-only";
import { connection } from "next/server";
import { accountForTelegram, LimitError } from "./accounts";
import { AuthError, authenticate, type Viewer } from "./auth";
import { withAccount } from "./db";
import { isDemo } from "./demo";

/** guest - qo'shimcha token hisobi (hostLabel - token bergan asosiy hisob kompyuteri nomi). */
export type AccountViewer = Viewer & { accountId: string; guest: boolean; hostLabel: string | null };

/**
 * Har API so'rovi: Telegram auth → token bilan bog'langan hisob → shu hisob kontekstida (RLS, bitta tranzaksiya) handler.
 * Hisob topilmasa 401 "token_required": ilova token kiritish oynasini ko'rsatadi.
 */
export function handler(fn: (req: Request, viewer: AccountViewer) => Promise<unknown>, opts?: { ownerOnly?: boolean }) {
  return async (req: Request) => {
    await connection(); // har doim so'rov vaqtida (prerender qilinmaydi)
    try {
      const viewer = authenticate(req);
      if (isDemo()) return json(await fn(req, { ...viewer, accountId: "00000000-0000-0000-0000-000000000000", guest: false, hostLabel: null }));
      const acc = await accountForTelegram(viewer.id);
      if (!acc) throw new AuthError("YtAuto ilovasidagi tokenni kiriting.", 401, viewer.id, "token_required");
      // ownerOnly: handler faqat ownerSql() (accounts.ts) bilan ishlaydi - hisob tranzaksiyasi ochilmaydi (aks holda har so'rov pool'dan
      // 2 ta ulanish ushlaydi va parallel so'rovlarda pool (max 3) to'lib qotib qoladi).
      if (opts?.ownerOnly) return json(await fn(req, { ...viewer, accountId: acc.id, guest: acc.guest, hostLabel: acc.hostLabel }));
      return json(await withAccount(acc.id, () => fn(req, { ...viewer, accountId: acc.id, guest: acc.guest, hostLabel: acc.hostLabel }), { guest: acc.guest }));
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

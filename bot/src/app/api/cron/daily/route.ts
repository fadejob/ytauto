import { timingSafeEqual } from "node:crypto";
import { connection } from "next/server";
import { activeAccounts } from "@/lib/accounts";
import { analyzeChannel } from "@/lib/channel/analyze";
import { generateSuggestions } from "@/lib/channel/suggest";

/**
 * Kunlik fon ishi (Vercel Cron, bot/vercel.json): har hisob uchun kanalda yangi video bo'lsa kontekst yangilanadi
 * va bugungi mavzu tavsiyalari tayyorlanadi. Faqat `Authorization: Bearer CRON_SECRET` bilan.
 * Vaqt yetmasa qolgan hisoblar bot ochilganda (/api/state, /api/channel) yangilanadi.
 */
export const maxDuration = 300;
const BUDGET_MS = 240_000;

function authorized(req: Request): boolean {
  const secret = process.env.CRON_SECRET ?? "";
  if (secret.length < 16) return false;
  const got = Buffer.from(req.headers.get("authorization") ?? ""), want = Buffer.from(`Bearer ${secret}`);
  return got.length === want.length && timingSafeEqual(got, want);
}

export async function GET(req: Request) {
  await connection();
  if (!authorized(req)) return Response.json({ error: "unauthorized" }, { status: 401 });
  const t0 = Date.now();
  const out: { account: string; channel: string; suggestions: boolean | string }[] = [];
  for (const acc of await activeAccounts()) {
    if (Date.now() - t0 > BUDGET_MS) break;
    const row = { account: acc.id.slice(0, 8), channel: "", suggestions: false as boolean | string };
    try {
      const r = await analyzeChannel(acc.id, { guest: acc.guest });
      row.channel = r.status;
      if (r.status !== "not_connected" && r.status !== "failed") row.suggestions = await generateSuggestions(acc.id, { guest: acc.guest });
    } catch (e) { row.suggestions = e instanceof Error ? e.message.slice(0, 200) : String(e); }
    out.push(row);
  }
  return Response.json({ ok: true, ms: Date.now() - t0, accounts: out }, { headers: { "Cache-Control": "no-store" } });
}

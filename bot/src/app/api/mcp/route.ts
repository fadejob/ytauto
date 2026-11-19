import { connection } from "next/server";
import { accountForToken, clientIp, LimitError } from "@/lib/accounts";
import { withAccount } from "@/lib/db";
import { isDemo } from "@/lib/demo";
import { handleRpc } from "@/lib/mcp";

/**
 * MCP endpoint (Streamable HTTP, holatsiz). Autentifikatsiya: "Authorization: Bearer <YtAuto token>" - botga kirishdagi token
 * (asosiy yoki qo'shimcha). Har tool chaqiruvi shu token hisobi kontekstida (RLS): boshqa foydalanuvchi ma'lumotiga yo'l yo'q.
 */
export const maxDuration = 60;

const json = (v: unknown, status = 200, extra: Record<string, string> = {}) =>
  Response.json(v, { status, headers: { "Cache-Control": "no-store", ...extra } });

export async function POST(req: Request) {
  await connection();
  if (isDemo()) return json({ jsonrpc: "2.0", id: null, error: { code: -32000, message: "MCP DEMO_MODE da ishlamaydi." } }, 503);
  const auth = req.headers.get("authorization") ?? "";
  const token = auth.match(/^Bearer\s+(\S+)$/i)?.[1] ?? "";
  let acc;
  try { acc = token ? await accountForToken(token, clientIp(req), { mcp: true }) : null; }
  catch (e) {
    if (e instanceof LimitError) return json({ jsonrpc: "2.0", id: null, error: { code: -32001, message: e.message } }, 429);
    console.error("[mcp] auth", e);
    return json({ jsonrpc: "2.0", id: null, error: { code: -32603, message: "Server xatosi (baza)." } }, 500);
  }
  if (!acc) {
    return json({ jsonrpc: "2.0", id: null, error: { code: -32001, message: "YtAuto MCP tokeni kerak: Authorization: Bearer ytm_... (botda Sozlamalar → Claude Code ulash) yoki hisob tokeni yt_..." } },
      401, { "WWW-Authenticate": 'Bearer realm="ytauto"' });
  }

  let body: unknown;
  try { body = await req.json(); }
  catch { return json({ jsonrpc: "2.0", id: null, error: { code: -32700, message: "Parse error" } }, 400); }

  const run = <T,>(fn: () => Promise<T>) => withAccount(acc.id, fn, { guest: acc.guest });
  const messages = Array.isArray(body) ? body : [body];
  const out = [];
  for (const m of messages) {
    const r = await handleRpc(m as never, run);
    if (r) out.push(r);
  }
  if (out.length === 0) return new Response(null, { status: 202 });
  return json(Array.isArray(body) ? out : out[0]);
}

/** Holatsiz server: server→mijoz SSE oqimi va sessiyalar yo'q. */
export async function GET() {
  return new Response("Method Not Allowed", { status: 405, headers: { Allow: "POST" } });
}

export async function DELETE() {
  return new Response("Method Not Allowed", { status: 405, headers: { Allow: "POST" } });
}

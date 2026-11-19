import { handler } from "@/lib/api";
import { createMcpToken, mcpTokenInfo, revokeMcpToken, type McpTokenInfo } from "@/lib/accounts";
import { isDemo } from "@/lib/demo";
import { setupText } from "@/lib/mcp";
import { originOf } from "@/lib/youtube";

/**
 * Claude Code ulash: alohida MCP tokeni (faqat /api/mcp ga kiradi) va Claude'ga beriladigan tayyor matn - server manzili, token,
 * ulash buyrug'i va ko'rsatma. Token o'zi faqat yaratilgan javobda qaytadi (bazada xeshi). Asosiy va qo'shimcha hisoblar uchun.
 */
type Res = McpTokenInfo & { url: string; token?: string; setup?: string; command?: string; message?: string };

export const GET = handler(async (req, viewer): Promise<Res> => {
  const url = `${originOf(req)}/api/mcp`;
  if (isDemo()) return { exists: false, createdAt: null, lastUsedAt: null, url };
  return { ...(await mcpTokenInfo(viewer.accountId)), url };
});

export const POST = handler(async (req, viewer): Promise<Res> => {
  if (isDemo()) throw new Error("Demo rejimda token yaratilmaydi.");
  const url = `${originOf(req)}/api/mcp`;
  const body = (await req.json().catch(() => ({}))) as { action?: string };
  if (body.action === "create") {
    const token = await createMcpToken(viewer.accountId);
    console.log(`[mcp-token] ${viewer.name}: yangi Claude Code tokeni`);
    return { ...(await mcpTokenInfo(viewer.accountId)), url, token, ...setupText(url, token), message: "Claude Code tokeni yaratildi — matnni nusxalab Claude'ga bering." };
  }
  if (body.action === "revoke") {
    await revokeMcpToken(viewer.accountId);
    console.log(`[mcp-token] ${viewer.name}: Claude Code tokeni bekor qilindi`);
    return { ...(await mcpTokenInfo(viewer.accountId)), url, message: "Token bekor qilindi: Claude Code endi u bilan ulana olmaydi." };
  }
  throw new Error("Noma'lum amal.");
});

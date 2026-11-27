import { handler } from "@/lib/api";
import { createMcpToken, mcpTokenInfo, revokeMcpToken, type McpTokenInfo } from "@/lib/accounts";
import { withAccount } from "@/lib/db";
import { isDemo } from "@/lib/demo";
import { channelServerNames, setupCommand } from "@/lib/mcp";
import { channelList, originOf } from "@/lib/youtube";

/**
 * Claude Code ulash: alohida MCP tokeni (faqat /api/mcp ga kiradi) va Claude'ga beriladigan tayyor buyruqlar - HAR BIR YouTube
 * kanali uchun alohida MCP server (ytauto-<kanal>, /api/mcp?channel=<id>): shu ulanishdagi tool'lar faqat o'sha kanal bilan
 * ishlaydi. Kanal yo'q bo'lsa - bitta umumiy "ytauto" (faol kanal). Token o'zi faqat yaratilgan javobda qaytadi (bazada xeshi).
 */
type ChannelMcp = { id: string; title: string; name: string; url: string; command?: string };
type Res = McpTokenInfo & { url: string; channels: ChannelMcp[]; token?: string; command?: string; message?: string };

/** Hisob kanallari va ularning MCP nomlari (hisob konteksti alohida qisqa tranzaksiyada - ownerOnly handler). */
async function channelMcps(accountId: string, guest: boolean, url: string): Promise<ChannelMcp[]> {
  const l = isDemo() ? await channelList() : await withAccount(accountId, () => channelList(), { guest });
  const names = channelServerNames(l.channels);
  return l.channels.map((c) => ({ id: c.id, title: c.title, name: names.get(c.id)!, url: `${url}?channel=${encodeURIComponent(c.id)}` }));
}

export const GET = handler(async (req, viewer): Promise<Res> => {
  const url = `${originOf(req)}/api/mcp`;
  if (isDemo()) return { exists: false, createdAt: null, lastUsedAt: null, url, channels: await channelMcps(viewer.accountId, viewer.guest, url) };
  // Ikkalasi mustaqil - birga.
  const [channels, info] = await Promise.all([channelMcps(viewer.accountId, viewer.guest, url), mcpTokenInfo(viewer.accountId)]);
  return { ...info, url, channels };
}, { ownerOnly: true });

export const POST = handler(async (req, viewer): Promise<Res> => {
  if (isDemo()) throw new Error("Demo rejimda token yaratilmaydi.");
  const url = `${originOf(req)}/api/mcp`;
  const body = (await req.json().catch(() => ({}))) as { action?: string };
  if (body.action === "create") {
    const token = await createMcpToken(viewer.accountId);
    console.log(`[mcp-token] ${viewer.name}: yangi Claude Code tokeni`);
    const channels = (await channelMcps(viewer.accountId, viewer.guest, url))
      .map((c) => ({ ...c, command: setupCommand(url, token, { id: c.id, name: c.name }) }));
    // Kanal ulanmagan bo'lsa - bitta umumiy server (faol kanal); kanallar bo'lsa - har biriga alohida buyruq.
    const command = channels.length ? channels.map((c) => c.command).join("\n") : setupCommand(url, token);
    return {
      ...(await mcpTokenInfo(viewer.accountId)), url, channels, token, command,
      message: channels.length > 1 ? `Claude Code tokeni yaratildi — ${channels.length} ta kanal uchun alohida buyruq.` : "Claude Code tokeni yaratildi — buyruqni nusxalang.",
    };
  }
  if (body.action === "revoke") {
    await revokeMcpToken(viewer.accountId);
    console.log(`[mcp-token] ${viewer.name}: Claude Code tokeni bekor qilindi`);
    return { ...(await mcpTokenInfo(viewer.accountId)), url, channels: await channelMcps(viewer.accountId, viewer.guest, url), message: "Token bekor qilindi: Claude Code endi u bilan ulana olmaydi." };
  }
  throw new Error("Noma'lum amal.");
}, { ownerOnly: true });

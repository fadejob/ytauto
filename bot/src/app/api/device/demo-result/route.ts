import { publicHandler } from "@/lib/api";
import { accountForToken, clientIp, isHostedGuest } from "@/lib/accounts";
import { AuthError } from "@/lib/auth";
import { withAccount } from "@/lib/db";
import { McpToolError, signResultUploads } from "@/lib/demo-video";

/**
 * Kompyuter (worker) tayyor demo videoni Storage'ga yuklashi uchun bir martalik havolalar. Auth - kompyuterdagi hisob tokeni
 * (Authorization: Bearer). Mehmon vazifasi bo'lsa (guest) - faqat shu hisob mezbon bo'lgan mehmon; fayl o'sha hisob papkasiga yoziladi.
 */
export const POST = publicHandler(async (req) => {
  const token = (req.headers.get("authorization") ?? "").match(/^Bearer\s+(\S+)$/i)?.[1] ?? "";
  const acc = token ? await accountForToken(token, clientIp(req)) : null;
  if (!acc) throw new AuthError("Token noto'g'ri.");
  if (acc.guest) throw new AuthError("Qo'shimcha token bilan kompyuter natija yubora olmaydi.", 403);
  const b = (await req.json().catch(() => ({}))) as { jobId?: unknown; guest?: unknown; files?: { name: string; size: number }[] };
  let target = acc.id;
  if (typeof b.guest === "string" && b.guest) {
    if (!(await isHostedGuest(acc.id, b.guest))) throw new AuthError("Bu mehmon hisobi shu kompyuterga tegishli emas.", 403);
    target = b.guest;
  }
  try { return await withAccount(target, () => signResultUploads(Number(b.jobId), Array.isArray(b.files) ? b.files : [])); }
  catch (e) { if (e instanceof McpToolError) throw new AuthError(e.message, 400); throw e; } // tekshiruv xatosi - qayta urinish foyda bermaydi
});

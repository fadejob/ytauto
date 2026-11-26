import { publicHandler } from "@/lib/api";
import { accountForToken, clientIp, isHostedGuest } from "@/lib/accounts";
import { AuthError } from "@/lib/auth";
import { withAccount } from "@/lib/db";
import { youtubeAccessToken, youtubeCredentialsFor } from "@/lib/youtube";

/**
 * Kompyuter (worker) bot orqali ulangan kanal uchun qisqa muddatli YouTube access token oladi. Refresh token va (umumiy) OAuth
 * client secret serverda qoladi - kompyuterga faqat ~1 soatlik access token beriladi. Auth - kompyuterdagi hisob tokeni.
 * Mehmon vazifasi bo'lsa (guest) - faqat shu hisob mezbon bo'lgan mehmon, va AYNAN o'sha mehmon kanalining tokeni.
 * channelId - vazifa bog'langan kanal (bo'sh - faol kanal); faqat shu hisob ro'yxatidagi kanal.
 */
export const POST = publicHandler(async (req) => {
  const token = (req.headers.get("authorization") ?? "").match(/^Bearer\s+(\S+)$/i)?.[1] ?? "";
  const acc = token ? await accountForToken(token, clientIp(req)) : null;
  if (!acc) throw new AuthError("Token noto'g'ri.");
  if (acc.guest) throw new AuthError("Qo'shimcha token bilan kompyuter YouTube tokeni ololmaydi.", 403);
  const b = (await req.json().catch(() => ({}))) as { guest?: unknown; channelId?: unknown };
  let target = acc.id;
  if (typeof b.guest === "string" && b.guest) {
    if (!(await isHostedGuest(acc.id, b.guest))) throw new AuthError("Bu mehmon hisobi shu kompyuterga tegishli emas.", 403);
    target = b.guest;
  }
  const channelId = typeof b.channelId === "string" ? b.channelId.trim().slice(0, 64) : "";
  const creds = await withAccount(target, () => youtubeCredentialsFor(channelId));
  if (!creds) throw new AuthError(channelId
    ? "Bu video bog'langan YouTube kanali botda uzilgan: shu kanalni botda qayta ulang."
    : "YouTube kanali ulanmagan: botda Sozlamalar → YouTube'ni ulang.", 409);
  try {
    const accessToken = await youtubeAccessToken(creds);
    return { accessToken, expiresIn: 3000, channelTitle: creds.channelTitle };
  } catch (e) {
    // invalid_grant va h.k.: qayta ulash kerak - qayta urinish foyda bermaydi.
    throw new AuthError(e instanceof Error ? e.message : String(e), 409);
  }
});

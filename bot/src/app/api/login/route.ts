import { viewerHandler } from "@/lib/api";
import { loginTelegram, logoutTelegram } from "@/lib/accounts";

/** Botga birinchi kirish: YtAuto ilovasidagi token bilan Telegram foydalanuvchisi hisobga bog'lanadi. */
export const POST = viewerHandler(async (req, viewer) => {
  const b = (await req.json().catch(() => ({}))) as { token?: string; logout?: boolean };
  if (b.logout) { await logoutTelegram(viewer.id); return { ok: true, message: "Chiqdingiz." }; }
  return loginTelegram(viewer.id, viewer.name, String(b.token ?? ""));
});

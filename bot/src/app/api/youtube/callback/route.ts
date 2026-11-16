import { connection } from "next/server";
import { checkState, finishYouTube, originOf } from "@/lib/youtube";

/** Google shu manzilga qaytaradi (brauzerda). Telegram auth yo'q: so'rov imzolangan `state` bilan tekshiriladi. */
const page = (title: string, text: string, ok: boolean) =>
  new Response(
    `<!doctype html><html lang="uz"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>YtAuto</title>` +
      `<body style="font-family:system-ui,sans-serif;max-width:480px;margin:60px auto;padding:0 20px;text-align:center">` +
      `<h2 style="color:${ok ? "#15803d" : "#b91c1c"}">${title}</h2><p>${text}</p></body></html>`,
    { status: ok ? 200 : 400, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } },
  );
const esc = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

export async function GET(req: Request) {
  await connection();
  const q = new URL(req.url).searchParams;
  if (!checkState(q.get("state"))) return page("Havola eskirgan yoki noto'g'ri", "Telegramdagi botga qaytib, «YouTube'ni ulash» ni qayta bosing.", false);
  if (q.get("error")) return page("Ruxsat berilmadi", `Google: ${esc(q.get("error")!)}. Telegramga qaytib, qayta urinib ko'ring.`, false);
  const code = q.get("code");
  if (!code) return page("Kod kelmadi", "Telegramga qaytib, qayta urinib ko'ring.", false);
  try {
    const title = await finishYouTube(code, originOf(req));
    return page("YouTube ulandi ✓", `Kanal: <b>${esc(title)}</b>. Endi Telegramdagi botga qaytishingiz mumkin.`, true);
  } catch (e) {
    return page("Ulanmadi", esc(e instanceof Error ? e.message : String(e)), false);
  }
}

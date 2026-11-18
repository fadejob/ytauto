import { after, connection } from "next/server";
import { analyzeChannel } from "@/lib/channel/analyze";
import { generateSuggestions } from "@/lib/channel/suggest";
import { withAccount } from "@/lib/db";
import { checkState, finishYouTube, originOf } from "@/lib/youtube";

export const maxDuration = 300;

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
  const accountId = checkState(q.get("state"));
  if (!accountId) return page("Havola eskirgan yoki noto'g'ri", "Telegramdagi botga qaytib, «YouTube'ni ulash» ni qayta bosing.", false);
  if (q.get("error")) return page("Ruxsat berilmadi", `Google: ${esc(q.get("error")!)}. Telegramga qaytib, qayta urinib ko'ring.`, false);
  const code = q.get("code");
  if (!code) return page("Kod kelmadi", "Telegramga qaytib, qayta urinib ko'ring.", false);
  try {
    const title = await withAccount(accountId, () => finishYouTube(code, originOf(req)));
    // Kanal ulangach darhol o'rganiladi (oxirgi 3 video) va bugungi mavzu tavsiyalari tayyorlanadi.
    after(async () => {
      try {
        const r = await analyzeChannel(accountId, { force: true });
        if (r.status === "analyzed") await generateSuggestions(accountId);
      } catch (e) { console.error("[channel] ulangandan keyingi tahlil:", e instanceof Error ? e.message : e); }
    });
    return page("YouTube ulandi ✓", `Kanal: <b>${esc(title)}</b>. Kanal hozir o'rganilmoqda (1–3 daqiqa) — natija botdagi «Kanal» bo'limida. Endi Telegramdagi botga qaytishingiz mumkin.`, true);
  } catch (e) {
    return page("Ulanmadi", esc(e instanceof Error ? e.message : String(e)), false);
  }
}

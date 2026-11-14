import "server-only";
import { createHmac, timingSafeEqual } from "node:crypto";

export type Viewer = { id: number; name: string; dev: boolean };

export class AuthError extends Error {
  constructor(message: string, public status = 401, public telegramId?: number) {
    super(message);
  }
}

/**
 * Telegram Web App initData tekshiruvi (https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app):
 * secret = HMAC_SHA256("WebAppData", bot_token); hash = HMAC_SHA256(secret, data_check_string).
 * Faqat ALLOWED_TELEGRAM_IDS dagi foydalanuvchilar kiradi. ALLOW_BROWSER_DEV=1 bo'lsa initData'siz (brauzerda) kirish mumkin.
 */
export function authenticate(req: Request): Viewer {
  const initData = req.headers.get("x-tg-init") ?? "";
  if (!initData) {
    if (process.env.ALLOW_BROWSER_DEV === "1") return { id: 0, name: "Brauzer (dev)", dev: true };
    throw new AuthError("Ilovani Telegram bot ichidan oching.");
  }
  const token = process.env.TELEGRAM_BOT_TOKEN ?? "";
  if (!token) throw new AuthError("TELEGRAM_BOT_TOKEN sozlanmagan.", 500);

  const params = new URLSearchParams(initData);
  const hash = params.get("hash") ?? "";
  params.delete("hash");
  const dataCheck = [...params.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => `${k}=${v}`).join("\n");
  const secret = createHmac("sha256", "WebAppData").update(token).digest();
  const expected = createHmac("sha256", secret).update(dataCheck).digest();
  const given = Buffer.from(hash, "hex");
  if (given.length !== expected.length || !timingSafeEqual(given, expected)) throw new AuthError("Telegram imzosi noto'g'ri.");

  const authDate = Number(params.get("auth_date") ?? 0);
  if (!authDate || Date.now() / 1000 - authDate > 7 * 24 * 3600) throw new AuthError("Sessiya eskirgan — ilovani qayta oching.");

  const user = JSON.parse(params.get("user") ?? "{}") as { id?: number; first_name?: string; username?: string };
  if (!user.id) throw new AuthError("Foydalanuvchi aniqlanmadi.");
  const allowed = (process.env.ALLOWED_TELEGRAM_IDS ?? "").split(/[,\s]+/).filter(Boolean).map(Number);
  if (!allowed.includes(user.id))
    throw new AuthError(`Sizga ruxsat yo'q. Telegram ID: ${user.id} — uni ALLOWED_TELEGRAM_IDS ga qo'shing.`, 403, user.id);
  return { id: user.id, name: user.first_name ?? user.username ?? String(user.id), dev: false };
}

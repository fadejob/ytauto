import "server-only";
import { createHash, randomInt } from "node:crypto";
import { ensureSchema, ownerConnectionInfo, ownerSql } from "./db";

/**
 * Hisoblar (ko'p foydalanuvchili rejim). Bu fayl jadval egasi ulanishi bilan ishlaydigan YAGONA joy: hisob, token xeshi,
 * qurilma rollari va Telegram bog'lanishlari. Ma'lumotlarga (mavzular, sozlamalar...) bu yerdan tegilmaydi.
 *
 * Oqim:
 *  1. Desktop ilova sozlashda token va baza parolini o'zi yaratadi, /api/device/register ga token XESHI va parolni yuboradi →
 *     hisob + shu kompyuter uchun Postgres roli yaratiladi, ilovaga ulanish ma'lumoti qaytariladi.
 *  2. Foydalanuvchi tokenni ilovadan nusxalab, botga birinchi kirishda kiritadi → Telegram ID shu hisobga bog'lanadi.
 *  3. Keyingi har so'rovda bog'lanish tekshiriladi: hisob tokeni almashtirilgan bo'lsa bog'lanish bekor (token qayta so'raladi).
 *
 * Qo'shimcha (mehmon) tokenlar: asosiy hisob egasi botda token yaratadi → alohida hisob (host_account_id = asosiy hisob).
 * Mehmon botga shu token bilan kiradi va asosiy hisob kabi ishlaydi (o'z mavzulari, sozlamalari, YouTube kanali), faqat
 * «Hozir chiqarish» yo'q: videolari asosiy hisob kompyuterida, ish vaqtida, deadline navbati bilan tayyorlanadi.
 * Ma'lumotlar RLS bilan ajratilgan: asosiy hisob mehmon videolarini ko'rmaydi, mehmon - asosiynikini.
 */

export const hashToken = (token: string) => createHash("sha256").update(token.trim()).digest("hex");
export const TOKEN_RE = /^yt_[0-9A-Za-z]{30,60}$/;
const HASH_RE = /^[0-9a-f]{64}$/;

export type LinkedAccount = { id: string; guest: boolean; hostLabel: string | null };

/** Telegram foydalanuvchisi bog'langan hisob (token hali amal qilsa) va u qo'shimcha (mehmon) tokenmi. */
export async function accountForTelegram(telegramId: number): Promise<LinkedAccount | null> {
  await ensureSchema();
  const [row] = await ownerSql()`
    SELECT l.account_id::text AS id, a.host_account_id IS NOT NULL AS guest, h.device_name AS host_label FROM ytauto.telegram_links l
    JOIN ytauto.accounts a ON a.id = l.account_id AND a.token_hash = l.token_hash
    LEFT JOIN ytauto.accounts h ON h.id = a.host_account_id
    WHERE l.telegram_id = ${telegramId}`;
  if (!row) return null;
  return { id: row.id as string, guest: row.guest === true, hostLabel: (row.host_label as string | null) ?? null };
}

// ------------------------------------------------------------------ qo'shimcha (mehmon) tokenlar
export const GUEST_LIMIT = 20;
const ALNUM = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

/** Yangi token: "yt_" + 40 ta tasodifiy harf/raqam (desktop ilova tokenlari bilan bir xil ko'rinish, TOKEN_RE). */
function newToken(): string {
  let t = "yt_";
  for (let i = 0; i < 40; i++) t += ALNUM[randomInt(ALNUM.length)];
  return t;
}

export type GuestToken = { id: string; label: string; createdAt: string; telegramUsers: number };

async function assertHost(hostId: string) {
  const [h] = await ownerSql()`SELECT host_account_id IS NOT NULL AS guest FROM ytauto.accounts WHERE id = ${hostId}`;
  if (!h) throw new Error("Hisob topilmadi.");
  if (h.guest === true) throw new Error("Qo'shimcha token bilan yangi token yaratib bo'lmaydi — buni faqat asosiy hisob egasi qiladi.");
}

/** Asosiy hisob yaratgan qo'shimcha tokenlar (faqat nomi va kim kirgani; mehmon videolari ko'rsatilmaydi). */
export async function listGuests(hostId: string): Promise<GuestToken[]> {
  await assertHost(hostId);
  const rows = await ownerSql()`
    SELECT a.id::text AS id, a.device_name AS label, a.created_at_utc AS created,
      (SELECT count(*)::int FROM ytauto.telegram_links l WHERE l.account_id = a.id AND l.token_hash = a.token_hash) AS users
    FROM ytauto.accounts a WHERE a.host_account_id = ${hostId} ORDER BY a.created_at_utc`;
  return rows.map((r) => ({ id: r.id as string, label: r.label as string, createdAt: (r.created as Date).toISOString(), telegramUsers: r.users as number }));
}

/** Yangi qo'shimcha token. Token o'zi faqat shu javobda qaytadi (bazada - xeshi). */
export async function createGuest(hostId: string, label: string): Promise<{ token: string; guest: GuestToken }> {
  await assertHost(hostId);
  const name = label.trim().slice(0, 60);
  if (!name) throw new Error("Token uchun nom kiriting (masalan, kim uchun).");
  const [c] = await ownerSql()`SELECT count(*)::int AS n FROM ytauto.accounts WHERE host_account_id = ${hostId}`;
  if ((c?.n as number) >= GUEST_LIMIT) throw new Error(`Qo'shimcha tokenlar ${GUEST_LIMIT} tadan oshmasin.`);
  const token = newToken();
  const [r] = await ownerSql()`
    INSERT INTO ytauto.accounts(token_hash, device_name, host_account_id) VALUES (${hashToken(token)}, ${name}, ${hostId})
    RETURNING id::text AS id, created_at_utc AS created`;
  return { token, guest: { id: r.id as string, label: name, createdAt: (r.created as Date).toISOString(), telegramUsers: 0 } };
}

/** Qo'shimcha tokenni o'chiradi: shu token hisobi va uning barcha mavzulari, sozlamalari o'chadi (bekor qilib bo'lmaydi). */
export async function deleteGuest(hostId: string, guestId: string): Promise<boolean> {
  await assertHost(hostId);
  if (!/^[0-9a-f-]{36}$/i.test(guestId)) return false;
  const rows = await ownerSql()`DELETE FROM ytauto.accounts WHERE id = ${guestId} AND host_account_id = ${hostId} RETURNING id`;
  return rows.length > 0;
}

/** Oddiy cheklov: kalit bo'yicha oynada ko'pi bilan max urinish (token terish/ro'yxatdan o'tishni suiiste'mol qilmaslik uchun). */
async function rateLimit(key: string, max: number, windowMinutes: number) {
  const [r] = await ownerSql()`
    INSERT INTO ytauto.login_attempts(key, attempts, window_start_utc) VALUES (${key}, 1, now())
    ON CONFLICT (key) DO UPDATE SET
      attempts = CASE WHEN ytauto.login_attempts.window_start_utc < now() - make_interval(mins => ${windowMinutes}) THEN 1 ELSE ytauto.login_attempts.attempts + 1 END,
      window_start_utc = CASE WHEN ytauto.login_attempts.window_start_utc < now() - make_interval(mins => ${windowMinutes}) THEN now() ELSE ytauto.login_attempts.window_start_utc END
    RETURNING attempts`;
  if ((r?.attempts as number) > max) throw new LimitError(`Juda ko'p urinish — ${windowMinutes} daqiqadan keyin qayta urinib ko'ring.`);
}

export class LimitError extends Error {}

/** Botga token bilan kirish: Telegram ID hisobga bog'lanadi (oldingi bog'lanish almashtiriladi). */
export async function loginTelegram(telegramId: number, name: string, token: string): Promise<{ ok: boolean; message: string }> {
  await ensureSchema();
  await rateLimit(`tg:${telegramId}`, 10, 15);
  const t = token.trim();
  if (!TOKEN_RE.test(t)) return { ok: false, message: "Token noto'g'ri ko'rinishda. YtAuto ilovasidagi tokenni to'liq nusxalang (yt_ bilan boshlanadi)." };
  const h = hashToken(t);
  const [acc] = await ownerSql()`SELECT id::text AS id FROM ytauto.accounts WHERE token_hash = ${h}`;
  if (!acc) return { ok: false, message: "Bunday token topilmadi. Ilovadagi joriy tokenni nusxalang (token yangilangan bo'lishi mumkin)." };
  await ownerSql()`
    INSERT INTO ytauto.telegram_links(telegram_id, account_id, token_hash, name) VALUES (${telegramId}, ${acc.id as string}, ${h}, ${name.slice(0, 80)})
    ON CONFLICT (telegram_id) DO UPDATE SET account_id = EXCLUDED.account_id, token_hash = EXCLUDED.token_hash, name = EXCLUDED.name, linked_at_utc = now()`;
  await ownerSql()`DELETE FROM ytauto.login_attempts WHERE key = ${`tg:${telegramId}`}`;
  const [k] = await ownerSql()`SELECT host_account_id IS NOT NULL AS guest FROM ytauto.accounts WHERE id = ${acc.id as string}`;
  return {
    ok: true,
    message: k?.guest === true
      ? "Kirdingiz (qo'shimcha token). Videolaringiz token bergan hisob kompyuterida, ish vaqtida tayyorlanadi."
      : "Kirdingiz. Bu Telegram hisobi endi shu kompyuter bilan bog'langan.",
  };
}

export async function logoutTelegram(telegramId: number) {
  await ensureSchema();
  await ownerSql()`DELETE FROM ytauto.telegram_links WHERE telegram_id = ${telegramId}`;
}

export type DeviceRequest = { deviceName?: unknown; machine?: unknown; tokenHash?: unknown; password?: unknown };
export type DeviceResponse = {
  accountId: string; roleName: string; botUsername: string;
  db: { host: string; port: number; database: string; username: string };
};

function validate(b: DeviceRequest) {
  const deviceName = String(b.deviceName ?? "").trim();
  const machine = String(b.machine ?? "").trim();
  const tokenHash = String(b.tokenHash ?? "").trim().toLowerCase();
  const password = String(b.password ?? "");
  if (!deviceName || deviceName.length > 80) throw new Error("Qurilma nomi 1–80 belgi bo'lsin.");
  if (machine.length > 80) throw new Error("Kompyuter nomi juda uzun.");
  if (!HASH_RE.test(tokenHash)) throw new Error("Token xeshi noto'g'ri.");
  if (password.length < 24 || password.length > 200 || !/^[0-9A-Za-z]+$/.test(password)) throw new Error("Parol noto'g'ri.");
  return { deviceName, machine, tokenHash, password };
}

/** Qurilma uchun ulanish ma'lumoti: bot ulangan host/port/baza, foydalanuvchi - yangi rol (pooler bo'lsa "<rol>.<loyiha>"). */
function deviceDb(role: string): DeviceResponse["db"] {
  const o = ownerConnectionInfo();
  const dot = o.username.indexOf(".");
  const pooler = /pooler\.supabase\.com$/i.test(o.host);
  // Desktop Npgsql: Supabase pooler'ning session rejimi (5432) - transaction rejimi (6543) emas.
  const port = Number(process.env.DEVICE_DB_PORT) || (pooler ? 5432 : o.port);
  return { host: process.env.DEVICE_DB_HOST?.trim() || o.host, port, database: o.database, username: dot > 0 ? role + o.username.slice(dot) : role };
}

let botName: string | null = null;
export async function botUsername(): Promise<string> {
  if (botName !== null) return botName;
  const env = process.env.TELEGRAM_BOT_USERNAME?.trim().replace(/^@/, "");
  if (env) return (botName = env);
  try {
    const r = await fetch(`https://api.telegram.org/bot${process.env.TELEGRAM_BOT_TOKEN ?? ""}/getMe`);
    const j = (await r.json()) as { result?: { username?: string } };
    botName = j.result?.username ?? "";
  } catch { botName = ""; }
  return botName;
}

/** Desktop sozlashi: yangi hisob va shu kompyuter roli. */
export async function registerDevice(body: DeviceRequest, ip: string): Promise<DeviceResponse> {
  await ensureSchema();
  await rateLimit(`reg:${ip}`, 5, 60);
  const v = validate(body);
  const [r] = await ownerSql()`SELECT account_id::text AS account_id, role_name FROM ytauto.register_account(${v.deviceName}, ${v.machine}, ${v.tokenHash}, ${v.password})`;
  return { accountId: r.account_id as string, roleName: r.role_name as string, db: deviceDb(r.role_name as string), botUsername: await botUsername() };
}

/** Mavjud token bilan (qayta o'rnatilgan yoki ikkinchi) kompyuterni hisobga ulash. */
export async function loginDevice(body: DeviceRequest, ip: string): Promise<DeviceResponse | null> {
  await ensureSchema();
  await rateLimit(`dev:${ip}`, 10, 15);
  const v = validate(body);
  const [g] = await ownerSql()`SELECT host_account_id IS NOT NULL AS guest FROM ytauto.accounts WHERE token_hash = ${v.tokenHash}`;
  if (g?.guest === true)
    throw new Error("Bu qo'shimcha token: u faqat Telegram botga kirish uchun. Videolar token bergan hisob kompyuterida tayyorlanadi — YtAuto ilovasini shu token bilan ulab bo'lmaydi.");
  const [r] = await ownerSql()`SELECT account_id::text AS account_id, role_name FROM ytauto.login_account(${v.tokenHash}, ${v.deviceName}, ${v.machine}, ${v.password})`;
  if (!r) return null;
  return { accountId: r.account_id as string, roleName: r.role_name as string, db: deviceDb(r.role_name as string), botUsername: await botUsername() };
}

export function clientIp(req: Request): string {
  const h = req.headers;
  return (h.get("x-forwarded-for")?.split(",")[0] ?? h.get("x-real-ip") ?? "local").trim().slice(0, 64);
}

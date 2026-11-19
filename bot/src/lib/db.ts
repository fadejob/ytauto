import "server-only";
import { AsyncLocalStorage } from "node:async_hooks";
import { readFileSync } from "node:fs";
import path from "node:path";
import postgres from "postgres";

/**
 * YtAuto online bazasi (Supabase PostgreSQL, "ytauto" sxemasi) — ko'p foydalanuvchili.
 *
 * Har API so'rovi bitta tranzaksiyada, hisob kontekstida bajariladi (withAccount):
 *   SET LOCAL ROLE ytauto_bot  - BYPASSRLS'siz rol (Supabase'ning "postgres" roli RLS'ni chetlab o'tadi, bu rol - yo'q);
 *   set_config('ytauto_ctx.account', <hisob>) - Row Level Security faqat shu hisob qatorlarini ko'rsatadi va yozdiradi.
 * Shu tufayli kodda WHERE account_id = ... unutilsa ham boshqa foydalanuvchi ma'lumoti ko'rinmaydi; yangi qatorlarga
 * account_id avtomatik qo'yiladi (DEFAULT ytauto.current_account()).
 *
 * sql() faqat hisob konteksti ichida ishlaydi (aks holda xato). Hisoblar, tokenlar va Telegram bog'lanishlari bilan faqat
 * accounts.ts ishlaydi - ownerSql() orqali.
 *
 * DATABASE_URL ikkala formatda: postgresql://... URL yoki Npgsql uslubidagi "Host=...;Username=...;Password=...".
 */
type Opts = postgres.Options<Record<string, postgres.PostgresType>>;

export function connectionOptions(raw: string): Opts & { url?: string } {
  const v = raw.trim();
  if (/^postgres(ql)?:\/\//i.test(v)) return { url: v };
  const kv = new Map<string, string>();
  for (const part of v.split(";")) {
    const i = part.indexOf("=");
    if (i > 0) kv.set(part.slice(0, i).trim().toLowerCase().replace(/\s+/g, ""), part.slice(i + 1).trim());
  }
  return {
    host: kv.get("host") ?? kv.get("server"),
    port: Number(kv.get("port") ?? 5432),
    database: kv.get("database") ?? "postgres",
    username: kv.get("username") ?? kv.get("userid") ?? kv.get("user"),
    password: kv.get("password"),
  };
}

/** DATABASE_URL dagi ulanish ma'lumoti (parolsiz): qurilma ulanish satrini qurish uchun. */
export function ownerConnectionInfo(): { host: string; port: number; database: string; username: string } {
  const o = connectionOptions(process.env.DATABASE_URL ?? "");
  if (o.url) {
    const u = new URL(o.url);
    return { host: u.hostname, port: Number(u.port || 5432), database: decodeURIComponent(u.pathname.replace(/^\//, "")) || "postgres", username: decodeURIComponent(u.username) };
  }
  return { host: String(o.host ?? ""), port: Number(o.port ?? 5432), database: String(o.database ?? "postgres"), username: String(o.username ?? "") };
}

/**
 * Supabase pooler'ning session rejimi (5432) har mijozga alohida ulanish beradi va pool_size (15) bilan cheklangan: serverless
 * (Vercel) instance'lar uni tez to'ldiradi - EMAXCONNSESSION. Bot faqat qisqa tranzaksiyalar ishlatadi (SET LOCAL, set_config(..., true),
 * pg_advisory_xact_lock, prepare: false), shuning uchun shu hostda doim transaction rejimi (6543). Desktop qurilmalar 5432 da qoladi (deviceDb).
 */
export function botPort(host: string, port: number): number {
  return /pooler\.supabase\.com$/i.test(host) && port === 5432 ? 6543 : port;
}

function create() {
  const raw = process.env.DATABASE_URL ?? "";
  if (!raw.trim()) throw new Error("DATABASE_URL sozlanmagan (bot/.env.local).");
  const { url: rawUrl, ...opts } = connectionOptions(raw);
  let url = rawUrl;
  if (url) {
    const u = new URL(url);
    const p = botPort(u.hostname, Number(u.port || 5432));
    if (String(p) !== (u.port || "5432")) { u.port = String(p); url = u.toString(); }
  } else opts.port = botPort(String(opts.host ?? ""), Number(opts.port ?? 5432));
  const local = /^(localhost|127\.0\.0\.1)$/.test(String(url ? new URL(url).hostname : opts.host ?? ""));
  const common = {
    ssl: local ? (false as const) : ("require" as const),
    max: 3,
    idle_timeout: 20,
    connect_timeout: 10,
    prepare: false, // Supabase pooler (transaction mode) bilan ham ishlaydi
    onnotice: () => {}, // migratsiyadagi "already exists" xabarlari
  };
  return url ? postgres(url, common) : postgres({ ...opts, ...common });
}

type Sql = ReturnType<typeof create>;
const g = globalThis as unknown as { __ytautoSql?: Sql; __ytautoSchema?: Promise<void> };

/** Jadval egasi ulanishi: FAQAT hisob/token/migratsiya kodi uchun (accounts.ts, schema). Ma'lumotlarga - sql(). */
export function ownerSql(): Sql {
  g.__ytautoSql ??= create();
  return g.__ytautoSql;
}

type Ctx = { tx: Sql; accountId: string; guest: boolean };
const als = new AsyncLocalStorage<Ctx>();

/** Joriy so'rov hisobining tranzaksiyasi (RLS bilan). Hisob konteksti bo'lmasa xato - tasodifan hamma ma'lumotni o'qib qo'ymaslik uchun. */
export function sql(): Sql {
  const c = als.getStore();
  if (!c) throw new Error("Ichki xato: hisob konteksti yo'q (withAccount).");
  return c.tx;
}

/** Joriy hisob qo'shimcha (mehmon) tokenmi: «Hozir chiqarish» va kompyuterga buyruqlar yo'q. */
export function isGuest(): boolean {
  return als.getStore()?.guest === true;
}

export function currentAccountId(): string {
  const c = als.getStore();
  if (!c) throw new Error("Ichki xato: hisob konteksti yo'q (withAccount).");
  return c.accountId;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** fn ni shu hisob kontekstida, bitta tranzaksiyada bajaradi (xato bo'lsa hammasi bekor qilinadi). */
export async function withAccount<T>(accountId: string, fn: () => Promise<T>, opts?: { guest?: boolean }): Promise<T> {
  if (!UUID.test(accountId)) throw new Error("Hisob aniqlanmadi.");
  await ensureSchema();
  const result = await ownerSql().begin(async (tx) => {
    await tx.unsafe("SET LOCAL ROLE ytauto_bot");
    await tx`SELECT set_config('ytauto_ctx.account', ${accountId}, true)`;
    return { v: await als.run({ tx: tx as unknown as Sql, accountId, guest: opts?.guest === true }, fn) };
  });
  return (result as { v: T }).v;
}

/**
 * Sxema (bot/db/schema.sql) versiyasini tekshiradi. Farq qilsa: DB_AUTO_MIGRATE=1 bo'lsa migratsiya qiladi, aks holda tushunarli xato
 * (production bazani tasodifan o'zgartirmaslik uchun migratsiya ataylab: `npm run db:migrate`). Har server jarayonida bir marta.
 */
export function ensureSchema(): Promise<void> {
  g.__ytautoSchema ??= (async () => {
    const { text, version } = schemaFile();
    if ((await dbSchemaVersion()) === version) return;
    if (process.env.DB_AUTO_MIGRATE !== "1")
      throw new Error(`Baza sxemasi eski (kerak: v${version}). Migratsiya: bot papkasida "npm run db:migrate" (yoki DB_AUTO_MIGRATE=1).`);
    await ownerSql().begin((tx) => tx.unsafe(text));
  })().catch((e) => { g.__ytautoSchema = undefined; throw e; });
  return g.__ytautoSchema;
}

export function schemaFile(): { text: string; version: string } {
  const text = readFileSync(path.join(process.cwd(), "db", "schema.sql"), "utf8");
  return { text, version: text.match(/ytauto-schema-version:\s*(\S+)/)?.[1] ?? "" };
}

export async function dbSchemaVersion(): Promise<string | null> {
  const db = ownerSql();
  const [meta] = await db`SELECT to_regclass('ytauto.schema_meta') IS NOT NULL AS ok`;
  if (!meta?.ok) return null;
  const [row] = await db`SELECT value FROM ytauto.schema_meta WHERE key = 'version'`;
  return (row?.value as string | undefined) ?? null;
}

export const SCHEMA = "ytauto";

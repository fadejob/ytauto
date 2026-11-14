import "server-only";
import postgres from "postgres";

/**
 * YtAuto online bazasi (Supabase PostgreSQL, "ytauto" sxemasi) — desktop ilova va worker ishlatadigan baza.
 * DATABASE_URL ikkala formatda: postgresql://... URL yoki Npgsql uslubidagi "Host=...;Username=...;Password=...".
 */
function connectionOptions(raw: string): postgres.Options<Record<string, postgres.PostgresType>> & { url?: string } {
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

function create() {
  const raw = process.env.DATABASE_URL ?? "";
  if (!raw.trim()) throw new Error("DATABASE_URL sozlanmagan (bot/.env.local).");
  const { url, ...opts } = connectionOptions(raw);
  const common = {
    ssl: "require" as const,
    max: 3,
    idle_timeout: 20,
    connect_timeout: 10,
    prepare: false, // Supabase pooler (transaction mode) bilan ham ishlaydi
  };
  return url ? postgres(url, common) : postgres({ ...opts, ...common });
}

const g = globalThis as unknown as { __ytautoSql?: ReturnType<typeof create> };

export function sql() {
  g.__ytautoSql ??= create();
  return g.__ytautoSql;
}

export const SCHEMA = "ytauto";

// Online baza sxemasini db/schema.sql versiyasiga keltiradi (idempotent, bitta tranzaksiyada). Ishlatish: npm run db:migrate
// Diqqat: v2 (ko'p foydalanuvchili) migratsiyasidan keyin eski (v1) desktop/worker versiyalari bazaga yoza olmaydi -
// avval yangi YtAuto.exe'ni o'rnating. Eski ma'lumotlar "legacy" hisobga o'tadi; ularni eski ulanish satri bor kompyuter
// ilovadagi "Sozlash" orqali o'z hisobiga oladi.
import { readFileSync } from "node:fs";
import postgres from "postgres";

const raw = (process.env.DATABASE_URL ?? "").trim();
if (!raw) { console.error("DATABASE_URL bo'sh (bot/.env.local)."); process.exit(1); }

function opts(v) {
  if (/^postgres(ql)?:\/\//i.test(v)) return v;
  const kv = new Map(v.split(";").filter((p) => p.includes("=")).map((p) => {
    const i = p.indexOf("="); return [p.slice(0, i).trim().toLowerCase().replace(/\s+/g, ""), p.slice(i + 1).trim()];
  }));
  return { host: kv.get("host") ?? kv.get("server"), port: Number(kv.get("port") ?? 5432), database: kv.get("database") ?? "postgres",
    username: kv.get("username") ?? kv.get("userid") ?? kv.get("user"), password: kv.get("password") };
}

const o = opts(raw);
const host = typeof o === "string" ? new URL(o).hostname : o.host;
const local = /^(localhost|127\.0\.0\.1)$/.test(host ?? "");
const common = { ssl: local ? false : "require", max: 1, connect_timeout: 15, prepare: false, onnotice: () => {} };
const sql = typeof o === "string" ? postgres(o, common) : postgres({ ...o, ...common });
const text = readFileSync(new URL("../db/schema.sql", import.meta.url), "utf8");
const version = text.match(/ytauto-schema-version:\s*(\S+)/)?.[1] ?? "";
try {
  const [m] = await sql`select to_regclass('ytauto.schema_meta') is not null as ok`;
  const current = m.ok ? (await sql`select value from ytauto.schema_meta where key = 'version'`)[0]?.value ?? null : null;
  console.log(`Baza: ${host}. Joriy sxema: ${current ?? "yo'q"}, kerak: ${version}.`);
  if (current === version) { console.log("✓ Sxema allaqachon yangi."); process.exit(0); }
  await sql.begin((tx) => tx.unsafe(text));
  const [a] = await sql`select count(*)::int as n, count(*) filter (where legacy)::int as legacy from ytauto.accounts`;
  console.log(`✓ Migratsiya tugadi (v${version}). Hisoblar: ${a.n} (eski ma'lumotlar hisobi: ${a.legacy}).`);
} catch (e) {
  console.error("✗ Migratsiya xatosi:", e.message);
  process.exitCode = 1;
} finally {
  await sql.end();
}

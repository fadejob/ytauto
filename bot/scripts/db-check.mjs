// Online baza ulanishini tekshiradi (sirlar ko'rsatilmaydi). Ishlatish: npm run db:check
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
const common = { ssl: "require", max: 1, connect_timeout: 15, prepare: false };
const sql = typeof o === "string" ? postgres(o, common) : postgres({ ...o, ...common });
try {
  const [v] = await sql`select current_database() as db, split_part(version(), ' ', 2) as ver`;
  console.log(`✓ Ulandi: ${typeof o === "string" ? new URL(o).hostname : o.host} — ${v.db}, PostgreSQL ${v.ver}`);
  const [s] = await sql`select count(*)::int as n from information_schema.tables where table_schema = 'ytauto'`;
  if (!s.n) { console.log("✗ 'ytauto' sxemasi yo'q — desktop ilovani online bazaga bir marta ulang."); process.exit(2); }
  const [c] = await sql`select exists(select 1 from information_schema.columns where table_schema='ytauto' and table_name='jobs' and column_name='publish_now') as ok`;
  console.log(`${c.ok ? "✓" : "✗"} jobs.publish_now ustuni ${c.ok ? "bor" : "yo'q — yangi ilova/worker versiyasini bir marta ishga tushiring"}`);
  const rows = await sql`select status, count(*)::int as n from ytauto.jobs group by status order by status`;
  console.log(`✓ Mavzular: ${rows.reduce((a, r) => a + r.n, 0)} ta (holat bo'yicha: ${rows.map((r) => `${r.status}:${r.n}`).join(", ") || "-"})`);
  const keys = await sql`select key, length(value)::int as len, updated_at_utc from ytauto.shared_settings order by key`;
  for (const k of keys) console.log(`  shared_settings.${k.key} — ${k.len} belgi, ${new Date(k.updated_at_utc).toISOString().slice(0, 16)}`);
  if (!keys.some((k) => k.key === "bot.catalog")) console.log("! bot.catalog yo'q — yangi YtAuto ilovasini oching (yoki YtAuto.Worker.exe --publish-catalog).");
} catch (e) {
  console.error("✗ Ulanib bo'lmadi:", String(e.message ?? e).split("\n")[0]);
  process.exitCode = 1;
} finally {
  await sql.end({ timeout: 2 });
}

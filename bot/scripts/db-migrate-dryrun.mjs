// Migratsiyani QURUQ sinash: schema.sql bitta tranzaksiyada bajariladi, tekshiriladi va doim BEKOR qilinadi (bazada o'zgarish qolmaydi).
// Ishlatish: node --env-file=.env.local scripts/db-migrate-dryrun.mjs
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
// Xato matnlarida ulanish satri/parol chiqmasligi uchun.
const clean = (m) => String(m ?? "").split("\n")[0].replace(/password=[^;\s]+/gi, "Password=***").replace(/:\/\/[^@\s]+@/g, "://***@");

const o = opts(raw);
const common = { ssl: "require", max: 1, connect_timeout: 15, prepare: false, onnotice: () => {} };
const sql = typeof o === "string" ? postgres(o, common) : postgres({ ...o, ...common });
const text = readFileSync(new URL("../db/schema.sql", import.meta.url), "utf8");
const ROLLBACK = "DRY_RUN_ROLLBACK";
try {
  const [v] = await sql`select value from ytauto.schema_meta where key = 'version'`;
  const [a] = await sql`select count(*)::int as n, count(*) filter (where legacy)::int as legacy from ytauto.accounts`;
  console.log(`Joriy versiya: ${v?.value} | hisoblar: ${a.n} | legacy: ${a.legacy}`);
  await sql.begin(async (tx) => {
    await tx.unsafe(text);
    const [m] = await tx`select value from ytauto.schema_meta where key = 'version'`;
    const [c] = await tx`select exists(select 1 from information_schema.columns where table_schema='ytauto' and table_name='accounts' and column_name='host_account_id') as col`;
    const [f] = await tx`select to_regprocedure('ytauto.hosted_accounts()') is not null as fn`;
    console.log(`Tranzaksiya ichida: versiya ${m.value} | host_account_id: ${c.col} | hosted_accounts(): ${f.fn}`);
    // Bot konteksti har hisob uchun faqat o'z qatorlarini ko'rishi (RLS) - v3 current_account() bilan ham.
    const accs = await tx`select a.id::text as id, (select count(*)::int from ytauto.jobs j where j.account_id = a.id) as own from ytauto.accounts a`;
    await tx.unsafe("SET LOCAL ROLE ytauto_bot");
    let ok = true;
    for (const acc of accs) {
      await tx`select set_config('ytauto_ctx.account', ${acc.id}, true)`;
      const [x] = await tx`select ytauto.current_account()::text as cur, count(*)::int as jobs, count(distinct account_id)::int as accs from ytauto.jobs`;
      const good = x.cur === acc.id && x.jobs === acc.own && x.accs <= 1;
      ok &&= good;
      console.log(`  hisob ${acc.id.slice(0, 8)}…: kontekst ${x.cur === acc.id ? "to'g'ri" : "XATO"}, ko'rinadigan mavzular ${x.jobs}/${acc.own}, boshqa hisoblar ${x.accs > 1 ? "KO'RINADI (XATO)" : "ko'rinmaydi"}`);
    }
    // v4: kanal konteksti jadvallari ham hisob bo'yicha ajratilgan (yozish - o'z hisobiga, boshqa hisob ko'rmaydi).
    const [t4] = await tx`select to_regclass('ytauto.channel_profile') is not null as p, to_regclass('ytauto.topic_suggestions') is not null as s`;
    console.log(`  v4 jadvallari: channel_profile ${t4.p}, topic_suggestions ${t4.s}`);
    ok &&= t4.p && t4.s;
    if (accs.length >= 1) {
      await tx`select set_config('ytauto_ctx.account', ${accs[0].id}, true)`;
      await tx`insert into ytauto.channel_profile(channel_title) values ('dryrun') on conflict (account_id) do update set channel_title = 'dryrun'`;
      await tx`insert into ytauto.topic_suggestions(for_date, topic) values (current_date, 'dryrun')`;
      const [own] = await tx`select count(*)::int as n from ytauto.channel_profile where channel_title = 'dryrun'`;
      ok &&= own.n === 1;
      if (accs.length >= 2) {
        await tx`select set_config('ytauto_ctx.account', ${accs[1].id}, true)`;
        const [other] = await tx`select (select count(*)::int from ytauto.channel_profile where channel_title = 'dryrun') + (select count(*)::int from ytauto.topic_suggestions where topic = 'dryrun') as n`;
        console.log(`  v4 izolyatsiya: boshqa hisob dryrun yozuvlarini ${other.n === 0 ? "ko'rmaydi" : "KO'RADI (XATO)"}`);
        ok &&= other.n === 0;
      }
    }
    await tx`select set_config('ytauto_ctx.account', '', true)`;
    const [none] = await tx`select count(*)::int as n from ytauto.jobs`;
    console.log(`  kontekstsiz: ${none.n} ta mavzu ko'rinadi (0 bo'lishi kerak)`);
    ok &&= none.n === 0;
    console.log(ok ? "✓ Izolyatsiya to'g'ri." : "✗ Izolyatsiyada muammo!");
    throw new Error(ROLLBACK);
  });
} catch (e) {
  console.log(e.message === ROLLBACK ? "✓ Quruq sinov tugadi, hammasi bekor qilindi (bazada o'zgarish yo'q)." : "✗ Xato: " + clean(e.message));
  if (e.message !== ROLLBACK) process.exitCode = 1;
} finally {
  await sql.end();
}

-- ytauto-schema-version: 5
-- YtAuto online bazasi (Supabase PostgreSQL, "ytauto" sxemasi) - YAGONA manba. Bot (bot/src/lib/schema.ts) ham, desktop ilova
-- (PostgresJobStore, egasi huquqi bilan ulanganda) ham shu faylni ishga tushiradi. Fayl idempotent: qayta ishga tushirish xavfsiz.
-- Yuqoridagi versiya o'zgarmasa, fayl qayta bajarilmaydi (ytauto.schema_meta). O'zgartirganda versiyani oshiring.
--
-- Ko'p foydalanuvchili arxitektura (v2):
--  * ytauto.accounts       - foydalanuvchi (hisob): uniqal id, token xeshi (token o'zi saqlanmaydi), qurilma nomi.
--  * ytauto.account_roles  - har qurilma uchun alohida Postgres login roli (ytauto_device guruhida). Desktop/worker shu rol bilan ulanadi.
--  * ytauto.telegram_links - Telegram foydalanuvchisi qaysi hisobga token bilan kirgani.
--  * Barcha ma'lumot jadvallarida account_id. Row Level Security (FORCE) har so'rovni ytauto.current_account() bilan cheklaydi:
--      - qurilma roli: hisob faqat account_roles'dan olinadi (o'zgartirib bo'lmaydi);
--      - bot (jadval egasi): har so'rov tranzaksiyasida set_config('ytauto_ctx.account', ...) qiladi; o'rnatilmasa hech narsa ko'rinmaydi.
--    Shu tufayli bir foydalanuvchi boshqasining ma'lumotini na o'qiy, na o'zgartira oladi - xato kod bo'lsa ham baza ruxsat bermaydi.
--
-- Qo'shimcha (mehmon) tokenlar (v3):
--  * Asosiy hisob egasi botda qo'shimcha token yaratadi: bu alohida hisob (accounts.host_account_id = asosiy hisob).
--    Mehmon botga shu token bilan kiradi va o'z mavzulari, sozlamalari (Gemini, promptlar) va YouTube kanali bilan ishlaydi.
--  * Videolar asosiy hisob kompyuterida tayyorlanadi: qurilma roli set_config('ytauto_ctx.guest', <mehmon>) qilsa,
--    current_account() faqat o'zi "mezbon" bo'lgan mehmon hisobini qaytaradi (boshqa hisobga o'tib bo'lmaydi).
--  * Bot konteksti o'zgarmaydi: asosiy hisob mehmonning ma'lumotini ko'rmaydi, mehmon - asosiynikini.

SELECT pg_advisory_xact_lock(hashtext('ytauto.schema.migrate'));

CREATE SCHEMA IF NOT EXISTS ytauto;
CREATE TABLE IF NOT EXISTS ytauto.schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

-- ------------------------------------------------------------------ asosiy jadvallar (v1)
CREATE TABLE IF NOT EXISTS ytauto.jobs (
  id BIGSERIAL PRIMARY KEY,
  topic TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', language TEXT NOT NULL, target_duration_sec INT NOT NULL DEFAULT 0,
  format INT NOT NULL, template_name TEXT NOT NULL DEFAULT '', voice_profile TEXT NOT NULL DEFAULT '',
  publish_at_utc TIMESTAMPTZ NOT NULL, status INT NOT NULL, created_at_utc TIMESTAMPTZ NOT NULL, updated_at_utc TIMESTAMPTZ NOT NULL,
  run_attempts INT NOT NULL DEFAULT 0, next_attempt_at_utc TIMESTAMPTZ, claimed_by TEXT, claimed_at_utc TIMESTAMPTZ,
  cancel_requested BOOLEAN NOT NULL DEFAULT FALSE, qa_fix_cycles INT NOT NULL DEFAULT 0,
  last_error_code TEXT, last_error_message TEXT, attention_reason TEXT, settings_snapshot TEXT,
  title TEXT, description TEXT, thumbnail_path TEXT, video_archive_path TEXT, audio_archive_path TEXT, subtitle_archive_path TEXT,
  audio_duration_sec DOUBLE PRECISION, youtube_video_id TEXT, youtube_privacy TEXT, youtube_upload_status TEXT, youtube_processing_status TEXT,
  youtube_checked_at_utc TIMESTAMPTZ, published_at_utc TIMESTAMPTZ, work_dir TEXT, work_dir_cleaned BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE INDEX IF NOT EXISTS ix_jobs_queue ON ytauto.jobs(status, created_at_utc, id);
CREATE TABLE IF NOT EXISTS ytauto.job_stages (
  job_id BIGINT NOT NULL REFERENCES ytauto.jobs(id) ON DELETE CASCADE, stage INT NOT NULL, state INT NOT NULL,
  started_at_utc TIMESTAMPTZ, finished_at_utc TIMESTAMPTZ, attempts INT NOT NULL DEFAULT 0, input_hash TEXT, outputs_json TEXT,
  error_code TEXT, error_message TEXT, duration_ms BIGINT, metrics_json TEXT, PRIMARY KEY(job_id, stage)
);
CREATE TABLE IF NOT EXISTS ytauto.job_events (
  id BIGSERIAL PRIMARY KEY, job_id BIGINT NOT NULL REFERENCES ytauto.jobs(id) ON DELETE CASCADE,
  at_utc TIMESTAMPTZ NOT NULL, level TEXT NOT NULL, stage TEXT, message TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_events_job ON ytauto.job_events(job_id, id);
CREATE TABLE IF NOT EXISTS ytauto.owned_processes (
  id BIGSERIAL PRIMARY KEY, job_id BIGINT, run_id TEXT NOT NULL, stage TEXT, tool TEXT, pid INT NOT NULL,
  process_start_utc TIMESTAMPTZ NOT NULL, exe_path TEXT, ended_at_utc TIMESTAMPTZ, end_reason TEXT, peak_memory_bytes BIGINT
);
CREATE TABLE IF NOT EXISTS ytauto.worker_runs (
  run_id TEXT PRIMARY KEY, pid INT NOT NULL, process_start_utc TIMESTAMPTZ NOT NULL, started_at_utc TIMESTAMPTZ NOT NULL,
  finished_at_utc TIMESTAMPTZ, outcome TEXT, job_id BIGINT, message TEXT, trigger TEXT, machine TEXT
);
CREATE TABLE IF NOT EXISTS ytauto.shared_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at_utc TIMESTAMPTZ NOT NULL);
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS publish_now BOOLEAN NOT NULL DEFAULT FALSE;
CREATE INDEX IF NOT EXISTS ix_jobs_publish_now ON ytauto.jobs(id) WHERE publish_now;
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS upload_requested BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE ytauto.worker_runs ADD COLUMN IF NOT EXISTS stats_json TEXT;
CREATE TABLE IF NOT EXISTS ytauto.machines (
  machine TEXT PRIMARY KEY, last_seen_utc TIMESTAMPTZ NOT NULL, app_open BOOLEAN NOT NULL DEFAULT FALSE, app_seen_utc TIMESTAMPTZ,
  worker_state TEXT, worker_job_id BIGINT, worker_detail TEXT, worker_seen_utc TIMESTAMPTZ, version TEXT,
  check_requested BOOLEAN NOT NULL DEFAULT FALSE, check_requested_at_utc TIMESTAMPTZ, check_result_json TEXT, check_finished_at_utc TIMESTAMPTZ
);
ALTER TABLE ytauto.machines ADD COLUMN IF NOT EXISTS run_requested BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE ytauto.machines ADD COLUMN IF NOT EXISTS run_requested_at_utc TIMESTAMPTZ;
CREATE TABLE IF NOT EXISTS ytauto.machine_commands (
  id BIGSERIAL PRIMARY KEY, machine TEXT NOT NULL, kind TEXT NOT NULL, arg TEXT NOT NULL DEFAULT '', done BOOLEAN NOT NULL DEFAULT FALSE,
  created_at_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS ytauto.templates (
  machine TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, size_bytes BIGINT NOT NULL DEFAULT 0, added_at_utc TIMESTAMPTZ,
  source TEXT NOT NULL DEFAULT 'upload', notes TEXT, PRIMARY KEY (machine, name)
);
CREATE TABLE IF NOT EXISTS ytauto.template_uploads (
  id BIGSERIAL PRIMARY KEY, group_id TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, storage_path TEXT NOT NULL, size_bytes BIGINT NOT NULL DEFAULT 0,
  machine TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending', progress INT NOT NULL DEFAULT 0, message TEXT, download_url TEXT,
  cleaned BOOLEAN NOT NULL DEFAULT FALSE, created_at_utc TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS manual_script TEXT;
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS manual_title TEXT;
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS manual_audio_path TEXT;
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS manual_audio_url TEXT;

-- ------------------------------------------------------------------ v4: kanal konteksti
-- Bot ulangan YouTube kanalini tahlil qiladi (nom, tavsif, sarlavhalar, oxirgi 3 videoning montaji/ovozi/subtitri/ssenariysi).
-- Hisobga bitta yozuv (PK account_id - pastda). Promptlarga qo'shiladigan ixcham matn shared_settings['channel.context'] da.
CREATE TABLE IF NOT EXISTS ytauto.channel_profile (
  channel_id TEXT NOT NULL DEFAULT '', channel_title TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'idle', error TEXT, enabled BOOLEAN NOT NULL DEFAULT TRUE,
  context_json TEXT, last_video_id TEXT, analyzed_at_utc TIMESTAMPTZ,
  started_at_utc TIMESTAMPTZ, updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- Kunlik mavzu tavsiyalari (kanal kontekstidan).
CREATE TABLE IF NOT EXISTS ytauto.topic_suggestions (
  id BIGSERIAL PRIMARY KEY, for_date DATE NOT NULL, topic TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', reason TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'new', job_id BIGINT, created_at_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ------------------------------------------------------------------ v5: MCP orqali demo video (Claude Code)
-- kind = 'demo': Claude Code loyihani o'rganib, screenshot/action videolarni MCP orqali yuboradi; ssenariy (Gemini, 2 bosqich),
-- ovoz, montaj va render YtAuto ichida. demo_spec - MCP so'rovi (loyiha, media va ularning imzolangan havolalari, talablar),
-- demo_result - tayyor video Storage'dagi yo'llari (worker yozadi). Holat 15 = "Tayyor" (YouTube'ga chiqarish kutilmoqda).
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS kind TEXT NOT NULL DEFAULT '';
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS demo_spec TEXT;
ALTER TABLE ytauto.jobs ADD COLUMN IF NOT EXISTS demo_result TEXT;
CREATE INDEX IF NOT EXISTS ix_jobs_demo ON ytauto.jobs(created_at_utc, id) WHERE kind = 'demo';

-- ------------------------------------------------------------------ v2: hisoblar va izolyatsiya
CREATE TABLE IF NOT EXISTS ytauto.accounts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  token_hash TEXT UNIQUE,                         -- sha256(token) hex; token o'zi faqat foydalanuvchida va uning kompyuterida
  device_name TEXT NOT NULL DEFAULT '',
  legacy BOOLEAN NOT NULL DEFAULT FALSE,          -- v2 dan oldingi (bitta foydalanuvchili) ma'lumotlar egasi
  created_at_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
  token_rotated_at_utc TIMESTAMPTZ
);
-- v3: qo'shimcha (mehmon) token hisobi - videolari shu (mezbon) hisob kompyuterida tayyorlanadi.
ALTER TABLE ytauto.accounts ADD COLUMN IF NOT EXISTS host_account_id UUID REFERENCES ytauto.accounts(id) ON DELETE CASCADE;
CREATE INDEX IF NOT EXISTS ix_accounts_host ON ytauto.accounts(host_account_id) WHERE host_account_id IS NOT NULL;
CREATE TABLE IF NOT EXISTS ytauto.account_roles (
  role_name TEXT PRIMARY KEY,
  account_id UUID NOT NULL REFERENCES ytauto.accounts(id) ON DELETE CASCADE,
  device_name TEXT NOT NULL DEFAULT '', machine TEXT NOT NULL DEFAULT '',
  created_at_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_account_roles_account ON ytauto.account_roles(account_id);
CREATE TABLE IF NOT EXISTS ytauto.telegram_links (
  telegram_id BIGINT PRIMARY KEY,
  account_id UUID NOT NULL REFERENCES ytauto.accounts(id) ON DELETE CASCADE,
  token_hash TEXT NOT NULL,                       -- kirishdagi token; hisob tokeni almashtirilsa bog'lanish o'z-o'zidan bekor bo'ladi
  name TEXT, linked_at_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_telegram_links_account ON ytauto.telegram_links(account_id);
CREATE TABLE IF NOT EXISTS ytauto.login_attempts (
  key TEXT PRIMARY KEY, attempts INT NOT NULL DEFAULT 0, window_start_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- v5: Claude Code (MCP) uchun alohida token - faqat /api/mcp ga kiradi (botga kirish, kompyuterni ulash yo'q), botda bekor qilinadi.
-- Hisobga bittadan; yangisi yaratilsa eskisi ishlamay qoladi. Token o'zi saqlanmaydi - faqat xeshi.
CREATE TABLE IF NOT EXISTS ytauto.mcp_tokens (
  account_id UUID PRIMARY KEY REFERENCES ytauto.accounts(id) ON DELETE CASCADE,
  token_hash TEXT NOT NULL UNIQUE,
  created_at_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_used_at_utc TIMESTAMPTZ
);

-- Rollar:
--  * ytauto_device - qurilma rollari guruhi (desktop/worker). Hisob faqat account_roles'dan olinadi.
--  * ytauto_bot    - bot har so'rovda SET LOCAL ROLE ytauto_bot qiladi: bu rolda BYPASSRLS yo'q, shuning uchun Supabase'ning
--                    "postgres" roli RLS'ni chetlab o'tsa ham bot so'rovlari faqat set_config('ytauto_ctx.account') hisobini ko'radi.
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ytauto_device') THEN CREATE ROLE ytauto_device NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ytauto_bot') THEN CREATE ROLE ytauto_bot NOLOGIN; END IF;
  EXECUTE format('GRANT ytauto_bot TO %I', current_user); -- SET ROLE uchun (takroriy GRANT zararsiz)
END $$;

-- Hisob konteksti. SECURITY DEFINER ichida current_user = funksiya egasi, shuning uchun session_user (ulangan login rol) ishlatiladi.
-- Qurilma roli uchun GUC hisobga olinmaydi - boshqa hisobga "o'tib" bo'lmaydi (mapping o'chirilgan bo'lsa ham NULL - hech narsa ko'rinmaydi).
CREATE OR REPLACE FUNCTION ytauto.current_account() RETURNS UUID
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE acc UUID; g UUID;
BEGIN
  SELECT account_id INTO acc FROM ytauto.account_roles WHERE role_name = session_user;
  IF acc IS NOT NULL OR session_user LIKE 'ytd\_%' OR pg_has_role(session_user, 'ytauto_device', 'USAGE') THEN
    -- Mezbon kompyuter mehmon vazifasini bajarayotganda: faqat shu hisob mezbon bo'lgan mehmon (aks holda o'z hisobi).
    g := NULLIF(current_setting('ytauto_ctx.guest', true), '')::UUID;
    IF acc IS NOT NULL AND g IS NOT NULL AND EXISTS (SELECT 1 FROM ytauto.accounts a WHERE a.id = g AND a.host_account_id = acc) THEN RETURN g; END IF;
    RETURN acc;
  END IF;
  RETURN NULLIF(current_setting('ytauto_ctx.account', true), '')::UUID;
END $$;

-- Ma'lumot jadvallari: account_id, eski ma'lumotlar "legacy" hisobga, RLS (FORCE), yangi PK'lar.
DO $$
DECLARE
  t TEXT;
  tables TEXT[] := ARRAY['jobs','job_stages','job_events','owned_processes','worker_runs','shared_settings','machines','machine_commands','templates','template_uploads',
                         'channel_profile','topic_suggestions'];
  legacy_id UUID;
  orphan BOOLEAN := FALSE;
  has BOOLEAN;
BEGIN
  FOREACH t IN ARRAY tables LOOP
    EXECUTE format('ALTER TABLE ytauto.%I NO FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ytauto.%I ADD COLUMN IF NOT EXISTS account_id UUID REFERENCES ytauto.accounts(id) ON DELETE CASCADE', t);
    EXECUTE format('SELECT EXISTS (SELECT 1 FROM ytauto.%I WHERE account_id IS NULL)', t) INTO has;
    orphan := orphan OR has;
  END LOOP;
  IF orphan THEN
    SELECT id INTO legacy_id FROM ytauto.accounts WHERE legacy ORDER BY created_at_utc LIMIT 1;
    IF legacy_id IS NULL THEN
      INSERT INTO ytauto.accounts(device_name, legacy) VALUES ('Oldingi ma''lumotlar', TRUE) RETURNING id INTO legacy_id;
    END IF;
    FOREACH t IN ARRAY tables LOOP
      EXECUTE format('UPDATE ytauto.%I SET account_id = $1 WHERE account_id IS NULL', t) USING legacy_id;
    END LOOP;
  END IF;
  FOREACH t IN ARRAY tables LOOP
    EXECUTE format('ALTER TABLE ytauto.%I ALTER COLUMN account_id SET DEFAULT ytauto.current_account()', t);
    EXECUTE format('ALTER TABLE ytauto.%I ALTER COLUMN account_id SET NOT NULL', t);
    EXECUTE format('ALTER TABLE ytauto.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('DROP POLICY IF EXISTS account_isolation ON ytauto.%I', t);
    EXECUTE format('CREATE POLICY account_isolation ON ytauto.%I USING (account_id = (SELECT ytauto.current_account())) WITH CHECK (account_id = (SELECT ytauto.current_account()))', t);
    EXECUTE format('ALTER TABLE ytauto.%I FORCE ROW LEVEL SECURITY', t);
  END LOOP;
END $$;

-- Har hisob uchun alohida kalitlar: bir xil kompyuter nomi yoki sozlama kaliti ikki hisobda to'qnashmaydi.
CREATE OR REPLACE FUNCTION ytauto._set_pk(tbl TEXT, cols TEXT) RETURNS VOID LANGUAGE plpgsql AS $$
DECLARE cname TEXT; def TEXT;
BEGIN
  SELECT conname, pg_get_constraintdef(oid) INTO cname, def FROM pg_constraint
  WHERE conrelid = format('ytauto.%I', tbl)::regclass AND contype = 'p';
  IF def IS DISTINCT FROM format('PRIMARY KEY (%s)', cols) THEN
    IF cname IS NOT NULL THEN EXECUTE format('ALTER TABLE ytauto.%I DROP CONSTRAINT %I', tbl, cname); END IF;
    EXECUTE format('ALTER TABLE ytauto.%I ADD PRIMARY KEY (%s)', tbl, cols);
  END IF;
END $$;
SELECT ytauto._set_pk('shared_settings', 'account_id, key');
SELECT ytauto._set_pk('machines', 'account_id, machine');
SELECT ytauto._set_pk('templates', 'account_id, machine, name');
SELECT ytauto._set_pk('channel_profile', 'account_id');
CREATE INDEX IF NOT EXISTS ix_topic_suggestions_account ON ytauto.topic_suggestions(account_id, for_date DESC, id);
CREATE INDEX IF NOT EXISTS ix_jobs_account_queue ON ytauto.jobs(account_id, status, created_at_utc, id);
CREATE INDEX IF NOT EXISTS ix_worker_runs_account ON ytauto.worker_runs(account_id, started_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_machine_commands_account ON ytauto.machine_commands(account_id, machine) WHERE NOT done;
CREATE INDEX IF NOT EXISTS ix_template_uploads_account ON ytauto.template_uploads(account_id, machine, status);

-- Hisob jadvallari: faqat egasi (bot) ko'radi; qurilmalarga faqat quyidagi funksiyalar orqali.
ALTER TABLE ytauto.accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE ytauto.account_roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE ytauto.telegram_links ENABLE ROW LEVEL SECURITY;
ALTER TABLE ytauto.login_attempts ENABLE ROW LEVEL SECURITY;
ALTER TABLE ytauto.mcp_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE ytauto.schema_meta ENABLE ROW LEVEL SECURITY;

-- ------------------------------------------------------------------ hisob funksiyalari (faqat egasi chaqiradi)
-- Yangi qurilma roli: tasodifiy nom, berilgan parol, ytauto_device guruhida, ulanishlar soni cheklangan.
CREATE OR REPLACE FUNCTION ytauto.create_device_role(p_account UUID, p_password TEXT, p_device TEXT, p_machine TEXT) RETURNS TEXT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE r TEXT := 'ytd_' || replace(gen_random_uuid()::TEXT, '-', '');
BEGIN
  IF length(coalesce(p_password, '')) < 24 THEN RAISE EXCEPTION 'parol juda qisqa'; END IF;
  EXECUTE format('CREATE ROLE %I LOGIN CONNECTION LIMIT 20 PASSWORD %L IN ROLE ytauto_device', r, p_password);
  INSERT INTO ytauto.account_roles(role_name, account_id, device_name, machine) VALUES (r, p_account, left(coalesce(p_device, ''), 80), left(coalesce(p_machine, ''), 80));
  RETURN r;
END $$;

-- Yangi hisob (birinchi o'rnatish).
CREATE OR REPLACE FUNCTION ytauto.register_account(p_device TEXT, p_machine TEXT, p_token_hash TEXT, p_password TEXT)
RETURNS TABLE(account_id UUID, role_name TEXT)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE acc UUID;
BEGIN
  IF p_token_hash !~ '^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'token xeshi noto''g''ri'; END IF;
  INSERT INTO ytauto.accounts(token_hash, device_name) VALUES (p_token_hash, left(coalesce(p_device, ''), 80)) RETURNING id INTO acc;
  RETURN QUERY SELECT acc, ytauto.create_device_role(acc, p_password, p_device, p_machine);
END $$;

-- Mavjud token bilan yangi (yoki qayta o'rnatilgan) kompyuterni ulash: hisob o'zgarmaydi, kompyuterga yangi rol beriladi.
CREATE OR REPLACE FUNCTION ytauto.login_account(p_token_hash TEXT, p_device TEXT, p_machine TEXT, p_password TEXT)
RETURNS TABLE(account_id UUID, role_name TEXT)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE acc UUID;
BEGIN
  SELECT id INTO acc FROM ytauto.accounts WHERE token_hash = p_token_hash;
  IF acc IS NULL THEN RETURN; END IF;
  RETURN QUERY SELECT acc, ytauto.create_device_role(acc, p_password, p_device, p_machine);
END $$;

-- v2 dan oldingi ma'lumotlar egasi (eski ulanish satri bor kompyuter) ularni o'z hisobiga oladi. Bir marta.
CREATE OR REPLACE FUNCTION ytauto.claim_legacy_account(p_device TEXT, p_machine TEXT, p_token_hash TEXT, p_password TEXT)
RETURNS TABLE(account_id UUID, role_name TEXT)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE acc UUID;
BEGIN
  IF p_token_hash !~ '^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'token xeshi noto''g''ri'; END IF;
  UPDATE ytauto.accounts SET token_hash = p_token_hash, device_name = left(coalesce(p_device, ''), 80), token_rotated_at_utc = now()
  WHERE id = (SELECT id FROM ytauto.accounts WHERE legacy AND token_hash IS NULL ORDER BY created_at_utc LIMIT 1)
  RETURNING id INTO acc;
  IF acc IS NULL THEN
    RETURN QUERY SELECT * FROM ytauto.register_account(p_device, p_machine, p_token_hash, p_password);
    RETURN;
  END IF;
  RETURN QUERY SELECT acc, ytauto.create_device_role(acc, p_password, p_device, p_machine);
END $$;

-- ------------------------------------------------------------------ qurilma (desktop/worker) chaqiradigan funksiyalar
-- Tokenni almashtirish: eski token bilan botga kirganlar chiqarib yuboriladi (qayta yangi token so'raladi).
CREATE OR REPLACE FUNCTION ytauto.rotate_token(p_new_hash TEXT) RETURNS VOID
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE acc UUID := ytauto.current_account();
BEGIN
  IF acc IS NULL THEN RAISE EXCEPTION 'hisob aniqlanmadi'; END IF;
  IF p_new_hash !~ '^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'token xeshi noto''g''ri'; END IF;
  UPDATE ytauto.accounts SET token_hash = p_new_hash, token_rotated_at_utc = now() WHERE id = acc;
  DELETE FROM ytauto.telegram_links WHERE account_id = acc;
END $$;

-- Shu qurilma hisobi mezbon bo'lgan mehmon hisoblari (worker ularning vazifalarini ham tayyorlaydi). Mehmon kontekstida ham
-- o'z (mezbon) hisobi bo'yicha qaytaradi.
CREATE OR REPLACE FUNCTION ytauto.hosted_accounts()
RETURNS TABLE(account_id UUID, label TEXT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT a.id, a.device_name FROM ytauto.accounts a
  WHERE a.host_account_id IS NOT NULL AND a.host_account_id = (SELECT r.account_id FROM ytauto.account_roles r WHERE r.role_name = session_user)
  ORDER BY a.created_at_utc;
$$;

CREATE OR REPLACE FUNCTION ytauto.account_info()
RETURNS TABLE(account_id UUID, device_name TEXT, created_at_utc TIMESTAMPTZ, telegram_users INT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT a.id, a.device_name, a.created_at_utc, (SELECT count(*)::INT FROM ytauto.telegram_links l WHERE l.account_id = a.id AND l.token_hash = a.token_hash)
  FROM ytauto.accounts a WHERE a.id = ytauto.current_account();
$$;

-- ------------------------------------------------------------------ huquqlar
REVOKE ALL ON FUNCTION ytauto.create_device_role(UUID, TEXT, TEXT, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION ytauto.register_account(TEXT, TEXT, TEXT, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION ytauto.login_account(TEXT, TEXT, TEXT, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION ytauto.claim_legacy_account(TEXT, TEXT, TEXT, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION ytauto._set_pk(TEXT, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION ytauto.rotate_token(TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION ytauto.account_info() FROM PUBLIC;
REVOKE ALL ON FUNCTION ytauto.hosted_accounts() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ytauto.current_account() TO PUBLIC;
GRANT EXECUTE ON FUNCTION ytauto.rotate_token(TEXT) TO ytauto_device;
GRANT EXECUTE ON FUNCTION ytauto.account_info() TO ytauto_device;
GRANT EXECUTE ON FUNCTION ytauto.hosted_accounts() TO ytauto_device;

GRANT USAGE ON SCHEMA ytauto TO ytauto_device, ytauto_bot;
GRANT SELECT, INSERT, UPDATE, DELETE ON ytauto.jobs, ytauto.job_stages, ytauto.job_events, ytauto.owned_processes, ytauto.worker_runs,
  ytauto.shared_settings, ytauto.machines, ytauto.machine_commands, ytauto.templates, ytauto.template_uploads,
  ytauto.channel_profile, ytauto.topic_suggestions TO ytauto_device, ytauto_bot;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ytauto TO ytauto_device, ytauto_bot;
REVOKE ALL ON ytauto.accounts, ytauto.account_roles, ytauto.telegram_links, ytauto.login_attempts, ytauto.schema_meta, ytauto.mcp_tokens FROM ytauto_device, ytauto_bot;

INSERT INTO ytauto.schema_meta(key, value) VALUES ('version', '5') ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;

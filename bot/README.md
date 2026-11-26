# YtAuto bot — Telegram web app

Mobil uchun Next.js web app. YtAuto online bazasi (Supabase PostgreSQL, `ytauto` sxemasi) bilan to'g'ridan-to'g'ri ishlaydi —
desktop ilova ochiq bo'lishi shart emas. **Ko'p foydalanuvchili**: har foydalanuvchi o'z kompyuteridagi YtAuto ilovasi bergan
token bilan kiradi va faqat o'z hisobini (mavzular, sozlamalar, promptlar, Gemini kalitlari, YouTube) ko'radi.

## Imkoniyatlar

- **Mavzular**: ro'yxat (Navbatda / Jarayonda / Chiqqan), bir vaqtda 5 tagacha mavzu qo'shish, tahrirlash, o'chirish.
  Tahrirlash va o'chirish **faqat video hali tayyorlanmagan** bo'lsa mumkin (worker band qilmagan, arxivda video yo'q, YouTube'ga yuklanmagan).
  Chiqish sanasi bo'sh bo'lsa — oxirgi rejadan +2 kun (desktop ilovadagi qoida).
- **⚡ Hozir chiqarish**: `publish_now` bayrog'i. Internetga ulangan, jadvali yoqilgan kompyuterdagi worker uni ~2 daqiqada oladi
  (Task Scheduler → "YtAuto Remote Trigger"), videoni tayyorlaydi yoki tayyorini oladi va YouTube'ga darhol chiqaradi.
- **Promptlar**: Gemini ssenariy va Claude montaj promptlarini tahrirlash (barcha kompyuterlar uchun umumiy).
- **AI yordamchi (Gemini)**: "kosmos haqida 3 ta mavzu qo'sh", "promptda shu joyni bunday qil", "shu videoni hozir chiqar".
  AI faqat **taklif** qiladi — har biri "Ruxsat berish" tugmasi bilan; bazaga faqat ruxsatdan keyin yoziladi.
- **Gemini kalitlari**: Sozlamalar → Gemini'da bir nechta kalit qo'shiladi. Ssenariy (worker) ham, AI yordamchi ham ularni
  tartib bilan ishlatadi: kalit limiti tugasa (HTTP 429) keyingisi bilan davom etadi; hammasi limitda bo'lsa ish to'xtaydi
  (vazifa "E'tibor kerak" holatiga o'tadi).
- **Til**: qaysi tilda yozsangiz ham mavzu `language = en` bilan saqlanadi — video, subtitr va YouTube title doim inglizcha
  (AI mavzu va tushuntirishni o'zi inglizchaga o'giradi).
- Tezlik: statik qobiq (bir zumda ochiladi), oxirgi ro'yxat keshdan darhol ko'rinadi, tashqi kutubxona va shriftlarsiz.

## Lokal ishga tushirish (brauzerda)

```bash
cd bot
npm install
cp .env.example .env.local   # qiymatlarni to'ldiring
npm run dev                  # http://localhost:3000
```

- `ALLOW_BROWSER_DEV=1` — Telegram'siz brauzerda ochish (faqat lokal).
- `DEMO_MODE=1` — bazasiz namunaviy ma'lumotlar (faqat dizaynni ko'rish; AI haqiqiy Gemini bilan ishlaydi — `GEMINI_API_KEY` kerak).
- Haqiqiy baza: `DATABASE_URL` ga YtAuto Sozlamalar → Online baza'dagi ulanish satrini qo'ying va `DEMO_MODE=0`.

## Baza sxemasi

Sxema — [`db/schema.sql`](db/schema.sql) (desktop ilova ham shu faylni ishlatadi). Versiya o'zgarganda:

```bash
npm run db:migrate
```

Yoki `DB_AUTO_MIGRATE=1` — bot birinchi so'rovda o'zi migratsiya qiladi. Aks holda eski sxemada API tushunarli xato qaytaradi.

## Telegram botga ulash (dizayn tayyor bo'lgach)

1. Deploy (masalan Vercel, Root Directory = `bot`). Environment Variables: `.env.example` dagilar;
   `ALLOW_BROWSER_DEV=0`, `DEMO_MODE=0`. Production manzil: **https://ytauto-kappa.vercel.app** — u exe'ga standart sifatida
   joylangan (`app/Directory.Build.props`), ilova sozlashda hisobni shu bot orqali yaratadi va manzilni so'ramaydi.
2. Botning pastki tugmasini ulash:
   ```bash
   WEBAPP_URL=https://ytauto-kappa.vercel.app node --env-file=.env.local scripts/telegram-setup.mjs
   ```
3. Botni oching → pastdagi **YtAuto** tugmasi → birinchi kirishda YtAuto ilovasidagi tokenni kiriting.

## Xavfsizlik

- Har API so'rovi Telegram `initData` imzosi (HMAC, bot tokeni bilan) tekshiriladi; so'ng Telegram ID token bilan bog'langan hisob
  topiladi va so'rov shu hisob kontekstida (`SET LOCAL ROLE ytauto_bot` + RLS, bitta tranzaksiya) bajariladi.
- `/api/device/register|login` — desktop sozlashi (ochiq, IP bo'yicha cheklangan). Ilova token va parolni o'zi yaratadi,
  botga faqat token xeshi va parol yuboriladi; bot hisob va shu kompyuter uchun alohida Postgres rolini yaratadi.
- Token terish cheklangan (Telegram ID bo'yicha 15 daqiqada 10 urinish). Storage fayllari `acc/<hisob>/` papkalarida.
- Bot tokeni, Gemini kalitlari va baza paroli faqat server muhitida (`.env.local` / Vercel); git'ga tushmaydi.

## YouTube'ni ulash — hamma uchun bitta ilova

Server **umumiy Google OAuth client** bilan ishlaydi (`YOUTUBE_CLIENT_ID` / `YOUTUBE_CLIENT_SECRET`, Vercel env): har bir
foydalanuvchi botda **Sozlamalar → YouTube → «YouTube'ni ulash»** ni bosib, o'z Google hisobini tanlaydi — Google Cloud loyihasi,
Client ID yoki Secret kerak emas. Refresh token shu foydalanuvchi hisobida (`shared_settings.youtube.oauth`, RLS) saqlanadi:
boshqa hisoblar uni ko'rmaydi va ularning kanaliga tegmaydi.

- Umumiy client secret hech kimga berilmaydi: kompyuter (worker) access token'ni `POST /api/device/youtube-token` dan oladi
  (kompyuter hisob tokeni bilan; mehmon vazifasida — aynan o'sha mehmon kanali).
- Hisob o'z OAuth client'ini kiritsa (Sozlamalar → YouTube → Kengaytirilgan), yangi ulanishlar o'shanisi bilan bo'ladi.
  Har token qaysi client bilan olingani saqlanadi (`client: "own" | "global"`), shu bilan yangilanadi.

Google Cloud sozlamasi (bir marta, administrator):

1. **Credentials → Create OAuth client → Web application**; *Authorized redirect URIs*: `https://<bot>/api/youtube/callback`.
   Client ID va Secret'ni Vercel env'ga kiriting va qayta deploy qiling.
2. **APIs & Services → Library**: *YouTube Data API v3* yoqilgan bo'lsin.
3. **Google Auth Platform → Branding**: ilova nomi, support email, logo (`public/logo-120.png`), bosh sahifa
   `https://<bot>/about`, Privacy Policy `https://<bot>/privacy`, Terms `https://<bot>/terms`, *Authorized domains* ga bot
   domeni. Sahifalardagi aloqa emaili — `CONTACT_EMAIL` env.
   Bosh sahifa sifatida ildiz `https://<bot>` ham bo'ladi: oddiy brauzerda (va Google robotida) ochiq bosh sahifa, Telegram ichida
   ilova ochiladi. Domen egaligi: Search Console → *URL prefix* `https://<bot>/` → *HTML tag* → `content` kodini
   `GOOGLE_SITE_VERIFICATION` env'ga yozib deploy qiling → **Verify**; so'ng Google Cloud'da shu Google hisobi loyiha egasi bo'lsin.
4. **Data Access**: `youtube.upload`, `youtube.readonly`, `youtube.force-ssl` scope'lari.
5. **Audience → Publish app** («In production»). Shundan keyin istalgan Gmail ulana oladi va token 7 kunda tugamaydi.
   Google tekshiruvi (verification) tugaguncha foydalanuvchilar «tekshirilmagan ilova» ogohlantirishini ko'radi
   (*Advanced → Go to …*) va jami 100 ta foydalanuvchi chegarasi bor; chegarasiz va ogohlantirishsiz bo'lishi uchun
   **Verification Center** orqali tekshiruvga yuboring (YouTube scope'lari uchun demo video va scope izohi so'raladi).

## Qo'shimcha tokenlar (sxema v3)

Asosiy hisob egasi botda **Sozlamalar → Qo'shimcha tokenlar** bo'limida boshqalar uchun token yaratadi. Token egasi botga shu token
bilan kiradi va asosiy hisob kabi ishlaydi (o'z mavzulari, Gemini kalitlari, promptlari, ovoz profillari, YouTube kanali), lekin:

- **«Hozir chiqarish» yo'q** (UI, AI yordamchi va API'da). Videolar asosiy hisob kompyuterida, **ish vaqtida**, umumiy navbatda
  tayyorlanadi: chiqish vaqti (deadline) eng yaqini birinchi. Kech tayyor bo'lsa YouTube'ga tayyor bo'lgandan 1 soat keyin chiqadi.
- Kompyuter darajasidagi sozlamalar (jadval, Claude, VoiceStudio, render, limitlar) va shablon yuklash — asosiy hisobda.
- Ma'lumotlar RLS bilan ajratilgan: asosiy hisob mehmon videolarini ko'rmaydi, mehmon — asosiynikini.
- Worker mehmon vazifasini mehmon hisobi nomidan bajaradi (`set_config('ytauto_ctx.guest', ...)`; baza faqat shu kompyuter
  mezbon bo'lgan mehmonni qabul qiladi). Mehmonning YouTube kanali ulanmagan bo'lsa video egasining kanaliga **yuklanmaydi**.
- Qo'shimcha token bilan YtAuto ilovasini (kompyuterni) ulab bo'lmaydi. Tokenni o'chirish shu hisob ma'lumotlarini ham o'chiradi.

Yangilash: `npm run db:migrate` (sxema v2 → v3), so'ng botni deploy qiling va kompyuterlardagi YtAuto'ni yangilang.

## Kanal konteksti (sxema v4)

YouTube kanali ulangach bot kanalni o'zi o'rganadi (fonda, 1–3 daqiqa) va **«Kanal»** bo'limida ko'rsatadi:

- **Nima o'rganiladi:** kanal nomi va tavsifi, so'nggi 30 ta sarlavha va **eng oxirgi 3 video**. Har video Gemini'ga YouTube
  havolasi bilan beriladi: u montajni, ovozni, subtitrni va ssenariyni bevosita ko'rib chiqadi. Video ochiq bo'lmasa
  (rejalashtirilgan/private), faqat sarlavha, tavsif va teglar ishlatiladi. Faqat `youtube.readonly` ruxsati kerak.
- **Natija:** `ytauto.channel_profile` (to'liq tahlil) va `shared_settings['channel.context']`. Ikkinchisi promptlarga
  qo'shiladigan ixcham bloklar va kanal ovoziga mos ovoz profili.
- **Qayerda ishlatiladi:**
  - Gemini ssenariy promptidagi `{channel_context}` o'rniga qo'yiladi; placeholder bo'lmasa, prompt oxiriga qo'shiladi.
  - Claude `instructions.md`'ga «Channel style» bo'limi qo'shiladi.
  - Yangi mavzuning ovozi tanlanmagan bo'lsa, tavsiya etilgan ovoz profili olinadi.
  - AI yordamchi ham kontekstni ko'radi.
  - Botda o'chirib qo'yish mumkin.
- **Yangilanish:**
  - Video YouTube'da public bo'lganda worker `channel.context.stale` belgisini qo'yadi. Bot keyingi ochilishda yoki kunlik
    cron'da kanalni qayta o'rganadi.
  - Bundan tashqari 12 soatda bir marta arzon tekshiruv bo'ladi: eng yangi video o'zgarmagan bo'lsa Gemini chaqirilmaydi.
- **Kunlik tavsiyalar:** har kuni kontekstdan 5 ta yangi mavzu taklif qilinadi. «Navbatga qo'shish» uni oddiy mavzu sifatida
  qo'shadi, rad etilganlari qayta taklif qilinmaydi.
- **Cron:** `bot/vercel.json` → `/api/cron/daily` (har kuni 03:00 UTC). Vercel'da `CRON_SECRET` (kamida 16 belgi) kiritilishi shart.

Yangilash tartibi:

1. `npm run db:migrate` (sxema v3 → v4).
2. Vercel'ga `CRON_SECRET` qo'shib, botni deploy qiling.
3. Kompyuterlardagi YtAuto'ni yangilang.

Ilova yangilanmaguncha kontekst faqat AI yordamchi va mavzu tavsiyalarida ishlatiladi.

## Claude Code orqali demo video — MCP (sxema v5)

Bot MCP server ham: **`/api/mcp`** (Streamable HTTP, holatsiz). Claude Code foydalanuvchi loyihasini o'rganadi, screenshot va qisqa
action videolarni oladi va YtAuto'ga yuboradi. Ssenariy, ovoz, subtitr, montaj va render — **YtAuto ichida** (mavjud pipeline).

**Ulash** — botda **Sozlamalar → Claude Code ulash (MCP)** → «Token yaratish» → «Matnni nusxalash» → matnni Claude Code'ga joylang.
Claude o'zi ulanadi (`claude mcp add --scope user ...` - token loyiha fayllariga yozilmaydi); yangi tool'lar ko'rinishi uchun sessiyani
qayta oching yoki `/mcp`. Matnda server URL, alohida **MCP tokeni** (`ytm_...`) va buyruq bor:

```bash
claude mcp add --transport http --scope user ytauto https://ytauto-kappa.vercel.app/api/mcp --header "Authorization: Bearer ytm_..."
```

MCP tokeni faqat `/api/mcp` ga kiradi (botga kirish va kompyuterni ulash uchun yaroqsiz), hisobga bittadan, faqat yaratilganda
ko'rsatiladi; «Yangi token» eskisini, «Bekor qilish» - darhol o'chiradi. Asosiy/qo'shimcha hisob tokeni (`yt_...`) ham MCP uchun ishlaydi.

So'ng Claude Code'ga: *«Shu loyihaning demo videosini YtAuto orqali yarat»* (chiqarish kerak bo'lsa — aniq ayting).

| Tool | Vazifasi |
|---|---|
| `request_media_upload` | Har screenshot/video uchun bir martalik Storage havolasi (PUT, 2 soat). Katta fayllar MCP orqali o'tmaydi. Screenshot ≤ 25 MB, video ≤ 500 MB, ko'pi bilan 30 ta. |
| `create_demo_video` | Loyiha konteksti + materiallar (har birida nima ko'rinishi, zoom joyi) + talablar (til, davomiylik, auditoriya, ohang, format, `effects` — montaj effektlari ro'yxati, bo'sh bo'lsa aralash). Darhol `job_id` qaytaradi — ish asinxron. |
| `get_video_status` | Bosqichlar (script → voice → align → edit → render → validate → archive), foiz, navbatdagi o'rni, kompyuter online'mi, oxirgi loglar. |
| `get_video_result` | Title, tavsif, davomiylik, MP4 / muqova / SRT uchun 24 soatlik havolalar, kompyuterdagi arxiv yo'li, YouTube havolasi. |
| `publish_video` | Faqat foydalanuvchi aniq buyurganda (`confirm: true`) — **faqat asosiy hisob YouTube kanaliga**. Tayyor fayl yuklanadi (qayta render qilinmaydi). |

Qanday ishlaydi:

1. `create_demo_video` → `ytauto.jobs` da `kind = 'demo'` vazifa (`demo_spec`: loyiha, materiallarning imzolangan havolalari, talablar).
   Ovoz — botda tanlangan profil (bo'lmasa standart), shablon — bot standarti.
2. Kompyuterdagi worker (`--poll`, ~2 daqiqada) demo navbatini jadvalni kutmasdan, **bittadan** bajaradi; disk/RAM yetmasa navbatda kutadi.
   Navbat bazada — kompyuter o'chib-yonsa ham yo'qolmaydi, uzilgan vazifa tayyor bosqichidan davom etadi.
3. Ssenariy — Gemini, 2 bosqich: (1) materiallarni ko'rib (vision) sahnalar qoralamasi; (2) faqat ovozda aytiladigan matn — sahna
   ko'rsatmalari, takrorlar va ortiqcha gaplar olib tashlanadi. Sahnalar (qaysi materialda nima aytiladi, zoom joyi) alohida metadata.
4. Ovoz (VoiceStudio) → subtitr vaqtlari (faster-whisper) → montaj (Claude + OpenFilm: faqat loyihaning o'z screenshot/videolari,
   to'liq kadrda — kanal shabloni, bezak fonlar, mockup va stok media yo'q; kamera harakatlari, o'tishlar, ajratib ko'rsatish,
   kinetik matn, ritm effektlari (`DemoEffects.cs` katalogi), fon musiqasi, SFX) → render → sifat tekshiruvi → arxiv.
5. Natija (MP4, muqova, SRT) bot orqali Storage'ga (`acc/<hisob>/demo-result/`) yuboriladi, vazifa **«Tayyor»** (holat 15) bo'ladi.
   Chiqarish so'ralgan bo'lsa — asosiy kanalga yuklanadi; mehmon (qo'shimcha token) hisoblari uchun MCP orqali chiqarish yo'q.

Xavfsizlik: har MCP chaqiruvi token hisobi kontekstida (RLS); noto'g'ri token IP bo'yicha cheklanadi; Storage yo'llari faqat
server tomonida, hisob papkasida quriladi. Worker natijani o'z tokeni bilan `/api/device/demo-result` orqali yuklaydi.

Yangilash: `npm run db:migrate` (v4 → v5), botni deploy qiling, kompyuterlardagi YtAuto'ni yangilang.

### Mavzular va YouTube videolari — MCP orqali

Claude Code oddiy video navbatini ham boshqaradi (bot UI bilan bir xil `applyAction` tekshiruvlari) va asosiy kanaldagi videolarni
tahrirlaydi/o'chiradi. Masalan: *«Kanalim uchun kiberxavfsizlik haqida 30 ta mavzu qo'sh, har biriga batafsil izoh yoz»*.

| Tool | Vazifasi |
|---|---|
| `get_topic_options` | Vaqt zonasi, standart chiqish vaqti, shablonlar, ovozlar, formatlar, fon musiqasi, standartlar. |
| `list_topics` | Navbat: chiqmaganlar + oxirgi 30 ta chiqqan (`filter`: pending / published / problems). |
| `add_topics` | **Bir chaqiruvda 40 tagacha** mavzu; har birining qismlarini Claude beradi: `topic`, `notes`, `publish_at`, `template`, `format`, `voice`, `music`, ixtiyoriy `title` + `script` (Gemini o'tkazib yuboriladi). Hammasi yoki hech biri: bitta xato bo'lsa hech narsa qo'shilmaydi. |
| `update_topics` | 40 tagacha mavzuni o'zgartiradi (faqat berilgan maydonlar); video hali tayyorlanmagan mavzular. |
| `delete_topics` | 40 tagacha mavzuni o'chiradi (`confirm: true`); tayyorlangan video bo'lsa o'chirilmaydi. |
| `get_youtube_channel` | Kanal nomi, havolasi, **logotipi (YouTube'dan)**, banner, obunachi/video/ko'rish soni. |
| `list_youtube_videos` | Kanal videolari (yangi → eski, sahifalab), kanal logotipi bilan; YtAuto mavzusi raqami bog'lanadi. |
| `get_youtube_video` | Bitta video: to'liq tavsif, teglar, kategoriya, holat, reja vaqti. |
| `update_youtube_video` | Title, tavsif, teglar, kategoriya, privacy, `publish_at` (reja), made for kids — faqat berilganlari o'zgaradi. |
| `delete_youtube_video` | Videoni **butunlay** o'chiradi (`confirm: true`, qaytarib bo'lmaydi); faqat ulangan kanalning videosi. |

YouTube tool'lari faqat asosiy hisob tokeni bilan ishlaydi. Tahrirlash/o'chirish uchun `youtube.force-ssl` ruxsati kerak — bu
o'zgarishdan oldin ulangan kanallar uchun botda **Sozlamalar → YouTube → kanalni qayta ulang** (aks holda tool shuni aytadi).

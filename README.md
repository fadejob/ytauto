# ytauto

| Papka | Nima |
|---|---|
| [`app/`](app/) | **YtAuto** — Windows desktop ilova va worker (C# / .NET 10): mavzudan YouTube videogacha. Batafsil: [app/README.md](app/README.md) |
| [`bot/`](bot/) | **Telegram web app** (Next.js): mavzular, "Hozir chiqarish", promptlar va Groq AI yordamchi — Supabase orqali. Batafsil: [bot/README.md](bot/README.md) |
| [`library/`](library/) | **Rasm kutubxonasi zavodi** (Python): Wikimedia/NASA tanlangan fotolari, fondan ajratilgan PNG'lar, Poly Haven 3D renderlari — litsenziyasi tekshirilgan holda `ytauto.assets` ga. Worker har sahnaga shu yerdan qidiradi. Batafsil: [library/README.md](library/README.md) |
| [`bot/` → `/api/mcp`](bot/README.md#claude-code-orqali-demo-video--mcp-sxema-v5) | **MCP server**: Claude Code loyihani o'rganib, screenshot/action videolarni yuboradi — YtAuto ulardan demo video yaratadi (ssenariy, ovoz, montaj, render) va so'ralsa asosiy YouTube kanaliga chiqaradi. |

Ikkalasi bitta online bazadan (Supabase PostgreSQL, `ytauto` sxemasi) foydalanadi. Baza **ko'p foydalanuvchili**: sxema —
[`bot/db/schema.sql`](bot/db/schema.sql) (yagona manba; exe ichiga ham joylanadi).

## Ko'p foydalanuvchili oqim

1. Foydalanuvchi `YtAuto.exe` ni yuklab ochadi → papkani tanlaydi → exe o'sha yerga o'rnatiladi va ochiladi.
2. Bitta tugma — **«Kerakli fayllarni yuklash va sozlash»**: OpenFilm, VoiceStudio, subtitr/ovoz modellari, Claude Code
   (bori qayta yuklanmaydi) → hisob yaratiladi → sozlamalar serverga → Task Scheduler (kunlik ish + kunduzi har 2 daqiqada so'rov almashish).
3. Ilova **token** ko'rsatadi (nusxalash tugmasi). Foydalanuvchi botni ochadi — birinchi kirishda token so'raladi.
4. Gemini kaliti, YouTube, promptlar va mavzular — botda. Gemini kaliti bo'lmasa mavzu qo'shib bo'lmaydi, AI yordamchi o'chiq,
   mavjud mavzular muzlatilgan (worker video tayyorlamaydi).

Izolyatsiya baza darajasida (Row Level Security): har kompyuter o'z Postgres roli bilan ulanadi, bot har so'rovni token bilan
bog'langan hisob kontekstida bajaradi — bir foydalanuvchi boshqasining ma'lumotini ko'ra olmaydi va o'zgartira olmaydi.
Token o'zi bazada saqlanmaydi (faqat SHA-256 xeshi); ilovada «Tokenni yangilash» eski token bilan kirganlarni chiqarib yuboradi.

**Yangilash (v1 → v2)**: avval yangi `YtAuto.exe`ni o'rnating, so'ng `bot` papkasida `npm run db:migrate`. Eski ma'lumotlar
"legacy" hisobga o'tadi; eski ulanish satri saqlangan kompyuterda «Sozlash» ularni shu kompyuter hisobiga oladi.
`app/` o'zgarganda GitHub Actions exe yig'adi va **Releases** ga chiqaradi.

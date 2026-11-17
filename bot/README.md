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

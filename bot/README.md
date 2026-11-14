# YtAuto bot — Telegram web app

Mobil uchun Next.js web app. YtAuto online bazasi (Supabase PostgreSQL, `ytauto` sxemasi) bilan to'g'ridan-to'g'ri ishlaydi —
desktop ilova ochiq bo'lishi shart emas.

## Imkoniyatlar

- **Mavzular**: ro'yxat (Navbatda / Jarayonda / Chiqqan), bir vaqtda 5 tagacha mavzu qo'shish, tahrirlash, o'chirish.
  Tahrirlash va o'chirish **faqat video hali tayyorlanmagan** bo'lsa mumkin (worker band qilmagan, arxivda video yo'q, YouTube'ga yuklanmagan).
  Chiqish sanasi bo'sh bo'lsa — oxirgi rejadan +2 kun (desktop ilovadagi qoida).
- **⚡ Hozir chiqarish**: `publish_now` bayrog'i. Internetga ulangan, jadvali yoqilgan kompyuterdagi worker uni ~2 daqiqada oladi
  (Task Scheduler → "YtAuto Remote Trigger"), videoni tayyorlaydi yoki tayyorini oladi va YouTube'ga darhol chiqaradi.
- **Promptlar**: Gemini ssenariy va Claude montaj promptlarini tahrirlash (barcha kompyuterlar uchun umumiy).
- **AI yordamchi (Groq)**: "kosmos haqida 3 ta mavzu qo'sh", "promptda shu joyni bunday qil", "shu videoni hozir chiqar".
  AI faqat **taklif** qiladi — har biri "Ruxsat berish" tugmasi bilan; bazaga faqat ruxsatdan keyin yoziladi.
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
- `DEMO_MODE=1` — bazasiz namunaviy ma'lumotlar (faqat dizaynni ko'rish; AI haqiqiy Groq bilan ishlaydi).
- Haqiqiy baza: `DATABASE_URL` ga YtAuto Sozlamalar → Online baza'dagi ulanish satrini qo'ying va `DEMO_MODE=0`.

## Telegram botga ulash (dizayn tayyor bo'lgach)

1. Deploy (masalan Vercel, Root Directory = `bot`). Environment Variables: `.env.example` dagilar;
   `ALLOW_BROWSER_DEV=0`, `DEMO_MODE=0`, `ALLOWED_TELEGRAM_IDS` — o'zingizning Telegram ID'ingiz.
2. Botning pastki tugmasini ulash:
   ```bash
   WEBAPP_URL=https://sizning-domen.vercel.app node --env-file=.env.local scripts/telegram-setup.mjs
   ```
3. Botni oching → pastdagi **YtAuto** tugmasi. Telegram ID'ingiz ruxsat ro'yxatida bo'lmasa, ilova uni ko'rsatadi.

## Xavfsizlik

- Har API so'rovi Telegram `initData` imzosi (HMAC, bot tokeni bilan) va `ALLOWED_TELEGRAM_IDS` bo'yicha tekshiriladi.
- Bot tokeni, Groq kaliti va baza paroli faqat server muhitida (`.env.local` / Vercel); git'ga tushmaydi.

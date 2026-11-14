# YtAuto — real sinov natijalari (2026-11-14)

Kompyuter: Windows 11 Pro, RAM 7.7 GB (sinov paytida bo'sh 0.5–1.6 GB), faqat CPU. C: diskda haqiqatda ~2.7 GB bo'sh, D: da ~177 GB.
O'rnatilgan versiyalar (qayta tekshirildi): VoiceStudio 0.5.7 (API 3900), OpenFilm 0.3.2 bundled CLI, ffmpeg 9.0.2 (OpenFilm ichida),
faster-whisper 1.2.1 + `Systran/faster-whisper-base`, Claude Code 2.1.310. Artefaktlar: [test-artifacts/](test-artifacts/).

## 1. Lokal zanjir (haqiqiy dasturlar bilan)

`YtAuto.Worker.exe --selftest-local` — alohida ma'lumotlar papkasida; Gemini o'rniga tayyor inglizcha ssenariy (80 so'z, 5 sahna),
YouTube'siz. Qolgan hamma narsa haqiqiy: VoiceStudio TTS (KittenTTS mini), faster-whisper, yangi `claude -p` sessiyasi, OpenFilm render, QA, arxiv.

| Bosqich | Vaqt | O'lchangan resurs / natija |
|---|---|---|
| VoiceStudio ishga tushishi (worker o'zi ochdi) | 112.6 s | `/health` → v0.5.7 |
| TTS (430 belgi → 41.42 s audio) | 200.8 s | Job peak **4616 MB**; peak −2.2 dB, RMS −19.3 dB, nutq ulushi 68% |
| VoiceStudio'ni yopish | ~2 s | `WM_CLOSE` 1 oynaga — majburan to'xtatish kerak bo'lmadi; qochgan jarayon 0; bo'sh RAM 0.86 → 1.68 GB |
| Alignment (faster-whisper base, CPU int8) | 37.6 s | Peak 1191 MB, CPU 46.8 s; asl matn bilan o'xshashlik 98.1%, so'zlar 100% mos; 17 subtitr cue. Farq yashirilmadi: ASR "o'clock" ni "o" + "clock" deb bo'lgan |
| Claude montaji (yangi sessiya) | 106.3 s | Peak 717 MB, $0.29; sessiyadan qolgan 2 child jarayon (OpenFilm Studio) avtomatik yopildi |
| `openfilm look` (worker) | 17.8 s | exit 0 |
| OpenFilm render (1 renderer, 30 fps, −14 LUFS, subtitrlar kuydirilgan) | 128.4 s | Peak 752 MB, CPU 158 s |
| QA | 16.2 s | Xato va ogohlantirish 0: birinchi kadr 0.00 s, vizual qamrov 41.43 s = audio, video 41.433 s / audio 41.421 s, −14.0 LUFS, max −2.0 dB, `look --full` toza |
| Arxiv | 0.4 s | MP4/WAV/SRT/VTT/JPG/JSON, SHA-256 bilan tekshirildi |
| **Jami** | **10.7 daqiqa** | Ishdan keyin worker ochgan jarayonlardan **0** ta qoldi |

Natija: 1920×1080 H.264 + AAC, 41.43 s, 7.6 MB — `test-artifacts/selftest-2026-11-14/*.mp4`, kadrlar: `contact-sheet.png`.
Kadrlarni ko'zdan kechirish: matn kesilmagan, sahnalar mavzuni tushuntiradi, subtitr pastki qismda o'qiladi (~40 px, 1080p).

Oldingi sinovdagi kamchilik (sahnalar orasida 0.7 s qora va oxirida 0.27 s qora) endi QA qoidasi bilan ushlanadi —
o'sha `film.html` ustida unit test: `gap` (4.46 s) va `ends_early` (14.00 s) xatolari. Yangi zanjirda bunday bo'shliq bo'lmadi.

## 2. Uzilish va davom ettirish (real)

1. Vazifa render bosqichiga qaytarildi, worker ishga tushirildi; render paytida OpenFilm katalogidan 6 ta jarayon ishlayotgan edi.
2. **Faqat worker jarayoni** majburan o'ldirildi (daraxt emas) → 3 s dan keyin OpenFilm/headless brauzer jarayonlari: **0**
   (Job Object `KILL_ON_JOB_CLOSE` — yetim jarayon qolmadi).
3. Qayta ishga tushirish: "Oldingi ishga tushish Rendering bosqichida uzilib qolgan" (urinish 2) →
   Script, Voice, Align, Edit **o'tkazib yuborildi** (TTS ham, Claude ham chaqirilmadi) → Render + QA + Arxiv: **2.9 daqiqa**.
4. Qayta render bit-ma-bit bir xil MP4 berdi (OpenFilm render deterministik); arxivda dublikat yaratilmadi.

## 3. Windows Task Scheduler (haqiqiy foydalanuvchi konteksti)

| Tekshiruv | Natija |
|---|---|
| Vazifani ikki marta ro'yxatdan o'tkazish | Task Scheduler'da bitta `YtAuto Daily Worker` (dublikat yo'q) |
| Parametrlar | Daily 01:00 (`+05:00`), Interactive only, `StartWhenAvailable`, `IgnoreNew`, aniq exe, `--scheduled`, working directory |
| Planirovshchik orqali ishga tushirish (navbat bo'sh) | Last Result 0; log: `Worker boshlandi (scheduled)` → `Navbatda bajariladigan vazifa yo'q. Og'ir dasturlar ochilmaydi` (66 ms) |
| OS mutex | Boshqa nusxa ushlab turganda ikkinchi worker exit **3**, hech narsa qilmadi |
| Integratsiya tekshiruvi + Claude autentifikatsiyasi planirovshchik ostida | Hammasi OK (Claude javobi 11 s); faqat Gemini kaliti va YouTube ulanmagan — `test-artifacts/check-from-task-scheduler.txt` |

Oldingi hisobotdagi ochiq savol — **mustaqil worker'da Claude autentifikatsiyasi** — Task Scheduler kontekstida tasdiqlandi.

Muhim topilma: Claude desktop ilovasi MSIX paket. Uning ichidan ishga tushgan jarayonlarning `%APPDATA%`/`%LOCALAPPDATA%` yozuvlari
paketning virtual `LocalCache` papkasiga tushadi. Shu sababli oldingi sinovdagi `%APPDATA%\npm\claude.cmd` haqiqiy profilda yo'q.
YtAuto Claude'ni avtomatik topadi (PATH → `~/.local/bin` → `%APPDATA%\npm` → `Packages\Claude_*\LocalCache\Roaming\npm` →
Claude ilovasidagi eng yangi `claude-code\<versiya>`), sozlangan yo'l yo'qolsa ham har safar qayta topadi.
Xuddi shu sababdan C: diskdagi haqiqiy bo'sh joy (2.7 GB) sandbox ko'rsatganidan kam edi — standart ish/arxiv papkalari
endi eng ko'p bo'sh joyli diskka (`D:\YtAuto\...`) qo'yiladi.

Sinovdan keyin `YtAuto Daily Worker` vazifasi **o'chirildi** (kompyuter boshlang'ich holatda). Sozlab bo'lgach, jadvalni UI'dan yoqing.

## 4. Qaytariladigan testlar

`dotnet test tests\YtAuto.Tests` — **60/60 o'tdi** (9 s). Asosiylari:

- Soxta adapterlar bilan to'liq pipeline: to'liq ishga tushish → `Scheduled`, arxiv va records saqlanadi, `jobs` tozalanadi;
  bitta ishga tushishda bitta vazifa; bo'sh navbatda TTS ochilmaydi; xatodan keyin tayyor bosqichlar takrorlanmaydi;
  kompyuter o'chib qolgandek holatdan davom etish; QA xatosi → Claude'ga qaytish (TTS bir marta); tuzatish limiti → `NeedsAttention`
  va upload yo'q; upload uzilishi → sessiya davom ettiriladi, qayta render/qayta upload yo'q; noaniq upload → `NeedsAttention`,
  yangi upload yo'q; public vaqti o'tgan → arxivlanadi, chiqarilmaydi, yangi vaqtdan keyin faqat upload; urinish limiti → `NeedsAttention`
  va navbat davom etadi; snapshot ishlayotgan vazifani sozlama o'zgarishidan himoya qiladi; bekor qilish; noto'g'ri sozlama Gemini'ni chaqirmasdan to'xtatadi.
- SQLite: eng eski vazifa birinchi, backoff, 8 parallel ulanishda 20 vazifa — dublikatsiz band qilish, eskirgan band qilishlarni bo'shatish.
- Jarayonlar: `start /b` bilan qoldirilgan child o'ldiriladi; timeout butun daraxtni to'xtatadi; `GEMINI_API_KEY` child'ga o'tmaydi; argumentlarni qo'shtirnoqlash.
- Fayllar: junction orqali arxivga yo'l bo'lsa ham tozalash arxivga tegmaydi; ildizdan tashqariga chiqish rad etiladi; SHA-256 bilan nusxa.
- QA, alignment, subtitr, ssenariy validatori, sana qoidasi, Task Scheduler XML, log redaktori.

## 5. Sinovdan o'tkazilmagan (va'da qilinmaydi)

- **Gemini** haqiqiy chaqiruvi — API kaliti kiritilmagan. Kod REST `generateContent` + `responseSchema` + Google Search grounding'ga mos yozilgan, lekin real javob bilan sinalmagan.
- **YouTube** haqiqiy upload — sizning alohida ruxsatingiz va OAuth client kerak. Resumable/davom ettirish mantiqi faqat soxta uploader bilan sinalgan.
- Ekran **qulflangan** holat va **tizimga kirilmagan** holat — sinalmadi (ekranni qulflash sizning ishingizni to'xtatgan bo'lardi).
  Vazifa `InteractiveToken`: faqat siz kirgan sessiyada ishlaydi; tizimga kirilmaganda ishlamaydi.
- Uyqudan uyg'otish (WakeToRun) — qurilma va quvvat sxemasiga bog'liq, sinalmadi.
- 3–5 daqiqali video — faqat 41 s sinaldi. Shu kompyuterda chiziqli baho (o'lchov emas): TTS ≈ 4.8 s har audio soniyasiga
  (3 daqiqa → ~15 daqiqa), render ≈ 3.1× real vaqt (3 daqiqa → ~9–10 daqiqa).
- OpenFilm shablon papkasini nusxalash va MP4 reference kadrlari — kod va unit darajada bor, real shablon bilan sinalmadi.
- Desktop UI: ishga tushishi, 4 sahifa va klaviatura bilan navigatsiya tekshirildi; tafsilotlar oynasi real ma'lumot bilan qo'lda sinalmadi.

## 5b. Qo'shimcha real tekshiruvlar (2026-11-14, kunduzi)

- YouTube kanali ulandi (OAuth, Testing rejimi). `videoCategories.list` (UZ) — 14 ta kategoriya o'qildi, UI'da "Ta'lim" nomi bilan tanlangan.
- VoiceStudio katalogi: 18 ta TTS dvigateli o'qildi, shu kompyuterda ishlatish mumkini — 1 (KittenTTS, 8 ovoz). Birinchi `/engines/tts`
  chaqiruvi 60 s dan uzoq davom etdi — timeout 5 daqiqaga oshirildi; o'qishdan keyin VoiceStudio jarayonlari 0 ta qoldi (≈99 s).
- Noutbuk batareyada bo'lganda standart sozlamali Task Scheduler vazifalari "Queued" holatida qoladi. YtAuto'ning kunlik vazifasida
  `DisallowStartIfOnBatteries=false` va `StopIfGoingOnBatteries=false` — batareyada ham ishga tushadi.
- Qaytariladigan testlar: 65/65.

## 6. Cheklovlar

- **O'zbek tilida ovoz**: o'rnatilgan KittenTTS faqat inglizcha. O'zbek tilini qo'llaydigan OmniVoice o'rnatilmagan va litsenziyasi
  non-commercial. faster-whisper **base** o'zbek tilida zaifroq — alignment o'xshashligi pasayishi mumkin (55% dan past bo'lsa vazifa `NeedsAttention`).
- **RAM**: VoiceStudio TTS paytida 4.6 GB ishlatdi (7.7 GB kompyuterda). Hard limit qo'yilmaydi; bosqichlar ketma-ket va VoiceStudio TTS'dan keyin darhol yopiladi.
  Sinov paytida VoiceStudio yiqilmadi; agar yiqilsa, worker `/health` orqali aniqlab, o'zi ochgan nusxani qayta ishga tushiradi.
- Subtitr uslubi — OpenFilm `--subtitles` standarti (o'lcham/rangni CLI orqali boshqarib bo'lmaydi).
- "Matn ekrandan kesilmaydi" — avtomatik OCR yo'q: Claude `openfilm look` kadrlarini ko'rib tekshiradi, worker esa bo'sh/oq/qora kadrlarni va timeline'ni tekshiradi.
- Jadval vaqt zonasi offset bilan yoziladi; yozgi vaqt o'tadigan zona tanlansa, o'tishdan keyin jadvalni qayta saqlash kerak (Asia/Tashkent'da yozgi vaqt yo'q).
- `containsSyntheticMedia` standart yoqilgan (YouTube'da AI kontent belgisi) — Sozlamalarda o'zgartirish mumkin.
- Dev muhit: .NET 10 SDK `D:\dotnet-sdk` ga o'rnatildi (faqat yig'ish uchun; distributivga kerak emas).

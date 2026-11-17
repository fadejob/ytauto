# YtAuto — mavzudan YouTube videogacha

Windows uchun yengil desktop ilova: navbatdagi mavzu bo'yicha ssenariy (Gemini), ovoz (VoiceStudio), so'z darajasidagi subtitr
(faster-whisper), montaj (Claude Code + OpenFilm), render va sifat tekshiruvi, arxiv va YouTube'ga **rejalashtirilgan** upload.
Ish Windows Task Scheduler orqali, foydalanuvchi aralashuvisiz: jadval vaqtida bazadan mavzular sinxronlanadi va navbatga qarab
navbatdagi **barcha mavzulardan** video tayyorlanadi (chiqish vaqti yaqinlari birinchi; ish oynasi tugasa qolgani keyingi kechaga). Chiqish vaqti faqat YouTube uchun: video private + publishAt bilan yuklanadi, YouTube vaqti kelganda o'zi public qiladi. Online bazadan **"Hozir chiqarish"** buyrug'i ham qabul qilinadi.

- `YtAuto.exe` — boshqaruv oynasi (WPF). Ochilishi video tayyorlashni boshlamaydi.
- `YtAuto.Worker.exe` — oynasiz worker. Jadval bo'yicha ishga tushadi, vazifalarni bajaradi yoki davom ettiradi va chiqadi.

Tayyor exe: GitHub → **Releases** (har `main` ga push'da avtomatik build, [`.github/workflows/build.yml`](.github/workflows/build.yml)).

Real sinov natijalari va o'lchovlar: [docs/TEST-REPORT.md](docs/TEST-REPORT.md).

## Arxitektura

| Loyiha | Vazifasi |
|---|---|
| `src/YtAuto.Core` | Holatlar va state machine, pipeline orkestratori (`JobRunner`), sana qoidasi, ssenariy/subtitr/QA qoidalari, xavfsiz fayl amallari. Tashqi dasturlarga bog'liq emas. |
| `src/YtAuto.Infrastructure` | SQLite (WAL), DPAPI sirlar, Win32 Job Object bilan jarayonlar, Gemini/VoiceStudio/faster-whisper/Claude/OpenFilm/ffmpeg/YouTube adapterlari, Task Scheduler. |
| `src/YtAuto.Worker` | Oynasiz exe: OS mutex, navbatdan bitta vazifa, `--check`, `--selftest-local`. |
| `src/YtAuto.Desktop` | WPF + MVVM interfeys. Biznes mantiq yo'q — Core/Infrastructure'ni chaqiradi. |
| `tests/YtAuto.Tests` | 60 ta qaytariladigan test (soxta adapterlar bilan to'liq pipeline, SQLite, jarayonlar, QA). |

Ma'lumot oqimi:
1. **Gemini** JSON qaytaradi: `subtitle_text` (videoning to'liq matni) va `youtube_title` (+ ixtiyoriy `youtube_description`, `tags`).
2. **Title** vazifada saqlanadi va YouTube'ga chiqarishda ishlatiladi.
3. **Ovoz** (VoiceStudio) `subtitle_text` dan yaratiladi; faster-whisper so'z vaqtlarini olib, aynan shu matndan vaqtli subtitr (VTT/SRT) tuziladi.
4. **Claude**'ga yuboriladi: saqlangan Claude prompti + subtitr (matn va vaqtlari) + ovoz + shablon (bo'lsa). Sahnalarni Claude subtitr ma'nosiga qarab o'zi bo'ladi.

Pipeline (og'ir bosqichlar ketma-ket, har biridan keyin checkpoint):
`Gemini → VoiceStudio (TTS) → VoiceStudio'ni yopish → alignment → alignment'ni yopish → Claude montaji → Claude'ni tugatish → OpenFilm render + QA → OpenFilm'ni yopish → arxiv → upload → worker tugaydi`.

## Talablar

- Windows 10/11 x64. .NET o'rnatish **shart emas** (distributiv self-contained).
- **VoiceStudio** (0.5.7 da sinalgan), unda TTS modeli o'rnatilgan (masalan, `KittenML/kitten-tts-mini-0.8`, faqat inglizcha)
  va alignment uchun `Systran/faster-whisper-base` keshda bo'lishi kerak. Worker VoiceStudio'ning o'z Python muhitidan foydalanadi.
- **OpenFilm** (0.3.2 da sinalgan). Ichidagi CLI, ffmpeg/ffprobe va headless brauzer ishlatiladi; `npx` ishlatilmaydi.
- **Claude Code** (2.1.310 da sinalgan), shu Windows foydalanuvchisi tizimga kirgan bo'lishi kerak.
- **Gemini API** kaliti.
- YouTube uchun Google Cloud loyihasi: *YouTube Data API v3* yoqilgan va *Desktop app* turidagi OAuth client.
- Tavsiya: kamida 8 GB RAM. Sinovdagi kompyuterda (7.7 GB) VoiceStudio TTS paytida ~4.6 GB ishlatdi.

## O'rnatish

1. `YtAuto-win-x64.zip` ni masalan `C:\Programs\YtAuto` ga oching (yo'lda bo'sh joy bo'lishi mumkin).
2. `YtAuto.exe` ni oching.

Ma'lumotlar: `%LOCALAPPDATA%\YtAuto\` (baza `ytauto.db`, `logs\`, `records\job-N\`, `secrets\` — DPAPI bilan shifrlangan).

Manba koddan yig'ish (.NET 10 SDK bilan):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

**Avtomatik build (CI)**: `main` ga har push'da GitHub Actions (`windows-latest`, .NET 10) testlarni ishga tushiradi, `publish.ps1`
bilan self-contained `YtAuto-win-x64.zip` yig'adi va uni `v1.0.<build raqami>` nomli GitHub Release sifatida chiqaradi
(zip workflow artifact sifatida ham 30 kun saqlanadi). Pull request'larda faqat build va testlar.

## Birinchi sozlash

Ilova birinchi ochilganda bitta tugma: **«Kerakli fayllarni yuklash va sozlash»** — yetishmagan dasturlar va modellar yuklanadi
(OpenFilm, VoiceStudio, faster-whisper, KittenTTS va ayol/erkak ovoz profillari, Claude Code; bori qayta yuklanmaydi), online hisob
va token yaratiladi, sozlamalar serverga yoziladi va Task Scheduler sozlanadi. So'ng bosh sahifada token (nusxalash, ko'rsatish,
yangilash), ish vaqti (jadval), Gemini va dasturlar holati. Claude Code'ga kirish — «Claude'ga kirish» tugmasi (bir marta).
Gemini kaliti, YouTube, promptlar va mavzular — Telegram botda. Quyidagilar **Kengaytirilgan sozlamalar** oynasida:

1. **Umumiy** — video va ovoz arxiv papkalari (doimiy), vaqtinchalik ish papkasi (`jobs`). Arxiv ish papkasi ichida bo'lmasligi kerak.
   Tizim diskida 20 GB dan kam joy bo'lsa, standart papkalar eng ko'p bo'sh joyli diskka qo'yiladi (sinovdagi kompyuterda `D:\YtAuto\...`).
2. **Gemini** — API kaliti (DPAPI'da saqlanadi), model (standart `gemini-2.5-flash`), ssenariy prompti.
   "Google Search bilan fakt varag'i" yoqilsa, hozirgi faktlar uchun avval qidiruv bilan fakt varag'i olinadi; tavsifdagi manbalar faqat
   API qaytargan haqiqiy havolalardan olinadi. Qidiruv ishlamasa, manbalar "tasdiqlangan" deb ko'rsatilmaydi.
3. **Claude** — `claude.exe` yo'li **avtomatik topiladi** (PATH, `~/.local/bin`, `%APPDATA%\npm`, Claude desktop ilovasining
   `Packages\Claude_*\LocalCache\Roaming\npm` va `Claude-Data\…\claude-code\<versiya>` nusxalari); yo'l yo'qolsa (ilova yangilansa) qayta topiladi.
   Montaj prompti, timeout, ixtiyoriy model va byudjet.
4. **Ovoz** — VoiceStudio yo'li, API manzili (`http://127.0.0.1:3900`), Python yo'li, ovoz profillari (model/voice).
5. **OpenFilm va shablonlar** — `OpenFilm.exe` yo'li, parallel renderer soni (8 GB RAM uchun 1), shablonlar:
   - `OpenFilmProject` — tahrirlanadigan loyiha papkasi (ichida `film.html`). Har vazifada **nusxasi** ishlatiladi, asl papka o'zgartirilmaydi.
   - `Mp4Reference` — uslub namunasi. Undan 7 ta kadr ajratilib, Claude'ga rasm sifatida beriladi.
6. **YouTube** — Client ID va Secret, so'ng **Kanalni ulash** (brauzerda Google ruxsat oynasi ochiladi). Scope'lar: `youtube.upload` va `youtube.readonly`.
   Public soati va vaqt zonasi (standart `18:00`, `Asia/Tashkent`).
7. **Integratsiyalarni tekshirish** — hamma narsani og'ir dasturlarni ishga tushirmasdan tekshiradi.
   "Claude autentifikatsiyasini ham tekshirish" belgilansa, Claude'ga juda kichik so'rov yuboriladi.

So'ng **Jadval va worker** sahifasida vaqtni (standart `01:00`, `Asia/Tashkent`) tanlab **Jadvalni yoqish**.
Task Scheduler'da ikkita vazifa yaratiladi (vaqt o'zgarsa yangilanadi, dublikat yaratilmaydi):
- `YtAuto Daily Worker` — kunlik ishga tushish (`--scheduled`);
- `YtAuto Remote Trigger` — siz Windows'ga kirgan va internet bor paytda har 2 daqiqada yengil `--poll`: online bazada
  "Hozir chiqarish" so'rovi bo'lsa bajaradi, bo'lmasa bir zumda jim chiqadi. Eski o'rnatmalarda ilova ochilganda avtomatik qo'shiladi.

### Google OAuth client yaratish (bir marta)

1. <https://console.cloud.google.com/> → yangi loyiha.
2. *APIs & Services → Library* → **YouTube Data API v3** → *Enable*.
3. *OAuth consent screen* → External, ilova nomi, o'z email'ingiz; *Test users* ga kanal egasining Google hisobini qo'shing.
4. *Credentials → Create credentials → OAuth client ID → Desktop app*. Client ID va Secret'ni YtAuto sozlamalariga kiriting.

### Online baza (Supabase PostgreSQL)

Ko'p foydalanuvchili: ilova bazaga **o'z qurilma roli** bilan ulanadi (sozlashda avtomatik yaratiladi, ulanish satri DPAPI'da) va
Row Level Security tufayli faqat o'z hisobi ma'lumotlarini ko'radi. Sxema — `bot/db/schema.sql` (exe ichida). Eski (v1) o'rnatmada
saqlangan bazaning egasi ulanish satri faqat bir marta — eski ma'lumotlarni shu kompyuter hisobiga o'tkazish uchun ishlatiladi va
keyin o'chiriladi. Hisob sozlanmagan bo'lsa worker hech narsa qilmaydi. Quyidagi tavsif umumiy ishlash haqida: Ulangandan keyin mavzular (yaratish, tahrirlash,
o'chirish), holatlar, bosqichlar, loglar, worker ishga tushishlari va **Gemini/Claude promptlari** online bazada (`ytauto` sxemasi)
saqlanadi — kompyuter almashsa ham yo'qolmaydi. Kompyuterga xos sozlamalar va sirlar lokal qoladi. Birinchi ulanishda lokal mavzular
avtomatik ko'chiriladi. Supabase'ning `db.*.supabase.co` manzili faqat IPv6 — IPv4 tarmoqda pooler ishlatiladi
(`aws-0-<region>.pooler.supabase.com:5432`, foydalanuvchi `postgres.<loyiha>`). Internet bo'lmasa ilova va worker ishga tushmaydi
(worker keyingi jadvalda qayta urinadi). Bir nechta kompyuter bitta bazadan foydalansa, band qilish atomar va heartbeat bilan himoyalangan.

### Ovoz, shablonlar va YouTube video sozlamalari

- **Ovoz**: Sozlamalar → Ovoz → "Kompyuterdagi modellarni o'qish". VoiceStudio'dan (`/engines/tts`, `/models`) barcha TTS dvigatellari,
  o'rnatilgan modellar va tayyor ovozlar o'qiladi (VoiceStudio yopiq bo'lsa ~2 daqiqaga ochilib, keyin yopiladi; natija keshlanadi).
  Dvigatel va ovozni tanlab "Profil sifatida qo'shish"; mavzu yaratishda shu profillardan biri tanlanadi.
- **Shablonlar**: Sozlamalar → OpenFilm va shablonlar → "Video shablon yuklash" yoki "OpenFilm loyiha papkasini yuklash".
  Fayl ilovaning shablonlar papkasiga (standart: `D:\YtAuto\templates`) nusxalanadi va lokal saqlanadi; asl fayl keyin kerak emas.
  "Yangi mavzu"da shablon rasm bilan tanlanadi va montaj paytida ishlatiladi.
- **Davomiylik** kiritilmaydi: Gemini mavzuga kerakli uzunlikda yozadi, video uzunligi tayyor ovoz va subtitrga qarab belgilanadi.
- **YouTube**: kategoriya nomi bilan (YouTube API `videoCategories.list` dan), bolalar auditoriyasi va AI (sintetik) kontent belgisi
  tushuntirish bilan, litsenziya, embed, statistika, obunachilarga bildirishnoma, yozib olingan sana/joy. API orqali sozlanmaydiganlar
  (yosh cheklovi, izohlar, monetizatsiya, end screen, pleylist) — YouTube Studio → Upload defaults.

## Ishlash tartibi

- **Yangi mavzu**: mavzu, izoh, til, davomiylik, format, shablon, ovoz profili, chiqish sanasi/soati. Sana taklifi:
  oxirgi (bekor qilinmagan, navbatdagilar ham) rejalashtirilgan sana + 2 kun; o'tgan bo'lsa yoki vazifa yo'q bo'lsa — bugun.
- Worker har vazifada: OS mutex → eskirgan band qilishlarni bo'shatish → chiqish vaqti eng yaqin yaroqli vazifani **atomar** band qilish →
  tugallanmagan bosqichdan davom ettirish. Navbat bo'sh bo'lsa og'ir dasturlar ochilmaydi.
- **Jadval vaqtida (`--scheduled`)**: avval "Hozir chiqarish" so'rovlari bajariladi, so'ng online bazadan navbatdagi mavzular
  olinadi (sinxronlash) va ish oynasi tugaguncha **barchasi** ketma-ket tayyorlanadi (chiqish vaqti yaqinlari birinchi):

  `video → 15 daqiqa tanaffus (kompyuter sovib oladi) → video → 15 daqiqa tanaffus → … → navbat bo'shaydi yoki ish oynasi tugaydi`

  Ish oynasi tugasa yangi video boshlanmaydi (boshlangani oxirigacha tugatiladi), qolganlari keyingi kechaga qoladi.

  Har videodan oldin mavzu bazadan qayta olinadi (boshqa kompyuterda o'zgargan bo'lsa ham). Vaqtinchalik xato (tarmoq, API limit)
  bo'lsa qolgan videolar keyingi jadvalga qoladi. Tanaffus paytida kelgan "Hozir chiqarish" so'rovi darhol bajariladi (reja soniga kirmaydi).
  Shu sababli kunlik vazifaning ish vaqti limiti kamida 10 soat.
- **"Hozir chiqarish"** (tafsilotlar sahifasidagi ⚡ tugma yoki Supabase'da `ytauto.jobs.publish_now = true`): so'rov online bazaga
  yoziladi; internetga ulangan, jadvali yoqilgan kompyuter uni ~2 daqiqa ichida oladi va o'zi ishga tushadi:
  - video hali tayyorlanmagan bo'lsa — navbatdan tashqari tayyorlanadi va YouTube'ga **darhol public** yuklanadi;
  - video tayyor, lekin yuklanmagan bo'lsa (masalan vaqti o'tib ketgan yoki kanal ulanmagan edi) — qayta yaratilmaydi, arxivdagisi darhol public yuklanadi;
  - video yuklangan, lekin hali chiqmagan bo'lsa (private/rejalashtirilgan) — YouTube'da darhol public qilinadi, qayta yuklanmaydi.

  Boshqa video tayyorlanayotgan bo'lsa, so'rov u tugagach bajariladi. Vaqtinchalik xatoda so'rov saqlanib, biroz keyin qayta uriniladi.
  Supabase SQL misoli: `update ytauto.jobs set publish_now = true where id = 42;`
- Vazifa boshlanganda sozlamalar, promptlar va shablon versiyasi **snapshot** qilinadi; keyingi sozlama o'zgarishlari unga ta'sir qilmaydi
  (tafsilotlarda "Joriy sozlamalar bilan qayta" tugmasi snapshot'ni yangilaydi). Tashqi dastur yo'llari esa joriy sozlamadan olinadi.
- Holatlar: `Queued, Scripting, GeneratingVoice, Aligning, Editing, Rendering, Validating, Archived, Uploading, Processing, Scheduled, Published, Failed, NeedsAttention, Cancelled`.
- Xatolar:
  - vaqtinchalik (tarmoq, API limit, ilova yiqilishi) — bosqich ichida cheklangan backoff, so'ng `Failed`; keyingi ishga tushishda qayta uriniladi;
  - urinishlar limiti (standart 3) tugasa — `NeedsAttention`, navbat keyingi vazifaga o'tadi;
  - noto'g'ri sozlama — darhol `NeedsAttention` va tushunarli sabab;
  - sifat tekshiruvi o'tmasa — aniq xatolar Claude'ga qaytariladi (standart 2 sikl, TTS takrorlanmaydi); limit tugasa video YouTube'ga yuborilmaydi.
- Video tayyor bo'lganda public vaqti o'tib ketgan (yoki 30 daqiqadan yaqin) bo'lsa, chiqish vaqti avtomatik **hozir + 1 soat**
  ga suriladi va video shu vaqtga rejalashtirib yuklanadi (log'da eski va yangi vaqt yoziladi).
- Upload: resumable; sessiya URI (DPAPI'da) va video ID darhol saqlanadi. Uzilsa sessiya davom ettiriladi. Natija noaniq bo'lsa
  (sessiya yo'qolgan va kanalda mos video topilmagan) yangi upload **boshlanmaydi** — `NeedsAttention`; YouTube Studio'ni tekshirib,
  tafsilotlardagi "Yangi upload" tugmasi bilan o'zingiz qaror qilasiz.
- `Processing` → `Scheduled` → `Published` faqat YouTube API javobi bo'yicha (sana o'tganining o'zi `Published` emas).
  Holat UI ochilganda, "YouTube holatini yangilash" bosilganda va har worker ishga tushishida (yengil so'rov) tekshiriladi.
- **Ish tugagach tozalash**: YouTube tasdiqlagach (`Scheduled`/`Published`) vazifaning barcha papkalari o'chiriladi —
  `jobs\job-N`, `records\job-N` (loglar, snapshot, ssenariy, QA) va arxivdagi qo'shimchalar (`.srt`, `.vtt`, `.jpg`, `.json`).
  **Arxivda faqat video (`.mp4`) va audio (`.wav`) qoladi.** Worker har ishga tushishda eski tugagan vazifalarni ham shu qoidaga
  keltiradi va bazadan o'chirilgan (masalan Telegram bot orqali) mavzularning yetim `job-N` papkalarini olib tashlaydi.
  Tozalash faqat ilova papkalari ichida, symlink/junction'larga kirmaydi; video arxivda bo'lmasa hech narsa o'chirilmaydi.
- **Telegram bot katalogi**: ilova va worker ovoz profillari, shablonlar va standart chiqish vaqtini online bazaga
  (`shared_settings` → `bot.catalog`) yozib qo'yadi — bot qo'shgan mavzular shular bilan yaratiladi (sirlar yuborilmaydi).

## Jarayonlar va resurslar

- Har tashqi dastur o'z **Windows Job Object**'ida, `CREATE_SUSPENDED` bilan yaratilib, job'ga biriktirilgandan keyin ishga tushadi —
  detached child'lar ham job ichida qoladi. Worker kutilmaganda o'lsa ham (KILL_ON_JOB_CLOSE) uning jarayonlari yetim qolmaydi.
- Yopish: avval oynalarga `WM_CLOSE`, cheklangan kutish, keyin faqat worker'ga tegishli job daraxti to'xtatiladi.
  Jarayon identifikatsiyasi: PID + yaratilish vaqti + exe yo'li (PID qayta ishlatilsa adashmaydi).
- Worker ishga tushishidan oldin ochiq bo'lgan VoiceStudio — foydalanuvchiniki: yopilmaydi.
- Hard RAM limit qo'yilmaydi; o'rniga bosqichlar ketma-ket, render parallelizmi 1, TTS bo'laklab (≈450 belgi), bo'sh RAM/diskni oldindan tekshirish.
- Child jarayonlar `BELOW_NORMAL` ustuvorlikda; stdout/stderr to'g'ridan-to'g'ri faylga (RAM'da yig'ilmaydi). Fayllar stream bilan ko'chiriladi va SHA-256 bilan tekshiriladi.

## Xavfsizlik

- Gemini kaliti, YouTube client secret, OAuth tokenlari va upload sessiya URI — DPAPI (CurrentUser).
- Loglarda kalitlar, tokenlar, `Authorization` header va `upload_id` maskalanadi.
- Claude'ga shell orqali emas, to'g'ridan-to'g'ri argumentlar beriladi; prompt qat'iy matn, foydalanuvchi matni faqat fayllarda.
  Claude muhitidan `GEMINI_*`, `GOOGLE_API*`, `YOUTUBE*`, ota-ona Claude sessiyasining `CLAUDE*` o'zgaruvchilari olib tashlanadi.
  Ruxsat etilgan vositalar: Read/Write/Edit/Glob/Grep va faqat `openfilm look|skill|get`, `cd`, `ls`.

## Worker buyruqlari

```powershell
YtAuto.Worker.exe --scheduled          # Task Scheduler: sinxronlash + navbatdagi barcha video (ish oynasi ichida)
YtAuto.Worker.exe --poll               # Task Scheduler (har 2 daq): faqat "Hozir chiqarish" so'rovlari
YtAuto.Worker.exe --manual             # qo'lda: bitta vazifa
YtAuto.Worker.exe --refresh            # faqat YouTube holatlarini yangilash
YtAuto.Worker.exe --check [--deep]     # integratsiyalar tekshiruvi -> %LOCALAPPDATA%\YtAuto\logs\last-check.txt
YtAuto.Worker.exe --selftest-local     # lokal zanjir sinovi (alohida papkada, Gemini/YouTube'siz)
```

Exit kodlari: `0` — muvaffaqiyatli yoki navbat bo'sh, `1` — xato (qayta uriniladi), `2` — e'tibor kerak, `3` — boshqa worker ishlamoqda, `4` — kutilmagan xato.

## Testlar

```powershell
dotnet test tests\YtAuto.Tests
```

## Cheklovlar

[docs/TEST-REPORT.md](docs/TEST-REPORT.md) ning oxirida.

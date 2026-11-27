# library — umumiy rasm kutubxonasi zavodi

YtAuto videolari uchun tiniq, litsenziyasi tekshirilgan rasmlar bazasi (`ytauto.assets`, sxema v9). Zavod rasmlarni bir marta yig'adi,
tozalaydi va bazaga yozadi; worker video paytida faqat **qidiradi** va kerakli rasmlarni yuklab oladi (AI generatsiya yo'q, har video
uchun 50–80 rasm ham bir soniyada topiladi).

## Qanday ishlaydi

```
manba (Wikimedia, NASA, Poly Haven, papka) → yuklab olish → sifat (o'lcham, tiniqlik) → takror (pHash) → baza
                                                     └─ ixtiyoriy: fondan ajratilgan PNG (rembg) → ombor → baza (kind=cutout)
```

| Manba | Litsenziya | Fayl qayerda | Tur |
|---|---|---|---|
| Wikimedia Commons (avval Quality/Featured/Valued, so'ng oddiy fayllar) | CC0, PD, CC BY (SA/NC/ND rad) | Wikimedia CDN (1920 px) | photo, diagram |
| NASA Image Library | Public domain (©/logolar rad) | NASA CDN | photo |
| Poly Haven 3D modellar → Blender render | CC0 (model); render o'zimizniki | ombor | render (shaffof PNG) |
| Papka (3dicons v1, ABO tanlovi, o'z paketlaringiz) | CLI'da beriladi | ombor | icon, cutout, ... |
| `--cutouts` | asl fotoniki | ombor | cutout (shaffof PNG) |

Worker tomoni: `LibraryStockSource` (C#) Gemini sahna rejasidagi har sahna uchun turiga qarab qidiradi (hero → fotolar, collage →
cutout/renderlar, ...), nomzodlar sonini elementlar sonidan oladi, faqat ruxsat etilgan hostlardan (`*.wikimedia.org`, `*.nasa.gov`,
`*.supabase.co`) yuklaydi. Kutubxonada yetmagan sahnalar OpenFilm stok qidiruviga (Pexels/Pixabay) o'tadi. Ishlatilgan rasmlar
`ytauto.asset_usage` ga yoziladi (shu kanalda 60 kun takrorlanmaydi), CC BY mualliflari video tavsifiga qo'shiladi.

## O'rnatish

```powershell
cd library
python -m pip install -r requirements.txt
python -m pip install rembg          # ixtiyoriy: --cutouts uchun (CPU'da ishlaydi)
```

Muhit:
- `YTLIB_DATABASE_URL` — bazaning **egasi** ulanish satri (qurilma rollari kutubxonaga yoza olmaydi).
- `YTLIB_CONTACT` — Wikimedia User-Agent siyosati uchun sayt yoki email (daqiqasiga 200 so'rov chegarasi shunga bog'liq).
- Ombor (cutout/render/papka uchun): `--store supabase:<bucket>` + `SUPABASE_URL`, `SUPABASE_SERVICE_KEY` (public bucket), yoki
  sinov uchun `--store dir:<papka>` + `YTLIB_PUBLIC_BASE`.

Avval sxema v9 qo'llangan bo'lishi kerak (`bot` papkasida `npm run db:migrate`).

## Buyruqlar

```powershell
python -m ytlib ingest wikimedia --topics topics/science.txt --per-topic 40 --cutouts --store supabase:library
python -m ytlib ingest nasa --topics topics/space.txt --per-topic 30
python -m ytlib ingest polyhaven --blender "C:\Program Files\Blender Foundation\Blender 4.2\blender.exe" --limit 40 --store supabase:library
python -m ytlib import-dir D:\packs\3dicons --source 3dicons --kind icon --license CC0 --store supabase:library
python -m ytlib stats
python -m ytlib search mitochondria "cell organelle"
```

Qayta ishga tushirish xavfsiz: bazada bori o'tkazib yuboriladi. Testlar (tarmoqsiz): `python -m unittest discover -s tests`.

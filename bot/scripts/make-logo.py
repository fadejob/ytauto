"""YtAuto logotipi: ko'k gradientli yumaloq kvadrat, ichida play belgisi va uni o'rab turgan "avtomatik" aylanma strelka.

Google OAuth (Branding) talabi: kvadrat, 120x120 px, 1 MB gacha. Shuningdek 512 px versiya (bot/sayt uchun).
Ishga tushirish:  python bot/scripts/make-logo.py   (Pillow kerak)
"""
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

OUT = Path(__file__).resolve().parent.parent / "public"
S = 2048  # supersampling: chiziqlar silliq chiqadi

TOP, BOTTOM = (0x4B, 0x8E, 0xF8), (0x1D, 0x4E, 0xD8)  # botdagi --accent (#2f7cf6) atrofida


def gradient(size):
    g = Image.new("RGB", (size, size))
    px = g.load()
    for y in range(size):
        for x in range(size):
            t = min(1.0, max(0.0, (x * 0.35 + y * 0.65) / size))
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM))
    return g


def draw_logo(size=S):
    # Gradient kichikroq o'lchamda hisoblanib kattalashtiriladi (tez).
    bg = gradient(256).resize((size, size), Image.BICUBIC)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=int(size * 0.225), fill=255)
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    img.paste(bg, (0, 0), mask)

    # Yuqori chapdan yengil yorug'lik.
    glow = Image.new("L", (size, size), 0)
    ImageDraw.Draw(glow).ellipse([-size * 0.35, -size * 0.55, size * 0.9, size * 0.45], fill=46)
    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.08))
    white = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    white.putalpha(Image.composite(glow, Image.new("L", (size, size), 0), mask))
    img = Image.alpha_composite(img, white)

    d = ImageDraw.Draw(img)
    cx = cy = size / 2
    r = size * 0.315
    w = int(size * 0.075)

    # Aylanma strelka: 300° yoy, oxirida strelka uchi (avtomatik takrorlanish).
    start, end = -40, 225  # gradus, PIL: 0 = o'ng, soat yo'nalishida
    d.arc([cx - r, cy - r, cx + r, cy + r], start=start, end=end, fill=(255, 255, 255, 255), width=w)
    for ang in (start, end):  # yoy uchlari yumaloq
        a = math.radians(ang)
        ex, ey = cx + (r - w / 2) * math.cos(a), cy + (r - w / 2) * math.sin(a)
        d.ellipse([ex - w / 2, ey - w / 2, ex + w / 2, ey + w / 2], fill="white")
    a = math.radians(end)
    tip_r = r - w / 2
    px, py = cx + tip_r * math.cos(a), cy + tip_r * math.sin(a)
    tx, ty = -math.sin(a), math.cos(a)  # yoy bo'ylab harakat yo'nalishi
    nx, ny = math.cos(a), math.sin(a)   # radial
    head, half, back = size * 0.13, size * 0.115, size * 0.03
    d.polygon([
        (px + tx * head, py + ty * head),
        (px - tx * back + nx * half, py - ty * back + ny * half),
        (px - tx * back - nx * half, py - ty * back - ny * half),
    ], fill="white")

    # Play uchburchagi (burchaklari yumaloq), optik markazga biroz o'ngga suriladi.
    tri = size * 0.30
    ox = cx + tri * 0.10
    pts = [(ox - tri * 0.42, cy - tri * 0.5), (ox - tri * 0.42, cy + tri * 0.5), (ox + tri * 0.48, cy)]
    tri_mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(tri_mask).polygon(pts, fill=255)
    rr = int(size * 0.018)
    tri_mask = tri_mask.filter(ImageFilter.MinFilter(rr * 2 + 1)).filter(ImageFilter.MaxFilter(rr * 2 + 1))
    tri_mask = tri_mask.filter(ImageFilter.GaussianBlur(rr * 0.6)).point(lambda v: 255 if v > 127 else 0)
    img.paste(Image.new("RGBA", (size, size), (255, 255, 255, 255)), (0, 0), tri_mask)
    return img


def main():
    OUT.mkdir(exist_ok=True)
    big = draw_logo()
    for px, name in ((512, "logo-512.png"), (120, "logo-120.png")):
        im = big.resize((px, px), Image.LANCZOS)
        path = OUT / name
        im.save(path, optimize=True)
        kb = path.stat().st_size / 1024
        assert kb < 1024, f"{name} 1 MB dan katta"
        print(f"{path}  {px}x{px}  {kb:.1f} KB")


if __name__ == "__main__":
    main()

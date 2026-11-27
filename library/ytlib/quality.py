"""Rasm sifati: o'lcham, tiniqlik (Laplasian), perceptual hash (takrorni topish), shaffoflik va umumiy ball.

Hech qanday GPU yoki og'ir model kerak emas: Pillow + numpy + scipy.
"""
from __future__ import annotations

import io
from dataclasses import dataclass

import numpy as np
from PIL import Image, ImageOps
from scipy.fft import dctn

# Videoda to'liq kadr bo'lishi uchun uzun tomoni kamida shuncha (1080p kadrning 2/3 qismidan katta).
MIN_LONG_SIDE_PHOTO = 1600
# Cutout/render/ikon kadrning bir qismi bo'ladi: kichikroq ham yaraydi.
MIN_LONG_SIDE_PART = 900
# Tiniqlik chegarasi: eng tiniq bo'laklar (90-persentil) Laplasian dispersiyasi shundan past bo'lsa rasm xira.
MIN_SHARPNESS = 60.0


@dataclass
class ImageStats:
    width: int
    height: int
    has_alpha: bool
    sharpness: float
    phash: str
    colors: list[str]

    @property
    def long_side(self) -> int:
        return max(self.width, self.height)

    @property
    def orientation(self) -> str:
        if self.width > self.height * 1.15:
            return "landscape"
        if self.height > self.width * 1.15:
            return "portrait"
        return "square"


def open_image(data: bytes) -> Image.Image:
    im = Image.open(io.BytesIO(data))
    im = ImageOps.exif_transpose(im)
    im.load()
    return im


def _gray(im: Image.Image, size: int) -> np.ndarray:
    g = im.convert("L")
    g.thumbnail((size, size), Image.LANCZOS)
    return np.asarray(g, dtype=np.float32)


def sharpness(im: Image.Image) -> float:
    """Eng tiniq joylarning Laplasian dispersiyasi (4x4 bo'lak, 90-persentil).

    Butun rasm bo'yicha o'rtacha emas: fon ataylab xira (bokeh) bo'lgan sifatli fotolar ham o'tadi, faqat hamma joyi xiralar rad etiladi.
    """
    a = _gray(im, 768)
    lap = (-4 * a[1:-1, 1:-1] + a[:-2, 1:-1] + a[2:, 1:-1] + a[1:-1, :-2] + a[1:-1, 2:])
    h, w = lap.shape
    if h < 16 or w < 16:
        return 0.0
    tiles = []
    for i in range(4):
        for j in range(4):
            t = lap[i * h // 4:(i + 1) * h // 4, j * w // 4:(j + 1) * w // 4]
            tiles.append(float(t.var()))
    return float(np.percentile(tiles, 90))


def phash(im: Image.Image) -> str:
    """64 bitli DCT perceptual hash (16 ta hex belgi)."""
    a = np.asarray(im.convert("L").resize((32, 32), Image.LANCZOS), dtype=np.float32)
    d = dctn(a, norm="ortho")[:8, :8]
    med = np.median(d.flatten()[1:])
    bits = (d > med).flatten()
    v = 0
    for b in bits:
        v = (v << 1) | int(b)
    return f"{v:016x}"


def hamming(a: str, b: str) -> int:
    return bin(int(a, 16) ^ int(b, 16)).count("1")


def has_alpha(im: Image.Image) -> bool:
    """Haqiqatan shaffof joyi bor (faqat alfa kanali borligi emas)."""
    if im.mode in ("RGBA", "LA") or (im.mode == "P" and "transparency" in im.info):
        alpha = np.asarray(im.convert("RGBA").getchannel("A"))
        return bool((alpha < 250).mean() > 0.02)
    return False


def dominant_colors(im: Image.Image, n: int = 3) -> list[str]:
    small = im.convert("RGB")
    small.thumbnail((96, 96))
    q = small.quantize(colors=n, method=Image.Quantize.MEDIANCUT)
    pal = q.getpalette()[: n * 3]
    counts = sorted(q.getcolors() or [], reverse=True)
    out = []
    for _, idx in counts[:n]:
        r, g, b = pal[idx * 3: idx * 3 + 3]
        out.append(f"#{r:02x}{g:02x}{b:02x}")
    return out


def analyze(data: bytes) -> ImageStats:
    im = open_image(data)
    return ImageStats(
        width=im.width,
        height=im.height,
        has_alpha=has_alpha(im),
        sharpness=round(sharpness(im), 1),
        phash=phash(im),
        colors=dominant_colors(im),
    )


def score(stats: ImageStats, curated: float) -> float:
    """0..1 ball: o'lcham (40%), tiniqlik (40%), manba tanlovi (20%: Featured 1.0, Quality 0.8, NASA 0.6 ...)."""
    res = min(1.0, stats.long_side / 3000)
    sharp = min(1.0, stats.sharpness / 400)
    return round(0.4 * res + 0.4 * sharp + 0.2 * max(0.0, min(1.0, curated)), 3)


def reject_reason(stats: ImageStats, part: bool) -> str | None:
    """Rasm bazaga kirmasligi sababi (None - o'tdi)."""
    need = MIN_LONG_SIDE_PART if part else MIN_LONG_SIDE_PHOTO
    if stats.long_side < need:
        return f"kichik ({stats.width}x{stats.height} < {need})"
    if not part and stats.sharpness < MIN_SHARPNESS:
        return f"xira (tiniqlik {stats.sharpness:.0f} < {MIN_SHARPNESS:.0f})"
    ratio = stats.long_side / max(1, min(stats.width, stats.height))
    if ratio > 3.2:
        return f"juda cho'zinchoq ({stats.width}x{stats.height})"
    return None

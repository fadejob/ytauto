"""Poly Haven 3D modellari (CC0) -> Blender'da shaffof fonli PNG renderlar.

Saytdagi tayyor rasmlar CC0 EMAS ("asset example renders" himoyalangan) - shuning uchun faqat model fayli (CC0) olinadi va
renderni o'zimiz qilamiz. API ToS: UA'da dastur nomi bo'lishi va manba ko'rsatilishi so'raladi (Powered by Poly Haven).
"""
from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import Iterator

from .. import net
from ..db import Asset
from ..store import content_name

API = "https://api.polyhaven.com"
HDRI = "studio_small_09"
RENDER = Path(__file__).resolve().parent.parent / "render_blender.py"


def models(categories: list[str] | None = None) -> list[tuple[str, dict]]:
    d = net.get_json(f"{API}/assets", {"t": "models"})
    out = []
    for k, v in d.items():
        if categories and not set(categories) & set(v.get("categories") or []):
            continue
        out.append((k, v))
    out.sort(key=lambda kv: -int(kv[1].get("download_count") or 0))
    return out


def _fetch(url: str, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(net.download(url, 200 * 1024 * 1024))


def render(blender: str, model_id: str, cache: Path, views: list[str], size: int) -> list[Path]:
    files = net.get_json(f"{API}/files/{model_id}")
    g = files["gltf"]["1k"]["gltf"]
    mdir = cache / "models" / model_id
    gltf = mdir / Path(g["url"]).name
    if not gltf.exists():
        _fetch(g["url"], gltf)
        for rel, inc in (g.get("include") or {}).items():
            _fetch(inc["url"], mdir / rel)
    hdr = cache / "hdri" / f"{HDRI}_1k.hdr"
    if not hdr.exists():
        _fetch(net.get_json(f"{API}/files/{HDRI}")["hdri"]["1k"]["hdr"]["url"], hdr)
    out = Path(tempfile.mkdtemp(prefix=f"ph_{model_id}_"))
    cmd = [blender, "-b", "--factory-startup", "-P", str(RENDER), "--", str(gltf), str(hdr), str(out), str(size), ",".join(views)]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=int(os.environ.get("YTLIB_RENDER_TIMEOUT", "1800")))
    if "RENDER_OK" not in r.stdout:
        tail = (r.stdout + r.stderr)[-600:]
        shutil.rmtree(out, ignore_errors=True)
        raise RuntimeError(f"Blender render xato (exit {r.returncode}): {tail}")
    return sorted(out.glob("view_*.png"))


def assets(blender: str, cache: Path, limit: int, categories: list[str] | None, views: list[str], size: int,
           known: set[str]) -> Iterator[tuple[Asset, bytes]]:
    n = 0
    for mid, info in models(categories):
        if n >= limit:
            return
        if all(f"{mid}/{v}" in known for v in views):
            continue
        try:
            pngs = render(blender, mid, cache, views, size)
        except Exception as e:
            print(f"  polyhaven {mid}: {e}")
            continue
        n += 1
        tags = ", ".join(dict.fromkeys((info.get("categories") or []) + (info.get("tags") or [])))
        authors = ", ".join((info.get("authors") or {}).keys())
        for p in pngs:
            view = p.stem.removeprefix("view_")
            data = p.read_bytes()
            a = Asset(source="polyhaven", source_id=f"{mid}/{view}", kind="render", title=info.get("name") or mid,
                      file_url="", tags=f"{tags}, 3d render, isolated, transparent background, {view.replace('34', ' three-quarter')} view",
                      description=f"Studio render of the CC0 3D model '{info.get('name') or mid}' from Poly Haven.",
                      license="CC0", license_url="https://polyhaven.com/license", author=authors,
                      page_url=f"https://polyhaven.com/a/{mid}", needs_credit=False)
            a.extra["curated"] = 0.9
            a.extra["upload"] = content_name("polyhaven", data, ".png")
            yield a, data
        shutil.rmtree(pngs[0].parent, ignore_errors=True)

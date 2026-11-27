"""Qo'lda olingan paketlar (3dicons v1, ABO tanlovi, o'z renderlaringiz): papkadagi PNG/JPG/WEBP fayllar omborga yuklanadi.

Sarlavha va teglar fayl nomidan olinadi ("dna-helix_iso_color.png" -> "dna helix"). Litsenziya CLI'da aniq beriladi.
Yonida <fayl>.json bo'lsa ({"title", "tags", "description", "author", "page_url"}) - undan olinadi.
"""
from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Iterator

from ..db import Asset
from ..licenses import License
from ..store import content_name

EXTS = {".png", ".jpg", ".jpeg", ".webp"}
_NOISE = {"color", "colour", "iso", "dynamic", "front", "premium", "gradient", "clay", "final", "render", "v1", "v2", "hd", "4k", "2k"}


def words(name: str) -> str:
    parts = [p for p in re.split(r"[^a-zA-Z0-9]+", name) if p and not p.isdigit() and p.lower() not in _NOISE]
    return " ".join(parts).strip().lower()


def scan(root: str, source: str, kind: str, lic: License, author: str = "", page_url: str = "") -> Iterator[tuple[Asset, bytes]]:
    base = Path(root)
    for p in sorted(base.rglob("*")):
        if p.suffix.lower() not in EXTS or not p.is_file():
            continue
        data = p.read_bytes()
        meta = {}
        side = p.with_suffix(p.suffix + ".json")
        if side.exists():
            try:
                meta = json.loads(side.read_text(encoding="utf-8"))
            except Exception:
                meta = {}
        rel = p.relative_to(base).as_posix()
        title = meta.get("title") or words(p.stem) or p.stem
        folder = words(" ".join(p.relative_to(base).parts[:-1]))
        tags = meta.get("tags") or ", ".join(x for x in (folder, kind) if x)
        a = Asset(source=source, source_id=rel, kind=kind, title=title[:200], file_url="", preview_url="",
                  tags=tags, description=meta.get("description", ""), license=lic.short, license_url=lic.url,
                  author=meta.get("author", author), page_url=meta.get("page_url", page_url), needs_credit=lic.needs_credit)
        a.extra["curated"] = 0.8
        a.extra["upload"] = content_name(source, data, p.suffix.lower())
        yield a, data

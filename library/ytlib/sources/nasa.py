"""NASA Image and Video Library (images-api.nasa.gov): kalitsiz, asosan public domain.

NASA logotiplari/emblemalari, boshqa tashkilotlar mualliflik huquqi bilan belgilangan (©, "courtesy of" ...) rasmlar olinmaydi.
Muallif sifatida NASA markazi va fotograf yoziladi (talab emas, lekin tavsifda ko'rsatish odobli).
"""
from __future__ import annotations

import re
from typing import Iterator

from .. import net
from ..db import Asset

API = "https://images-api.nasa.gov"
_BAD = re.compile(r"©|\bcopyright\b|courtesy of|all rights reserved|getty|reuters|associated press|\bap photo\b|afp", re.I)
_LOGO = re.compile(r"\b(logo|insignia|emblem|patch|seal|meatball|worm)\b", re.I)


def search(topic: str, limit: int) -> Iterator[Asset]:
    page = 1
    seen = 0
    while seen < limit:
        try:
            d = net.get_json(f"{API}/search", {"q": topic, "media_type": "image", "page": str(page), "page_size": "50"})
        except Exception as e:
            print(f"  nasa xato ({topic}): {e}")
            return
        items = (d.get("collection") or {}).get("items") or []
        if not items:
            return
        for it in items:
            seen += 1
            if seen > limit:
                return
            a = _to_asset(it)
            if a is not None:
                yield a
        page += 1
        if page > 10:
            return


def _to_asset(it: dict) -> Asset | None:
    data = (it.get("data") or [{}])[0]
    nid = data.get("nasa_id", "")
    title = (data.get("title") or "").strip()
    desc = re.sub(r"\s+", " ", data.get("description") or "").strip()
    kw = data.get("keywords") or []
    blob = " ".join([title, desc, " ".join(kw), data.get("photographer", ""), data.get("secondary_creator", "")])
    if not nid or _BAD.search(blob) or _LOGO.search(title + " " + " ".join(kw)):
        return None
    preview = next((l.get("href", "") for l in it.get("links") or [] if l.get("rel") == "preview"), "")
    if not preview:
        return None
    base = preview.rsplit("~", 1)[0] if "~" in preview else ""
    if not base:
        return None
    author = ", ".join(x for x in (data.get("photographer"), data.get("center") and f"NASA {data['center']}") if x) or "NASA"
    a = Asset(source="nasa", source_id=nid, kind="photo", title=title[:200], file_url=f"{base}~large.jpg",
              preview_url=f"{base}~small.jpg", tags=", ".join(kw[:12]), description=desc[:400],
              license="Public domain", license_url="https://www.nasa.gov/nasa-brand-center/images-and-media/",
              author=author[:120], page_url=f"https://images.nasa.gov/details/{nid}", needs_credit=False)
    a.extra["curated"] = 0.6
    a.extra["fallback_url"] = f"{base}~orig.jpg"
    return a

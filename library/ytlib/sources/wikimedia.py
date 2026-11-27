"""Wikimedia Commons: avval tanlangan rasmlar (Quality / Featured / Valued), so'ng oddiy fayllar (qattiqroq sifat filtri bilan).

Faqat CC0 / Public domain / CC BY fayllar olinadi (licenses.classify). Fayl Wikimedia CDN'ida qoladi: file_url - 1920 px
nusxa (SVG diagrammalar ham PNG ko'rinishida), preview_url - 480 px.
"""
from __future__ import annotations

import html
import re
import time
from typing import Iterator

from .. import licenses, net
from ..db import Asset

API = "https://commons.wikimedia.org/w/api.php"
CURATED = "Quality_images|Featured_pictures_on_Wikimedia_Commons|Valued_images_sorted_by_promotion_date"
# Litsenziya kategoriyalari (yashirin): qidiruvni oldindan toraytiradi; yakuniy tekshiruv baribir extmetadata bo'yicha.
FREE_CATS = ("CC-BY-4.0|CC-BY-3.0|CC-BY-2.5|CC-BY-2.0|CC-BY-1.0|CC-Zero|PD-self|PD-user|PD-author|PD-old|PD-USGov|"
             "PD-USGov-NASA|PD-USGov-NIH|PD-USGov-NOAA|PD-USGov-USGS|PD-USGov-CDC|PD-ineligible|Public_domain")
META = "LicenseShortName|License|LicenseUrl|Artist|Credit|AttributionRequired|Restrictions|ImageDescription|Categories|ObjectName|NonFree"
MIN_INTERVAL = 1.0
_last = 0.0


def _call(params: dict) -> dict:
    global _last
    wait = MIN_INTERVAL - (time.monotonic() - _last)
    if wait > 0:
        time.sleep(wait)
    _last = time.monotonic()
    return net.get_json(API, {"format": "json", "formatversion": "2", "action": "query", **params})


_TAG = re.compile(r"<[^>]+>")
# Taniladigan odamlar (shaxsiy huquq) va odam asosiy mavzu bo'lgan rasmlar olinmaydi: kutubxona - obyektlar, tabiat, fan.
_PEOPLE = re.compile(r"\b(people|persons?|wom[ae]n|m[ae]n|girls?|boys?|child(ren)?|kids?|bab(y|ies)|teen(ager)?s?|portraits?|selfies?|"
                     r"famil(y|ies)|weddings?|brides?|models? \(person\)|nudes?|crowds?|tourists?|actors?|actress(es)?|"
                     r"politicians?|celebrit(y|ies))\b", re.I)


def text(s: str | None, limit: int = 400) -> str:
    t = html.unescape(_TAG.sub(" ", s or ""))
    t = re.sub(r"\s+", " ", t).strip()
    return t[:limit]


def _meta(ii: dict, key: str) -> str:
    v = (ii.get("extmetadata") or {}).get(key, {}).get("value", "")
    return v if isinstance(v, str) else str(v)


def _tags(categories: str) -> str:
    skip = ("taken with", "license", "cc-", "pd-", "self-published", "quality images", "featured pictures", "valued images",
            "pages with", "files with", "media ", "uploaded", "photographs by", "images with", "gfdl", "retouched", "supported by")
    out = []
    for c in (categories or "").split("|"):
        c = c.replace("_", " ").strip()
        if c and not any(s in c.lower() for s in skip) and len(c) < 60:
            out.append(c)
    return ", ".join(out[:12])


def search(topic: str, curated: bool, limit: int) -> Iterator[Asset]:
    q = f"{topic} incategory:{FREE_CATS}" + (f" incategory:{CURATED}" if curated else " filew:>1599")
    offset = 0
    seen = 0
    while seen < limit:
        try:
            d = _call({"generator": "search", "gsrnamespace": "6", "gsrsearch": q, "gsrlimit": str(min(50, limit - seen)),
                       "gsroffset": str(offset), "prop": "imageinfo", "iiprop": "url|size|mime|extmetadata",
                       "iiurlwidth": "1920", "iiextmetadatafilter": META})
        except Exception as e:  # tarmoq yoki API: shu mavzu o'tkazib yuboriladi
            print(f"  wikimedia xato ({topic}): {e}")
            return
        pages = (d.get("query") or {}).get("pages") or []
        if not pages:
            return
        for p in sorted(pages, key=lambda x: x.get("index", 0)):
            seen += 1
            a = _to_asset(p, curated)
            if a is not None:
                yield a
        cont = d.get("continue", {}).get("gsroffset")
        if cont is None:
            return
        offset = int(cont)


def _to_asset(p: dict, curated: bool) -> Asset | None:
    info = (p.get("imageinfo") or [None])[0]
    if not info:
        return None
    mime = info.get("mime", "")
    if mime not in ("image/jpeg", "image/png", "image/svg+xml", "image/webp", "image/tiff"):
        return None
    if _meta(info, "NonFree").lower() == "true" or text(_meta(info, "Restrictions")):
        return None  # savdo belgisi, shaxsiy huquq va h.k. cheklovlar
    if _PEOPLE.search(" ".join((p.get("title", ""), _meta(info, "ObjectName"), _meta(info, "Categories"), text(_meta(info, "ImageDescription"))))):
        return None
    lic = licenses.classify(_meta(info, "License"), text(_meta(info, "LicenseShortName")), _meta(info, "LicenseUrl"))
    if lic is None:
        return None
    w, h = int(info.get("width") or 0), int(info.get("height") or 0)
    thumb = info.get("thumburl") or info.get("url")
    preview = thumb.replace("/1920px-", "/480px-") if "/1920px-" in thumb else thumb
    title = text(_meta(info, "ObjectName")) or p["title"].removeprefix("File:").rsplit(".", 1)[0]
    cats = _meta(info, "Categories")
    featured = "Featured pictures" in cats or "Featured_pictures" in cats
    a = Asset(
        source="wikimedia", source_id=str(p.get("pageid")), kind="diagram" if mime == "image/svg+xml" else "photo",
        title=title[:200], file_url=thumb, preview_url=preview, tags=_tags(cats),
        description=text(_meta(info, "ImageDescription")), width=w, height=h,
        license=lic.short, license_url=lic.url, author=text(_meta(info, "Artist"), 120) or text(_meta(info, "Credit"), 120),
        page_url=info.get("descriptionurl", ""), needs_credit=lic.needs_credit)
    a.extra["curated"] = 1.0 if featured else (0.8 if curated else 0.5)
    a.extra["svg"] = mime == "image/svg+xml"
    return a

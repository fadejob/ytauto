"""Litsenziya filtri: faqat tijoratda (monetizatsiyali YouTube) erkin ishlatiladiganlar.

Ruxsat: CC0, Public Domain (PD-*), CC BY 1.0-4.0. Rad: SA (butun videoni SA'ga tortishi noaniq), NC, ND, GFDL-faqat, noma'lum.
"""
from __future__ import annotations

import re
from dataclasses import dataclass


@dataclass(frozen=True)
class License:
    short: str          # "CC0", "Public domain", "CC BY 4.0"
    url: str
    needs_credit: bool


_CC_BY = re.compile(r"^cc[- ]by[- ]?(\d(?:\.\d)?)$")


def classify(code: str, short_name: str = "", url: str = "") -> License | None:
    """Wikimedia extmetadata 'License' kodi (cc-by-4.0, cc0, pd, ...) va qisqa nomidan. Mos kelmasa None."""
    c = (code or "").strip().lower()
    s = (short_name or "").strip()
    sl = s.lower()
    if any(x in c or x in sl for x in ("-sa", " sa", "-nc", " nc", "-nd", " nd", "noncommercial", "sharealike", "noderiv")):
        return None
    if c in ("cc0", "cc-zero", "cc0-1.0") or sl in ("cc0", "cc0 1.0", "cc zero"):
        return License("CC0", url or "https://creativecommons.org/publicdomain/zero/1.0/", False)
    if c.startswith("pd") or sl.startswith("public domain") or c == "public domain":
        return License("Public domain", url, False)
    m = _CC_BY.match(c) or _CC_BY.match(sl.replace(" ", "-"))
    if m:
        v = m.group(1)
        if "." not in v:
            v += ".0"
        return License(f"CC BY {v}", url or f"https://creativecommons.org/licenses/by/{v}/", True)
    return None


def parse_named(text: str) -> License | None:
    """CLI va import uchun: 'CC0', 'CC BY 4.0', 'PD'."""
    t = (text or "").strip().lower().replace("_", " ")
    if t in ("cc0", "cc0 1.0"):
        return License("CC0", "https://creativecommons.org/publicdomain/zero/1.0/", False)
    if t in ("pd", "public domain"):
        return License("Public domain", "", False)
    m = re.match(r"^cc by (\d(?:\.\d)?)$", t)
    if m:
        v = m.group(1) if "." in m.group(1) else m.group(1) + ".0"
        return License(f"CC BY {v}", f"https://creativecommons.org/licenses/by/{v}/", True)
    return None

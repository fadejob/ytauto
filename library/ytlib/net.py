"""HTTP: bitta sessiya, odobli User-Agent, 429/5xx da kutib qayta urinish, hajm chegarasi."""
from __future__ import annotations

import os
import time

import requests

MAX_BYTES = 30 * 1024 * 1024


def user_agent() -> str:
    # Wikimedia siyosati: UA ichida aloqa (sayt yoki email). Uni kutubxona operatori YTLIB_CONTACT bilan beradi.
    contact = os.environ.get("YTLIB_CONTACT", "").strip()
    return f"YtAutoLibrary/1.0 ({contact or 'asset library builder'}) python-requests"


_session: requests.Session | None = None


def session() -> requests.Session:
    global _session
    if _session is None:
        _session = requests.Session()
        _session.headers["User-Agent"] = user_agent()
    return _session


def get(url: str, params: dict | None = None, *, timeout: float = 40, tries: int = 4, stream: bool = False) -> requests.Response:
    delay = 2.0
    for i in range(tries):
        try:
            r = session().get(url, params=params, timeout=timeout, stream=stream)
        except requests.RequestException:
            if i == tries - 1:
                raise
            time.sleep(delay)
            delay *= 2
            continue
        if r.status_code == 429 or r.status_code >= 500:
            if i == tries - 1:
                r.raise_for_status()
            wait = float(r.headers.get("Retry-After", delay)) if r.headers.get("Retry-After", "").isdigit() else delay
            time.sleep(max(wait, 5.0 if r.status_code == 429 else delay))
            delay *= 2
            continue
        r.raise_for_status()
        return r
    raise RuntimeError("unreachable")


def get_json(url: str, params: dict | None = None) -> dict:
    return get(url, params).json()


def download(url: str, max_bytes: int = MAX_BYTES) -> bytes:
    r = get(url, stream=True, timeout=90)
    n = int(r.headers.get("Content-Length") or 0)
    if n > max_bytes:
        raise ValueError(f"juda katta: {n} bayt")
    buf = bytearray()
    for chunk in r.iter_content(1 << 16):
        buf.extend(chunk)
        if len(buf) > max_bytes:
            raise ValueError("juda katta")
    return bytes(buf)

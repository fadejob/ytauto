"""Hosila fayllar (cutout, render, import qilingan paketlar) qayerga qo'yiladi.

Wikimedia va NASA fotolari ko'chirilmaydi: file_url - manbaning o'z CDN'i (worker video paytida o'sha yerdan yuklaydi).
Faqat biz yaratgan yoki qo'lda olingan fayllar omborga yuklanadi:
  - dir:<papka>       + YTLIB_PUBLIC_BASE  - lokal papka (sinov yoki o'z web-serveringiz)
  - supabase:<bucket>                     - Supabase Storage (public bucket); SUPABASE_URL va SUPABASE_SERVICE_KEY kerak
"""
from __future__ import annotations

import hashlib
import mimetypes
import os
from pathlib import Path

import requests


class Store:
    def put(self, rel: str, data: bytes) -> str:  # pragma: no cover - interfeys
        raise NotImplementedError


class DirStore(Store):
    def __init__(self, root: str, public_base: str):
        self.root = Path(root)
        self.base = public_base.rstrip("/")
        if not self.base:
            raise SystemExit("dir: ombori uchun YTLIB_PUBLIC_BASE (masalan http://127.0.0.1:8765) kerak.")

    def put(self, rel: str, data: bytes) -> str:
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
        return f"{self.base}/{rel}"


class SupabaseStore(Store):
    def __init__(self, bucket: str):
        self.url = os.environ.get("SUPABASE_URL", "").rstrip("/")
        self.key = os.environ.get("SUPABASE_SERVICE_KEY", "")
        self.bucket = bucket
        if not self.url or not self.key:
            raise SystemExit("supabase: ombori uchun SUPABASE_URL va SUPABASE_SERVICE_KEY kerak.")

    def put(self, rel: str, data: bytes) -> str:
        ctype = mimetypes.guess_type(rel)[0] or "application/octet-stream"
        r = requests.post(f"{self.url}/storage/v1/object/{self.bucket}/{rel}", data=data, timeout=120,
                          headers={"Authorization": f"Bearer {self.key}", "apikey": self.key, "Content-Type": ctype,
                                   "x-upsert": "true", "Cache-Control": "max-age=31536000"})
        if r.status_code >= 300:
            raise RuntimeError(f"Supabase Storage {r.status_code}: {r.text[:200]}")
        return f"{self.url}/storage/v1/object/public/{self.bucket}/{rel}"


def from_spec(spec: str | None) -> Store | None:
    if not spec:
        return None
    kind, _, arg = spec.partition(":")
    if kind == "dir":
        return DirStore(arg, os.environ.get("YTLIB_PUBLIC_BASE", ""))
    if kind == "supabase":
        return SupabaseStore(arg or "library")
    raise SystemExit(f"Noma'lum ombor: {spec} (dir:<papka> yoki supabase:<bucket>)")


def content_name(source: str, data: bytes, ext: str) -> str:
    h = hashlib.sha256(data).hexdigest()
    return f"{source}/{h[:2]}/{h[:24]}{ext}"

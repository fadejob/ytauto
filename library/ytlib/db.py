"""ytauto.assets jadvali bilan ishlash (egasi ulanishi: YTLIB_DATABASE_URL)."""
from __future__ import annotations

import os
from dataclasses import dataclass, field

import psycopg


@dataclass
class Asset:
    source: str
    source_id: str
    kind: str                      # photo | cutout | render | icon | texture
    title: str
    file_url: str
    license: str
    preview_url: str = ""
    tags: str = ""
    description: str = ""
    width: int = 0
    height: int = 0
    has_alpha: bool = False
    bytes: int = 0
    phash: str = ""
    sharpness: float = 0.0
    quality: float = 0.5
    license_url: str = ""
    author: str = ""
    page_url: str = ""
    needs_credit: bool = False
    parent_id: int | None = None
    extra: dict = field(default_factory=dict)


COLUMNS = ("source", "source_id", "kind", "title", "tags", "description", "file_url", "preview_url", "width", "height",
           "has_alpha", "bytes", "phash", "sharpness", "quality", "license", "license_url", "author", "page_url",
           "needs_credit", "parent_id")


def connect(url: str | None = None) -> psycopg.Connection:
    url = url or os.environ.get("YTLIB_DATABASE_URL", "")
    if not url:
        raise SystemExit("YTLIB_DATABASE_URL o'rnatilmagan (kutubxona bazasining egasi ulanish satri).")
    return psycopg.connect(url, autocommit=True)


def ensure_schema(conn: psycopg.Connection) -> None:
    """Jadval bo'lmasa - aniq xato (sxemani bot `npm run db:migrate` yoki schema.sql bilan qo'llash kerak)."""
    row = conn.execute("SELECT to_regclass('ytauto.assets')").fetchone()
    if not row or row[0] is None:
        raise SystemExit("ytauto.assets jadvali yo'q: avval bot/db/schema.sql (v9+) ni qo'llang.")


def exists(conn: psycopg.Connection, source: str, source_id: str, kind: str) -> bool:
    return conn.execute("SELECT 1 FROM ytauto.assets WHERE source=%s AND source_id=%s AND kind=%s",
                        (source, source_id, kind)).fetchone() is not None


def known_ids(conn: psycopg.Connection, source: str) -> set[str]:
    return {r[0] for r in conn.execute("SELECT source_id FROM ytauto.assets WHERE source=%s", (source,))}


def phashes(conn: psycopg.Connection) -> list[str]:
    return [r[0] for r in conn.execute("SELECT phash FROM ytauto.assets WHERE phash <> '' AND kind IN ('photo','render')")]


def insert(conn: psycopg.Connection, a: Asset) -> int | None:
    vals = [getattr(a, c) for c in COLUMNS]
    row = conn.execute(
        f"INSERT INTO ytauto.assets({', '.join(COLUMNS)}) VALUES ({', '.join(['%s'] * len(COLUMNS))}) "
        "ON CONFLICT (source, source_id, kind) DO NOTHING RETURNING id", vals).fetchone()
    return row[0] if row else None


def stats(conn: psycopg.Connection) -> list[tuple]:
    return conn.execute(
        "SELECT source, kind, count(*), round(avg(quality)::numeric, 2), sum(needs_credit::int) "
        "FROM ytauto.assets WHERE NOT disabled GROUP BY 1, 2 ORDER BY 1, 2").fetchall()


def search(conn: psycopg.Connection, keywords: list[str], limit: int = 10) -> list[tuple]:
    """Worker'dagi qidiruv bilan bir xil mantiq (sinov uchun)."""
    q = " or ".join(f'"{k}"' if " " in k else k for k in keywords if k.strip())
    return conn.execute(
        "SELECT id, kind, title, width, height, license, round((ts_rank_cd(search, q, 32) * (0.5 + quality))::numeric, 4) AS r "
        "FROM ytauto.assets, websearch_to_tsquery('english', %s) q WHERE NOT disabled AND search @@ q "
        "ORDER BY r DESC LIMIT %s", (q, limit)).fetchall()

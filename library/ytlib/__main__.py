"""Kutubxona zavodi CLI.

  python -m ytlib ingest wikimedia --topics topics/science.txt --per-topic 40 [--cutouts --store dir:...]
  python -m ytlib ingest nasa      --topics topics/space.txt   --per-topic 30
  python -m ytlib ingest polyhaven --blender "C:/Program Files/Blender Foundation/Blender 4.2/blender.exe" --limit 40 --store supabase:library
  python -m ytlib import-dir <papka> --source 3dicons --kind icon --license CC0 --store supabase:library
  python -m ytlib stats
  python -m ytlib search mitochondria "cell organelle"

Muhit: YTLIB_DATABASE_URL (baza egasi), YTLIB_CONTACT (Wikimedia UA uchun sayt/email), ombor uchun YTLIB_PUBLIC_BASE yoki
SUPABASE_URL + SUPABASE_SERVICE_KEY.
"""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

from . import db, licenses
from .pipeline import Ingestor
from .store import from_spec

HERE = Path(__file__).resolve().parent.parent


def read_topics(path: str | None, inline: list[str]) -> list[str]:
    out = list(inline or [])
    if path:
        for line in Path(path).read_text(encoding="utf-8").splitlines():
            line = line.split("#", 1)[0].strip()
            if line:
                out.append(line)
    return list(dict.fromkeys(out))


def cmd_ingest(a: argparse.Namespace) -> None:
    conn = db.connect()
    db.ensure_schema(conn)
    ing = Ingestor(conn, from_spec(a.store), cutouts=a.cutouts, dry_run=a.dry_run)
    if a.source in ("wikimedia", "nasa"):
        topics = read_topics(a.topics, a.topic)
        if not topics:
            raise SystemExit("--topics fayl yoki --topic kerak")
        for i, t in enumerate(topics, 1):
            print(f"[{i}/{len(topics)}] {a.source}: {t}")
            if a.source == "wikimedia":
                from .sources import wikimedia
                got = 0
                for curated in (True, False):
                    if got >= a.per_topic:
                        break
                    for asset in wikimedia.search(t, curated, a.per_topic * 3):
                        if ing.add(asset):
                            got += 1
                        if got >= a.per_topic:
                            break
            else:
                from .sources import nasa
                got = 0
                for asset in nasa.search(t, a.per_topic * 3):
                    if ing.add(asset):
                        got += 1
                    if got >= a.per_topic:
                        break
            print(f"  -> {ing.c.line()}")
    elif a.source == "polyhaven":
        from .sources import polyhaven
        blender = a.blender or os.environ.get("YTLIB_BLENDER", "")
        if not blender or not Path(blender).exists():
            raise SystemExit("--blender <blender.exe> kerak (Blender 3.6+; GPU shart emas)")
        if ing.store is None and not a.dry_run:
            raise SystemExit("renderlar omborga yuklanadi: --store kerak")
        cache = Path(a.cache or (HERE / ".cache"))
        known = db.known_ids(conn, "polyhaven")
        cats = [c for c in (a.categories or "").split(",") if c]
        for asset, data in polyhaven.assets(blender, cache, a.limit, cats or None, a.views.split(","), a.size, known):
            ing.add(asset, data)
        print(f"  -> {ing.c.line()}")
    print("Tayyor:", ing.c.line())


def cmd_import_dir(a: argparse.Namespace) -> None:
    from .sources import localdir
    lic = licenses.parse_named(a.license)
    if lic is None:
        raise SystemExit("--license: CC0, PD yoki 'CC BY 4.0' (SA/NC/ND qabul qilinmaydi)")
    conn = db.connect()
    db.ensure_schema(conn)
    store = from_spec(a.store)
    if store is None:
        raise SystemExit("--store kerak")
    ing = Ingestor(conn, store, dry_run=a.dry_run)
    for asset, data in localdir.scan(a.dir, a.source, a.kind, lic, a.author, a.page_url):
        ing.add(asset, data)
    print("Tayyor:", ing.c.line())


def cmd_stats(_: argparse.Namespace) -> None:
    conn = db.connect()
    rows = db.stats(conn)
    total = sum(r[2] for r in rows)
    print(f"{'manba':12} {'tur':8} {'soni':>7} {'sifat':>6} {'CC BY':>6}")
    for s, k, n, q, cr in rows:
        print(f"{s:12} {k:8} {n:7} {q:6} {cr:6}")
    print(f"Jami: {total}")


def cmd_search(a: argparse.Namespace) -> None:
    conn = db.connect()
    for r in db.search(conn, a.keywords, a.limit):
        print(r)


def main(argv: list[str] | None = None) -> None:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(prog="ytlib", description="YtAuto rasm kutubxonasi zavodi")
    sub = p.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("ingest")
    g.add_argument("source", choices=["wikimedia", "nasa", "polyhaven"])
    g.add_argument("--topics", help="mavzular fayli (har qatorda bitta, # izoh)")
    g.add_argument("--topic", action="append", default=[])
    g.add_argument("--per-topic", type=int, default=30)
    g.add_argument("--store", help="dir:<papka> yoki supabase:<bucket> (cutout/render uchun)")
    g.add_argument("--cutouts", action="store_true", help="fotolardan fondan ajratilgan PNG ham (rembg kerak)")
    g.add_argument("--dry-run", action="store_true")
    g.add_argument("--blender")
    g.add_argument("--limit", type=int, default=20)
    g.add_argument("--categories", default="")
    g.add_argument("--views", default="front34,front,side,top34")
    g.add_argument("--size", type=int, default=1600)
    g.add_argument("--cache")
    g.set_defaults(fn=cmd_ingest)
    d = sub.add_parser("import-dir")
    d.add_argument("dir")
    d.add_argument("--source", required=True)
    d.add_argument("--kind", default="icon", choices=["icon", "cutout", "render", "photo", "texture", "diagram"])
    d.add_argument("--license", required=True)
    d.add_argument("--author", default="")
    d.add_argument("--page-url", default="")
    d.add_argument("--store", required=True)
    d.add_argument("--dry-run", action="store_true")
    d.set_defaults(fn=cmd_import_dir)
    s = sub.add_parser("stats")
    s.set_defaults(fn=cmd_stats)
    q = sub.add_parser("search")
    q.add_argument("keywords", nargs="+")
    q.add_argument("--limit", type=int, default=10)
    q.set_defaults(fn=cmd_search)
    a = p.parse_args(argv)
    a.fn(a)


if __name__ == "__main__":
    main()

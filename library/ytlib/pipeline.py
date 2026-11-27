"""Nomzod -> yuklab olish -> sifat tekshiruvi -> takror (pHash) -> bazaga yozish. Ixtiyoriy: fondan ajratilgan cutout."""
from __future__ import annotations

import io
from dataclasses import dataclass, field

from PIL import Image

from . import db, net, quality
from .db import Asset
from .store import Store, content_name

PART_KINDS = ("cutout", "render", "icon", "diagram", "texture")
DUP_DISTANCE = 6


@dataclass
class Counters:
    added: int = 0
    known: int = 0
    rejected: dict[str, int] = field(default_factory=dict)

    def reject(self, why: str) -> None:
        key = why.split(" (")[0]
        self.rejected[key] = self.rejected.get(key, 0) + 1

    def line(self) -> str:
        rej = ", ".join(f"{k}: {v}" for k, v in sorted(self.rejected.items(), key=lambda x: -x[1]))
        return f"qo'shildi {self.added}, oldin bor {self.known}, rad etildi {sum(self.rejected.values())}" + (f" ({rej})" if rej else "")


class Ingestor:
    def __init__(self, conn, store: Store | None = None, cutouts: bool = False, dry_run: bool = False):
        self.conn = conn
        self.store = store
        self.cutouts = cutouts
        self.dry = dry_run
        self.hashes = db.phashes(conn)
        self.known: dict[str, set[str]] = {}
        self.c = Counters()
        self._rembg = None

    def is_known(self, a: Asset) -> bool:
        if a.source not in self.known:
            self.known[a.source] = db.known_ids(self.conn, a.source)
        return a.source_id in self.known[a.source]

    def add(self, a: Asset, data: bytes | None = None) -> int | None:
        """Bitta nomzod. data berilmasa file_url'dan yuklanadi. Qaytaradi: yangi id yoki None."""
        if self.is_known(a):
            self.c.known += 1
            return None
        try:
            if data is None:
                try:
                    data = net.download(a.file_url)
                except Exception:
                    fb = a.extra.get("fallback_url")
                    if not fb:
                        raise
                    data = net.download(fb)
                    a.file_url = fb
            st = quality.analyze(data)
        except Exception as e:
            self.c.reject(f"yuklanmadi ({e})")
            return None
        part = a.kind in PART_KINDS
        why = quality.reject_reason(st, part)
        fb = a.extra.get("fallback_url")
        if why and why.startswith("kichik") and fb and fb != a.file_url:
            # NASA: ~large kichik bo'lsa asl fayl (~orig) olinadi.
            try:
                big = net.download(fb)
                st2 = quality.analyze(big)
                if st2.long_side > st.long_side:
                    data, st, a.file_url = big, st2, fb
                    why = quality.reject_reason(st, part)
            except Exception:
                pass
        if why:
            self.c.reject(why)
            return None
        if a.kind in ("photo", "render") and any(quality.hamming(st.phash, h) <= DUP_DISTANCE for h in self.hashes):
            self.c.reject("takror")
            return None
        a.width, a.height, a.has_alpha, a.bytes = st.width, st.height, st.has_alpha, len(data)
        a.phash, a.sharpness = st.phash, st.sharpness
        a.quality = quality.score(st, float(a.extra.get("curated", 0.5)))
        if self.dry:
            self.c.added += 1
            print(f"  [dry] {a.kind:7} {st.width}x{st.height} q={a.quality:.2f} {a.license:13} {a.title[:60]}")
            return None
        if not a.file_url:
            # Hosila/qo'lda olingan fayl: faqat sifatdan o'tgach omborga yuklanadi.
            if self.store is None:
                raise SystemExit("Bu manba uchun --store kerak (dir:<papka> yoki supabase:<bucket>).")
            a.file_url = self.store.put(a.extra["upload"], data)
            a.preview_url = a.preview_url or self._preview(a.extra["upload"], data)
        new_id = db.insert(self.conn, a)
        if new_id is None:
            self.c.known += 1
            return None
        self.c.added += 1
        self.known.setdefault(a.source, set()).add(a.source_id)
        if a.kind in ("photo", "render"):
            self.hashes.append(st.phash)
        print(f"  + #{new_id} {a.kind:7} {st.width}x{st.height} q={a.quality:.2f} {a.license:13} {a.title[:60]}")
        if self.cutouts and a.kind == "photo" and self.store is not None:
            self.make_cutout(new_id, a, data)
        return new_id

    def _preview(self, name: str, data: bytes) -> str:
        im = quality.open_image(data)
        im.thumbnail((480, 480), Image.LANCZOS)
        buf = io.BytesIO()
        if im.mode in ("RGBA", "LA", "P"):
            im.convert("RGBA").save(buf, "PNG", optimize=True)
            ext = ".png"
        else:
            im.convert("RGB").save(buf, "JPEG", quality=85)
            ext = ".jpg"
        stem = name.rsplit(".", 1)[0]
        return self.store.put(f"{stem}.preview{ext}", buf.getvalue())

    # ------------------------------------------------------------ cutout (fondan ajratish, rembg/BiRefNet)
    def make_cutout(self, parent_id: int, a: Asset, data: bytes) -> int | None:
        try:
            if self._rembg is None:
                from rembg import new_session, remove  # type: ignore
                self._rembg = (new_session("isnet-general-use"), remove)
            sess, remove = self._rembg
            im = quality.open_image(data).convert("RGB")
            im.thumbnail((2048, 2048), Image.LANCZOS)
            cut = remove(im, session=sess)
        except ImportError:
            print("  cutout o'tkazildi: rembg o'rnatilmagan (pip install rembg)")
            self.cutouts = False
            return None
        except Exception as e:
            self.c.reject(f"cutout xato ({e})")
            return None
        bbox = cut.getchannel("A").point(lambda v: 255 if v > 24 else 0).getbbox()
        if not bbox:
            return None
        cut = cut.crop(bbox)
        area = (bbox[2] - bbox[0]) * (bbox[3] - bbox[1]) / float(im.width * im.height)
        alpha = cut.getchannel("A")
        fill = sum(1 for v in alpha.getdata() if v > 128) / float(max(1, cut.width * cut.height))
        # Obyekt kadrning bir qismi bo'lishi va aniq siluet berishi kerak (butun fon yoki parcha-parcha shovqin emas).
        if not (0.04 < area < 0.92) or fill < 0.25 or max(cut.size) < quality.MIN_LONG_SIDE_PART:
            self.c.reject("cutout yaroqsiz")
            return None
        buf = io.BytesIO()
        cut.save(buf, "PNG", optimize=True)
        png = buf.getvalue()
        prev = cut.copy()
        prev.thumbnail((480, 480))
        pbuf = io.BytesIO()
        prev.save(pbuf, "PNG", optimize=True)
        url = self.store.put(content_name("cutout", png, ".png"), png)
        purl = self.store.put(content_name("cutout-preview", pbuf.getvalue(), ".png"), pbuf.getvalue())
        c = Asset(source=a.source, source_id=a.source_id, kind="cutout", title=a.title, file_url=url, preview_url=purl,
                  tags=(a.tags + ", cutout, isolated, transparent background").strip(", "), description=a.description,
                  width=cut.width, height=cut.height, has_alpha=True, bytes=len(png), phash=quality.phash(cut),
                  sharpness=a.sharpness, quality=round(a.quality * 0.95, 3), license=a.license, license_url=a.license_url,
                  author=a.author, page_url=a.page_url, needs_credit=a.needs_credit, parent_id=parent_id)
        new_id = db.insert(self.conn, c)
        if new_id:
            self.c.added += 1
            print(f"    cutout #{new_id} {cut.width}x{cut.height}")
        return new_id

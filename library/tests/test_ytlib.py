"""Tarmoqsiz testlar:  python -m unittest discover -s library/tests -t library"""
import io
import unittest

import numpy as np
from PIL import Image, ImageFilter

from ytlib import licenses, quality
from ytlib.sources import localdir, nasa, wikimedia


def png(im: Image.Image, fmt: str = "PNG") -> bytes:
    b = io.BytesIO()
    im.save(b, fmt)
    return b.getvalue()


def noise(w: int, h: int, seed: int = 1) -> Image.Image:
    rng = np.random.default_rng(seed)
    return Image.fromarray(rng.integers(0, 255, (h, w, 3), dtype=np.uint8), "RGB")


class LicenseTests(unittest.TestCase):
    def test_free_licenses_accepted(self):
        self.assertEqual(licenses.classify("cc0").short, "CC0")
        self.assertEqual(licenses.classify("pd").short, "Public domain")
        self.assertEqual(licenses.classify("", "Public domain").short, "Public domain")
        by = licenses.classify("cc-by-4.0", "CC BY 4.0")
        self.assertEqual(by.short, "CC BY 4.0")
        self.assertTrue(by.needs_credit)
        self.assertEqual(licenses.classify("cc-by-2.0").short, "CC BY 2.0")

    def test_sa_nc_nd_unknown_rejected(self):
        for code, name in [("cc-by-sa-4.0", "CC BY-SA 4.0"), ("cc-by-nc-2.0", "CC BY-NC 2.0"), ("cc-by-nd-3.0", ""),
                           ("", "GFDL 1.2"), ("", ""), ("arr", "All rights reserved")]:
            self.assertIsNone(licenses.classify(code, name), (code, name))

    def test_named(self):
        self.assertEqual(licenses.parse_named("CC0").short, "CC0")
        self.assertEqual(licenses.parse_named("cc by 4").short, "CC BY 4.0")
        self.assertIsNone(licenses.parse_named("CC BY-SA 4.0"))


class QualityTests(unittest.TestCase):
    def test_sharp_vs_blurry(self):
        sharp = noise(2000, 1400)
        blurry = sharp.filter(ImageFilter.GaussianBlur(12))
        s1, s2 = quality.analyze(png(sharp, "JPEG")), quality.analyze(png(blurry, "JPEG"))
        self.assertGreater(s1.sharpness, quality.MIN_SHARPNESS)
        self.assertLess(s2.sharpness, quality.MIN_SHARPNESS)
        self.assertIsNone(quality.reject_reason(s1, part=False))
        self.assertIn("xira", quality.reject_reason(s2, part=False))
        self.assertIsNone(quality.reject_reason(s2, part=True))  # cutout/render uchun tiniqlik majburiy emas (silliq fon)

    def test_small_and_stretched_rejected(self):
        self.assertIn("kichik", quality.reject_reason(quality.analyze(png(noise(1000, 800))), part=False))
        self.assertIsNone(quality.reject_reason(quality.analyze(png(noise(1000, 800))), part=True))
        self.assertIn("cho'zinchoq", quality.reject_reason(quality.analyze(png(noise(3300, 900))), part=False))

    def test_phash_finds_resized_duplicate(self):
        a = noise(1600, 1200, 3).filter(ImageFilter.GaussianBlur(3))
        b = a.resize((800, 600))
        c = noise(1600, 1200, 4).filter(ImageFilter.GaussianBlur(3))
        ha, hb, hc = quality.phash(a), quality.phash(b), quality.phash(c)
        self.assertLessEqual(quality.hamming(ha, hb), 6)
        self.assertGreater(quality.hamming(ha, hc), 6)

    def test_alpha_and_orientation(self):
        im = Image.new("RGBA", (1200, 1600), (0, 0, 0, 0))
        im.paste((200, 50, 50, 255), (300, 300, 900, 1300))
        st = quality.analyze(png(im))
        self.assertTrue(st.has_alpha)
        self.assertEqual(st.orientation, "portrait")
        self.assertFalse(quality.analyze(png(noise(1200, 800))).has_alpha)

    def test_score_range(self):
        st = quality.analyze(png(noise(3000, 2000), "JPEG"))
        self.assertGreater(quality.score(st, 1.0), 0.8)
        self.assertLessEqual(quality.score(st, 1.0), 1.0)


class SourceParsingTests(unittest.TestCase):
    def test_wikimedia_page_to_asset(self):
        page = {"pageid": 42, "title": "File:Bee.jpg", "index": 1, "imageinfo": [{
            "mime": "image/jpeg", "width": 4000, "height": 3000,
            "thumburl": "https://upload.wikimedia.org/wikipedia/commons/thumb/a/ab/Bee.jpg/1920px-Bee.jpg",
            "descriptionurl": "https://commons.wikimedia.org/wiki/File:Bee.jpg",
            "extmetadata": {"License": {"value": "cc-by-4.0"}, "LicenseShortName": {"value": "CC BY 4.0"},
                            "Artist": {"value": "<a href='x'>Jane Doe</a>"}, "ObjectName": {"value": "Honey bee"},
                            "Categories": {"value": "Apis mellifera|Featured pictures on Wikimedia Commons|CC-BY-4.0"},
                            "ImageDescription": {"value": "A <b>bee</b> on lavender"}}}]}
        a = wikimedia._to_asset(page, curated=True)
        self.assertEqual((a.title, a.author, a.license, a.needs_credit), ("Honey bee", "Jane Doe", "CC BY 4.0", True))
        self.assertEqual(a.description, "A bee on lavender")
        self.assertIn("Apis mellifera", a.tags)
        self.assertNotIn("CC-BY", a.tags)
        self.assertTrue(a.preview_url.endswith("/480px-Bee.jpg"))
        self.assertEqual(a.extra["curated"], 1.0)

    def test_wikimedia_restricted_or_sa_skipped(self):
        base = {"pageid": 1, "title": "File:x.jpg", "imageinfo": [{"mime": "image/jpeg", "url": "u", "extmetadata": {}}]}
        base["imageinfo"][0]["extmetadata"] = {"License": {"value": "cc-by-sa-4.0"}}
        self.assertIsNone(wikimedia._to_asset(base, True))
        base["imageinfo"][0]["extmetadata"] = {"License": {"value": "cc0"}, "Restrictions": {"value": "trademarked"}}
        self.assertIsNone(wikimedia._to_asset(base, True))

    def test_wikimedia_people_skipped(self):
        def page(title, cats, desc=""):
            return {"pageid": 7, "title": f"File:{title}.jpg", "imageinfo": [{"mime": "image/jpeg", "url": "u", "extmetadata": {
                "License": {"value": "cc0"}, "Categories": {"value": cats}, "ImageDescription": {"value": desc}}}]}
        self.assertIsNone(wikimedia._to_asset(page("Girl in lavender field", "Lavender fields"), True))
        self.assertIsNone(wikimedia._to_asset(page("Lavender 3", "Children in Provence|Lavender"), True))
        self.assertIsNone(wikimedia._to_asset(page("Field", "Lavender", "A woman walking"), True))
        self.assertIsNotNone(wikimedia._to_asset(page("Lavender field at dusk", "Lavandula angustifolia|Human eye diagrams"), True))
        self.assertIsNotNone(wikimedia._to_asset(page("Hylaeus taeniolatus female 1", "Hylaeus|Bees of Australia"), True))  # hasharot jinsi

    def test_nasa_copyright_and_logos_skipped(self):
        def item(title, desc="", kw=()):
            return {"data": [{"nasa_id": "x1", "title": title, "description": desc, "keywords": list(kw)}],
                    "links": [{"rel": "preview", "href": "https://images-assets.nasa.gov/image/x1/x1~thumb.jpg"}]}
        a = nasa._to_asset(item("Saturn rings", "Cassini view"))
        self.assertEqual(a.file_url, "https://images-assets.nasa.gov/image/x1/x1~large.jpg")
        self.assertEqual(a.extra["fallback_url"], "https://images-assets.nasa.gov/image/x1/x1~orig.jpg")
        self.assertIsNone(nasa._to_asset(item("Launch", "Photo © 2020 SpaceX")))
        self.assertIsNone(nasa._to_asset(item("NASA logo", "")))
        self.assertIsNone(nasa._to_asset(item("Crew", "", ["mission patch"])))

    def test_localdir_words(self):
        self.assertEqual(localdir.words("dna-helix_iso_color"), "dna helix")
        self.assertEqual(localdir.words("Microscope-front-premium@2x"), "microscope 2x")


if __name__ == "__main__":
    unittest.main()

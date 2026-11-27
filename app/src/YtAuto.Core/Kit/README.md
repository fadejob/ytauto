# film/kit - motion and composition kit

Ready-made building blocks for premium, layered scenes. Use them instead of writing the same animation code in every page.
Everything draws from `t` only (OpenFilm's rule). A page in `film/` loads them like this:

```html
<!doctype html><html><head><meta charset="utf-8">
<link rel="stylesheet" href="kit/kit.css"><link rel="stylesheet" href="style.css">
</head><body>
<div class="k-stage">
  <div class="k-layer" id="bg"><img class="k-photo-bg" src="assets/stock/s03/img1.jpg"><img class="k-photo" src="assets/stock/s03/img1.jpg"></div>
  <div class="k-layer" id="objs">
    <img class="k-cutout" src="assets/stock/s03/img2.png" data-x="300" data-y="900" data-r="-8" data-s="0.9" data-depth="0.8" style="width:520px">
    <img class="k-cutout" src="assets/stock/s03/img4.png" data-x="780" data-y="760" data-r="6" data-depth="0.5" style="width:420px">
  </div>
  <div class="k-headline" id="h" style="left:80px;top:220px;font-size:120px">Bees <span class="k-accent">see</span> UV</div>
  <div class="k-vignette"></div><div class="k-grain"></div>
</div>
<script type="module">
import * as K from "./kit/kit.js";
const D = 3.2;                                   // this page's length (= its clip length in film.html)
K.page(D, (t) => {
  K.kenBurns(document.getElementById("bg"), t, D, { to: 1.1, x: -30 });
  K.collage(document.querySelectorAll("#objs .k-cutout"), t, 0.15);
  K.reveal(document.getElementById("h"), t, 0.0, { from: "up", dur: 0.45 });
});
</script></body></html>
```

## kit.js

| function | what it does |
|---|---|
| `page(duration, draw)` | declares `window.film` (waits for images) |
| `prog(t, at, dur, ease)` / `ease.*` / `lerp` / `clamp` / `rng(seed)` | timing helpers; `rng` is deterministic (never use Math.random) |
| `kenBurns(el, t, total, {from,to,x,y})` | slow push/pan on a photo or layer |
| `cameraPush(el, t, total, {to, fx, fy})` | push towards a focus point (hero scenes) |
| `parallax(els, t, total, {dx,dy,zoom})` | 2.5D depth: elements with `data-depth` 0 (far) .. 1 (near) move at different speeds |
| `reveal(el, t, at, {from, dur, dist})` | entrance: up / down / left / right / scale / fade / blur |
| `stagger(els, t, at, gap, opts)` | the same entrance for a list, one after another |
| `collage(els, t, at, {gap, spread, seed})` | cut-outs fly in from scattered positions (`data-x`, `data-y`, `data-r`, `data-s`, `data-depth`), then float |
| `float(el, t, {amp, period})` | gentle continuous bob so a held frame stays alive |
| `typeOn(el, text, t, at, cps)` | typewriter with caret (notes app, search bar, chat) |
| `countUp(el, t, at, dur, to, {suffix})` | numbers counting up |
| `drawPath(path, t, at, dur)` | draws an SVG arrow/outline/leader line |
| `wall = wordWallBuild(el, phrase, rows, {hot})` + `wordWall(wall, t)` | the repeated-phrase kinetic wall, painted on a canvas (rows may run off the frame on purpose); style it with `.k-wall` + `font-size` |
| `whipOut(el, t, total)` | fast blur/slide out in the last 0.25 s: an edited cut instead of a fade |

## kit.css

`.k-stage` `.k-layer` `.k-photo` `.k-photo-bg` (blurred fill behind a photo of another shape) `.k-scrim` (top/bottom darkening for text)
`.k-cutout` (contact shadow for PNG objects) `.k-glow` `.k-card` `.k-glass` `.k-polaroid` `.k-phone` + `.k-screen` (phone mockup)
`.k-headline` `.k-kicker` `.k-accent` `.k-accent2` `.k-outline` `.k-label` (pill label) `.k-number` `.k-wall` `.k-vignette` `.k-grain` `.k-lightleak`.

Colours and fonts are CSS variables (`--k-bg`, `--k-accent`, `--k-font`, ...): set them in `film/style.css` from the template so the kit
follows the channel style. Do not edit files in `kit/`; copy a function into a page only if you need to change it.

// YtAuto motion kit for OpenFilm pages. Every function draws from t only (the same t always gives the same picture):
// no timers, no Math.random, no state kept between frames. Import it from a page:  import * as K from "./kit/kit.js";

export const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
export const lerp = (a, b, p) => a + (b - a) * p;

export const ease = {
  linear: (p) => p,
  out: (p) => 1 - Math.pow(1 - p, 3),
  in: (p) => p * p * p,
  inOut: (p) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2),
  expo: (p) => (p >= 1 ? 1 : 1 - Math.pow(2, -10 * p)),
  back: (p) => { const c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(p - 1, 3) + c1 * Math.pow(p - 1, 2); },
};

/** Progress 0..1 of an animation that starts at `at` and lasts `dur` seconds, eased. */
export function prog(t, at, dur, e = ease.out) {
  return e(clamp((t - at) / Math.max(1e-6, dur)));
}

/** Deterministic pseudo-random numbers (same seed -> same sequence). */
export function rng(seed = 1) {
  let s = (seed >>> 0) || 1;
  return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; };
}

/** Wait for every <img> in the page (use as window.film.ready). */
export function imagesReady(root = document) {
  return Promise.all([...root.querySelectorAll("img")].map((im) => (im.decode ? im.decode().catch(() => {}) : Promise.resolve())))
    .then(() => (document.fonts ? document.fonts.ready : null));
}

/** Slow camera move on a photo or a whole layer: scale from->to and pan (px) over the scene. */
export function kenBurns(el, t, total, { from = 1.0, to = 1.08, x = 0, y = 0, e = ease.inOut } = {}) {
  const p = e(clamp(t / Math.max(0.01, total)));
  el.style.transformOrigin = "50% 50%";
  el.style.transform = `translate(${lerp(0, x, p)}px, ${lerp(0, y, p)}px) scale(${lerp(from, to, p)})`;
}

/**
 * 2.5D parallax: elements with data-depth (0 = far background, 1 = foreground) drift by depth, so the camera seems to move
 * through a real space. `dx`/`dy` = total foreground travel in px; `zoom` = extra foreground scale at the end.
 */
export function parallax(els, t, total, { dx = -80, dy = 0, zoom = 0.06, e = ease.inOut } = {}) {
  const p = e(clamp(t / Math.max(0.01, total)));
  for (const el of els) {
    const d = parseFloat(el.dataset.depth ?? "0.5");
    el.style.transform = `translate(${dx * d * p}px, ${dy * d * p}px) scale(${1 + zoom * d * p})`;
  }
}

/** Entrance: fade + move/scale in, starting at `at`. from: up | down | left | right | scale | fade | blur. */
export function reveal(el, t, at, { dur = 0.5, from = "up", dist = 60, e = ease.out } = {}) {
  const p = prog(t, at, dur, e);
  const k = 1 - p;
  let tr = "";
  if (from === "up") tr = `translateY(${dist * k}px)`;
  else if (from === "down") tr = `translateY(${-dist * k}px)`;
  else if (from === "left") tr = `translateX(${-dist * k}px)`;
  else if (from === "right") tr = `translateX(${dist * k}px)`;
  else if (from === "scale") tr = `scale(${lerp(0.6, 1, p)})`;
  el.style.opacity = String(from === "blur" ? 1 : p);
  el.style.filter = from === "blur" ? `blur(${18 * k}px)` : "";
  el.style.transform = tr;
}

/** Same entrance for a list of elements, one after another (`gap` seconds apart). */
export function stagger(els, t, at, gap = 0.12, opts = {}) {
  [...els].forEach((el, i) => reveal(el, t, at + i * gap, opts));
}

/**
 * Pinterest-style collage: cut-out objects (transparent PNG) fly in from scattered positions with a slight rotation, then float.
 * Each element needs data-x, data-y (final centre in px), optional data-r (final rotation deg), data-s (scale), data-depth.
 */
export function collage(els, t, at, { gap = 0.1, dur = 0.7, spread = 260, seed = 7, floatAmp = 8 } = {}) {
  const r = rng(seed);
  [...els].forEach((el, i) => {
    const ox = (r() - 0.5) * spread * 2, oy = (r() - 0.5) * spread * 2, or = (r() - 0.5) * 40;
    const p = prog(t, at + i * gap, dur, ease.back);
    const x = parseFloat(el.dataset.x ?? "0"), y = parseFloat(el.dataset.y ?? "0");
    const rot = parseFloat(el.dataset.r ?? "0"), s = parseFloat(el.dataset.s ?? "1");
    const depth = parseFloat(el.dataset.depth ?? "0.5");
    const bob = Math.sin((t + i * 0.9) * 1.3) * floatAmp * depth;
    el.style.position = "absolute";
    el.style.left = "0"; el.style.top = "0";
    el.style.opacity = String(clamp(p * 1.4));
    el.style.transform = `translate(-50%, -50%) translate(${x + ox * (1 - p)}px, ${y + oy * (1 - p) + bob}px) rotate(${rot + or * (1 - p)}deg) scale(${s * lerp(0.7, 1, p)})`;
  });
}

/** Gentle continuous float for any element (keeps a held frame alive). */
export function float(el, t, { amp = 6, period = 4, phase = 0, rot = 0.6 } = {}) {
  const a = ((t + phase) / period) * Math.PI * 2;
  el.style.transform = `translateY(${Math.sin(a) * amp}px) rotate(${Math.sin(a * 0.7) * rot}deg)`;
}

/** Typewriter: shows `text` letter by letter from `at` (cps = characters per second), with a blinking caret span. */
export function typeOn(el, text, t, at, cps = 28, { caret = true } = {}) {
  const n = Math.floor(clamp((t - at) * cps, 0, text.length));
  const blink = Math.floor(t * 2) % 2 === 0;
  el.textContent = text.slice(0, n);
  if (caret) {
    const c = document.createElement("span");
    c.className = "k-caret";
    c.style.opacity = n < text.length || blink ? "1" : "0";
    el.appendChild(c);
  }
}

/** Number counting up to `to` between at and at+dur. */
export function countUp(el, t, at, dur, to, { decimals = 0, prefix = "", suffix = "", from = 0 } = {}) {
  const v = lerp(from, to, prog(t, at, dur, ease.out));
  el.textContent = prefix + v.toLocaleString("en-US", { minimumFractionDigits: decimals, maximumFractionDigits: decimals }) + suffix;
}

/** Draws an SVG path/line (arrows, outlines, labels' leader lines) from at to at+dur. */
export function drawPath(path, t, at, dur = 0.6) {
  const len = path.getTotalLength ? path.getTotalLength() : 1000;
  path.style.strokeDasharray = `${len}`;
  path.style.strokeDashoffset = `${len * (1 - prog(t, at, dur, ease.inOut))}`;
}

/**
 * Kinetic word wall: rows of a repeated phrase sliding in alternate directions (the "Let them replicate" look). The row whose
 * index is `hot` is drawn in the accent class. Build the rows once (wordWallBuild), then call wordWall every frame.
 */
export function wordWallBuild(container, phrase, rows = 7, { hot = 3, repeat = 6 } = {}) {
  container.innerHTML = "";
  for (let i = 0; i < rows; i++) {
    const row = document.createElement("div");
    row.className = "k-wall-row" + (i === hot ? " k-accent" : "");
    row.textContent = Array(repeat).fill(phrase).join("  ");
    container.appendChild(row);
  }
  return [...container.children];
}
export function wordWall(rowsEls, t, { speed = 90 } = {}) {
  rowsEls.forEach((row, i) => {
    const dir = i % 2 === 0 ? -1 : 1;
    row.style.transform = `translateX(${dir * speed * t - (dir > 0 ? 600 : 0)}px)`;
  });
}

/** Camera "push" on the whole composition towards a focus point (0..1 of the stage) - for hero scenes. */
export function cameraPush(el, t, total, { from = 1.0, to = 1.15, fx = 0.5, fy = 0.5, e = ease.inOut } = {}) {
  el.style.transformOrigin = `${fx * 100}% ${fy * 100}%`;
  el.style.transform = `scale(${lerp(from, to, e(clamp(t / Math.max(0.01, total))))})`;
}

/** Quick whip/zoom-blur out at the end of a scene (last `dur` seconds) - a cut that feels edited, not a fade to black. */
export function whipOut(el, t, total, { dur = 0.25, dir = -1 } = {}) {
  const p = prog(t, total - dur, dur, ease.in);
  el.style.filter = p > 0 ? `blur(${p * 14}px)` : "";
  el.style.translate = `${dir * p * 220}px 0`;
}

/** Helper: declare the page for OpenFilm. draw(t) is called for every frame. */
export function page(duration, draw, { ready } = {}) {
  window.film = { duration, frame: (t) => draw(t), ready: ready ?? imagesReady() };
}

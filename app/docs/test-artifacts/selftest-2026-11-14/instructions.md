# Task: build the OpenFilm project for one video

You are running unattended in a fresh session. Never ask questions. Work only inside this folder.
Do not render the final video and do not delete anything outside `film/`.

## Montage brief (from the channel settings)

You are a motion designer building an explainer video in OpenFilm, unattended.
Never ask questions. Work only inside this folder. Do not render the final video: the worker renders it.

Design rules:
- Keep the template's style (colours, fonts, layout) if a template or style reference is given; otherwise use a clean dark theme with one accent colour.
- Every scene explains its narration with a simple, meaningful visual: diagrams, icons drawn in SVG/CSS, charts, keyword typography, simple shape motion.
  No random filler, no unrelated stock imagery, no lorem ipsum.
- Large, readable text: headline at least 56 px on a 1920x1080 stage (scale with the stage), max 6 words, keep 6% safe margins on every side so nothing is cut off.
  On a vertical 1080x1920 stage keep text in the middle 80% of the width.
- Smooth motion that depends only on t. No flashing, no fast strobing.
- Do not draw subtitles inside pages: subtitles come from voice.vtt (the worker burns them in).
  Leave the lower 22% of the stage free of important text so subtitles do not cover it.

Timeline rules (strict):
- The first visual clip starts at 0.0 s. Visual clips cover the whole audio from 0 to its end with no gap; a moment with no clip is black, which is an error.
- Scene cuts follow the meaning of the narration (the scene plan), not every subtitle cue. During pauses in the voice the picture keeps going.
- The last visual clip ends at or after the end of the audio.
- If you add background music, put it on its own track at volume 0.15 or lower so speech stays clear. The voice track stays at volume 1.
- An intentionally black scene is allowed only if the scene plan says so; then mention it in BUILD_NOTES.md.

When finished, check your work with `openfilm look` (contact sheet) and open several of the frames it saved,
fix anything cut off, unreadable or empty, then write BUILD_NOTES.md as instructed and reply DONE.

## Read first

- `openfilm-edit-skill.md`: OpenFilm's own rules for film.html and pages. Read it whole before writing anything.
- `script.json`: narration, scene plan (visual idea and on-screen text per scene), title.

## Materials

- `film/assets/voice.wav`: the narration, **41.421 seconds**. You cannot hear it; use the timings below.
- `film/assets/voice.vtt`: word-timed subtitles (WebVTT) beside the voice. Do not edit, move or rename it.

## Scene plan with real timings (seconds in the audio)

| # | start | end | narration | visual idea | on-screen text |
|---|---|---|---|---|---|
| 1 | 0.00 | 6.94 | Every night at one o'clock, a small program wakes up on this computer. | A dark desk scene: a clock showing 01:00 and a small glowing computer icon that lights up. | 01:00 - worker wakes |
| 2 | 6.94 | 13.82 | First, it takes the oldest topic from the queue and writes a short script. | A vertical queue of topic cards; the bottom (oldest) card slides out and turns into a page of text lines. | Oldest topic → script |
| 3 | 13.82 | 23.28 | Then a local voice engine reads the script aloud, and a speech model finds the exact time of every word. | A waveform grows from left to right; small word labels snap onto the waveform with time ticks. | Voice + word timing |
| 4 | 23.28 | 31.96 | Next, an editor builds the scenes, and the video is rendered and checked frame by frame. | Four scene thumbnails assemble on a timeline; a check mark passes over each frame. | Build, render, check |
| 5 | 31.96 | 41.42 | Only a video that passes every check is saved and scheduled. By morning, the work is done. | A shield with a check mark, then a calendar page with a sunrise. | Checked → scheduled |

Scene start/end come from the real voice. Make one page per scene and cut them end to end on one track,
so the first scene starts at 0 and the last one ends at or after the audio end. Small adjustments are fine; gaps are not.

## Hard requirements (the worker checks them automatically)

1. `film/film.html` with `<meta name="viewport" content="width=1920, height=1080">`.
2. Visual clips cover 0.00 → 41.42 s with no gap (every page clip has `#t=0,<length>` and an `at`).
3. `assets/voice.wav` on its own audio track, `at="0"`, volume 1, speed 1. Optional music on another track at volume ≤ 0.2.
4. No subtitles drawn in pages (they are burned in from voice.vtt). Keep the lower 22% of the frame free of key text.
5. Every page follows "the picture depends only on t". No external network resources (no CDN fonts or images); everything local.
6. All text inside the safe area and readable on a phone. Nothing cut off at the edges.
7. Leave `film/assets/voice.wav` and `film/assets/voice.vtt` untouched.

## Check, then finish

- Run `openfilm look` in `film/` and open the contact sheet PNGs it prints. Use `openfilm look <t>` for single frames around each scene start.
- Fix anything empty, cut off, overlapping or unreadable, then look again.
- Write `film/BUILD_NOTES.md`: the files you made, each scene's start/end, and for any scene that is black on purpose a line `intentionally black: <page>.html`.
- Reply DONE.

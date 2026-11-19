namespace YtAuto.Core.Domain;

/// <summary>
/// Standart promptlar. Foydalanuvchi ularni Sozlamalar oynasida o'zgartiradi; app ichida saqlanadi.
/// Placeholderlar: {topic} {notes} {language} {language_name} {length_rule} {format} {fact_sheet}
/// Davomiylik oldindan belgilanmaydi: video uzunligi tayyor ovoz bilan aniqlanadi.
/// </summary>
public static class DefaultPrompts
{
    /// <summary>Eski promptlardagi qat'iy uzunlik qatori (avtomatik dinamik qoidaga almashtiriladi).</summary>
    /// <summary>
    /// Oldingi standart promptdagi tavsif/tag qatorlari va ularning yangisi: saqlangan (o'zgartirilgan bo'lsa ham) promptda shu qatorlar
    /// almashtiriladi - tavsif va aynan 5 ta tag har videoda ssenariy bilan birga yoziladi.
    /// </summary>
    public static readonly (string Old, string New)[] ScriptLineUpdates =
    {
        ("- youtube_description (optional): 2 short paragraphs summarising the video. Mention sources only if they are in the fact sheet.",
         "- youtube_description: 2 short paragraphs summarising the video. Mention sources only if they are in the fact sheet."),
        ("- tags (optional): up to 15 short search tags.",
         "- tags: exactly 5 short search tags for this video (1-3 words each, no # sign), most specific first."),
    };

    /// <summary>Bitta videoga ssenariy bilan yoziladigan tag soni.</summary>
    public const int TagsPerVideo = 5;

    public const string LegacyLengthLine ="Target length: about {duration_sec} seconds of narration, which is about {target_words} spoken words.";

    public const string DynamicLengthRule =
        "Length: there is no fixed duration. Use as many words as this topic needs to be explained clearly and completely, and no more: " +
        "no filler, no repetition, no padding. The final video length will follow the recorded voice.";

    /// <summary>Oldingi standart (sahnalar rejali) ssenariy prompti - migratsiya uchun.</summary>
    public const string ScriptPrevious = """
You are a senior scriptwriter for an educational YouTube channel.
Write ONE complete video script about the topic below.

Topic: {topic}
Extra notes from the channel owner: {notes}
Language of narration, subtitles, title and description: {language_name} ({language})
{length_rule}
Video format: {format}

Quality rules:
- Open with a concrete hook in the first sentence (a question, a surprising fact, or a clear promise). No "welcome to my channel".
- Explain one idea per scene, in plain words a 14-year-old understands. Use short sentences that are easy to say aloud.
- Be accurate and careful. Do not invent numbers, dates, names, quotes or studies.
  Use only facts from the FACT SHEET below for anything recent or numeric; for timeless general knowledge, state it plainly.
  If a fact is uncertain, say so in the narration or leave it out.
- The narration is read aloud by a text-to-speech voice. It must contain ONLY the words to be spoken:
  no stage directions, no [brackets], no (parentheses with directions), no "Scene 1:", no speaker names, no markdown, no emoji, no URLs.
  Write numbers the way they should be spoken when that helps a TTS voice.
- End with a short recap or a clear takeaway. A call to action, if any, is one short sentence.
- Scenes: 4 to 12 scenes. The narration of all scenes joined together must equal narration_text exactly.
  For each scene describe a simple explanatory visual (diagram, icon, chart, keyword animation) that matches its words.
  Avoid random stock footage ideas that do not explain anything.
- on_screen_text per scene: 2 to 6 words, a key term or number, never a full sentence.
- youtube_title: under 70 characters, specific, no clickbait lies, no ALL CAPS.
- youtube_description: 2 short paragraphs summarising the video, then key points as plain lines. Mention sources only if they are in the fact sheet.
- sources: only URLs that appear in the fact sheet. If the fact sheet is empty, return an empty list.
- unverified_claims: list any statement in the narration you could not support with the fact sheet and that is not common knowledge (ideally empty).

FACT SHEET (from a search-grounded step; may be empty):
{fact_sheet}
""";

    public const string FactSheet = """
Collect a short, accurate fact sheet for an educational video.
Topic: {topic}
Notes: {notes}
List 5-12 key facts that a good explainer on this topic needs, with emphasis on anything that is recent, numeric, or may have changed.
For each fact write one line: the fact, then the source title. Only include facts you found in search results.
If something is disputed or unclear, say so. Write in English.
""";

    /// <summary>Oldingi standart montaj prompti - migratsiya uchun.</summary>
    public const string MontagePrevious = """
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
""";

    /// <summary>
    /// Ssenariy prompti. Gemini JSON qaytaradi: subtitle_text (ovozga o'qiladigan va subtitr bo'ladigan matn) va youtube_title
    /// (YouTube'ga chiqarishgacha saqlanadi); youtube_description va tags ixtiyoriy.
    /// </summary>
    public const string Script = """
You are a senior scriptwriter for an educational YouTube channel.
Write the spoken text for ONE complete video about the topic below.

Topic: {topic}
Extra notes from the channel owner: {notes}
Language of the text, title and description: {language_name} ({language})
{length_rule}
Video format: {format}

Return JSON with:
- subtitle_text: the full text of the video. It is read aloud by a text-to-speech voice AND shown as subtitles, word for word.
- youtube_title: the YouTube title. It is saved and used when the video is published.
- youtube_description: 2 short paragraphs summarising the video. Mention sources only if they are in the fact sheet.
- tags: exactly 5 short search tags for this video (1-3 words each, no # sign), most specific first.
- sources (optional): only URLs from the fact sheet that you actually used. Empty if the fact sheet is empty.

Rules for subtitle_text:
- Open with a concrete hook in the first sentence (a question, a surprising fact, or a clear promise). No "welcome to my channel".
- Plain words a 14-year-old understands; short sentences that are easy to say aloud and easy to read as subtitles.
- ONLY the words to be spoken: no stage directions, no [brackets], no (parentheses with directions), no "Scene 1:", no speaker names,
  no markdown, no emoji, no URLs. Write numbers the way they should be spoken when that helps a TTS voice.
- Be accurate and careful. Do not invent numbers, dates, names, quotes or studies. Use only facts from the FACT SHEET below for anything
  recent or numeric; for timeless general knowledge, state it plainly. If a fact is uncertain, say so or leave it out.
- End with a short recap or a clear takeaway. A call to action, if any, is one short sentence.

Rules for youtube_title: under 70 characters, specific, no clickbait lies, no ALL CAPS, no < or > characters.

FACT SHEET (from a search-grounded step; may be empty):
{fact_sheet}
""";

    /// <summary>
    /// Montaj (Claude) prompti. Claude'ga shu prompt bilan birga subtitr (matn va vaqtli), ovoz va bo'lsa shablon beriladi.
    /// </summary>
    public const string Montage = """
You are a motion designer building an explainer video in OpenFilm, unattended.
Never ask questions. Work only inside this folder. Do not render the final video: the worker renders it.

You get: the subtitles (the exact words of the voice, plain and word-timed), the voice recording, and, if given, the channel template.
Decide the scenes yourself from the meaning of the subtitles: one scene per idea, not one scene per subtitle line.

Design rules:
- Keep the template's style (colours, fonts, layout) if a template or style reference is given; otherwise use a clean dark theme with one accent colour.
- Every scene explains what is being said with a simple, meaningful visual: diagrams, icons drawn in SVG/CSS, charts, keyword typography,
  simple shape motion. No random filler, no unrelated stock imagery, no lorem ipsum.
- Large, readable text: headline at least 56 px on a 1920x1080 stage (scale with the stage), max 6 words, keep 6% safe margins on every side.
  On a vertical 1080x1920 stage keep text in the middle 80% of the width.
- Smooth motion that depends only on t. No flashing, no fast strobing.
- Do not draw subtitles inside pages: they are burned in from voice.vtt by the worker. Keep the lower 22% of the stage free of important text.
- If you add background music, put it on its own track at volume 0.15 or lower so speech stays clear.

When finished, check your work with `openfilm look` (contact sheet) and open several of the frames it saved,
fix anything cut off, unreadable or empty, then write BUILD_NOTES.md as instructed and reply DONE.
""";
}

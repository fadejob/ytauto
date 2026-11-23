namespace YtAuto.Core.Demo;

/// <summary>
/// Demo (intro) video montaj effektlari katalogi. MCP so'rovidagi <c>requirements.effects</c> shu kalitlardan tanlaydi;
/// bo'sh bo'lsa - hammasi ruxsat (Claude sahnaga mosini tanlaydi). Har effekt faqat loyihaning o'z materiali ustida ishlaydi:
/// hech biri yangi fon, bezak yoki stok rasm qo'shmaydi.
/// </summary>
public static class DemoEffects
{
    public sealed record Effect(string Key, string Group, string Recipe);

    public const string Camera = "camera", Transition = "transition", Highlight = "highlight", Text = "text", Rhythm = "rhythm";

    public static readonly IReadOnlyList<Effect> Catalog = new Effect[]
    {
        // Kamera harakati (material ustida)
        new("zoom_in", Camera, "smooth push-in from the whole screen onto the Focus element (scale 1.0→1.6–2.2 with translate), hold, ease out before the cut"),
        new("zoom_out", Camera, "start tight on a detail, then pull back to reveal the whole screen (scale 2.0→1.0)"),
        new("pan", Camera, "slow camera pan across a wide or detailed screen, left→right or top→bottom, at a fixed zoom of 1.3–1.6"),
        new("ken_burns", Camera, "gentle continuous drift (scale 1.00→1.08 plus a small translate) so a still never looks frozen"),
        new("scroll_through", Camera, "tall screenshot scrolled vertically inside the frame like a real page scroll (translateY with ease-in-out, short holds on key sections)"),
        new("parallax_3d", Camera, "the screen as a 3D plane: perspective + rotateY/rotateX 8–18° settling to 0°, with a soft depth shadow of the screen itself"),
        new("split_screen", Camera, "two product materials side by side (or before/after with a moving divider) when the narration compares or links them"),
        new("picture_in_picture", Camera, "a screen recording in a smaller rounded inset over the related screenshot, sliding in and out"),
        // O'tishlar
        new("crossfade", Transition, "0.3–0.5 s dissolve between two materials"),
        new("slide_push", Transition, "the next screen pushes the previous one out (translateX/Y 100%), 0.4–0.6 s ease-in-out"),
        new("whip_pan", Transition, "very fast slide with directional motion blur (CSS blur 0→12px→0) — energetic cut, 0.25–0.35 s"),
        new("zoom_through", Transition, "zoom deep into the Focus element until it fills the frame, then the next scene emerges from it"),
        new("wipe", Transition, "linear or circular clip-path wipe revealing the next screen, 0.4–0.6 s"),
        new("blur_dissolve", Transition, "outgoing screen blurs out while the next sharpens in, 0.4 s"),
        new("glitch", Transition, "short (≤0.25 s) RGB-split / slice glitch on the product image itself — at most twice per video, for a tech feel"),
        // Ajratib ko'rsatish
        new("spotlight", Highlight, "dim the rest of the product screen (dark overlay with a soft-edged hole) so only the named element stays bright"),
        new("outline_box", Highlight, "animated rounded outline drawn around the named element (stroke draws on, then glows) in the accent colour"),
        new("cursor_click", Highlight, "animated pointer moving along a curved path to the button, click ripple + click.wav at the press"),
        new("magnifier", Highlight, "circular loupe that follows a path and enlarges the UI under it (2× scaled copy of the same material in a circle mask)"),
        new("callout", Highlight, "small label pill with a thin pointer line to the element, popping in with pop.wav"),
        // Matn
        new("title_reveal", Text, "kinetic product-name title over the dimmed first product screen: letters/words slide or mask in, 1–2 s"),
        new("kinetic_text", Text, "the on-screen label appears word by word in sync with the voice (only the short label, never the subtitles)"),
        // Ritm
        new("cut_ins", Rhythm, "fast cuts between the full screen and 2–3 close-up crops of the SAME material, on the narration's phrases, so a long scene becomes several shots"),
        new("speed_ramp", Rhythm, "recordings: fast through the boring part (speed 1.5–3), back to 1× for the key action"),
        new("freeze_frame", Rhythm, "freeze a recording on the key moment, then zoom into it while the voice explains"),
        new("impact_shake", Rhythm, "tiny 0.15 s camera shake/scale bump on a big moment (a successful save, a result appearing) — sparingly"),
    };

    private static readonly Dictionary<string, Effect> ByKey = Catalog.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Keys => Catalog.Select(e => e.Key);

    /// <summary>
    /// Foydalanuvchi tanlagan effektlar: noma'lumlari tashlanadi, takrorlar olib tashlanadi. Hech biri qolmasa - butun katalog.
    /// O'tish (transition) tanlanmagan bo'lsa crossfade, kamera harakati tanlanmagan bo'lsa ken_burns qo'shiladi: sahna
    /// almashishi va "tirik" kadr har doim kerak.
    /// </summary>
    public static IReadOnlyList<Effect> Resolve(IEnumerable<string>? keys, List<string>? unknown = null)
    {
        var chosen = new List<Effect>();
        foreach (var k in keys ?? Array.Empty<string>())
        {
            var key = (k ?? "").Trim();
            if (key.Length == 0) continue;
            if (!ByKey.TryGetValue(key, out var e)) { unknown?.Add(key); continue; }
            if (!chosen.Contains(e)) chosen.Add(e);
        }
        if (chosen.Count == 0) return Catalog;
        if (!chosen.Any(e => e.Group == Transition)) chosen.Add(ByKey["crossfade"]);
        if (!chosen.Any(e => e.Group == Camera)) chosen.Add(ByKey["ken_burns"]);
        return chosen;
    }

    /// <summary>Videoda kamida nechta turli effekt ishlatilsin: sahnalar soniga qarab, tanlanganlardan oshmasdan.</summary>
    public static int MinDistinct(int scenes, int available) => Math.Min(available, Math.Clamp(scenes + 2, 4, 10));
}

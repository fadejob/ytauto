import type { ChannelContext, VoiceProfileCfg } from "../types";

/**
 * Kanal kontekstidan promptlarga qo'shiladigan ixcham matnlar (ingliz tilida: promptlar va videolar inglizcha).
 * Worker ularni ssenariy promptidagi {channel_context} o'rniga va montaj instructions.md dagi "## Channel style" bo'limiga qo'yadi.
 */
const MAX = 2500;
const bullets = (xs: string[], n = 6) => xs.filter(Boolean).slice(0, n).map((x) => `- ${x}`).join("\n");
const clip = (s: string) => (s.length > MAX ? s.slice(0, MAX - 1) + "…" : s);

export function scriptBlock(c: ChannelContext): string {
  const v = c.videos.filter((x) => x.source === "video");
  const parts = [
    `This video is for the YouTube channel "${c.channel.title}"${c.niche ? ` (${c.niche})` : ""}. Match the channel's established style.`,
    c.audience && `Audience: ${c.audience}`,
    c.tone && `Tone of voice: ${c.tone}`,
    c.scriptGuidelines.length && `How scripts on this channel work (learned from the latest videos):\n${bullets(c.scriptGuidelines)}`,
    v.length && `Hooks that opened the latest videos:\n${bullets(v.map((x) => x.script.hook), 3)}`,
    c.titlePatterns.length && `Title patterns that fit the channel:\n${bullets(c.titlePatterns, 4)}`,
    c.doList.length && `Do:\n${bullets(c.doList, 5)}`,
    c.avoidList.length && `Avoid:\n${bullets(c.avoidList, 5)}`,
    c.recentTitles.length && `Already published (do not repeat these topics or titles): ${c.recentTitles.slice(0, 12).join(" | ")}`,
  ];
  return clip(parts.filter(Boolean).join("\n\n"));
}

export function montageBlock(c: ChannelContext): string {
  const v = c.videos.filter((x) => x.source === "video");
  const parts = [
    `Keep the visual identity of the channel "${c.channel.title}" consistent with its latest videos.`,
    c.montageGuidelines.length && `Montage style:\n${bullets(c.montageGuidelines)}`,
    v.length && `Observed in the latest videos:\n${bullets(v.map((x) => `${x.montage.visualStyle}; pace: ${x.montage.editingPace}; on-screen text: ${x.montage.onScreenText}; transitions: ${x.montage.transitions}`), 3)}`,
    c.subtitleGuidelines.length && `Subtitles:\n${bullets(c.subtitleGuidelines, 4)}`,
    c.avoidList.length && `Avoid:\n${bullets(c.avoidList, 4)}`,
  ];
  return clip(parts.filter(Boolean).join("\n\n"));
}

/** Kanal ovoziga mos profil: faqat mavjud profillardan (nomi yoki voice id'si bo'yicha jinsi). Topilmasa "". */
export function pickVoiceProfile(gender: ChannelContext["voice"]["gender"], profiles: Pick<VoiceProfileCfg, "name" | "voice">[]): string {
  if (gender === "unknown" || !profiles.length) return "";
  const female = /(ayol|female|woman|girl|\bf\b|[-_]f$|[-_]f[-_])/i, male = /(erkak|\bmale\b|\bman\b|boy|\bm\b|[-_]m$|[-_]m[-_])/i;
  const test = gender === "female" ? female : male;
  return profiles.find((p) => test.test(p.name) || test.test(p.voice))?.name ?? "";
}

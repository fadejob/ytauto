import "server-only";
import { getConfig, getGeminiKeys } from "../config";
import { sql, withAccount } from "../db";
import { geminiGenerate, responseJson } from "../gemini";
import type { ChannelContext, VideoInsight } from "../types";
import { youtubeAccessToken, youtubeCredentials } from "../youtube";
import { markChecked, parseContext, readProfile, saveError, saveResult, staleSince, tryStart } from "./store";
import { montageBlock, pickVoiceProfile, scriptBlock } from "./text";
import { fetchChannel, fetchRecentIds, fetchVideos, type VideoMeta } from "./youtube-data";

/**
 * Kanalni o'rganish: kanal nomi va tavsifi, oxirgi sarlavhalar va ENG OXIRGI 3 VIDEO.
 * Har video Gemini'ga YouTube havolasi bilan beriladi - u montaj, ovoz, subtitr va ssenariyni bevosita ko'rib chiqadi.
 * Video ochiq bo'lmasa (rejalashtirilgan/private) yoki Gemini ko'ra olmasa: sarlavha, tavsif, teglar va (YtAuto videosi bo'lsa) matn.
 *
 * Tarmoq va Gemini chaqiruvlari tranzaksiyadan tashqarida; bazaga qisqa withAccount bloklarida yoziladi.
 */
const LATEST = 3;
const MAX_VIDEO_SEC = 480; // uzun videoning boshidagi 8 daqiqasi yetarli (token va vaqt tejaladi)

type GeminiKeys = Awaited<ReturnType<typeof getGeminiKeys>>;

const S = (description?: string) => ({ type: "STRING", ...(description ? { description } : {}) });
const A = (description: string) => ({ type: "ARRAY", items: { type: "STRING" }, description });

const VIDEO_SCHEMA = {
  type: "OBJECT",
  properties: {
    summary: S("1-2 sentences: what the video is about and its format"),
    script: {
      type: "OBJECT",
      properties: {
        hook: S("the exact or near-exact opening line(s)"), structure: S("how the narration is organised, section by section"),
        tone: S(), pacing: S("words per minute feel, sentence length"), cta: S("call to action, if any"),
        transcriptExcerpt: S("the first ~120 words of the narration, as spoken"),
      },
      required: ["hook", "structure", "tone", "pacing", "cta", "transcriptExcerpt"],
    },
    voice: {
      type: "OBJECT",
      properties: { gender: S("female, male or unknown"), style: S("e.g. calm documentary narrator, energetic host"), pace: S("slow, normal or fast"), energy: S() },
      required: ["gender", "style", "pace", "energy"],
    },
    montage: {
      type: "OBJECT",
      properties: {
        visualStyle: S("footage type, animation/2D/stock, colour palette, layout"), editingPace: S("average shot length, cuts per minute feel"),
        onScreenText: S("titles, captions, callouts, fonts"), transitions: S(), music: S("music and sound design"),
      },
      required: ["visualStyle", "editingPace", "onScreenText", "transitions", "music"],
    },
    subtitles: { type: "OBJECT", properties: { present: { type: "BOOLEAN" }, style: S("position, size, word-by-word highlight, colours") }, required: ["present", "style"] },
  },
  required: ["summary", "script", "voice", "montage", "subtitles"],
};

const CHANNEL_SCHEMA = {
  type: "OBJECT",
  properties: {
    summary: S("2-3 sentences about the channel"), niche: S("short niche label"), audience: S("who watches and why"), tone: S(),
    titlePatterns: A("title formulas that this channel uses"), recurringTopics: A("topic areas the channel covers"),
    scriptGuidelines: A("concrete rules for writing the next script in this channel's style"),
    montageGuidelines: A("concrete rules for editing the next video in this channel's visual style"),
    subtitleGuidelines: A("subtitle style rules"),
    voice: { type: "OBJECT", properties: { gender: S("female, male or unknown"), style: S(), pace: S("slow, normal or fast") }, required: ["gender", "style", "pace"] },
    doList: A("what works and should be kept"), avoidList: A("what to avoid"),
  },
  required: ["summary", "niche", "audience", "tone", "titlePatterns", "recurringTopics", "scriptGuidelines", "montageGuidelines", "subtitleGuidelines", "voice", "doList", "avoidList"],
};

type VideoAnalysis = Pick<VideoInsight, "summary" | "script" | "voice" | "montage" | "subtitles">;
type ChannelAnalysis = Omit<ChannelContext, "channel" | "videos" | "recentTitles" | "voice"> & { voice: { gender: string; style: string; pace: string } };

const clip = (s: string, n: number) => (s.length > n ? s.slice(0, n) + "…" : s);
const meta = (v: VideoMeta, script?: string | null) =>
  `Title: ${v.title}\nPublished: ${v.publishedAt}\nDuration: ${v.durationSec}s${v.isShort ? " (Short)" : ""}\nViews: ${v.views}, likes: ${v.likes}, comments: ${v.comments}\n` +
  `Tags: ${v.tags.slice(0, 15).join(", ") || "-"}\nDescription:\n${clip(v.description, 1500) || "-"}` + (script ? `\n\nNarration script (exact):\n${clip(script, 5000)}` : "");

async function analyzeVideo(v: VideoMeta, keys: GeminiKeys, script: string | null): Promise<VideoInsight> {
  const base = { videoId: v.id, title: v.title, publishedAt: v.publishedAt, durationSec: v.durationSec, views: v.views, likes: v.likes, comments: v.comments, isShort: v.isShort };
  const ask = (watch: boolean) =>
    `You are analysing a video from a YouTube channel so that an automated studio can produce new videos in the same style.\n` +
    (watch ? "Watch the video: look at the editing, on-screen text, subtitles and listen to the narration and voice.\n" : "The video itself is not available; infer what you can from the metadata (and script if given). Say \"unknown\" where you cannot tell.\n") +
    `Answer in English, concretely (no generic advice).\n\n${meta(v, script)}`;
  const config = { temperature: 0.3, responseMimeType: "application/json", responseSchema: VIDEO_SCHEMA, thinkingConfig: { thinkingBudget: 1024 } };
  if (v.privacy === "public") {
    try {
      const part: Record<string, unknown> = { fileData: { fileUri: `https://www.youtube.com/watch?v=${v.id}` } };
      if (v.durationSec > MAX_VIDEO_SEC) part.videoMetadata = { startOffset: "0s", endOffset: `${MAX_VIDEO_SEC}s` };
      const { data } = await geminiGenerate(
        { contents: [{ role: "user", parts: [part, { text: ask(true) }] }], generationConfig: { ...config, mediaResolution: "MEDIA_RESOLUTION_LOW" } },
        { keys, timeoutMs: 150_000 },
      );
      return { ...base, source: "video", ...responseJson<VideoAnalysis>(data) };
    } catch (e) {
      console.warn(`[channel] video ${v.id} ko'rilmadi, metadata bilan:`, e instanceof Error ? e.message : e);
    }
  }
  const { data } = await geminiGenerate({ contents: [{ role: "user", parts: [{ text: ask(false) }] }], generationConfig: config }, { keys, timeoutMs: 90_000 });
  return { ...base, source: "metadata", ...responseJson<VideoAnalysis>(data) };
}

async function synthesize(ch: { title: string; description: string; subscribers: number; videoCount: number }, titles: string[], videos: VideoInsight[], keys: GeminiKeys): Promise<ChannelAnalysis> {
  const vids = videos.map((v, i) =>
    `### Latest video ${i + 1}: ${v.title} (${v.source === "video" ? "watched" : "metadata only"}, ${v.views} views, ${v.durationSec}s)\n` +
    JSON.stringify({ summary: v.summary, script: v.script, voice: v.voice, montage: v.montage, subtitles: v.subtitles })).join("\n\n");
  const prompt =
    `Build a compact "channel memory" for an automated video studio that will produce the next videos for this YouTube channel.\n` +
    `Base every point on the evidence below; prefer specific, actionable rules over generic advice. English only.\n\n` +
    `Channel: ${ch.title}\nSubscribers: ${ch.subscribers}, videos: ${ch.videoCount}\nChannel description:\n${clip(ch.description, 2000) || "-"}\n\n` +
    `Recent video titles (newest first):\n${titles.slice(0, 30).map((t) => `- ${t}`).join("\n") || "-"}\n\n${vids}`;
  const { data } = await geminiGenerate(
    { contents: [{ role: "user", parts: [{ text: prompt }] }], generationConfig: { temperature: 0.4, responseMimeType: "application/json", responseSchema: CHANNEL_SCHEMA, thinkingConfig: { thinkingBudget: 2048 } } },
    { keys, timeoutMs: 120_000 },
  );
  return responseJson<ChannelAnalysis>(data);
}

const gender = (s: string | undefined): ChannelContext["voice"]["gender"] => (/female|woman/i.test(s ?? "") ? "female" : /\bmale\b|\bman\b/i.test(s ?? "") ? "male" : "unknown");

export type AnalyzeResult = { status: "analyzed" | "unchanged" | "busy" | "not_connected" | "failed"; message: string };

/**
 * Kanalni tahlil qiladi. force=false bo'lsa: eng yangi video o'zgarmagan va worker "yangi video" belgisini qo'ymagan bo'lsa
 * Gemini chaqirilmaydi (faqat 3 ta arzon YouTube so'rovi).
 */
export async function analyzeChannel(accountId: string, opts: { force?: boolean; guest?: boolean } = {}): Promise<AnalyzeResult> {
  const prep = await withAccount(accountId, async () => {
    const creds = await youtubeCredentials();
    if (!creds) return null;
    // Shu kanal tahlil qilinadi: orada boshqa kanal faol qilinsa ham natija aynan shu kanalga yoziladi.
    const chId = creds.channelId;
    if (!(await tryStart(chId))) return "busy" as const;
    const [profile, stale, keys, cfg] = await Promise.all([readProfile(chId), staleSince(), getGeminiKeys(), getConfig()]);
    // YtAuto o'zi yaratgan videolarning aniq matni (qo'lda kiritilgan bo'lsa) - Gemini video ko'ra olmasa ham ssenariy ma'lum.
    const own = await sql()`SELECT youtube_video_id AS id, manual_script AS script FROM ytauto.jobs
      WHERE channel_id = ${chId} AND youtube_video_id IS NOT NULL AND manual_script IS NOT NULL ORDER BY id DESC LIMIT 20`;
    return { chId, creds, profile, stale, keys, profiles: cfg.voice.profiles, scripts: new Map(own.map((r) => [r.id as string, r.script as string])) };
  }, { guest: opts.guest });
  if (prep === null) return { status: "not_connected", message: "YouTube kanali ulanmagan." };
  if (prep === "busy") return { status: "busy", message: "Kanal hozir tahlil qilinmoqda." };

  try {
    if (!prep.keys.keys.length) throw new Error("Gemini API kaliti kiritilmagan (Sozlamalar → Gemini).");
    const token = await youtubeAccessToken(prep.creds);
    const ch = await fetchChannel(token);
    const ids = await fetchRecentIds(token, ch.uploads, 30);
    if (!ids.length) throw new Error("Kanalda hali video yo'q — birinchi video chiqqach tahlil qilinadi.");

    const staleNewer = !!prep.stale && (!prep.profile?.analyzed_at_utc || prep.stale > prep.profile.analyzed_at_utc);
    if (!opts.force && !staleNewer && prep.profile?.last_video_id === ids[0] && parseContext(prep.profile)) {
      await withAccount(accountId, () => markChecked(prep.chId), { guest: opts.guest });
      return { status: "unchanged", message: "Yangi video yo'q — kontekst o'zgarmadi." };
    }

    const metas = await fetchVideos(token, ids);
    const latest = metas.slice(0, LATEST);
    const videos = await Promise.all(latest.map((v) => analyzeVideo(v, prep.keys, prep.scripts.get(v.id) ?? null)));
    const a = await synthesize(ch, metas.map((m) => m.title), videos, prep.keys);
    const g = gender(a.voice?.gender) !== "unknown" ? gender(a.voice.gender) : gender(videos.find((v) => gender(v.voice.gender) !== "unknown")?.voice.gender);
    const ctx: ChannelContext = {
      ...a,
      channel: { id: ch.id, title: ch.title, description: ch.description, subscribers: ch.subscribers, videoCount: ch.videoCount, url: ch.customUrl ? `https://www.youtube.com/${ch.customUrl}` : `https://www.youtube.com/channel/${ch.id}` },
      voice: { gender: g, style: a.voice?.style ?? "", pace: a.voice?.pace ?? "", recommendedProfile: pickVoiceProfile(g, prep.profiles) },
      videos, recentTitles: metas.map((m) => m.title),
    };
    await withAccount(accountId, () => saveResult(prep.chId, ctx, ids[0], { script: scriptBlock(ctx), montage: montageBlock(ctx), voiceProfile: ctx.voice.recommendedProfile }), { guest: opts.guest });
    const watched = videos.filter((v) => v.source === "video").length;
    return { status: "analyzed", message: `Kanal o'rganildi: ${videos.length} ta video (${watched} tasi ko'rib chiqildi).` };
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e);
    console.error("[channel] tahlil xatosi:", msg);
    await withAccount(accountId, () => saveError(prep.chId, msg), { guest: opts.guest }).catch(() => {});
    return { status: "failed", message: msg };
  }
}

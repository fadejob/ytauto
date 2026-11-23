import "server-only";
import type { ActionResult, ChannelContext, ChannelState, Suggestion, VideoInsight } from "../types";
import { montageBlock, scriptBlock } from "./text";

/** DEMO_MODE: «Kanal» sahifasi dizaynini bazasiz ko'rish uchun namunaviy kanal xotirasi. */
const vid = (i: number, title: string, views: number, source: VideoInsight["source"] = "video"): VideoInsight => ({
  videoId: `demo${i}`, title, publishedAt: new Date(Date.now() - i * 3 * 86400_000).toISOString(), durationSec: 420 + i * 35, views, likes: Math.round(views / 22), comments: Math.round(views / 180), isShort: false, source,
  summary: "A narrated 2D explainer that breaks one engineering idea into a story with a concrete everyday example.",
  script: {
    hook: i === 1 ? "You pressed PAY twice. Why were you only charged once?" : "Every app you use hides this one trick.",
    structure: "Hook question → everyday scenario → the naive approach fails → the real mechanism in 3 steps → recap → CTA",
    tone: "Confident, friendly, slightly playful", pacing: "Fast, ~165 wpm, short sentences", cta: "Subscribe for one engineering idea a week",
    transcriptExcerpt: "You pressed PAY twice. Your bank only charged you once. That's not luck — it's a design decision called idempotency...",
  },
  voice: { gender: "male", style: "Calm tech explainer", pace: "fast", energy: "medium-high" },
  montage: { visualStyle: "Flat 2D motion graphics, dark navy background, neon accent colours", editingPace: "New visual every 2-3 seconds", onScreenText: "Bold keyword callouts, code snippets", transitions: "Quick slide and zoom", music: "Low lo-fi beat under narration" },
  subtitles: { present: true, style: "Burned-in, bottom centre, word-by-word yellow highlight" },
});

function demoContext(): ChannelContext {
  const c: ChannelContext = {
    channel: { id: "UCdemo", title: "Fadejob", description: "Full-stack engineering ideas explained with simple animations.", subscribers: 1240, videoCount: 38, url: "https://www.youtube.com/@fadejob" },
    summary: "Short animated explainers about software engineering concepts that hide behind everyday apps, aimed at junior developers and curious users.",
    niche: "Software engineering explainers", audience: "Junior developers and tech-curious viewers, 18–34",
    tone: "Confident, friendly, slightly playful",
    titlePatterns: ["Question about an everyday app moment (\"What happens if…?\")", "\"X Explained in N Minutes\"", "Bold claim + concept name"],
    recurringTopics: ["Payments and reliability", "Databases", "Web performance", "System design"],
    scriptGuidelines: ["Open with an everyday moment framed as a question", "Explain the naive solution first, then why it fails", "Use one running example from start to end", "Keep sentences short; ~165 wpm", "End with a one-line recap and a soft CTA"],
    montageGuidelines: ["Flat 2D motion graphics on dark navy", "Change the visual every 2–3 seconds", "Show the key term as a bold on-screen callout", "Use simple code snippets, never full files"],
    subtitleGuidelines: ["Burned-in, bottom centre", "Word-by-word highlight in yellow"],
    voice: { gender: "male", style: "Calm tech explainer", pace: "fast", recommendedProfile: "MOSS erkak" },
    doList: ["Concrete numbers and real app examples", "One idea per video"], avoidList: ["Long intros", "Jargon without an analogy"],
    videos: [vid(1, "Double Click Pay? Idempotency Prevents Duplicate Charges", 5400), vid(2, "How Your Phone Knows Where You Are", 3100), vid(3, "Why Databases Need Indexes", 1900, "metadata")],
    recentTitles: ["Double Click Pay? Idempotency Prevents Duplicate Charges", "How Your Phone Knows Where You Are", "Why Databases Need Indexes", "Caching Explained in 5 Minutes"],
  };
  return c;
}

const g = globalThis as unknown as { __ytautoDemoChannel?: { enabled: boolean; startedAt: number | null; suggestions: Suggestion[] } };
const st = () => (g.__ytautoDemoChannel ??= {
  enabled: true, startedAt: null,
  suggestions: [
    { id: 1, forDate: new Date().toISOString().slice(0, 10), topic: "What Happens When You Press Refresh During a Payment?", notes: "Follow one payment request through retries, timeouts and idempotency keys.", hook: "You hit refresh while paying. Did you just pay twice?", reason: "Eng ko'p ko'rilgan to'lov videosining davomi — auditoriya shu mavzuni yaxshi qabul qilgan.", status: "new" },
    { id: 2, forDate: new Date().toISOString().slice(0, 10), topic: "Why Your Food Delivery App Knows You're Hungry", notes: "Event streams and recommendation basics with a delivery app example.", hook: "How does your delivery app know you're hungry before you do?", reason: "Kundalik ilova misoli bilan boshlash kanal formulasiga mos.", status: "new" },
    { id: 3, forDate: new Date().toISOString().slice(0, 10), topic: "Rate Limiting Explained With a Nightclub Bouncer", notes: "Token bucket vs fixed window, one analogy end to end.", hook: "Why does a website suddenly say: slow down, try again later?", reason: "Tizim dizayni mavzusi, bitta o'xshatish bilan — kanal uslubi.", status: "new" },
  ],
});

export function demoChannelState(): ChannelState {
  const s = st();
  const running = s.startedAt !== null && Date.now() - s.startedAt < 5000;
  const ctx = demoContext();
  return {
    connected: true, channelTitle: ctx.channel.title, status: running ? "running" : "idle", error: null, enabled: s.enabled,
    analyzedAt: new Date(s.startedAt && !running ? s.startedAt + 5000 : Date.now() - 3 * 3600_000).toISOString(), startedAt: s.startedAt ? new Date(s.startedAt).toISOString() : null,
    context: ctx, blocks: { script: scriptBlock(ctx), montage: montageBlock(ctx), voiceProfile: ctx.voice.recommendedProfile },
    suggestions: s.suggestions, suggestionsPending: false,
  };
}

export function demoChannelAction(a: { action: string; enabled?: boolean; id?: number }): ActionResult {
  const s = st();
  if (a.action === "analyze") { s.startedAt = Date.now(); return { ok: true, message: "Tahlil boshlandi. [demo]" }; }
  if (a.action === "toggle") { s.enabled = !!a.enabled; return { ok: true, message: s.enabled ? "Kontekst yoqildi. [demo]" : "Kontekst o'chirildi. [demo]" }; }
  const sug = s.suggestions.find((x) => x.id === a.id);
  if (!sug || sug.status !== "new") return { ok: false, message: "Tavsiya topilmadi." };
  sug.status = a.action === "accept" ? "accepted" : "dismissed";
  return { ok: true, message: a.action === "accept" ? `"${sug.topic}" navbatga qo'shildi. [demo]` : "Tavsiya rad etildi. [demo]" };
}

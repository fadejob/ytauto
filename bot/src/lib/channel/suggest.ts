import "server-only";
import { getGeminiKeys } from "../config";
import { sql, withAccount } from "../db";
import { geminiGenerate, responseJson } from "../gemini";
import { utcToZoned } from "../time";
import { getCatalog } from "../topics";
import { Status } from "../types";
import { insertSuggestions, listSuggestions, parseContext, readProfile, recentSuggestionTopics } from "./store";

/** Kunlik mavzu tavsiyalari: kanal konteksti asosida 5 ta yangi inglizcha mavzu (kuniga bir marta). */
const COUNT = 5;

const SCHEMA = {
  type: "OBJECT",
  properties: {
    ideas: {
      type: "ARRAY",
      items: {
        type: "OBJECT",
        properties: {
          topic: { type: "STRING", description: "video topic in English, max 90 characters" },
          notes: { type: "STRING", description: "what the video should cover, angle and hook, in English, 1-3 sentences" },
          reason: { type: "STRING", description: "why this fits the channel and its audience, in Uzbek (Latin script), one sentence" },
        },
        required: ["topic", "notes", "reason"],
      },
    },
  },
  required: ["ideas"],
};

/** Hisob vaqt zonasidagi bugungi sana (YYYY-MM-DD). Hisob konteksti ichida. */
export async function todayLocal(): Promise<string> {
  return utcToZoned(new Date(), (await getCatalog()).youtube.timeZone).date;
}

/** Bugun tavsiyalar hali yo'q va kontekst bor bo'lsa yaratadi. Natija: yaratildimi. */
export async function generateSuggestions(accountId: string, opts: { guest?: boolean } = {}): Promise<boolean> {
  const prep = await withAccount(accountId, async () => {
    const date = await todayLocal();
    if ((await listSuggestions(date)).length) return null;
    const ctx = parseContext(await readProfile());
    if (!ctx) return null;
    const keys = await getGeminiKeys();
    if (!keys.keys.length) return null;
    const queued = await sql()`SELECT topic FROM ytauto.jobs WHERE status <> ${Status.Cancelled} ORDER BY id DESC LIMIT 40`;
    return { date, ctx, keys, queued: queued.map((r) => r.topic as string), recent: await recentSuggestionTopics() };
  }, { guest: opts.guest });
  if (!prep) return false;

  const { ctx } = prep;
  const prompt =
    `Suggest ${COUNT} new video ideas for the YouTube channel "${ctx.channel.title}".\n` +
    `Niche: ${ctx.niche}\nAudience: ${ctx.audience}\nTone: ${ctx.tone}\nTopics the channel covers: ${ctx.recurringTopics.join("; ")}\n` +
    `Title patterns that work: ${ctx.titlePatterns.join("; ")}\n` +
    `Best performing recent videos: ${[...ctx.videos].sort((a, b) => b.views - a.views).map((v) => `${v.title} (${v.views} views)`).join("; ")}\n\n` +
    `Do NOT repeat or closely paraphrase any of these (already published, queued or rejected):\n` +
    [...ctx.recentTitles, ...prep.queued, ...prep.recent.dismissed, ...prep.recent.accepted].slice(0, 90).map((t) => `- ${t}`).join("\n") +
    `\n\nIdeas must be specific, evergreen or timely, and realistic to explain with narration and 2D motion graphics. Topic and notes in English; reason in Uzbek.`;
  const { data } = await geminiGenerate(
    { contents: [{ role: "user", parts: [{ text: prompt }] }], generationConfig: { temperature: 0.9, responseMimeType: "application/json", responseSchema: SCHEMA, thinkingConfig: { thinkingBudget: 1024 } } },
    { keys: prep.keys, timeoutMs: 90_000 },
  );
  const ideas = (responseJson<{ ideas?: { topic?: string; notes?: string; reason?: string }[] }>(data).ideas ?? [])
    .map((i) => ({ topic: (i.topic ?? "").trim(), notes: (i.notes ?? "").trim(), reason: (i.reason ?? "").trim() }))
    .filter((i) => i.topic)
    .slice(0, COUNT);
  return withAccount(accountId, () => insertSuggestions(prep.date, ideas), { guest: opts.guest });
}

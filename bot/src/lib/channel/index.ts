import "server-only";
import { after } from "next/server";
import { currentAccountId, isGuest } from "../db";
import { isDemo } from "../demo";
import { applyAction } from "../topics";
import type { ActionResult, ChannelState } from "../types";
import { youtubeCredentials } from "../youtube";
import { analyzeChannel } from "./analyze";
import { demoChannelState, demoChannelAction } from "./demo";
import { getSuggestion, listSuggestions, parseContext, readBlocks, readProfile, setEnabled, setSuggestionStatus, staleSince, type ProfileRow } from "./store";
import { generateSuggestions, todayLocal } from "./suggest";

/** Yangi video bormi tekshiruvi (arzon: 3 ta YouTube so'rovi, Gemini yo'q) shu oraliqda bir marta. */
const CHECK_EVERY_MS = 12 * 3600_000;
/** Xatodan keyin avtomatik qayta urinish oralig'i. */
const RETRY_FAILED_MS = 6 * 3600_000;
const RUNNING_TTL_MS = 10 * 60_000;

function needsRefresh(p: ProfileRow | null, stale: Date | null): boolean {
  if (!p || (!p.context_json && p.status === "idle")) return true;
  const age = Date.now() - p.updated_at_utc.getTime();
  if (p.status === "running") return Date.now() - (p.started_at_utc?.getTime() ?? 0) > RUNNING_TTL_MS;
  const staleNewer = !!stale && (!p.analyzed_at_utc || stale > p.analyzed_at_utc);
  if (p.status === "failed") return age > RETRY_FAILED_MS || (staleNewer && stale! > p.updated_at_utc);
  return staleNewer || age > CHECK_EVERY_MS;
}

/** Fon ishi: javob qaytgach (after) tahlil va kerak bo'lsa bugungi tavsiyalar. Hisob konteksti ichida chaqiriladi. */
function runInBackground(opts: { force?: boolean; suggestions?: boolean }) {
  const accountId = currentAccountId(), guest = isGuest();
  after(async () => {
    try {
      const r = await analyzeChannel(accountId, { force: opts.force, guest });
      if (r.status === "failed" || r.status === "not_connected") return;
      if (opts.suggestions !== false) await generateSuggestions(accountId, { guest });
    } catch (e) { console.error("[channel] fon ishi:", e instanceof Error ? e.message : e); }
  });
}

/** /api/state har ochilishda: kerak bo'lsa (yangi video, eskirgan) kanal kontekstini fonda yangilaydi. */
export async function maybeRefreshChannel(): Promise<void> {
  if (isDemo()) return;
  try {
    const creds = await youtubeCredentials();
    if (!creds) return;
    if (needsRefresh(await readProfile(creds.channelId), await staleSince())) runInBackground({});
  } catch (e) { console.warn("[channel] yangilash tekshiruvi:", e instanceof Error ? e.message : e); }
}

export async function getChannelState(): Promise<ChannelState> {
  if (isDemo()) return demoChannelState();
  // Faol kanal bo'yicha (kanal Sozlamalar → YouTube'da almashtiriladi).
  const creds = await youtubeCredentials();
  const ch = creds?.channelId ?? "";
  const [p, stale, blocks, date] = await Promise.all([ch ? readProfile(ch) : null, staleSince(), ch ? readBlocks(ch) : null, todayLocal()]);
  const suggestions = ch ? await listSuggestions(ch, date) : [];
  const context = parseContext(p);
  let running = p?.status === "running" && Date.now() - (p.started_at_utc?.getTime() ?? 0) < RUNNING_TTL_MS;
  let suggestionsPending = false;
  if (creds && needsRefresh(p, stale)) { runInBackground({}); running = true; }
  else if (creds && context && !suggestions.length && !running) { runInBackground({ suggestions: true }); suggestionsPending = true; }
  return {
    connected: !!creds, channelTitle: context?.channel.title || p?.channel_title || creds?.channelTitle || "",
    status: running ? "running" : !p ? "none" : p.status === "running" ? "idle" : p.status,
    error: p?.status === "failed" ? p.error : null, enabled: p?.enabled !== false,
    analyzedAt: p?.analyzed_at_utc?.toISOString() ?? null, startedAt: p?.started_at_utc?.toISOString() ?? null,
    context, blocks: blocks ? { script: blocks.script, montage: blocks.montage, voiceProfile: blocks.voiceProfile } : null,
    suggestions, suggestionsPending: suggestionsPending || (running && !suggestions.length),
  };
}

export type ChannelActionInput = { action: "analyze" } | { action: "toggle"; enabled: boolean } | { action: "accept"; id: number } | { action: "dismiss"; id: number };

export async function channelAction(a: ChannelActionInput): Promise<ActionResult> {
  if (isDemo()) return demoChannelAction(a);
  switch (a.action) {
    case "analyze": {
      const creds = await youtubeCredentials();
      if (!creds) return { ok: false, message: "Avval Sozlamalar → YouTube'da kanalni ulang." };
      const p = await readProfile(creds.channelId);
      if (p?.status === "running" && Date.now() - (p.started_at_utc?.getTime() ?? 0) < RUNNING_TTL_MS) return { ok: false, message: "Kanal hozir tahlil qilinmoqda — biroz kuting." };
      runInBackground({ force: true });
      return { ok: true, message: "Tahlil boshlandi: oxirgi 3 video ko'rib chiqiladi (1–3 daqiqa)." };
    }
    case "toggle": {
      const creds = await youtubeCredentials();
      if (!creds) return { ok: false, message: "Avval Sozlamalar → YouTube'da kanalni ulang." };
      await setEnabled(creds.channelId, !!a.enabled);
    }
      return { ok: true, message: a.enabled ? "Kanal konteksti promptlarga qo'shiladi." : "Kanal konteksti promptlarga qo'shilmaydi (tahlil saqlanib qoladi)." };
    case "accept": {
      const s = await getSuggestion(Number(a.id));
      if (!s || s.status !== "new") return { ok: false, message: "Tavsiya topilmadi yoki allaqachon ko'rib chiqilgan." };
      // Hook tavsiya bilan keladi (eski tavsiyalarda yo'q bo'lsa - mavzuni qo'lda, o'z savolingiz bilan qo'shing).
      const r = await applyAction({ type: "create_topics", topics: [{ topic: s.topic, notes: s.notes, hook: s.hook }] });
      if (r.ok) await setSuggestionStatus(s.id, "accepted", Number(/#(\d+)/.exec(r.message)?.[1]) || undefined);
      return r;
    }
    case "dismiss":
      await setSuggestionStatus(Number(a.id), "dismissed");
      return { ok: true, message: "Tavsiya rad etildi — shunga o'xshashlari qayta taklif qilinmaydi." };
  }
}

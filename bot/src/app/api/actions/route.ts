import { handler } from "@/lib/api";
import { buildState } from "@/lib/state";
import { applyAction } from "@/lib/topics";
import type { Action, ActionResult, State } from "@/lib/types";

/**
 * Bir yoki bir nechta amal (qo'lda yoki AI taklifiga ruxsat berilganda). Har biri alohida tekshiriladi.
 * Javobda yangilangan holat ham qaytadi (shu tranzaksiyada): ilova alohida /api/state so'rovini kutmaydi - o'zgarish darhol ko'rinadi.
 */
export const POST = handler(async (req, viewer): Promise<{ results: ActionResult[]; state: State }> => {
  const body = (await req.json()) as { actions?: Action[] };
  const actions = (body.actions ?? []).slice(0, 10);
  const results: ActionResult[] = [];
  for (const a of actions) {
    try { results.push(await applyAction(a)); }
    catch (e) { results.push({ ok: false, message: e instanceof Error ? e.message.split("\n")[0] : String(e) }); }
  }
  console.log(`[actions] ${viewer.name}: ${actions.map((a) => a.type).join(", ")} -> ${results.map((r) => (r.ok ? "ok" : "fail")).join(", ")}`);
  return { results, state: await buildState(viewer) };
});

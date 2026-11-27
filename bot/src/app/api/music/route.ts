import { accountHandler, handler } from "@/lib/api";
import { withAccount } from "@/lib/db";
import { isDemo } from "@/lib/demo";
import { addTrack, getLibrary, removeTrack, setMusicSettings } from "@/lib/music";

/** Trek yuklab olinib Storage'ga qo'yiladi - bir necha soniya ketishi mumkin. */
export const maxDuration = 60;

export const GET = handler(async () => getLibrary());

/** "add" - tarmoq ishi (Openverse, yuklab olish, Storage) tranzaksiyadan tashqarida; qolganlari - qisqa tranzaksiyada. */
export const POST = accountHandler(async (req, viewer) => {
  const b = (await req.json().catch(() => ({}))) as { action?: string; id?: string; enabled?: unknown; volume?: unknown };
  const run = <T,>(fn: () => Promise<T>): Promise<T> => (isDemo() ? fn() : withAccount(viewer.accountId, fn, { guest: viewer.guest }));
  if (b.action === "add") return addTrack(String(b.id ?? ""), run, viewer.accountId);
  if (b.action === "remove") return { library: await run(() => removeTrack(String(b.id ?? ""))), message: "Kutubxonadan olib tashlandi." };
  if (b.action === "settings") return { library: await run(() => setMusicSettings(b)), message: "Saqlandi." };
  throw new Error("Noma'lum amal.");
});

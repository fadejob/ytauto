import "server-only";
import { sql, wakeAgents } from "./db";

/** Botdan kompyuterlarga buyruqlar (ytauto.machine_commands): kompyuterdagi ilova/yengil worker olib bajaradi (shablonni o'chirish, upload sessiyasini tozalash). */
export async function queueCommand(machine: string, kind: string, arg: string) {
  await sql()`INSERT INTO ytauto.machine_commands(machine, kind, arg) VALUES (${machine}, ${kind}, ${arg})`;
  wakeAgents("command");
}

/** Barcha ma'lum kompyuterlarga (lokal sir/fayl qaysi birida ekani noma'lum bo'lganda). */
export async function queueForAll(kind: string, arg: string) {
  const ms = await sql()`SELECT machine FROM ytauto.machines`;
  // Bitta ko'p qatorli INSERT (har kompyuterga alohida so'rov emas).
  if (ms.length) await sql()`INSERT INTO ytauto.machine_commands ${sql()(ms.map((m) => ({ machine: m.machine as string, kind, arg })))}`;
  if (ms.length) wakeAgents("command");
  return ms.length;
}

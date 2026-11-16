import "server-only";
import { sql } from "./db";

/** Botdan kompyuterlarga buyruqlar (ytauto.machine_commands): kompyuterdagi ilova/yengil worker olib bajaradi (shablonni o'chirish, upload sessiyasini tozalash). */
export async function queueCommand(machine: string, kind: string, arg: string) {
  await sql()`INSERT INTO ytauto.machine_commands(machine, kind, arg) VALUES (${machine}, ${kind}, ${arg})`;
}

/** Barcha ma'lum kompyuterlarga (lokal sir/fayl qaysi birida ekani noma'lum bo'lganda). */
export async function queueForAll(kind: string, arg: string) {
  const ms = await sql()`SELECT machine FROM ytauto.machines`;
  for (const m of ms) await sql()`INSERT INTO ytauto.machine_commands(machine, kind, arg) VALUES (${m.machine as string}, ${kind}, ${arg})`;
  return ms.length;
}

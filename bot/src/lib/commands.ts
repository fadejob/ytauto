import "server-only";
import { sql } from "./db";

/** Botdan kompyuterlarga buyruqlar (ytauto.machine_commands): kompyuterdagi ilova/yengil worker olib bajaradi (shablonni o'chirish, upload sessiyasini tozalash). */
let ready = false;
async function ensure() {
  if (ready) return;
  await sql()`CREATE TABLE IF NOT EXISTS ytauto.machine_commands (
    id BIGSERIAL PRIMARY KEY, machine TEXT NOT NULL, kind TEXT NOT NULL, arg TEXT NOT NULL DEFAULT '', done BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT now())`;
  ready = true;
}

export async function queueCommand(machine: string, kind: string, arg: string) {
  await ensure();
  await sql()`INSERT INTO ytauto.machine_commands(machine, kind, arg) VALUES (${machine}, ${kind}, ${arg})`;
}

/** Barcha ma'lum kompyuterlarga (lokal sir/fayl qaysi birida ekani noma'lum bo'lganda). */
export async function queueForAll(kind: string, arg: string) {
  await ensure();
  const ms = await sql()`SELECT machine FROM ytauto.machines`;
  for (const m of ms) await sql()`INSERT INTO ytauto.machine_commands(machine, kind, arg) VALUES (${m.machine as string}, ${kind}, ${arg})`;
  return ms.length;
}

/** Vaqt zonasi bilan ishlash (kutubxonasiz, Intl orqali). "Local" — kanal zonasi (masalan Asia/Tashkent). */

function offsetMinutes(utcMs: number, tz: string): number {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: tz, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
  }).formatToParts(new Date(utcMs));
  const n = (t: string) => Number(parts.find((p) => p.type === t)?.value);
  const asUtc = Date.UTC(n("year"), n("month") - 1, n("day"), n("hour"), n("minute"), n("second"));
  return Math.round((asUtc - utcMs) / 60000);
}

/** "YYYY-MM-DD" + "HH:mm" (zona bo'yicha) → UTC Date. */
export function zonedToUtc(date: string, time: string, tz: string): Date {
  const [y, m, d] = date.split("-").map(Number);
  const [hh, mm] = time.split(":").map(Number);
  const guess = Date.UTC(y, m - 1, d, hh, mm);
  let t = guess - offsetMinutes(guess, tz) * 60000;
  t = guess - offsetMinutes(t, tz) * 60000;
  return new Date(t);
}

/** UTC → { date: "YYYY-MM-DD", time: "HH:mm" } zona bo'yicha. */
export function utcToZoned(utc: Date, tz: string): { date: string; time: string } {
  const local = new Date(utc.getTime() + offsetMinutes(utc.getTime(), tz) * 60000);
  const iso = local.toISOString();
  return { date: iso.slice(0, 10), time: iso.slice(11, 16) };
}

export function addDays(date: string, days: number): string {
  const [y, m, d] = date.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10);
}

/** "YYYY-MM-DD HH:mm" yoki "YYYY-MM-DDTHH:mm" ni ajratadi. */
export function parseLocal(s: string): { date: string; time: string } | null {
  const m = /^(\d{4}-\d{2}-\d{2})[ T](\d{1,2}:\d{2})/.exec(s.trim());
  if (!m) return null;
  const [h, mi] = m[2].split(":");
  return { date: m[1], time: `${h.padStart(2, "0")}:${mi}` };
}

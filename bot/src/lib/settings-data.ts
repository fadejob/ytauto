"use client";

/**
 * Sozlamalar sahifalari uchun ma'lumot: bot config (/api/config) va jadval (/api/schedule).
 * Har karta o'z qismini saqlaydi: saveConfig(patch) saqlangan config'dan qoralama yasaydi, faqat patch o'zgartirgan qismni
 * almashtiradi va butun config'ni yuboradi (server qolganini o'zgarishsiz qoldiradi: sirlar bo'sh bo'lsa eskisi saqlanadi).
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { api, cache } from "./client";
import { diffDraft, toDraft, type ConfigDraft } from "./config-draft";
import type { BotConfig } from "./types";

export { toDraft, type ConfigDraft };

/** Bot config: avval keshdan (darhol ko'rinadi), keyin serverdan. error - yuklab bo'lmadi (shu sahifada ko'rsatiladi). */
export function useBotConfig() {
  const [config, setConfig] = useState<BotConfig | null>(null);
  const [error, setError] = useState<string | null>(null);
  /** Serverdan kelgan eng so'nggi config (saqlash shu asosda farq hisoblaydi). */
  const latest = useRef<BotConfig | null>(null);
  const reload = useCallback(async () => {
    try { const c = await api<BotConfig>("/api/config"); latest.current = c; setConfig(c); cache.set("config", c); setError(null); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  }, []);
  useEffect(() => {
    const c = cache.get<BotConfig>("config");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi
    if (c) setConfig(c);
    void reload();
  }, [reload]);
  /**
   * Faqat shu kartaning o'zgarishi bilan saqlaydi. Xato bo'lsa throw (useBusy toast qiladi).
   * Bitta so'rov: faqat O'ZGARGAN maydonlar yuboriladi ({patch}), server ularni bazadagi eng so'nggi config ustiga qo'yadi - ko'rsatilgan
   * config keshdan yoki eski bo'lsa ham boshqa qismlar (masalan boshqa qurilmada qo'shilgan Gemini kalitlari) qaytib qolmaydi.
   * Saqlashlar navbat bilan bajariladi: ikki karta birdan saqlansa, ikkinchisi birinchisining natijasi ustiga yoziladi.
   */
  const queue = useRef<Promise<unknown>>(Promise.resolve());
  const saveConfig = useCallback((patch: (d: ConfigDraft) => ConfigDraft): Promise<BotConfig> => {
    const job = queue.current.catch(() => {}).then(async () => {
      // Asos - eng so'nggi ma'lum config (oldingi saqlash natijasi yoki ko'rsatilgani); yo'q bo'lsa serverdan.
      const base = latest.current ?? (await api<BotConfig>("/api/config"));
      const before = toDraft(base);
      const next = await api<BotConfig>("/api/config", { patch: diffDraft(before, patch(toDraft(base))) });
      latest.current = next;
      setConfig(next); cache.set("config", next); setError(null);
      return next;
    });
    queue.current = job;
    return job;
  }, []);
  return { config, error, reload, saveConfig };
}

export type Schedule = { enabled: boolean; startTime: string; endTime: string; timeZoneId: string; wakeToRun: boolean; executionLimitHours: number };

export function useSchedule() {
  const [schedule, setSchedule] = useState<Schedule | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const c = cache.get<Schedule>("schedule");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi
    if (c) setSchedule(c);
    api<Schedule>("/api/schedule").then((s) => { setSchedule(s); cache.set("schedule", s); }).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);
  const saveSchedule = useCallback(async (s: Schedule) => {
    const r = await api<Schedule>("/api/schedule", s);
    setSchedule(r); cache.set("schedule", r);
    return r;
  }, []);
  return { schedule, error, saveSchedule };
}

/** Qoralama saqlangandan farq qiladimi (karta "Saqlash" tugmasi uchun). */
export const differs = (a: unknown, b: unknown) => JSON.stringify(a) !== JSON.stringify(b);

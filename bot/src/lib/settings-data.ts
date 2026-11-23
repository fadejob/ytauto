"use client";

/**
 * Sozlamalar sahifalari uchun ma'lumot: bot config (/api/config) va jadval (/api/schedule).
 * Har karta o'z qismini saqlaydi: saveConfig(patch) saqlangan config'dan qoralama yasaydi, faqat patch o'zgartirgan qismni
 * almashtiradi va butun config'ni yuboradi (server qolganini o'zgarishsiz qoldiradi: sirlar bo'sh bo'lsa eskisi saqlanadi).
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { api, cache } from "./client";
import type { BotConfig, BotConfigInput } from "./types";

export type ConfigDraft = BotConfigInput;

export const toDraft = (c: BotConfig): ConfigDraft => ({
  gemini: { ...c.gemini, apiKeys: c.gemini.apiKeys.map((k) => ({ ...k })) },
  limits: { ...c.limits },
  defaults: { ...c.defaults },
  youtube: { ...c.youtube, clientSecret: "" },
  openFilm: { ...c.openFilm },
  visuals: { ...c.visuals, pexelsApiKey: "", pixabayApiKey: "" },
  claude: { ...c.claude },
  voice: { profiles: c.voice.profiles.map((p) => ({ ...p })), settings: { ...c.voice.settings } },
});

/** Bot config: avval keshdan (darhol ko'rinadi), keyin serverdan. error - yuklab bo'lmadi (shu sahifada ko'rsatiladi). */
export function useBotConfig() {
  const [config, setConfig] = useState<BotConfig | null>(null);
  const [error, setError] = useState<string | null>(null);
  const reload = useCallback(async () => {
    try { const c = await api<BotConfig>("/api/config"); setConfig(c); cache.set("config", c); setError(null); }
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
   * Asos har safar serverdan yangi olinadi: ko'rsatilgan config keshdan (localStorage) bo'lishi yoki boshqa qurilma/kompyuterda
   * o'zgargan bo'lishi mumkin - eski asos bilan saqlansa boshqa qismlar (masalan boshqa joyda qo'shilgan Gemini kalitlari) qaytib qolardi.
   * Saqlashlar navbat bilan bajariladi: ikki karta birdan saqlansa, ikkinchisi birinchisining natijasi ustiga yoziladi.
   */
  const queue = useRef<Promise<unknown>>(Promise.resolve());
  const saveConfig = useCallback((patch: (d: ConfigDraft) => ConfigDraft): Promise<BotConfig> => {
    const job = queue.current.catch(() => {}).then(async () => {
      const base = await api<BotConfig>("/api/config");
      const next = await api<BotConfig>("/api/config", patch(toDraft(base)));
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

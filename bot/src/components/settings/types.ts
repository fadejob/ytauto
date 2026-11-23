import type { Run, Toast } from "../App";
import type { State } from "@/lib/types";

/** Sozlamalarning ichki sahifalari (hub'dan ochiladi). */
export type SettingsPage = "channels" | "voice" | "video" | "library" | "ai" | "computer" | "account";

/** Har bir sozlama sahifasi oladigan umumiy props. */
export type SettingsPageProps = {
  state: State | null;
  toast: Toast;
  run: Run;
  /** Hub'ga qaytish (sahifa sarlavhasidagi «Sozlamalar» tugmasi; Telegram'ning orqaga tugmasi ham shuni chaqiradi). */
  back: () => void;
  /** Boshqa sozlama sahifasini ochish (masalan Gemini kaliti yo'q → AI va promptlar). */
  open: (p: SettingsPage) => void;
  /** Umumiy holat (mavzular, faol kanal, Gemini) o'zgargan bo'lishi mumkin - /api/state qayta olinadi. */
  onChanged: () => void;
  onLogout: () => void;
  /**
   * Sahifa ichidagi ichki ekran ochilganda (masalan «Mening ovozim», prompt muharriri) Telegram'ning «orqaga» tugmasi avval shu
   * funksiyani chaqiradi. Ichki ekran yopilganda null bilan tozalanadi.
   */
  setSubBack: (fn: (() => void) | null) => void;
};

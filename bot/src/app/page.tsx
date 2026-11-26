import type { Metadata } from "next";
import AppGate from "@/components/AppGate";
import Landing from "@/components/legal/Landing";

export const metadata: Metadata = {
  title: "YtAuto — automated YouTube videos",
  description: "YtAuto plans, produces and publishes narrated videos to your own YouTube channel.",
};

/**
 * "/" ikki xil: Telegram ichida - ilova (hydration'dan keyin yuklanadi, AppGate), oddiy brauzerda (va Google OAuth tekshiruvi
 * robotida) - ochiq bosh sahifa: ilova nomi, maqsadi, Privacy/Terms havolalari, login'siz. Qaysi biri ko'rinishini birinchi
 * chizishdan oldin layout'dagi skript belgilaydi (html.in-app); CSS globals.css da. Lokal dev/demo rejimda brauzerda ham ilova.
 */
export default function Page() {
  const browserApp = process.env.ALLOW_BROWSER_DEV === "1" || process.env.DEMO_MODE === "1";
  return (
    <div className="root-switch" data-browser-app={browserApp ? "1" : undefined}>
      <div className="landing-root"><Landing /></div>
      <div className="app-root"><AppGate browserApp={browserApp} /></div>
    </div>
  );
}

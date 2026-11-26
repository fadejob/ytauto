import type { Metadata, Viewport } from "next";
import { Onest } from "next/font/google";
import Script from "next/script";
import "./globals.css";

// Onest: o'zbek lotin (o', g') va kirill harflari uchun yaxshi; dizayn tizimining yagona shrifti.
const onest = Onest({ subsets: ["latin", "latin-ext", "cyrillic"], variable: "--font-onest", display: "swap" });

export const metadata: Metadata = {
  title: "YtAuto",
  description: "YtAuto — videolar, kanallar va sozlamalar boshqaruvi",
  // Google Search Console (sayt egaligi - OAuth ilovasini tekshiruvdan o'tkazish uchun): HTML-teg usulidagi kod.
  verification: process.env.GOOGLE_SITE_VERIFICATION?.trim() ? { google: process.env.GOOGLE_SITE_VERIFICATION.trim() } : undefined,
};

/**
 * Telegram ichidami - birinchi chizishdan OLDIN (SDK yuklanishini kutmasdan): Telegram ilovani URL hash'ida tgWebAppData bilan ochadi,
 * qayta yuklanganda SDK uni sessionStorage'da saqlaydi. Shunda "/" da Telegram'da darhol ilova, oddiy brauzerda bosh sahifa ko'rinadi.
 */
// SDK initParams'ni oddiy brauzerda ham ("{}") yozadi - shuning uchun ichida tgWebAppData borligi tekshiriladi.
const IN_APP = `try{var t=window.Telegram&&Telegram.WebApp;if((location.hash||"").indexOf("tgWebAppData")>=0||(sessionStorage.getItem("__telegram__initParams")||"").indexOf("tgWebAppData")>=0||(t&&t.initData))document.documentElement.classList.add("in-app")}catch(e){}`;

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  maximumScale: 1,
  userScalable: false,
  viewportFit: "cover",
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#F5F1EA" },
    { media: "(prefers-color-scheme: dark)", color: "#F5F1EA" },
  ],
};

/** Birinchi chizishdan oldin mavzu: Telegram ichida uning colorScheme, brauzerda tizim sozlamasi (keyin initTelegram kuzatadi). */
// Ilova faqat yorug' mavzuda ishlaydi (Telegram yoki tizim qorong'i bo'lsa ham).
const THEME = `document.documentElement.dataset.theme="light"`;

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="uz" className={onest.variable} suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: IN_APP }} />
      </head>
      <body>
        {/* Telegram Mini App SDK: initData (auth), mavzu, haptic, tasdiqlash oynalari, orqaga tugmasi. */}
        <Script src="https://telegram.org/js/telegram-web-app.js?59" strategy="beforeInteractive" />
        <Script id="theme" strategy="beforeInteractive">{THEME}</Script>
        {children}
      </body>
    </html>
  );
}

import type { Metadata, Viewport } from "next";
import { Onest } from "next/font/google";
import Script from "next/script";
import "./globals.css";

// Onest: o'zbek lotin (o', g') va kirill harflari uchun yaxshi; dizayn tizimining yagona shrifti.
const onest = Onest({ subsets: ["latin", "latin-ext", "cyrillic"], variable: "--font-onest", display: "swap" });

export const metadata: Metadata = {
  title: "YtAuto",
  description: "YtAuto — videolar, kanallar va sozlamalar boshqaruvi",
};

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
      <body>
        {/* Telegram Mini App SDK: initData (auth), mavzu, haptic, tasdiqlash oynalari, orqaga tugmasi. */}
        <Script src="https://telegram.org/js/telegram-web-app.js?59" strategy="beforeInteractive" />
        <Script id="theme" strategy="beforeInteractive">{THEME}</Script>
        {children}
      </body>
    </html>
  );
}

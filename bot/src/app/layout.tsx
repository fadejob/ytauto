import type { Metadata, Viewport } from "next";
import Script from "next/script";
import "./globals.css";

export const metadata: Metadata = {
  title: "YtAuto",
  description: "YtAuto — mavzular, hozir chiqarish va promptlar boshqaruvi",
};

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  maximumScale: 1,
  userScalable: false,
  viewportFit: "cover",
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#f4f4f7" },
    { media: "(prefers-color-scheme: dark)", color: "#101114" },
  ],
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    // telegram-web-app.js React'dan oldin <html> ga --tg-* CSS o'zgaruvchilarini qo'yadi.
    <html lang="uz" suppressHydrationWarning>
      <body>
        {/* Telegram Mini App SDK: initData (auth), mavzu ranglari, haptic, tasdiqlash oynalari. */}
        <Script src="https://telegram.org/js/telegram-web-app.js?59" strategy="beforeInteractive" />
        {children}
      </body>
    </html>
  );
}

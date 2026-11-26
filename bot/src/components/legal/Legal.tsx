import Link from "next/link";
import type { ReactNode } from "react";
import s from "./legal.module.css";

/** Ochiq sahifalar (Google OAuth Branding: bosh sahifa, maxfiylik siyosati, shartlar). Telegram'siz, auth'siz ochiladi. */

export const UPDATED = "2026-11-26";

/** Aloqa: CONTACT_EMAIL (server env) - bo'lmasa Telegram bot orqali. */
export function Contact() {
  const email = process.env.CONTACT_EMAIL?.trim();
  return email ? <a href={`mailto:${email}`}>{email}</a> : <>a message to the YtAuto Telegram bot</>;
}

export function LegalShell({ children }: { children: ReactNode }) {
  return (
    <div className={s.page}>
      <nav className={s.nav}>
        <Link href="/about" className={s.brand}>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src="/logo-120.png" alt="" width={36} height={36} />
          YtAuto
        </Link>
        <div className={s.links}>
          <Link href="/about">Home</Link>
          <Link href="/privacy">Privacy Policy</Link>
          <Link href="/terms">Terms of Service</Link>
        </div>
      </nav>
      {children}
      <footer className={s.footer}>
        © {new Date(UPDATED).getFullYear()} YtAuto · Contact: <Contact /> ·{" "}
        YtAuto uses YouTube API Services: <a href="https://www.youtube.com/t/terms">YouTube Terms of Service</a>,{" "}
        <a href="https://policies.google.com/privacy">Google Privacy Policy</a>.
      </footer>
    </div>
  );
}

export { s as legalStyles };

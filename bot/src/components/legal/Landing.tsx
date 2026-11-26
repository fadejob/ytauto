import Link from "next/link";
import { LegalShell, legalStyles as s } from "./Legal";

/**
 * Ochiq bosh sahifa (Google OAuth tekshiruvi: ilova nomi, maqsadi, Privacy/Terms havolalari, login'siz ko'rinadi).
 * "/" da Telegram'dan tashqari brauzerga va "/about" da ko'rsatiladi. Server tomonda to'liq HTML bo'lib chiqadi (JS'siz ham o'qiladi).
 */
export default function Landing() {
  const bot = process.env.TELEGRAM_BOT_USERNAME?.trim().replace(/^@/, "");
  return (
    <LegalShell>
      <section className={s.hero}>
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src="/logo-512.png" alt="YtAuto logo" width={112} height={112} />
        <h1>YtAuto</h1>
        <p className={s.lead}>
          YtAuto is a YouTube automation tool for creators. You plan video topics; YtAuto writes the script, records the narration, edits
          and renders each video on your own computer, and publishes it to <b>your own</b> YouTube channel on the date you choose.
        </p>
        {bot ? <a className={s.cta} href={`https://t.me/${bot}`}>Open YtAuto in Telegram</a> : null}
      </section>

      <h2>What YtAuto does</h2>
      <div className={s.grid}>
        <div className={s.card}><h3>Topic queue</h3><p>Add video topics (or accept AI suggestions), with a publish date, template, format and voice for each.</p></div>
        <div className={s.card}><h3>Made on your PC</h3><p>The YtAuto desktop app writes the script, generates the voice, edits and renders every video locally.</p></div>
        <div className={s.card}><h3>Publishes to your channel</h3><p>Finished videos are uploaded to your YouTube channel with title, description, tags and schedule.</p></div>
        <div className={s.card}><h3>Manage your videos</h3><p>See your channel and videos in YtAuto, and edit or delete a video when you ask for it.</p></div>
      </div>

      <h2>How to use it</h2>
      <ol>
        <li>Install the YtAuto desktop app on your Windows PC and open the YtAuto bot in Telegram.</li>
        <li>Connect your YouTube channel with Google sign-in (Settings → YouTube).</li>
        <li>Add topics. YtAuto makes the videos and publishes them on schedule.</li>
      </ol>

      <h2>How YtAuto uses your Google account</h2>
      <p>
        When you choose <b>Connect YouTube</b>, Google asks you to allow YtAuto to upload videos, view your channel and manage your videos.
        YtAuto uses this access only to publish the videos you queued, to show your channel and videos inside YtAuto, to learn your
        channel&apos;s style for new scripts, and to edit or delete a video when you ask for it. Each user&apos;s data is kept separate and is never
        sold or used for advertising. Read the full <Link href="/privacy">Privacy Policy</Link> and <Link href="/terms">Terms of Service</Link>.
        You can disconnect at any time in YtAuto (Settings → YouTube) or at{" "}
        <a href="https://myaccount.google.com/permissions">Google Account → Third-party access</a>.
      </p>
    </LegalShell>
  );
}

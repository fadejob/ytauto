import type { Metadata } from "next";
import Link from "next/link";
import { LegalShell, legalStyles as s } from "@/components/legal/Legal";

export const metadata: Metadata = {
  title: "YtAuto — automated YouTube videos",
  description: "YtAuto plans, produces and publishes narrated videos to your own YouTube channel.",
};

export default function About() {
  return (
    <LegalShell>
      <section className={s.hero}>
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src="/logo-512.png" alt="YtAuto logo" width={112} height={112} />
        <h1>YtAuto</h1>
        <p className={s.lead}>
          Plan video topics, let YtAuto write, narrate and edit each video on your own computer, and publish it to <b>your own</b> YouTube
          channel on schedule.
        </p>
        <Link className={s.cta} href="/">Open YtAuto</Link>
      </section>

      <div className={s.grid}>
        <div className={s.card}><h3>Topic queue</h3><p>Add topics (or let AI suggest them), set the publish date, template, format and voice.</p></div>
        <div className={s.card}><h3>Made on your PC</h3><p>The YtAuto desktop app writes the script, generates the voice, edits and renders the video locally.</p></div>
        <div className={s.card}><h3>Your channel only</h3><p>Each user connects their own YouTube channel with Google sign-in. Accounts are fully separated.</p></div>
        <div className={s.card}><h3>Publishing</h3><p>Finished videos are uploaded with title, description and schedule; you can edit or remove them later.</p></div>
      </div>

      <h2>How YtAuto uses your Google account</h2>
      <p>
        When you choose <b>Connect YouTube</b>, Google asks you to allow YtAuto to upload videos, read your channel and manage your
        videos. YtAuto uses this access only to publish the videos you queued, to show your channel and videos inside YtAuto, to learn
        your channel&apos;s style for new scripts, and to edit or delete a video when you ask for it. Details are in the{" "}
        <Link href="/privacy">Privacy Policy</Link>. You can disconnect at any time in YtAuto (Settings → YouTube → Disconnect) or at{" "}
        <a href="https://myaccount.google.com/permissions">Google Account → Third-party access</a>.
      </p>
    </LegalShell>
  );
}

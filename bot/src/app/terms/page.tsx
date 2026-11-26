import type { Metadata } from "next";
import Link from "next/link";
import { Contact, LegalShell, legalStyles as s, UPDATED } from "@/components/legal/Legal";

export const metadata: Metadata = { title: "Terms of Service — YtAuto", description: "Terms for using YtAuto." };

export default function Terms() {
  return (
    <LegalShell>
      <h1 className={s.title}>Terms of Service</h1>
      <p className={s.meta}>Last updated: {UPDATED}</p>

      <div className={s.summary}>
        <h2>Qisqacha (o&apos;zbekcha)</h2>
        <ul>
          <li>YtAuto&apos;dan foydalanib, siz kanalingizga chiqadigan har bir video va uning mazmuni uchun o&apos;zingiz javob berasiz.</li>
          <li>YouTube qoidalariga rioya qilishingiz shart; YtAuto faqat siz ruxsat bergan amallarni bajaradi.</li>
          <li>Xizmat &quot;boricha&quot; taqdim etiladi; istalgan vaqtda foydalanishni to&apos;xtatib, kanalni uzishingiz mumkin.</li>
        </ul>
      </div>

      <h2>1. The service</h2>
      <p>
        YtAuto helps you plan video topics, produce narrated videos on your own computer with the YtAuto desktop app, and publish them to your own
        YouTube channel. By using YtAuto you agree to these terms and to the <Link href="/privacy">Privacy Policy</Link>.
      </p>

      <h2>2. YouTube</h2>
      <p>
        YtAuto uses YouTube API Services. By connecting a channel you agree to be bound by the{" "}
        <a href="https://www.youtube.com/t/terms">YouTube Terms of Service</a> and acknowledge the{" "}
        <a href="https://policies.google.com/privacy">Google Privacy Policy</a>. You must own the channel you connect or be authorized to manage it.
        You can revoke YtAuto&apos;s access at any time in YtAuto or at <a href="https://myaccount.google.com/permissions">Google Account → Third-party access</a>.
      </p>

      <h2>3. Your account</h2>
      <ul>
        <li>Keep your YtAuto token secret; anyone with it can act on your account.</li>
        <li>You are responsible for activity under your account, including actions taken by an AI assistant you connect to it.</li>
        <li>You must be at least 13 years old (or the minimum age in your country) to use YtAuto.</li>
      </ul>

      <h2>4. Your content</h2>
      <ul>
        <li>You keep all rights to your topics, scripts, files, voice and videos. You give YtAuto the permission needed to store and process them to provide the service.</li>
        <li>You are responsible for what is published to your channel: it must not infringe others&apos; rights, mislead viewers, or break YouTube&apos;s policies (including disclosure of altered or synthetic content where required).</li>
        <li>AI-generated scripts and voices can contain mistakes. Review important content before it is published.</li>
        <li>Only upload a voice recording of your own voice, or one you have permission to use.</li>
      </ul>

      <h2>5. Acceptable use</h2>
      <p>Do not use YtAuto for spam, illegal content, harassment, impersonation, or to access accounts or channels you are not authorized to use, and do not try to break or overload the service.</p>

      <h2>6. Third-party services</h2>
      <p>YtAuto depends on Google (YouTube, Gemini), Telegram, Supabase and Vercel. Their availability, limits (for example the YouTube API daily quota) and terms are outside our control.</p>

      <h2>7. Disclaimer and liability</h2>
      <p>
        YtAuto is provided &quot;as is&quot; without warranties. To the maximum extent permitted by law, we are not liable for lost data, lost revenue,
        channel strikes or other indirect damages resulting from use of the service.
      </p>

      <h2>8. Ending use</h2>
      <p>You can stop using YtAuto at any time and ask us to delete your account. We may suspend accounts that break these terms.</p>

      <h2>9. Changes</h2>
      <p>We may update these terms; the date above shows the latest version. Continued use after a change means you accept it.</p>

      <h2>10. Contact</h2>
      <p>Questions: <Contact />.</p>
    </LegalShell>
  );
}

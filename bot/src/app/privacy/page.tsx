import type { Metadata } from "next";
import { Contact, LegalShell, legalStyles as s, UPDATED } from "@/components/legal/Legal";

export const metadata: Metadata = { title: "Privacy Policy — YtAuto", description: "How YtAuto collects, uses, stores and deletes your data." };

export default function Privacy() {
  return (
    <LegalShell>
      <h1 className={s.title}>Privacy Policy</h1>
      <p className={s.meta}>Last updated: {UPDATED}</p>

      <div className={s.summary}>
        <h2>Qisqacha (o&apos;zbekcha)</h2>
        <ul>
          <li>YtAuto YouTube kanalingizga faqat siz bergan ruxsat bilan kiradi: siz navbatga qo&apos;ygan videolarni yuklaydi, kanal va videolaringizni ko&apos;rsatadi, siz so&apos;raganda videoni tahrirlaydi yoki o&apos;chiradi.</li>
          <li>Ma&apos;lumotlaringiz faqat sizning hisobingizda saqlanadi — boshqa foydalanuvchilar ko&apos;rmaydi. Hech kimga sotilmaydi, reklama uchun ishlatilmaydi.</li>
          <li>Kanalni istalgan vaqtda uzishingiz mumkin (Sozlamalar → YouTube → Uzish) — saqlangan ruxsat darhol o&apos;chiriladi.</li>
        </ul>
      </div>

      <p>
        This policy explains what data YtAuto (&quot;we&quot;, the YtAuto Telegram mini app at this website and the YtAuto desktop app) collects,
        how we use it, and the choices you have. YtAuto uses <b>YouTube API Services</b>; by connecting a YouTube channel you also agree to the{" "}
        <a href="https://www.youtube.com/t/terms">YouTube Terms of Service</a>, and Google&apos;s handling of your data is described in the{" "}
        <a href="https://policies.google.com/privacy">Google Privacy Policy</a>.
      </p>

      <h2>Google user data at a glance</h2>
      <p>This section summarizes how YtAuto handles data it receives from Google APIs (&quot;Google user data&quot;). Sections 1–6 give the details.</p>
      <ul>
        <li><b>Data accessed:</b> with your permission, YtAuto accesses your YouTube channel information (ID, title, description, logo,
          banner, statistics) and your uploaded videos&apos; metadata (ID, title, description, tags, category, privacy status, schedule,
          statistics, thumbnail), and receives an OAuth token for your channel. YtAuto does not access your Gmail, contacts, Drive or any
          other Google service.</li>
        <li><b>Data usage:</b> only to provide the features you use — uploading the videos you queued, showing your channel and videos in
          YtAuto, tailoring new scripts to your channel&apos;s style, and editing or deleting a video when you ask. Not for advertising, not
          sold, and not used to train general AI/ML models.</li>
        <li><b>Data sharing:</b> not shared with other users or third parties, except the service providers that run YtAuto for you
          (Google itself, Supabase for storage, Vercel for hosting) and when required by law.</li>
        <li><b>Data storage &amp; protection:</b> stored in our database in your account only, isolated from other accounts with row-level
          security; transferred only over HTTPS (TLS); OAuth tokens never leave our server — your computers receive only short-lived
          access tokens; only the service operator can administer the database.</li>
        <li><b>Retention &amp; deletion:</b> kept while your channel is connected. Disconnecting a channel in YtAuto deletes its token
          immediately; you can also revoke access in your Google Account. Ask us to delete your account and all its data at any time —
          we do so within 30 days.</li>
      </ul>

      <h2>1. Data we collect</h2>
      <h3>Account data</h3>
      <ul>
        <li>Your Telegram user ID (stored, to link Telegram to your YtAuto account) and display name (only shown in the app while you use it).</li>
        <li>A hash of your YtAuto access token and the names of the computers you connect.</li>
      </ul>
      <h3>Google / YouTube data (only after you choose &quot;Connect YouTube&quot;)</h3>
      <ul>
        <li>An OAuth refresh token for your channel, and the permissions (scopes) you granted.</li>
        <li>Channel information: channel ID, title, description, custom URL, logo/avatar and banner image URLs, subscriber, video and view counts.</li>
        <li>Information about your uploaded videos: video ID, title, description, tags, category, privacy status, schedule, duration, view/like/comment counts and thumbnail URL.</li>
      </ul>
      <p>We request these Google permissions:</p>
      <ul>
        <li><code>youtube.upload</code> — to upload the videos you queued in YtAuto to your channel.</li>
        <li><code>youtube.readonly</code> — to show your channel and videos in YtAuto, check upload status, and learn your channel&apos;s style.</li>
        <li><code>youtube.force-ssl</code> — to edit a video&apos;s title, description, tags, privacy or schedule, or delete a video, <b>only when you ask for it</b>.</li>
      </ul>
      <h3>Content you provide</h3>
      <ul>
        <li>Video topics, notes, scripts, titles, prompts, schedules and settings.</li>
        <li>Optional files you upload: ready-made audio, templates, background music, screenshots/recordings for product demo videos, and a short voice recording if you use &quot;My voice&quot;.</li>
        <li>Your own Gemini API keys (if you add them), used only for your own requests.</li>
      </ul>

      <h2>2. How we use the data</h2>
      <ul>
        <li>To run the service for you: keep your topic queue, produce videos on your own computer, and upload them to your channel at the time you chose.</li>
        <li>To show your channel, videos and their status inside YtAuto.</li>
        <li>To analyze your channel&apos;s recent videos (title, description and the video itself, via the Google Gemini API using <b>your own</b> API key) so new scripts match your channel&apos;s topic and style. The result is stored only in your account.</li>
        <li>To edit or delete a video on your channel when you instruct YtAuto to do so (in the app or through your own AI assistant connected to your account).</li>
      </ul>
      <p>
        We do <b>not</b> sell your data, use it for advertising, or use Google user data to train general AI models. YtAuto&apos;s use and transfer of
        information received from Google APIs adheres to the{" "}
        <a href="https://developers.google.com/terms/api-services-user-data-policy">Google API Services User Data Policy</a>, including the Limited Use requirements.
      </p>

      <h2>3. Sharing</h2>
      <p>Your data is never shown to other YtAuto users. We share it only with the services needed to run YtAuto:</p>
      <ul>
        <li><b>Google</b> (YouTube Data API, Google OAuth, and the Gemini API with your own key) — to upload, read and manage your videos and generate scripts.</li>
        <li><b>Supabase</b> (database and file storage) and <b>Vercel</b> (web hosting) — to store your account data and run this website.</li>
        <li><b>Telegram</b> — the mini app runs inside Telegram.</li>
        <li>Your own computers running the YtAuto desktop app — they receive the jobs and files needed to make your videos.</li>
      </ul>
      <p>We may disclose data if required by law.</p>

      <h2>4. Storage and security</h2>
      <ul>
        <li>Each account&apos;s data is separated in the database with row-level security; files are stored in a folder for your account only.</li>
        <li>The YouTube refresh token stays on our server. Your computers receive only short-lived access tokens.</li>
        <li>All connections use HTTPS.</li>
      </ul>

      <h2>5. Retention and deletion</h2>
      <ul>
        <li>Data is kept while your account is active.</li>
        <li><b>Disconnect YouTube</b> (Settings → YouTube → Disconnect) deletes the stored Google token immediately. You can also revoke access at{" "}
          <a href="https://myaccount.google.com/permissions">Google Account → Third-party access</a>.</li>
        <li>Channel analysis data is refreshed when new videos appear and is deleted together with your account.</li>
        <li>Uploaded voice samples and audio files can be deleted in the app; ready-made audio is removed automatically after the video is published.</li>
        <li>To delete your whole account and all its data, contact us (below); we delete it within 30 days.</li>
      </ul>

      <h2>6. Your choices</h2>
      <p>You can view and change your topics and settings in the app, disconnect YouTube, delete files, and ask us for a copy or deletion of your data.</p>

      <h2>7. Children</h2>
      <p>YtAuto is not directed at children under 13 (or the minimum age in your country) and they should not use it.</p>

      <h2>8. Changes</h2>
      <p>We will update this page when the policy changes and change the date above. Significant changes will be announced in the app.</p>

      <h2>9. Contact</h2>
      <p>Questions or requests: <Contact />.</p>
    </LegalShell>
  );
}

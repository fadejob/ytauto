import "server-only";
import { forgetChannelAssetDefaults } from "./channel-assets";
import { queueForAll } from "./commands";
import { currentAccountId, sql } from "./db";
import { isDemo } from "./demo";
import { removeObjects } from "./storage";
import { Status } from "./types";
import { findChannel, forgetChannelSettings } from "./youtube";

/**
 * Kanal ma'lumotlarini tozalash (Sozlamalar → Kanallar va YouTube). YtAuto bazasi, Storage'dagi fayllar va kompyuterlardagi fayllar
 * (arxivdagi video/ovoz/subtitr/muqova, ish papkasi, loglar - "delete_job_files" buyrug'i) - YouTube'dagi videolarga TEGILMAYDI. Hozir kompyuter tayyorlayotgan video (claimed_by) o'chirilmaydi - natijada "o'tkazib yuborildi" deyiladi.
 *  - "published": shu kanalning chiqqan videolari tarixi (yozuvlari, loglari, bosqichlari).
 *  - "all": shu kanalga tegishli hamma narsa - barcha mavzu/video yozuvlari, loglar, kanal tahlili, tavsiyalar, kontekst,
 *    standart teg/tavsif va mavzu fayllari (tayyor audio, muqova, demo materiallari). Kanal ulanishi qoladi (uzish - alohida).
 * Hisob konteksti ichida (RLS): boshqa hisob ma'lumotiga tegmaydi.
 */
export type PurgeScope = "published" | "all";
export type PurgeResult = { ok: boolean; deleted: number; skipped: number; message: string };

type JobFiles = { id: number; audio: string | null; cover: string | null; spec: string | null; kind: string };
/** Kompyuterdagi fayllar (worker yozgan yo'llar; kompyuter faqat o'z arxiv/ish papkalari ichidagisini o'chiradi). */
type LocalFiles = { id: number; video: string | null; laudio: string | null; subtitle: string | null; thumbnail: string | null; work_dir: string | null };
/** Bitta buyruqdagi vazifalar soni (arg juda katta bo'lmasin). */
const LOCAL_CHUNK = 100;

/** Har kompyuterga: o'chirilgan videolarning lokal fayllarini o'chirish (qaysi kompyuterda tayyorlangani noma'lum - hammasiga). */
async function queueLocalDelete(rows: LocalFiles[]) {
  const jobs = rows.map((r) => ({ id: r.id, video: r.video, audio: r.laudio, subtitle: r.subtitle, thumbnail: r.thumbnail, work_dir: r.work_dir }))
    .filter((j) => j.video || j.audio || j.subtitle || j.thumbnail || j.work_dir);
  for (let i = 0; i < jobs.length; i += LOCAL_CHUNK) await queueForAll("delete_job_files", JSON.stringify({ jobs: jobs.slice(i, i + LOCAL_CHUNK) }));
}

/** Vazifaning Storage'dagi fayllari: tayyor audio, muqova, demo materiallari va demo natijasi (faqat shu hisob papkasida). */
function filesOf(j: JobFiles, acc: string): string[] {
  const own = (p: unknown): p is string => typeof p === "string" && p.startsWith(`acc/${acc}/`) && !p.includes("..");
  const out = [j.audio, j.cover].filter(own);
  if (j.kind === "demo") {
    try {
      const spec = JSON.parse(j.spec ?? "{}") as { media?: { media_id?: string }[] };
      for (const m of spec.media ?? []) if (m.media_id) out.push(`acc/${acc}/demo-media/${m.media_id}`);
    } catch { /* buzilgan */ }
    for (const f of ["video.mp4", "cover.jpg", "subtitles.srt"]) out.push(`acc/${acc}/demo-result/job-${j.id}/${f}`);
  }
  return out.filter(own);
}

export async function purgeChannel(channelId: string, scope: PurgeScope): Promise<PurgeResult> {
  const ch = await findChannel(channelId);
  if (!ch) throw new Error("Kanal topilmadi — ro'yxatni yangilang.");
  const name = ch.title || ch.id;
  if (isDemo()) return { ok: true, deleted: 0, skipped: 0, message: `Demo rejim: «${name}» ma'lumotlari o'chirilmaydi.` };

  const published = scope === "published";
  const [rows, [busy]] = await Promise.all([
    sql()`SELECT id::int AS id, manual_audio_path AS audio, manual_cover_path AS cover, demo_spec AS spec, kind,
        video_archive_path AS video, audio_archive_path AS laudio, subtitle_archive_path AS subtitle, thumbnail_path AS thumbnail,
        CASE WHEN work_dir_cleaned THEN NULL ELSE work_dir END AS work_dir FROM ytauto.jobs
      WHERE channel_id = ${ch.id} AND claimed_by IS NULL ${published ? sql()`AND status = ${Status.Published}` : sql()``}`,
    sql()`SELECT count(*)::int AS n FROM ytauto.jobs WHERE channel_id = ${ch.id} AND claimed_by IS NOT NULL
      ${published ? sql()`AND status = ${Status.Published}` : sql()``}`,
  ]);
  const ids = rows.map((r) => r.id as number);
  const acc = currentAccountId();
  const files = rows.flatMap((r) => filesOf(r as unknown as JobFiles, acc)); // Storage (bulut) fayllari

  // Vazifalar (bosqichlar va loglar - ON DELETE CASCADE). Qayta tekshiruv: shu orada kompyuter band qilganlari qoladi.
  const gone = ids.length ? await sql()`DELETE FROM ytauto.jobs WHERE id IN ${sql()(ids)} AND claimed_by IS NULL RETURNING id::int AS id` : [];
  const deleted = gone.length;
  const goneIds = new Set(gone.map((r) => r.id as number));
  const work: Promise<unknown>[] = [];
  if (ids.length) work.push(sql()`DELETE FROM ytauto.topic_suggestions WHERE job_id IN ${sql()(ids)}`);
  if (!published) {
    work.push(
      sql()`DELETE FROM ytauto.topic_suggestions WHERE channel_id = ${ch.id}`,
      sql()`DELETE FROM ytauto.channel_profile WHERE channel_id = ${ch.id}`,
      forgetChannelSettings(ch.id),
      forgetChannelAssetDefaults(ch.id),
    );
  }
  // Kompyuterdagi fayllar: buyruq navbatga (kompyuter darhol bajaradi, o'chiq bo'lsa - yoqilganda). Faqat haqiqatan o'chganlari.
  work.push(queueLocalDelete(rows.filter((r) => goneIds.has(r.id as number)) as unknown as LocalFiles[]));
  await Promise.all(work);
  // Fayllar - eng oxirida (baza o'zgargach); Storage vaqtincha ishlamasa ham ma'lumot o'chgan bo'ladi.
  if (files.length) await removeObjects(files).catch(() => false);

  const skipped = busy?.n as number ?? 0;
  const skipNote = skipped ? ` ${skipped} ta video hozir kompyuterda tayyorlanmoqda — u o'chirilmadi (tugagach qayta urinib ko'ring).` : "";
  const message = published
    ? `«${name}»: ${deleted} ta chiqqan video tarixi tozalandi, fayllari kompyuterdan ham o'chiriladi. YouTube'dagi videolar o'zgarmadi.${skipNote}`
    : `«${name}» kanalining barcha ma'lumotlari o'chirildi: ${deleted} ta video/mavzu (fayllari kompyuterdan ham), kanal tahlili, tavsiyalar, teg va tavsif. YouTube'dagi videolar o'zgarmadi, kanal ulanishi qoldi.${skipNote}`;
  return { ok: true, deleted, skipped, message };
}

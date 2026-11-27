import "server-only";
import { isGuest, sql } from "./db";
import { McpToolError } from "./demo-video";
import { activeChannelId, CHANNEL_DESC_MAX, CHANNEL_TAGS_MAX, channelDefaults, findChannel, setChannelDefaults, youtubeAccessToken, youtubeCredentials, youtubeCanManage } from "./youtube";
import { isoDuration } from "./channel/youtube-data";

/**
 * Claude Code (MCP) orqali asosiy YouTube kanalidagi videolar: ro'yxat (kanal nomi va logotipi bilan), tahrirlash va o'chirish.
 * Tarmoq so'rovlari baza tranzaksiyasidan TASHQARIDA: run() faqat token/bazaga tegadigan qisqa qismlar uchun.
 * Mehmon (qo'shimcha) token bilan ishlamaydi: kanal asosiy hisobniki.
 */

type Json = Record<string, unknown>;
type Run = <T>(fn: () => Promise<T>) => Promise<T>;
const fail = (m: string): never => { throw new McpToolError(m); };
const API = "https://www.googleapis.com/youtube/v3/";
const VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;
const PRIVACY = ["public", "unlisted", "private"];

async function token(run: Run, manage: boolean): Promise<string> {
  const creds = await run(async () => {
    if (isGuest()) fail("Bu qo'shimcha (mehmon) token: YouTube kanali bilan faqat asosiy hisob tokeni ishlaydi.");
    const c = await youtubeCredentials();
    if (!c) fail("YouTube kanali ulanmagan (botda Sozlamalar → YouTube).");
    if (manage && !(await youtubeCanManage()))
      fail("YouTube ruxsati faqat yuklash va o'qish uchun berilgan. Videoni tahrirlash/o'chirish uchun botda Sozlamalar → YouTube → kanalni qayta ulang (yangi ruxsat so'raladi).");
    return c!;
  });
  try { return await youtubeAccessToken(creds); } catch (e) { return fail(e instanceof Error ? e.message : String(e)); }
}

async function api<T>(tok: string, path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(API + path, {
    ...init, signal: AbortSignal.timeout(30_000),
    headers: { Authorization: `Bearer ${tok}`, ...(init.body ? { "Content-Type": "application/json" } : {}) },
  });
  if (res.status === 204) return {} as T;
  const j = (await res.json().catch(() => ({}))) as T & { error?: { message?: string; errors?: { reason?: string }[] } };
  if (!res.ok) {
    const reason = j.error?.errors?.[0]?.reason;
    if (reason === "quotaExceeded") fail("YouTube API kunlik kvotasi tugadi — ertaga qayta urinib ko'ring.");
    if (reason === "insufficientPermissions" || res.status === 403 && /scope|permission/i.test(j.error?.message ?? ""))
      fail("YouTube ruxsati yetarli emas: botda Sozlamalar → YouTube → kanalni qayta ulang.");
    if (res.status === 404 || reason === "videoNotFound") fail("Video topilmadi (yoki bu kanalniki emas).");
    fail(`YouTube API xatosi: ${j.error?.message ?? res.status}`);
  }
  return j;
}

type Thumbs = Record<string, { url?: string; width?: number; height?: number }> | undefined;
const bestThumb = (t: Thumbs) => t?.maxres?.url ?? t?.high?.url ?? t?.medium?.url ?? t?.default?.url ?? null;

type ChannelR = { items?: { id: string; snippet?: { title?: string; description?: string; customUrl?: string; thumbnails?: Thumbs }; statistics?: { subscriberCount?: string; videoCount?: string; viewCount?: string }; contentDetails?: { relatedPlaylists?: { uploads?: string } }; brandingSettings?: { image?: { bannerExternalUrl?: string } } }[] };

async function channel(tok: string) {
  const j = await api<ChannelR>(tok, "channels?part=snippet,statistics,contentDetails,brandingSettings&mine=true");
  const c = j.items?.[0];
  if (!c) fail("Bu Google hisobida YouTube kanali topilmadi.");
  const info = {
    id: c!.id, title: c!.snippet?.title ?? "",
    url: c!.snippet?.customUrl ? `https://www.youtube.com/${c!.snippet.customUrl}` : `https://www.youtube.com/channel/${c!.id}`,
    // Kanal logotipi (avatar) YouTube'dan: eng katta o'lchami.
    logo_url: bestThumb(c!.snippet?.thumbnails),
    banner_url: c!.brandingSettings?.image?.bannerExternalUrl ?? null,
    subscribers: Number(c!.statistics?.subscriberCount ?? 0), videos: Number(c!.statistics?.videoCount ?? 0), views: Number(c!.statistics?.viewCount ?? 0),
  };
  return { ...info, info, uploads: c!.contentDetails?.relatedPlaylists?.uploads ?? "" };
}

type Snippet = { channelId?: string; title?: string; description?: string; tags?: string[]; categoryId?: string; defaultLanguage?: string; defaultAudioLanguage?: string; publishedAt?: string; thumbnails?: Thumbs };
type Status = { privacyStatus?: string; publishAt?: string; selfDeclaredMadeForKids?: boolean; embeddable?: boolean; license?: string; publicStatsViewable?: boolean; uploadStatus?: string };
type VideoR = { items?: { id: string; snippet?: Snippet; status?: Status; contentDetails?: { duration?: string }; statistics?: { viewCount?: string; likeCount?: string; commentCount?: string } }[] };

function videoView(v: NonNullable<VideoR["items"]>[number], full: boolean) {
  return {
    video_id: v.id, url: `https://youtu.be/${v.id}`, title: v.snippet?.title ?? "",
    description: full ? v.snippet?.description ?? "" : (v.snippet?.description ?? "").slice(0, 300),
    tags: full ? v.snippet?.tags ?? [] : undefined, category_id: v.snippet?.categoryId,
    privacy: v.status?.privacyStatus, publish_at: v.status?.publishAt ?? null, published_at: v.snippet?.publishedAt,
    made_for_kids: v.status?.selfDeclaredMadeForKids ?? null,
    duration_sec: isoDuration(v.contentDetails?.duration),
    views: Number(v.statistics?.viewCount ?? 0), likes: Number(v.statistics?.likeCount ?? 0), comments: Number(v.statistics?.commentCount ?? 0),
    thumbnail_url: bestThumb(v.snippet?.thumbnails),
  };
}

async function getVideo(tok: string, id: string) {
  if (!VIDEO_ID.test(id)) fail("video_id 11 belgili YouTube id bo'lsin (masalan dQw4w9WgXcQ).");
  const j = await api<VideoR>(tok, `videos?part=snippet,status,contentDetails,statistics&id=${id}`);
  const v = j.items?.[0];
  if (!v) fail(`Video ${id} topilmadi (yoki bu kanalniki emas).`);
  return v!;
}

/** Video bu kanalniki ekanini tekshiradi (boshqa kanal videosini o'zgartirib bo'lmaydi; aniq xabar uchun). */
async function ownVideo(tok: string, id: string) {
  const [ch, v] = await Promise.all([channel(tok), getVideo(tok, id)]);
  if (v.snippet?.channelId !== ch.id) fail(`Video ${id} ulangan kanal (${ch.title}) niki emas.`);
  return v;
}

// ------------------------------------------------------------------ tool'lar
export async function youtubeChannel(_args: Json, run: Run) {
  const tok = await token(run, false);
  const { info } = await channel(tok);
  return { channel: info, can_edit_videos: await run(() => youtubeCanManage()) };
}

export async function listYouTubeVideos(args: Json, run: Run) {
  const tok = await token(run, false);
  const ch = await channel(tok);
  const max = Math.min(50, Math.max(1, Number(args.max_results) || 25));
  const page = typeof args.page_token === "string" && /^[A-Za-z0-9_-]{1,100}$/.test(args.page_token) ? `&pageToken=${args.page_token}` : "";
  const pl = ch.uploads
    ? await api<{ items?: { contentDetails?: { videoId?: string } }[]; nextPageToken?: string; pageInfo?: { totalResults?: number } }>(
      tok, `playlistItems?part=contentDetails&maxResults=${max}&playlistId=${encodeURIComponent(ch.uploads)}${page}`)
    : {};
  const ids = (pl.items ?? []).map((i) => i.contentDetails?.videoId).filter((v): v is string => !!v);
  const vids = ids.length ? await api<VideoR>(tok, `videos?part=snippet,status,contentDetails,statistics&id=${ids.join(",")}`) : {};
  const byId = new Map((vids.items ?? []).map((v) => [v.id, v]));
  // YtAuto yaratgan videolar: mavzu raqami bilan bog'lanadi.
  const jobs = ids.length ? await run(() => sql()`SELECT id::int AS id, youtube_video_id AS vid FROM ytauto.jobs WHERE youtube_video_id IN ${sql()(ids)}`) : [];
  const jobOf = new Map(jobs.map((r) => [r.vid as string, r.id as number]));
  return {
    channel: ch.info,
    total: pl.pageInfo?.totalResults ?? ids.length,
    videos: ids.flatMap((id) => { const v = byId.get(id); return v ? [{ ...videoView(v, false), ytauto_topic_id: jobOf.get(id) ?? null }] : []; }),
    next_page_token: pl.nextPageToken ?? null,
  };
}

export async function getYouTubeVideo(args: Json, run: Run) {
  const tok = await token(run, false);
  return videoView(await getVideo(tok, String(args.video_id ?? "")), true);
}

export async function updateYouTubeVideo(args: Json, run: Run) {
  const id = String(args.video_id ?? "");
  const tok = await token(run, true);
  const v = await ownVideo(tok, id);
  const sn = v.snippet ?? {}, st = v.status ?? {};

  const title = typeof args.title === "string" ? args.title.replace(/\s+/g, " ").trim() : sn.title ?? "";
  if (!title) fail("title bo'sh bo'lmasin.");
  if (title.length > 100) fail(`title 100 belgidan oshmasin (${title.length}).`);
  if (/[<>]/.test(title)) fail("title ichida < yoki > bo'lmasin (YouTube rad etadi).");
  const description = typeof args.description === "string" ? args.description : sn.description ?? "";
  if (description.length > 5000) fail("description 5000 belgidan oshmasin.");
  if (/[<>]/.test(description)) fail("description ichida < yoki > bo'lmasin (YouTube rad etadi).");
  let tags = sn.tags ?? [];
  if (args.tags !== undefined) {
    if (!Array.isArray(args.tags) || args.tags.some((t) => typeof t !== "string")) fail("tags satrlar ro'yxati bo'lsin.");
    tags = (args.tags as string[]).map((t) => t.trim()).filter(Boolean);
    if (tags.join(",").length > 500) fail("tags jami 500 belgidan oshmasin.");
  }
  const categoryId = args.category_id !== undefined ? String(args.category_id) : sn.categoryId ?? "22";
  if (!/^\d{1,3}$/.test(categoryId)) fail("category_id raqam bo'lsin (masalan 28 = Science & Technology).");

  const snippet: Snippet = { title, description, tags, categoryId };
  if (sn.defaultLanguage) snippet.defaultLanguage = sn.defaultLanguage;
  if (sn.defaultAudioLanguage) snippet.defaultAudioLanguage = sn.defaultAudioLanguage;

  // status: faqat yoziladigan maydonlar, eskisi saqlanadi (aks holda YouTube standartga qaytaradi).
  const status: Status = { privacyStatus: st.privacyStatus, selfDeclaredMadeForKids: st.selfDeclaredMadeForKids, embeddable: st.embeddable, license: st.license, publicStatsViewable: st.publicStatsViewable };
  if (st.publishAt) status.publishAt = st.publishAt;
  if (args.privacy !== undefined) {
    if (!PRIVACY.includes(String(args.privacy))) fail(`privacy: ${PRIVACY.join(" / ")}.`);
    status.privacyStatus = String(args.privacy);
    if (status.privacyStatus !== "private") delete status.publishAt; // rejalashtirish faqat private video uchun
  }
  if (args.publish_at !== undefined) {
    if (args.publish_at === null || args.publish_at === "") delete status.publishAt;
    else {
      const at = new Date(String(args.publish_at));
      if (Number.isNaN(at.getTime())) fail("publish_at ISO 8601 vaqt bo'lsin (masalan 2026-12-01T15:00:00Z).");
      if (at.getTime() < Date.now() + 15 * 60 * 1000) fail("publish_at kamida 15 daqiqa kelajakda bo'lsin.");
      if (st.privacyStatus === "public" && args.privacy === undefined) fail("Allaqachon public videoni rejalashtirib bo'lmaydi.");
      status.privacyStatus = "private"; // YouTube qoidasi: publishAt faqat private bilan
      status.publishAt = at.toISOString();
    }
  }
  if (args.made_for_kids !== undefined) status.selfDeclaredMadeForKids = args.made_for_kids === true;
  for (const k of Object.keys(status) as (keyof Status)[]) if (status[k] === undefined) delete status[k];

  const updated = await api<NonNullable<VideoR["items"]>[number]>(tok, "videos?part=snippet,status", {
    method: "PUT", body: JSON.stringify({ id, snippet, status }),
  });
  // YtAuto mavzusidagi title ham mos bo'lsin (bot ro'yxatida eski nom ko'rinmasin).
  await run(() => sql()`UPDATE ytauto.jobs SET title = ${title}, youtube_privacy = ${status.privacyStatus ?? null}, updated_at_utc = now() WHERE youtube_video_id = ${id}`);
  return { ok: true, message: `Video ${id} yangilandi.`, video: videoView({ ...v, ...updated, snippet: { ...sn, ...updated.snippet }, status: { ...st, ...updated.status } }, true) };
}

export async function deleteYouTubeVideo(args: Json, run: Run) {
  if (args.confirm !== true) fail("confirm: true kerak — faqat foydalanuvchi videoni YouTube'dan o'chirishni aniq so'ragan bo'lsa. Bu qaytarib bo'lmaydi.");
  const id = String(args.video_id ?? "");
  const tok = await token(run, true);
  const v = await ownVideo(tok, id);
  await api(tok, `videos?id=${id}`, { method: "DELETE" });
  // YtAuto mavzusi yozuvi tarix sifatida qoladi; havola o'chirilgan videoni ko'rsatmasin degan belgi qo'yiladi.
  await run(() => sql()`INSERT INTO ytauto.job_events(job_id, at_utc, level, stage, message)
    SELECT id, now(), 'Warning', NULL, ${`YouTube videosi MCP orqali o'chirildi: ${id}`} FROM ytauto.jobs WHERE youtube_video_id = ${id}`).catch(() => undefined);
  return { ok: true, message: `"${v.snippet?.title ?? id}" (${id}) YouTube'dan o'chirildi. Bu qaytarib bo'lmaydi.` };
}

// ------------------------------------------------------------------ kanalning standart teglari va tavsifi
/**
 * Kanal standart teglari va tavsifi (botdagi Sozlamalar → Kanallar va YouTube → «Tavsif va teglar» bilan bir xil qiymat):
 * YtAuto shu kanalga chiqaradigan har bir yangi videoga qo'yiladi; bo'sh bo'lsa Gemini o'zi yozadi.
 * Kanal MCP'sida - bog'langan kanal, oddiy ytauto'da - botdagi faol kanal. Faqat bazaga tegadi (YouTube API emas).
 */
async function defaultsChannel() {
  if (isGuest()) fail("Bu qo'shimcha (mehmon) token: kanal sozlamalari bilan faqat asosiy hisob tokeni ishlaydi.");
  const id = await activeChannelId();
  const ch = id ? await findChannel(id) : null;
  if (!ch) fail("YouTube kanali ulanmagan (botda Sozlamalar → Kanallar va YouTube).");
  return ch!;
}

const tagList = (t: string) => t.split(",").map((x) => x.trim()).filter(Boolean);
const defaultsView = (ch: { id: string; title: string }, d: { tags: string; description: string }) => ({
  channel: ch, tags: tagList(d.tags), description: d.description,
  limits: { tags_total_chars: CHANNEL_TAGS_MAX, description_chars: CHANNEL_DESC_MAX },
  note: "Applied to every NEW video YtAuto publishes on this channel (already published videos are not changed; use update_youtube_video for those). Empty = Gemini writes them per video.",
});

export async function getYouTubeDefaults(_args: Json, run: Run) {
  return run(async () => {
    const ch = await defaultsChannel();
    return defaultsView(ch, await channelDefaults(ch.id));
  });
}

export async function updateYouTubeDefaults(args: Json, run: Run) {
  const patch: { tags?: string; description?: string } = {};
  if (args.tags !== undefined) {
    if (typeof args.tags === "string") patch.tags = args.tags;
    else if (Array.isArray(args.tags) && args.tags.every((t) => typeof t === "string")) patch.tags = (args.tags as string[]).join(", ");
    else fail("tags satrlar ro'yxati (yoki vergul bilan ajratilgan satr) bo'lsin.");
  }
  if (args.description !== undefined) {
    if (typeof args.description !== "string") fail("description satr bo'lsin.");
    patch.description = args.description as string;
  }
  if (patch.tags === undefined && patch.description === undefined) fail("tags yoki description dan kamida bittasini yuboring.");
  return run(async () => {
    const ch = await defaultsChannel();
    // setChannelDefaults tekshiradi (500/2000 belgi, < >, takror teglar) - xabari modelga ko'rinsin.
    const d = await setChannelDefaults(ch.id, patch).catch((e: unknown) => fail(e instanceof Error ? e.message : String(e)));
    return { ok: true, message: `«${ch.title}» kanalining standart ${[patch.tags !== undefined && "teglari", patch.description !== undefined && "tavsifi"].filter(Boolean).join(" va ")} saqlandi.`, ...defaultsView(ch, d) };
  });
}

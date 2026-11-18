import "server-only";

/**
 * YouTube Data API (faqat youtube.readonly): kanal, oxirgi videolar va ularning statistikasi.
 * Kvota: channels 1 + playlistItems 1 + videos 1 = ~3 birlik (tahlil uchun arzon).
 */
const API = "https://www.googleapis.com/youtube/v3/";

export type ChannelInfo = { id: string; title: string; description: string; subscribers: number; videoCount: number; uploads: string; customUrl: string };
export type VideoMeta = {
  id: string; title: string; description: string; tags: string[]; publishedAt: string; durationSec: number;
  views: number; likes: number; comments: number; privacy: string; isShort: boolean;
};

async function get<T>(path: string, token: string): Promise<T> {
  const res = await fetch(API + path, { headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(20_000) });
  const j = (await res.json().catch(() => ({}))) as T & { error?: { message?: string; errors?: { reason?: string }[] } };
  if (!res.ok) {
    const reason = j.error?.errors?.[0]?.reason;
    throw new Error(reason === "quotaExceeded" ? "YouTube API kunlik kvotasi tugadi — ertaga qayta urinib ko'ring." : `YouTube API xatosi: ${j.error?.message ?? res.status}`);
  }
  return j;
}

/** ISO 8601 davomiylik (PT1H2M3S) → soniya. */
export function isoDuration(s: string | undefined): number {
  const m = /^P(?:(\d+)D)?T?(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?$/.exec(s ?? "");
  if (!m) return 0;
  return (Number(m[1] ?? 0) * 86400) + (Number(m[2] ?? 0) * 3600) + (Number(m[3] ?? 0) * 60) + Number(m[4] ?? 0);
}

export async function fetchChannel(token: string): Promise<ChannelInfo> {
  type R = { items?: { id: string; snippet?: { title?: string; description?: string; customUrl?: string }; statistics?: { subscriberCount?: string; videoCount?: string }; contentDetails?: { relatedPlaylists?: { uploads?: string } } }[] };
  const j = await get<R>("channels?part=snippet,statistics,contentDetails&mine=true", token);
  const c = j.items?.[0];
  if (!c) throw new Error("Bu Google hisobida YouTube kanali topilmadi.");
  return {
    id: c.id, title: c.snippet?.title ?? "", description: c.snippet?.description ?? "", customUrl: c.snippet?.customUrl ?? "",
    subscribers: Number(c.statistics?.subscriberCount ?? 0), videoCount: Number(c.statistics?.videoCount ?? 0),
    uploads: c.contentDetails?.relatedPlaylists?.uploads ?? "",
  };
}

/** Uploads pleylistidan eng yangi videolar id'lari (yangi → eski). */
export async function fetchRecentIds(token: string, uploads: string, max = 30): Promise<string[]> {
  if (!uploads) return [];
  type R = { items?: { contentDetails?: { videoId?: string; videoPublishedAt?: string } }[] };
  const j = await get<R>(`playlistItems?part=contentDetails&maxResults=${Math.min(50, max)}&playlistId=${encodeURIComponent(uploads)}`, token);
  return (j.items ?? [])
    .map((i) => ({ id: i.contentDetails?.videoId ?? "", at: i.contentDetails?.videoPublishedAt ?? "" }))
    .filter((v) => v.id)
    .sort((a, b) => b.at.localeCompare(a.at))
    .map((v) => v.id);
}

export async function fetchVideos(token: string, ids: string[]): Promise<VideoMeta[]> {
  if (!ids.length) return [];
  type R = { items?: { id: string; snippet?: { title?: string; description?: string; tags?: string[]; publishedAt?: string }; contentDetails?: { duration?: string }; statistics?: { viewCount?: string; likeCount?: string; commentCount?: string }; status?: { privacyStatus?: string } }[] };
  const j = await get<R>(`videos?part=snippet,contentDetails,statistics,status&id=${ids.slice(0, 50).map(encodeURIComponent).join(",")}`, token);
  const byId = new Map((j.items ?? []).map((v) => [v.id, v]));
  return ids.flatMap((id) => {
    const v = byId.get(id);
    if (!v) return [];
    const durationSec = isoDuration(v.contentDetails?.duration);
    return [{
      id, title: v.snippet?.title ?? "", description: v.snippet?.description ?? "", tags: v.snippet?.tags ?? [], publishedAt: v.snippet?.publishedAt ?? "",
      durationSec, views: Number(v.statistics?.viewCount ?? 0), likes: Number(v.statistics?.likeCount ?? 0), comments: Number(v.statistics?.commentCount ?? 0),
      privacy: v.status?.privacyStatus ?? "",
      // Shorts: 60 s gacha yoki 3 daqiqagacha va #shorts belgisi bilan (API aniq belgi bermaydi).
      isShort: durationSec > 0 && (durationSec <= 60 || (durationSec <= 180 && /#shorts/i.test(`${v.snippet?.title} ${v.snippet?.description}`))),
    }];
  });
}

import "server-only";
import { createDemoVideo, DEMO_EFFECTS, McpToolError, MEDIA_LIMIT, publishVideo, requestMediaUpload, videoResult, videoResultLive, videoStatus, videoStatusLive } from "./demo-video";
import { addTopics, deleteTopics, listTopicsTool, topicOptions, updateTopics } from "./mcp-topics";
import { deleteYouTubeVideo, getYouTubeDefaults, getYouTubeVideo, listYouTubeVideos, updateYouTubeDefaults, updateYouTubeVideo, youtubeChannel } from "./mcp-youtube";
import { MCP_MAX_TOPICS } from "./topics";

/**
 * YtAuto MCP server (Model Context Protocol, Streamable HTTP, holatsiz: har POST - mustaqil JSON-RPC so'rov, SSE yo'q).
 * Claude Code ulanadi: claude mcp add --transport http ytauto https://<bot>/api/mcp --header "Authorization: Bearer <token>".
 * Auth va hisob konteksti route.ts da; bu yerda - protokol va tool'lar.
 */
export const SERVER_INFO = { name: "ytauto", title: "YtAuto", version: "1.3.0" };
const VERSIONS = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

export const INSTRUCTIONS = `YtAuto turns a software project into a narrated product demo / intro video. You (Claude Code) only study the project and capture visuals; YtAuto writes the script (Gemini), generates the voice (VoiceStudio, the user's chosen voice), edits (OpenFilm: camera moves, cuts, transitions, highlights, kinetic text, subtitles, music, SFX; only the product's own screenshots and recordings are shown, never decorative backgrounds), renders and optionally publishes to the user's main YouTube channel.

Workflow:
1. Study the project: README, docs, routes/screens, main user workflows. Pick the 3-8 features that matter most to a new user.
2. Run the project locally (dev server, CLI, app). Use realistic but harmless demo data. Never capture secrets, tokens, real personal data or private URLs.
3. Capture visuals: PNG screenshots of the key screens (1920x1080 for a 16:9 video, 1080x1920 for 9:16) and short screen recordings (MP4/WebM, 4-20 s, one action each: e.g. creating an item, filtering, a successful save). Playwright (page.screenshot / recordVideo) works well for web apps. Up to ${MEDIA_LIMIT} files; screenshots up to 25 MB, recordings up to 500 MB.
4. request_media_upload with the list of files → upload each file with the returned curl line (HTTP PUT of the raw bytes).
5. create_demo_video with: the project context (name, summary, features), every media_id with a precise title + description of what is visible/what happens, and a highlight (the UI area to zoom into); plus requirements (language — ask the user if the narration should be English or Uzbek (uz) when unclear —, duration, audience, tone, format, effects, notes from the user). The video shows only your captures, so capture clean full-window views of the app without browser chrome, dev tools or the desktop around it. Set publish.requested only if the user explicitly asked to publish.
6. Video production is asynchronous and runs on the user's PC (minutes to an hour). The PC runs a YtAuto agent that is notified instantly and reports progress in real time: call get_video_status with wait_sec: 25 — it returns as soon as the job changes (or after 25 s), so just call it again in a loop. When status is "ready" call get_video_result and give the user the links (direct.watch_url streams the video straight from the PC; video.url is a 24 h download link) and the local path.
7. publish_video only when the user explicitly tells you to publish; it goes to the user's main YouTube channel only.

Topics (the regular video queue): each topic becomes one narrated video that YtAuto makes on the user's PC and publishes on its date.
- get_topic_options first: time zone, templates, voices, formats, music tracks and defaults.
- add_topics adds up to ${MCP_MAX_TOPICS} topics in one call. You supply every part of each topic yourself: topic (the subject), notes (angle, key points, facts, audience, what to avoid), and optionally publish_at, template, format, voice, music; or a finished title + script (exact narration) to skip Gemini. The call is all-or-nothing: if one topic is invalid nothing is added.
- list_topics / update_topics / delete_topics manage the queue. Only topics whose video is not made yet can be edited or deleted. Delete only when the user asked.

YouTube channel (main account only; the channel this connection is bound to — or, for the plain ytauto server without ?channel, the channel currently active in the YtAuto bot): get_youtube_channel (name, logo, banner, stats), list_youtube_videos, get_youtube_video, update_youtube_video (title, description, tags, category, privacy, scheduled time, made for kids), delete_youtube_video. Edit or delete only when the user explicitly asks; deleting is permanent, so confirm the exact video with the user first.
- Channel defaults: get_youtube_defaults / update_youtube_defaults read and change the default tags and description that YtAuto puts on every new video of this channel (empty = Gemini writes them per video). Change them only when the user asks.`;

type Json = Record<string, unknown>;
const obj = (properties: Json, required: string[] = []) => ({ type: "object", properties, required, additionalProperties: false });
const s = (description: string, extra: Json = {}) => ({ type: "string", description, ...extra });

export const TOOLS = [
  {
    name: "request_media_upload",
    title: "Get upload links for screenshots and recordings",
    description: "Step 1 of a demo video: returns a single-use HTTPS upload link (PUT, expires in 2 h) and a media_id for each screenshot or screen recording you captured. Upload the raw file bytes with the returned curl command, then use the media_id values in create_demo_video. Large files never pass through the MCP connection.",
    inputSchema: obj({
      files: {
        type: "array", minItems: 1, maxItems: MEDIA_LIMIT, description: "Files to upload, in the order they should appear.",
        items: obj({
          name: s("Local file name or path, e.g. shots/01-dashboard.png"),
          size_bytes: { type: "integer", minimum: 1, description: "File size in bytes" },
          kind: { type: "string", enum: ["screenshot", "video"], description: "screenshot (png/jpg/webp/gif) or video (mp4/webm/mov/mkv screen recording)" },
        }, ["name", "size_bytes", "kind"]),
      },
    }, ["files"]),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  },
  {
    name: "create_demo_video",
    title: "Create a product demo video",
    description: "Step 2: queues a demo video job in YtAuto with the project context and the uploaded visuals. YtAuto writes the script with Gemini (draft + optimisation), generates the voice with the user's VoiceStudio voice (or the default), edits with OpenFilm (zoom/pan, transitions, subtitles synced to the voice, music, SFX) and renders on the user's PC. Returns a job_id immediately; the work is asynchronous. Publishing is separate (publish_video) unless publish.requested is true because the user explicitly asked for it.",
    inputSchema: obj({
      project: obj({
        name: s("Project / product name", { maxLength: 120 }),
        summary: s("What the project does, for whom, and the problem it solves (2-6 sentences, from your analysis of the code base)", { maxLength: 3000 }),
        url: s("Public website or app URL, if any"),
        repository: s("Repository URL, if public"),
        tech_stack: s("Main technologies, e.g. Next.js, PostgreSQL"),
        features: {
          type: "array", maxItems: 20, description: "Key features in the order the video should present them",
          items: obj({ name: s("Feature name"), description: s("What it does for the user") }, ["name", "description"]),
        },
      }, ["name", "summary"]),
      media: {
        type: "array", minItems: 1, maxItems: MEDIA_LIMIT, description: "Every uploaded visual with its context, in presentation order",
        items: obj({
          media_id: s("media_id returned by request_media_upload"),
          title: s("Short title, e.g. 'Dashboard overview'"),
          description: s("What exactly is visible (screenshot) or what action happens (recording); the script is written from this"),
          feature: s("Which feature it demonstrates"),
          highlight: s("UI area to zoom into, e.g. 'the Export button, top right'"),
        }, ["media_id", "title", "description"]),
      },
      requirements: obj({
        language: s("Narration language, ISO 639-1 (default en). \"uz\" = Uzbek (Latin script): script, voice and subtitles are all Uzbek. Needs a voice profile whose language is that language (picked automatically, or pass voice_profile); other languages: ru, tr, kk, de, fr, es, ar"),
        duration_sec: { type: "integer", minimum: 0, maximum: 600, description: "Target length in seconds; 0 = automatic (default)" },
        audience: s("Who the video is for"),
        tone: s("e.g. friendly and technical"),
        format: { type: "string", enum: ["16:9", "9:16", "1:1"], description: "Default 16:9" },
        notes: s("Any other wishes of the user (what to stress, what to avoid)"),
        voice_profile: s("Optional voice profile name; default is the voice chosen in the YtAuto Telegram bot"),
        effects: {
          type: "array", maxItems: DEMO_EFFECTS.length, uniqueItems: true,
          description: "Edit effects to use (any mix; omit or empty = YtAuto picks a varied mix from all of them). Camera: zoom_in, zoom_out, pan, ken_burns, scroll_through, parallax_3d, split_screen, picture_in_picture. Transitions: crossfade, slide_push, whip_pan, zoom_through, wipe, blur_dissolve, glitch. Highlights: spotlight, outline_box, cursor_click, magnifier, callout. Text: title_reveal, kinetic_text. Rhythm: cut_ins, speed_ramp, freeze_frame, impact_shake. All effects work on the product's own visuals only; the video never gets decorative backgrounds.",
          items: { type: "string", enum: DEMO_EFFECTS },
        },
      }),
      publish: obj({
        requested: { type: "boolean", description: "true ONLY if the user explicitly asked to publish to YouTube. Main account channel only." },
        when: s("\"now\" or an ISO 8601 time at least 30 min ahead (default now)"),
      }),
    }, ["project", "media"]),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  },
  {
    name: "get_video_status",
    title: "Demo video status",
    description: "Progress of a demo video job: queued / in_progress (script, voice, align, edit, render, validate, archive) / ready / publishing / published / failed, with percent, queue position, the user's PCs online state and recent log lines. Real time: with wait_sec (1-25) the call waits for the next change reported by the PC agent and returns immediately when it happens (long-poll) — no need to sleep between calls.",
    inputSchema: obj({
      job_id: { type: "integer", minimum: 1 },
      wait_sec: { type: "integer", minimum: 0, maximum: 25, description: "Wait up to this many seconds for the next change (0 = answer now). Recommended: 25." },
    }, ["job_id"]),
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  {
    name: "get_video_result",
    title: "Get the finished demo video",
    description: "When the job is ready: title, description, duration, a 24 h download link for the MP4 (and cover image + SRT subtitles), a direct public link that streams the file from the user's PC (direct.watch_url, while the PC is on), the local archive path on the user's PC, and the YouTube link if published.",
    inputSchema: obj({ job_id: { type: "integer", minimum: 1 } }, ["job_id"]),
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  {
    name: "publish_video",
    title: "Publish a demo video to YouTube",
    description: "Publishes the finished video to the user's MAIN YouTube channel (never to additional/guest accounts). Call only when the user explicitly tells you to publish. The finished file is uploaded as is (not re-rendered). If the video is still queued, it will be published as soon as it is ready.",
    inputSchema: obj({
      job_id: { type: "integer", minimum: 1 },
      when: s("\"now\" (default) or an ISO 8601 time at least 30 min ahead"),
      confirm: { type: "boolean", const: true, description: "Must be true: the user explicitly asked to publish" },
    }, ["job_id", "confirm"]),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: true },
  },
  {
    name: "get_topic_options",
    title: "Options for new topics",
    description: "Channel time zone, default publish time, available templates, voices, formats, background music tracks and defaults. Call before add_topics/update_topics so every value you send is valid.",
    inputSchema: obj({}),
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  {
    name: "list_topics",
    title: "List video topics",
    description: "The user's topic queue: all not-yet-published topics plus the 30 latest published, with id, status, publish time (channel time zone), template/format/voice/music, YouTube link, error and whether it can still be edited/deleted.",
    inputSchema: obj({ filter: { type: "string", enum: ["all", "pending", "published", "problems"], description: "pending = video not made yet (editable); default all" } }),
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  {
    name: "add_topics",
    title: "Add video topics (up to 40)",
    description: `Adds 1-${MCP_MAX_TOPICS} topics to the queue in ONE call; YtAuto makes and publishes one video per topic on the user's PC. Provide all parts of each topic yourself. All-or-nothing: if any topic is invalid, nothing is added and the error says which. Without title+script, Gemini writes the script from topic + notes, so make notes rich (angle, key points, facts, examples, audience, call to action, what to avoid). Videos are always in English.`,
    inputSchema: obj({
      topics: {
        type: "array", minItems: 1, maxItems: MCP_MAX_TOPICS, description: "Topics in publishing order",
        items: obj({
          topic: s("The subject of the video, e.g. 'How HTTPS keeps your password safe'", { maxLength: 500 }),
          notes: s("Everything the script writer should know: angle, structure, key points, facts, examples, audience, tone, call to action, what to avoid", { maxLength: 4000 }),
          publish_at: s("Local time in the channel time zone, \"YYYY-MM-DD HH:mm\", at least 30 min ahead. Omit = next free slot"),
          template: s("Template name from get_topic_options (omit = default)"),
          format: { type: "string", enum: ["16:9", "9:16", "1:1"], description: "16:9 regular, 9:16 Shorts, 1:1 square (omit = default)" },
          voice: s("Voice profile from get_topic_options (omit = default)"),
          music: s("Background music: omit = automatic, \"none\" = no music, or a track id from get_topic_options"),
          title: s("Optional finished YouTube title (max 100 chars, no < >). Must be given together with script", { maxLength: 100 }),
          script: s("Optional exact narration / subtitle text. Must be given together with title; then Gemini is skipped", { maxLength: 30000 }),
        }, ["topic"]),
      },
    }, ["topics"]),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  },
  {
    name: "update_topics",
    title: "Update video topics",
    description: `Changes 1-${MCP_MAX_TOPICS} topics whose video is not made yet. Only the fields you send change. Changing content (topic, notes, template, format, voice, music, title/script) puts the topic back in the queue; changing only publish_at keeps it. Each update is applied separately; the result lists ok/failed per id.`,
    inputSchema: obj({
      updates: {
        type: "array", minItems: 1, maxItems: MCP_MAX_TOPICS,
        items: obj({
          id: { type: "integer", minimum: 1, description: "Topic id from list_topics" },
          topic: s("New subject", { maxLength: 500 }),
          notes: s("New notes", { maxLength: 4000 }),
          publish_at: s("Local time \"YYYY-MM-DD HH:mm\" in the channel time zone"),
          template: s("Template name"),
          format: { type: "string", enum: ["16:9", "9:16", "1:1"] },
          voice: s("Voice profile"),
          music: s("\"\" = automatic, \"none\" or a track id"),
          title: s("Manual title (with script); send \"\" for both title and script to let Gemini write again", { maxLength: 100 }),
          script: s("Manual narration (with title)", { maxLength: 30000 }),
        }, ["id"]),
      },
    }, ["updates"]),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  },
  {
    name: "delete_topics",
    title: "Delete video topics",
    description: `Deletes 1-${MCP_MAX_TOPICS} topics whose video is not made yet (queued, failed, needs attention or cancelled). Call only when the user asked to delete them. Does not touch YouTube; use delete_youtube_video for a published video.`,
    inputSchema: obj({
      ids: { type: "array", minItems: 1, maxItems: MCP_MAX_TOPICS, items: { type: "integer", minimum: 1 } },
      confirm: { type: "boolean", const: true, description: "Must be true: the user asked to delete these topics" },
    }, ["ids", "confirm"]),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false },
  },
  {
    name: "get_youtube_channel",
    title: "YouTube channel info and logo",
    description: "The user's connected main YouTube channel: name, URL, logo (avatar) image URL taken from YouTube, banner URL, subscriber/video/view counts, and whether the connection allows editing/deleting videos.",
    inputSchema: obj({}),
    annotations: { readOnlyHint: true, openWorldHint: true },
  },
  {
    name: "list_youtube_videos",
    title: "List YouTube videos",
    description: "Videos of the connected main channel, newest first (title, privacy, schedule, stats, thumbnail, linked YtAuto topic id), plus the channel name and logo. Paginate with page_token.",
    inputSchema: obj({
      max_results: { type: "integer", minimum: 1, maximum: 50, description: "Default 25" },
      page_token: s("next_page_token from the previous call"),
    }),
    annotations: { readOnlyHint: true, openWorldHint: true },
  },
  {
    name: "get_youtube_video",
    title: "Get a YouTube video",
    description: "Full details of one video: title, full description, tags, category, privacy, scheduled time, made-for-kids, stats.",
    inputSchema: obj({ video_id: s("11-character YouTube video id") }, ["video_id"]),
    annotations: { readOnlyHint: true, openWorldHint: true },
  },
  {
    name: "update_youtube_video",
    title: "Edit a YouTube video",
    description: "Edits a video on the connected main channel. Only fields you send change; the rest is kept. Call only when the user asked for the change. Scheduling (publish_at) makes the video private until that time.",
    inputSchema: obj({
      video_id: s("11-character YouTube video id"),
      title: s("New title (max 100 chars, no < >)", { maxLength: 100 }),
      description: s("New full description (max 5000 chars, no < >)", { maxLength: 5000 }),
      tags: { type: "array", items: { type: "string" }, description: "Replaces all tags (max 500 chars total)" },
      category_id: s("YouTube category id, e.g. 22 People & Blogs, 27 Education, 28 Science & Technology"),
      privacy: { type: "string", enum: ["public", "unlisted", "private"] },
      publish_at: s("ISO 8601 time to go public (video stays private until then); \"\" removes the schedule"),
      made_for_kids: { type: "boolean" },
    }, ["video_id"]),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: true },
  },
  {
    name: "delete_youtube_video",
    title: "Delete a YouTube video",
    description: "PERMANENTLY deletes a video from the connected main channel (views, comments and likes are lost; cannot be undone). Call only when the user explicitly asked to delete this exact video; confirm title and id with the user first.",
    inputSchema: obj({
      video_id: s("11-character YouTube video id"),
      confirm: { type: "boolean", const: true, description: "Must be true: the user explicitly asked to delete this video" },
    }, ["video_id", "confirm"]),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: true },
  },
  {
    name: "get_youtube_defaults",
    title: "Channel default tags and description",
    description: "The default tags and description YtAuto puts on every new video it publishes on this channel (the same values as in the YtAuto bot: Settings → Channels and YouTube → Description and tags). Empty = Gemini writes them per video.",
    inputSchema: obj({}),
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  {
    name: "update_youtube_defaults",
    title: "Set channel default tags and description",
    description: "Sets the default tags and/or description for every NEW video YtAuto publishes on this channel. Only the fields you send change; send \"\" (or [] for tags) to clear one so Gemini writes it per video. Already published videos are not changed (use update_youtube_video for those). Call only when the user asked for the change.",
    inputSchema: obj({
      tags: { type: "array", items: { type: "string" }, description: "Replaces all default tags (duplicates removed; max 500 chars total, no < >). [] clears them" },
      description: s("Replaces the default description (max 2000 chars, no < >). \"\" clears it", { maxLength: 2000 }),
    }),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  },
] as const;

type Run = <T>(fn: () => Promise<T>) => Promise<T>;
/** Faqat bazaga tegadigan tool: butunlay bitta hisob tranzaksiyasida. */
const db = (f: (a: Json) => Promise<unknown>) => (a: Json, run: Run) => run(() => f(a));

/** YouTube tool'lari tarmoq so'rovlarini tranzaksiyadan tashqarida qiladi va run()ni o'zi chaqiradi. */
const HANDLERS: Record<string, (a: Json, run: Run) => Promise<unknown>> = {
  request_media_upload: db(requestMediaUpload),
  create_demo_video: db(createDemoVideo),
  get_video_status: db(videoStatus),
  get_video_result: db(videoResult),
  publish_video: db(publishVideo),
  get_topic_options: db(topicOptions),
  list_topics: db(listTopicsTool),
  add_topics: db(addTopics),
  update_topics: db(updateTopics),
  delete_topics: db(deleteTopics),
  get_youtube_channel: youtubeChannel,
  list_youtube_videos: listYouTubeVideos,
  get_youtube_video: getYouTubeVideo,
  update_youtube_video: updateYouTubeVideo,
  delete_youtube_video: deleteYouTubeVideo,
  get_youtube_defaults: getYouTubeDefaults,
  update_youtube_defaults: updateYouTubeDefaults,
};

type RpcRequest = { jsonrpc?: string; id?: string | number | null; method?: string; params?: Json };
type RpcResponse = { jsonrpc: "2.0"; id: string | number | null; result?: unknown; error?: { code: number; message: string } };

const ok = (id: RpcRequest["id"], result: unknown): RpcResponse => ({ jsonrpc: "2.0", id: id ?? null, result });
const err = (id: RpcRequest["id"], code: number, message: string): RpcResponse => ({ jsonrpc: "2.0", id: id ?? null, error: { code, message } });

/**
 * Bitta JSON-RPC xabar. Bildirishnoma (id yo'q) - javobsiz (null). Tool xatolari MCP qoidasiga ko'ra natija ichida (isError: true),
 * shunda model xabarni ko'radi va tuzatib qayta urinadi; protokol xatolari - JSON-RPC error.
 * run - tool'ni hisob kontekstida bajaradi (route.ts: withAccount).
 */
/** channel - kanal MCP'si (/api/mcp?channel=...): hamma tool'lar shu kanal bilan ishlaydi; Claude'ga shu aytiladi. */
export async function handleRpc(m: RpcRequest, run: Run, accountId: string, channel?: { id: string; title: string } | null): Promise<RpcResponse | null> {
  if (!m || typeof m !== "object" || m.jsonrpc !== "2.0" || typeof m.method !== "string") return err(m?.id, -32600, "Invalid Request");
  const isNotification = m.id === undefined;
  switch (m.method) {
    case "initialize": {
      const asked = String(m.params?.protocolVersion ?? "");
      return ok(m.id, {
        protocolVersion: VERSIONS.includes(asked) ? asked : VERSIONS[0],
        capabilities: { tools: { listChanged: false } },
        serverInfo: channel ? { ...SERVER_INFO, title: `YtAuto — ${channel.title}` } : SERVER_INFO,
        instructions: channel ? channelNote(channel) + INSTRUCTIONS : INSTRUCTIONS,
      });
    }
    case "ping":
      return ok(m.id, {});
    case "tools/list":
      return ok(m.id, { tools: TOOLS });
    case "tools/call": {
      const name = String(m.params?.name ?? "");
      const fn = HANDLERS[name];
      if (!fn) return err(m.id, -32602, `Unknown tool: ${name}`);
      const args = (m.params?.arguments ?? {}) as Json;
      try {
        // Holat va natija: agent bilan aloqa (kutish, havola) DB tranzaksiyasidan tashqarida; YouTube tool'lari ham run()ni o'zi chaqiradi.
        const result = name === "get_video_status" ? await videoStatusLive(args, run, accountId)
          : name === "get_video_result" ? await videoResultLive(args, run, accountId)
          : await fn(args, run);
        return ok(m.id, { content: [{ type: "text", text: JSON.stringify(result, null, 2) }], isError: false });
      } catch (e) {
        const known = e instanceof McpToolError;
        if (!known) console.error("[mcp]", name, e);
        const msg = e instanceof Error ? e.message.split("\n")[0] : String(e);
        return ok(m.id, { content: [{ type: "text", text: known ? msg : `Server error: ${msg}` }], isError: true });
      }
    }
    default:
      if (isNotification || m.method.startsWith("notifications/")) return null;
      return err(m.id, -32601, `Method not found: ${m.method}`);
  }
}

/** Botdagi «Claude Code ulash»: nusxalanadigan bitta buyruq (terminalda yoki Claude Code'ning o'zida bajariladi). */
export function setupCommand(url: string, token: string, channel?: { id: string; name: string }): string {
  const name = channel ? channel.name : "ytauto";
  const target = channel ? `${url}?channel=${encodeURIComponent(channel.id)}` : url;
  return `claude mcp add --transport http --scope user ${name} ${target} --header "Authorization: Bearer ${token}"`;
}

/** Kanal MCP'sining Claude Code'dagi nomi: ytauto-<kanal nomi>; nomlar takrorlansa kanal id oxiri qo'shiladi. */
export function channelServerNames(channels: { id: string; title: string }[]): Map<string, string> {
  const slug = (t: string) => t.normalize("NFKD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 32).replace(/-+$/, "");
  const base = channels.map((c) => slug(c.title) || c.id.slice(-6).toLowerCase());
  return new Map(channels.map((c, i) => {
    const dup = base.filter((b) => b === base[i]).length > 1;
    return [c.id, `ytauto-${base[i]}${dup ? "-" + c.id.slice(-4).toLowerCase() : ""}`];
  }));
}

function channelNote(c: { id: string; title: string }) {
  return `THIS CONNECTION IS BOUND TO ONE YOUTUBE CHANNEL: "${c.title}" (${c.id}). Every tool here works only with this channel: topics are added to and listed from this channel's queue, YouTube tools read/edit/delete this channel's videos, demo videos are published to this channel. The user may have other channels with their own ytauto-* MCP servers; use the server of the channel the user means.

`;
}

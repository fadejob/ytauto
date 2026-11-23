import "server-only";
import { createDemoVideo, DEMO_EFFECTS, McpToolError, MEDIA_LIMIT, publishVideo, requestMediaUpload, videoResult, videoResultLive, videoStatus, videoStatusLive } from "./demo-video";

/**
 * YtAuto MCP server (Model Context Protocol, Streamable HTTP, holatsiz: har POST - mustaqil JSON-RPC so'rov, SSE yo'q).
 * Claude Code ulanadi: claude mcp add --transport http ytauto https://<bot>/api/mcp --header "Authorization: Bearer <token>".
 * Auth va hisob konteksti route.ts da; bu yerda - protokol va tool'lar.
 */
export const SERVER_INFO = { name: "ytauto", title: "YtAuto demo video", version: "1.1.0" };
const VERSIONS = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

export const INSTRUCTIONS = `YtAuto turns a software project into a narrated product demo / intro video. You (Claude Code) only study the project and capture visuals; YtAuto writes the script (Gemini), generates the voice (VoiceStudio, the user's chosen voice), edits (OpenFilm: camera moves, cuts, transitions, highlights, kinetic text, subtitles, music, SFX; only the product's own screenshots and recordings are shown, never decorative backgrounds), renders and optionally publishes to the user's main YouTube channel.

Workflow:
1. Study the project: README, docs, routes/screens, main user workflows. Pick the 3-8 features that matter most to a new user.
2. Run the project locally (dev server, CLI, app). Use realistic but harmless demo data. Never capture secrets, tokens, real personal data or private URLs.
3. Capture visuals: PNG screenshots of the key screens (1920x1080 for a 16:9 video, 1080x1920 for 9:16) and short screen recordings (MP4/WebM, 4-20 s, one action each: e.g. creating an item, filtering, a successful save). Playwright (page.screenshot / recordVideo) works well for web apps. Up to ${MEDIA_LIMIT} files; screenshots up to 25 MB, recordings up to 500 MB.
4. request_media_upload with the list of files → upload each file with the returned curl line (HTTP PUT of the raw bytes).
5. create_demo_video with: the project context (name, summary, features), every media_id with a precise title + description of what is visible/what happens, and a highlight (the UI area to zoom into); plus requirements (language — ask the user if the narration should be English or Uzbek (uz) when unclear —, duration, audience, tone, format, effects, notes from the user). The video shows only your captures, so capture clean full-window views of the app without browser chrome, dev tools or the desktop around it. Set publish.requested only if the user explicitly asked to publish.
6. Video production is asynchronous and runs on the user's PC (minutes to an hour). The PC runs a YtAuto agent that is notified instantly and reports progress in real time: call get_video_status with wait_sec: 25 — it returns as soon as the job changes (or after 25 s), so just call it again in a loop. When status is "ready" call get_video_result and give the user the links (direct.watch_url streams the video straight from the PC; video.url is a 24 h download link) and the local path.
7. publish_video only when the user explicitly tells you to publish; it goes to the user's main YouTube channel only.`;

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
] as const;

const HANDLERS: Record<string, (a: Json) => Promise<unknown>> = {
  request_media_upload: requestMediaUpload,
  create_demo_video: createDemoVideo,
  get_video_status: videoStatus,
  get_video_result: videoResult,
  publish_video: publishVideo,
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
export async function handleRpc(m: RpcRequest, run: <T>(fn: () => Promise<T>) => Promise<T>, accountId: string): Promise<RpcResponse | null> {
  if (!m || typeof m !== "object" || m.jsonrpc !== "2.0" || typeof m.method !== "string") return err(m?.id, -32600, "Invalid Request");
  const isNotification = m.id === undefined;
  switch (m.method) {
    case "initialize": {
      const asked = String(m.params?.protocolVersion ?? "");
      return ok(m.id, {
        protocolVersion: VERSIONS.includes(asked) ? asked : VERSIONS[0],
        capabilities: { tools: { listChanged: false } },
        serverInfo: SERVER_INFO,
        instructions: INSTRUCTIONS,
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
        // Holat va natija: agent bilan aloqa (kutish, havola) DB tranzaksiyasidan tashqarida.
        const result = name === "get_video_status" ? await videoStatusLive(args, run, accountId)
          : name === "get_video_result" ? await videoResultLive(args, run, accountId)
          : await run(() => fn(args));
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
export function setupCommand(url: string, token: string): string {
  return `claude mcp add --transport http --scope user ytauto ${url} --header "Authorization: Bearer ${token}"`;
}

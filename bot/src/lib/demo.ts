import "server-only";
import { addDays, parseLocal, utcToZoned, zonedToUtc } from "./time";
import { Status, type Action, type ActionResult, type Catalog, type JobLogs, type Prompts, type Topic, type WorkerRun } from "./types";

/**
 * DEMO_MODE=1: online bazasiz, xotiradagi namunaviy ma'lumotlar — faqat dizaynni brauzerda ko'rish uchun.
 * Server qayta ishga tushsa ma'lumotlar boshlang'ich holatga qaytadi.
 */
export const isDemo = () => process.env.DEMO_MODE === "1";

const TZ = "Asia/Tashkent";
export const demoCatalog: Catalog = {
  voiceProfiles: ["MOSS ayol", "MOSS erkak"], templates: [{ name: "Clean explainer", kind: "OpenFilmProject" }, { name: "Neon facts", kind: "Mp4Reference" }], pendingTemplates: [], defaults: { voiceProfile: "MOSS ayol", template: "", format: 0 },
  youtube: { publishTime: "18:00", timeZone: TZ }, known: true,
};

type Row = { id: number; topic: string; notes: string; title: string | null; status: number; at: Date; yt: string | null; made: boolean; publishNow: boolean; claimed: boolean; error: string | null; archived?: boolean; uploadRequested?: boolean; template?: string; format?: number; voice?: string; script?: string; mtitle?: string; audio?: boolean };

function seed(): { rows: Row[]; prompts: Prompts; nextId: number } {
  const today = utcToZoned(new Date(), TZ).date;
  const at = (d: number) => zonedToUtc(addDays(today, d), "18:00", TZ);
  return {
    nextId: 109,
    rows: [
      { id: 108, topic: "What happens if you click the PAY button twice?", notes: "", title: "Double Click Pay? Idempotency Prevents Duplicate Charges", status: Status.NeedsAttention, at: at(-0.2), yt: null, made: true, publishNow: false, claimed: false, error: "Public vaqti o'tib ketgan — video arxivda.", archived: true },
      { id: 101, topic: "How black holes form", notes: "Explain stellar collapse step by step for curious teenagers; end with the image of M87*.", title: null, status: Status.Queued, at: at(2), yt: null, made: false, publishNow: false, claimed: false, error: null },
      { id: 102, topic: "Why the sky is blue", notes: "Rayleigh scattering with simple analogies.", title: null, status: Status.Queued, at: at(4), yt: null, made: false, publishNow: false, claimed: false, error: null },
      { id: 103, topic: "The history of the Silk Road", notes: "Focus on Samarkand and Bukhara.", title: null, status: Status.NeedsAttention, at: at(6), yt: null, made: false, publishNow: false, claimed: false, error: "Ovoz profili topilmadi — Sozlamalar → Ovoz." },
      { id: 104, topic: "How vaccines train the immune system", notes: "", title: "How Vaccines Teach Your Body to Fight", status: Status.Rendering, at: at(1), yt: null, made: true, publishNow: false, claimed: true, error: null },
      { id: 105, topic: "Octopus intelligence", notes: "", title: "Octopuses Are Smarter Than You Think", status: Status.Scheduled, at: at(0.5), yt: "dQw4w9WgXcQ", made: true, publishNow: false, claimed: false, error: null },
      { id: 106, topic: "How GPS works", notes: "", title: "How Your Phone Knows Where You Are", status: Status.Published, at: at(-2), yt: "dQw4w9WgXcQ", made: true, publishNow: false, claimed: false, error: null },
      { id: 107, topic: "The Great Wall from space", notes: "", title: "Can You See the Great Wall from Space?", status: Status.Published, at: at(-4), yt: "dQw4w9WgXcQ", made: true, publishNow: false, claimed: false, error: null },
    ],
    prompts: {
      script: "You are writing a narrated YouTube explainer.\nTopic: {topic}\nExtra notes from the channel owner: {notes}\nLanguage of narration, subtitles, title and description: {language_name} ({language})\n{length_rule}\nVideo format: {format}\n\nWrite clear, friendly narration...\n{fact_sheet}",
      montage: "You are a video editor using OpenFilm. Split the narration into scenes by meaning, keep text readable, use calm transitions...",
    },
  };
}

const g = globalThis as unknown as { __ytautoDemo?: ReturnType<typeof seed> };
const db = () => (g.__ytautoDemo ??= seed());

export function demoTopics(): Topic[] {
  return db().rows.map((r) => ({
    id: r.id, topic: r.topic, notes: r.notes, title: r.title, status: r.status, publishAt: r.at.toISOString(), publishLocal: utcToZoned(r.at, TZ),
    youtubeUrl: r.yt ? `https://youtu.be/${r.yt}` : null, publishNow: r.publishNow, claimed: r.claimed, error: r.error,
    editable: !r.made && !r.claimed && [0, 12, 13].includes(r.status), deletable: !r.made && !r.claimed && [0, 12, 13, 14].includes(r.status),
    canPublishNow: !r.publishNow && ![11, 14].includes(r.status),
    canUploadReady: !!r.archived && !r.yt && !r.claimed && !r.uploadRequested && !r.publishNow && ![11, 14].includes(r.status),
    uploadRequested: !!r.uploadRequested && !r.yt,
    template: r.template ?? "", format: r.format ?? 0, voice: r.voice ?? "MOSS ayol",
    manualScript: r.script || null, manualTitle: r.mtitle || null, hasAudio: !!r.audio,
    canRetry: !r.claimed && [12, 13].includes(r.status), canCancel: ![11, 14].includes(r.status),
    canReschedule: (r.made || r.claimed) && !r.claimed && ![11, 14].includes(r.status), canForceUpload: r.status === 13 && !r.yt && !!r.archived && !r.claimed,
  }));
}

export function demoRuns(): WorkerRun[] {
  const now = Date.now();
  const mk = (i: number, min: number, outcome: string | null, jobId: number | null, trigger: string, message: string | null): WorkerRun => ({
    runId: `worker-PC1-${i}`, startedAt: new Date(now - min * 60000).toISOString(),
    finishedAt: outcome ? new Date(now - (min - 38) * 60000).toISOString() : null, outcome, jobId, message, trigger, machine: "PC1",
    stats: outcome === "idle" || !outcome ? null : {
      machine: "PC1", at: new Date(now - (min - 38) * 60000).toISOString(), durationSec: 38 * 60,
      disk: [
        { name: "Whisper modeli (subtitr)", path: "C:\\hf_cache\\models--Systran--faster-whisper-base", bytes: 145_000_000 },
        { name: "Video arxiv", path: "D:\\YtAuto\\video", bytes: 3_400_000_000 + i * 180_000_000 },
        { name: "Ovoz arxiv", path: "D:\\YtAuto\\audio", bytes: 410_000_000 + i * 12_000_000 },
        { name: "VoiceStudio (dastur)", path: "C:\\Users\\u\\AppData\\Local\\Programs\\VoiceStudio", bytes: 1_034_000_000 },
        { name: "VoiceStudio (ma'lumot va modellar)", path: "C:\\Users\\u\\AppData\\Roaming\\VoiceStudio", bytes: 2_660_000_000 },
        { name: "OpenFilm (dastur)", path: "C:\\Users\\u\\AppData\\Local\\Programs\\OpenFilm", bytes: 1_087_000_000 },
        { name: "Claude Code", path: "C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@anthropic-ai\\claude-code", bytes: 603_000_000 },
      ],
      diskTotalBytes: 9_339_000_000 + i * 192_000_000,
      tools: [
        { name: "VoiceStudio", peakRamMb: 4600, cpuSeconds: 410 }, { name: "Python (ovoz/subtitr)", peakRamMb: 1250, cpuSeconds: 160 },
        { name: "Claude Code", peakRamMb: 820, cpuSeconds: 210 }, { name: "Render (ffmpeg/brauzer)", peakRamMb: 1900, cpuSeconds: 640 },
      ],
      system: { totalRamGb: 15.8, peakUsedRamGb: 9.4 + i * 0.3, diskRoot: "D:\\", freeDiskGbStart: 61.4, freeDiskGbEnd: 61.0 },
    },
  });
  return [
    mk(4, 20, null, 104, "remote", null),
    mk(3, 60 * 20, "ok", 105, "scheduled", "Video tayyor va YouTube'ga yuklandi."),
    mk(2, 60 * 44, "failed", 108, "scheduled", "Public vaqti o'tib ketgan — video arxivda."),
    mk(1, 60 * 68, "idle", null, "remote", "Navbat bo'sh."),
  ];
}

export function demoLogs(id: number): JobLogs | null {
  const r = db().rows.find((x) => x.id === id);
  if (!r) return null;
  const base = Date.now() - 3600_000;
  const ev = (i: number, level: string, stage: string | null, message: string) => ({ id: i, at: new Date(base + i * 90_000).toISOString(), level, stage, message });
  return {
    id, topic: r.topic, status: r.status,
    events: [
      ev(1, "Info", null, "Vazifa boshlandi (worker-PC1)."),
      ev(2, "Info", "Script", "Gemini ssenariy yozmoqda…"),
      ev(3, "Info", "Script", "Ssenariy tayyor: 214 so'z."),
      ev(4, "Info", "Voice", "VoiceStudio: 5 ta bo'lak sintez qilindi."),
      ev(5, "Warn", "Align", "faster-whisper: 2 ta so'z vaqti taxminiy."),
      ev(6, r.error ? "Error" : "Info", "Edit", r.error ?? "Claude montaji tugadi."),
    ],
    stages: [
      { stage: 1, state: 2, attempts: 1, durationMs: 21000, error: null },
      { stage: 2, state: 2, attempts: 1, durationMs: 148000, error: null },
      { stage: 3, state: 2, attempts: 1, durationMs: 31000, error: null },
      { stage: 4, state: r.error ? 3 : 2, attempts: 1, durationMs: 612000, error: r.error },
    ],
  };
}

export const demoPrompts = (): Prompts => db().prompts;

export function demoApply(a: Action): ActionResult {
  const d = db();
  const find = (id: number) => d.rows.find((r) => r.id === id);
  const when = (s?: string) => { const p = s ? parseLocal(s) : null; return p ? zonedToUtc(p.date, p.time, TZ) : null; };
  switch (a.type) {
    case "create_topics": {
      const items = a.topics.map((t) => ({ ...t, topic: t.topic?.trim() || t.title?.trim() || "" })).filter((t) => t.topic).slice(0, 5);
      if (items.some((t) => !!t.script?.trim() !== !!t.title?.trim() || (t.audioPath && !t.script?.trim())))
        return { ok: false, message: "Subtitr matni va title birga kiritilishi kerak (tayyor audio bilan majburiy). [demo]" };
      if (!items.length) return { ok: false, message: "Mavzu kiritilmagan." };
      let last = d.rows.reduce((m, r) => (r.at > m ? r.at : m), new Date());
      const ids = items.map((t) => {
        last = when(t.publishAt) ?? new Date(last.getTime() + 2 * 86400000);
        const id = d.nextId++;
        d.rows.push({ id, topic: t.topic.trim(), notes: t.notes?.trim() ?? "", title: null, status: Status.Queued, at: last, yt: null, made: false, publishNow: false, claimed: false, error: null, template: t.template ?? "", format: t.format ?? 0, voice: t.voice ?? "", script: t.script?.trim(), mtitle: t.title?.trim(), audio: !!t.audioPath });
        return id;
      });
      return { ok: true, message: `${ids.length} ta mavzu qo'shildi (#${ids.join(", #")}). [demo]` };
    }
    case "update_topic": {
      const r = find(a.id);
      if (!r || r.made || r.claimed) return { ok: false, message: `#${a.id}: video tayyorlangan yoki tayyorlanmoqda — o'zgartirib bo'lmaydi.` };
      if (a.topic) r.topic = a.topic.trim();
      if (a.notes !== undefined) r.notes = a.notes.trim();
      if (a.template !== undefined) r.template = a.template;
      if (a.format !== undefined) r.format = a.format;
      if (a.voice !== undefined) r.voice = a.voice;
      if (a.script !== undefined) r.script = a.script.trim();
      if (a.title !== undefined) r.mtitle = a.title.trim();
      r.at = when(a.publishAt) ?? r.at; r.status = Status.Queued; r.error = null;
      return { ok: true, message: `#${a.id} yangilandi. [demo]` };
    }
    case "delete_topic": {
      const r = find(a.id);
      if (!r || r.made || r.claimed) return { ok: false, message: `#${a.id}: o'chirib bo'lmaydi.` };
      d.rows = d.rows.filter((x) => x.id !== a.id);
      return { ok: true, message: `#${a.id} o'chirildi. [demo]` };
    }
    case "publish_now": {
      const r = find(a.id);
      if (!r || [11, 14].includes(r.status)) return { ok: false, message: `#${a.id}: hozir chiqarib bo'lmaydi.` };
      r.publishNow = true;
      return { ok: true, message: `#${a.id}: "Hozir chiqarish" yuborildi. [demo]` };
    }
    case "cancel_publish_now": {
      const r = find(a.id); if (r) r.publishNow = false;
      return { ok: true, message: `#${a.id}: bekor qilindi. [demo]` };
    }
    case "upload_ready": {
      const r = find(a.id);
      if (!r || !r.archived || r.yt) return { ok: false, message: `#${a.id}: video tayyor emas yoki allaqachon YouTube'da.` };
      r.uploadRequested = true;
      return { ok: true, message: `#${a.id}: "YouTube'ga yuklash" yuborildi. [demo]` };
    }
    case "cancel_upload_ready": {
      const r = find(a.id); if (r) r.uploadRequested = false;
      return { ok: true, message: `#${a.id}: bekor qilindi. [demo]` };
    }
    case "retry":
    case "retry_settings": {
      const r = find(a.id);
      if (!r || r.claimed || ![12, 13].includes(r.status)) return { ok: false, message: `#${a.id}: qayta urinib bo'lmaydi.` };
      r.status = Status.Queued; r.error = null;
      return { ok: true, message: `#${a.id}: navbatga qaytarildi. [demo]` };
    }
    case "cancel": {
      const r = find(a.id);
      if (!r || [11, 14].includes(r.status)) return { ok: false, message: `#${a.id}: bekor qilib bo'lmaydi.` };
      r.status = 14;
      return { ok: true, message: `#${a.id}: bekor qilindi. [demo]` };
    }
    case "reschedule": {
      const r = find(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      r.at = when(a.publishAt) ?? r.at; if (r.status === 13) { r.status = Status.Queued; r.error = null; }
      return { ok: true, message: `#${a.id}: yangi vaqt saqlandi. [demo]` };
    }
    case "force_new_upload": {
      const r = find(a.id);
      if (!r) return { ok: false, message: `#${a.id} topilmadi.` };
      r.status = Status.Queued; r.error = null;
      return { ok: true, message: `#${a.id}: yangi upload tayyorlandi. [demo]` };
    }
    case "update_prompt": {
      let text = d.prompts[a.key];
      if (a.reset) text = d.prompts[a.key]; else if (a.text) text = a.text;
      else for (const e of a.edits ?? []) { if (!text.includes(e.find)) return { ok: false, message: `"${e.find.slice(0, 50)}" topilmadi.` }; text = text.replace(e.find, () => e.replace); }
      d.prompts[a.key] = text;
      return { ok: true, message: "Prompt yangilandi. [demo]" };
    }
  }
}

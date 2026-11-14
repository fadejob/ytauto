import "server-only";
import { addDays, parseLocal, utcToZoned, zonedToUtc } from "./time";
import { Status, type Action, type ActionResult, type Catalog, type Prompts, type Topic } from "./types";

/**
 * DEMO_MODE=1: online bazasiz, xotiradagi namunaviy ma'lumotlar — faqat dizaynni brauzerda ko'rish uchun.
 * Server qayta ishga tushsa ma'lumotlar boshlang'ich holatga qaytadi.
 */
export const isDemo = () => process.env.DEMO_MODE === "1";

const TZ = "Asia/Tashkent";
export const demoCatalog: Catalog = {
  voiceProfiles: ["Narrator"], templates: [], defaults: { voiceProfile: "Narrator", template: "", format: 0 },
  youtube: { publishTime: "18:00", timeZone: TZ }, known: true,
};

type Row = { id: number; topic: string; notes: string; title: string | null; status: number; at: Date; yt: string | null; made: boolean; publishNow: boolean; claimed: boolean; error: string | null; archived?: boolean; uploadRequested?: boolean };

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
  }));
}

export const demoPrompts = (): Prompts => db().prompts;

export function demoApply(a: Action): ActionResult {
  const d = db();
  const find = (id: number) => d.rows.find((r) => r.id === id);
  const when = (s?: string) => { const p = s ? parseLocal(s) : null; return p ? zonedToUtc(p.date, p.time, TZ) : null; };
  switch (a.type) {
    case "create_topics": {
      const items = a.topics.filter((t) => t.topic?.trim()).slice(0, 5);
      if (!items.length) return { ok: false, message: "Mavzu kiritilmagan." };
      let last = d.rows.reduce((m, r) => (r.at > m ? r.at : m), new Date());
      const ids = items.map((t) => {
        last = when(t.publishAt) ?? new Date(last.getTime() + 2 * 86400000);
        const id = d.nextId++;
        d.rows.push({ id, topic: t.topic.trim(), notes: t.notes?.trim() ?? "", title: null, status: Status.Queued, at: last, yt: null, made: false, publishNow: false, claimed: false, error: null });
        return id;
      });
      return { ok: true, message: `${ids.length} ta mavzu qo'shildi (#${ids.join(", #")}). [demo]` };
    }
    case "update_topic": {
      const r = find(a.id);
      if (!r || r.made || r.claimed) return { ok: false, message: `#${a.id}: video tayyorlangan yoki tayyorlanmoqda — o'zgartirib bo'lmaydi.` };
      if (a.topic) r.topic = a.topic.trim();
      if (a.notes !== undefined) r.notes = a.notes.trim();
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
    case "update_prompt": {
      let text = d.prompts[a.key];
      if (a.text) text = a.text;
      else for (const e of a.edits ?? []) { if (!text.includes(e.find)) return { ok: false, message: `"${e.find.slice(0, 50)}" topilmadi.` }; text = text.replace(e.find, () => e.replace); }
      d.prompts[a.key] = text;
      return { ok: true, message: "Prompt yangilandi. [demo]" };
    }
  }
}

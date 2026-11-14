import "server-only";
import { getCatalog, getPrompts, listTopics, previewPrompt } from "./topics";
import { StatusText, type Action, type ChatMessage, type Proposal } from "./types";

const GROQ_URL = "https://api.groq.com/openai/v1/chat/completions";

const topicProps = {
  topic: { type: "string", description: "Video mavzusi — DOIM INGLIZ TILIDA (foydalanuvchi boshqa tilda yozsa ham tarjima qiling). Qisqa va aniq." },
  notes: { type: "string", description: "Mavzu tushuntirishi / ssenariy uchun ko'rsatmalar — DOIM INGLIZ TILIDA: nimalarni yoritish, ohang, auditoriya, muhim faktlar." },
  publish_at: { type: "string", description: "Ixtiyoriy chiqish vaqti 'YYYY-MM-DD HH:mm' (kanal zonasi). Foydalanuvchi aytmasa bermang — avtomatik tanlanadi." },
};

const TOOLS = [
  {
    type: "function",
    function: {
      name: "create_topics",
      description: "Yangi video mavzularini qo'shishni TAKLIF qiladi (bir vaqtda 1-5 ta, har biri turli mavzu).",
      parameters: {
        type: "object",
        properties: { topics: { type: "array", minItems: 1, maxItems: 5, items: { type: "object", properties: topicProps, required: ["topic", "notes"] } } },
        required: ["topics"],
      },
    },
  },
  {
    type: "function",
    function: {
      name: "update_topic",
      description: "Mavjud mavzuni o'zgartirishni taklif qiladi (faqat video hali tayyorlanmagan bo'lsa, ro'yxatda editable=true).",
      parameters: { type: "object", properties: { id: { type: "integer" }, ...topicProps }, required: ["id"] },
    },
  },
  {
    type: "function",
    function: {
      name: "delete_topic",
      description: "Mavzuni o'chirishni taklif qiladi (faqat video hali tayyorlanmagan bo'lsa, deletable=true).",
      parameters: { type: "object", properties: { id: { type: "integer" } }, required: ["id"] },
    },
  },
  {
    type: "function",
    function: {
      name: "publish_now",
      description: "Mavzu videosini HOZIR tayyorlab (yoki tayyorini olib) YouTube'ga darhol chiqarishni taklif qiladi.",
      parameters: { type: "object", properties: { id: { type: "integer" } }, required: ["id"] },
    },
  },
  {
    type: "function",
    function: {
      name: "upload_ready",
      description: "TAYYOR (arxivda turgan), lekin YouTube'ga hali yuklanmagan videoni qayta tayyorlamasdan YouTube'ga yuklashni taklif qiladi (ro'yxatda tayyor-yuklanmagan belgisi bo'lsa). Vaqti o'tib ketgan bo'lsa hozir + 35 daqiqaga rejalashtiriladi.",
      parameters: { type: "object", properties: { id: { type: "integer" } }, required: ["id"] },
    },
  },
  {
    type: "function",
    function: {
      name: "update_prompt",
      description:
        "Promptni o'zgartirishni taklif qiladi. key='script' — Gemini ssenariy prompti, key='montage' — Claude montaj prompti. " +
        "Kichik o'zgarish uchun 'edits' ishlating: har 'find' promptdagi AYNAN mavjud matn (bir marta uchraydi), 'replace' — yangisi. " +
        "Butunlay qayta yozish so'ralsa 'text' ga to'liq yangi prompt. Ssenariy promptidagi {topic} {notes} {language} {language_name} {length_rule} {format} {fact_sheet} placeholder'larini saqlang.",
      parameters: {
        type: "object",
        properties: {
          key: { type: "string", enum: ["script", "montage"] },
          edits: { type: "array", items: { type: "object", properties: { find: { type: "string" }, replace: { type: "string" } }, required: ["find", "replace"] } },
          text: { type: "string" },
        },
        required: ["key"],
      },
    },
  },
];

async function systemPrompt(): Promise<string> {
  const cat = await getCatalog();
  const [topics, prompts] = await Promise.all([listTopics(cat.youtube.timeZone), getPrompts()]);
  const now = new Intl.DateTimeFormat("sv-SE", { timeZone: cat.youtube.timeZone, dateStyle: "short", timeStyle: "short" }).format(new Date());
  const list = topics
    .map((t) => `#${t.id} | ${t.topic} | ${StatusText[t.status]} | chiqish ${t.publishLocal.date} ${t.publishLocal.time}${t.editable ? " | editable" : ""}${t.deletable ? " | deletable" : ""}${t.publishNow ? " | hozir-chiqarish-kutilmoqda" : ""}${t.canUploadReady ? " | tayyor-yuklanmagan" : ""}${t.uploadRequested ? " | yuklash-kutilmoqda" : ""}`)
    .join("\n");
  return `Siz YtAuto YouTube kanali yordamchisisiz. Foydalanuvchi bilan uning tilida (odatda o'zbekcha) qisqa va aniq gaplashing.
Vazifangiz: mavzular qo'shish/o'zgartirish/o'chirish, videoni hozir chiqarish va promptlarni o'zgartirishni TAKLIF qilish — buning uchun tool'larni chaqiring.
Muhim qoidalar:
- Siz hech narsani o'zingiz bajarmaysiz: har bir tool chaqiruvi foydalanuvchiga "Ruxsat berish" tugmasi bilan ko'rsatiladi. Javobda "bajarildi" demang — "ruxsat bersangiz qo'shiladi" deng.
- Video, subtitr va YouTube title DOIM ingliz tilida. Shuning uchun topic va notes ni har doim INGLIZ tilida yozing, foydalanuvchi qaysi tilda so'rasa ham.
- Bir so'rovda eng ko'pi 5 ta mavzu. Har mavzu uchun notes'da yaxshi tushuntirish yozing (nimani yoritish, struktura, auditoriya).
- Faqat editable=true mavzuni o'zgartirish, deletable=true mavzuni o'chirish mumkin. Boshqasini so'rasa, nega mumkin emasligini tushuntiring.
- Mavzu raqami aniq bo'lmasa, ro'yxatdan eng mosini tanlang yoki so'rang.
- Prompt o'zgarishida promptdagi matndan aynan nusxa olib 'find' ga qo'ying.
- Bitta xabarda bir nechta ish so'ralsa (masalan mavzu qo'shish VA promptni o'zgartirish), HAR BIRI uchun tool'ni shu javobning o'zida chaqiring — birortasini ham tashlab ketmang.

Hozirgi vaqt (${cat.youtube.timeZone}): ${now}. Standart chiqish soati: ${cat.youtube.publishTime}.

Mavzular (id | mavzu | holat | chiqish vaqti | belgilar):
${list || "(hozircha yo'q)"}

=== Ssenariy prompti (key=script) ===
${prompts.script || "(bo'sh)"}

=== Montaj prompti (key=montage) ===
${prompts.montage || "(bo'sh)"}`;
}

type ToolTopic = { topic?: string; notes?: string; publish_at?: string };
type ToolArgs = ToolTopic & { topics?: ToolTopic[]; id?: number; key?: string; text?: string; edits?: { find: string; replace: string }[] };

function toAction(name: string, args: ToolArgs): Action | null {
  switch (name) {
    case "create_topics":
      return { type: "create_topics", topics: (args.topics ?? []).slice(0, 5).map((t) => ({ topic: String(t.topic ?? ""), notes: String(t.notes ?? ""), publishAt: t.publish_at || undefined })) };
    case "update_topic":
      return { type: "update_topic", id: Number(args.id), topic: args.topic ?? undefined, notes: args.notes ?? undefined, publishAt: args.publish_at || undefined };
    case "delete_topic":
      return { type: "delete_topic", id: Number(args.id) };
    case "publish_now":
      return { type: "publish_now", id: Number(args.id) };
    case "upload_ready":
      return { type: "upload_ready", id: Number(args.id) };
    case "update_prompt":
      return { type: "update_prompt", key: args.key === "montage" ? "montage" : "script", text: args.text || undefined, edits: Array.isArray(args.edits) ? args.edits : undefined };
    default:
      return null;
  }
}

const clip = (s: string, n: number) => (s.length > n ? s.slice(0, n) + "…" : s);

async function describe(a: Action): Promise<Pick<Proposal, "summary" | "preview" | "error">> {
  switch (a.type) {
    case "create_topics":
      return {
        summary: `${a.topics.length} ta yangi mavzu qo'shish`,
        preview: a.topics.map((t, i) => `${i + 1}. ${t.topic}${t.publishAt ? `  (${t.publishAt})` : ""}\n   ${clip(t.notes ?? "", 400)}`).join("\n\n"),
      };
    case "update_topic":
      return {
        summary: `#${a.id} mavzusini o'zgartirish`,
        preview: [a.topic && `Mavzu: ${a.topic}`, a.notes && `Tushuntirish: ${clip(a.notes, 400)}`, a.publishAt && `Chiqish: ${a.publishAt}`].filter(Boolean).join("\n"),
      };
    case "delete_topic":
      return { summary: `#${a.id} mavzusini o'chirish` };
    case "publish_now":
      return { summary: `#${a.id} videosini HOZIR chiqarish` };
    case "upload_ready":
      return { summary: `#${a.id} tayyor videosini YouTube'ga yuklash (qayta tayyorlanmaydi)` };
    case "update_prompt": {
      const res = await previewPrompt(a);
      const label = a.key === "script" ? "Ssenariy (Gemini)" : "Montaj (Claude)";
      if ("error" in res) return { summary: `${label} promptini o'zgartirish`, error: res.error };
      const preview = a.edits?.length
        ? a.edits.map((e) => `− ${clip(e.find, 300)}\n+ ${clip(e.replace, 300)}`).join("\n\n")
        : `Yangi prompt (${res.text.length} belgi):\n${clip(res.text, 1200)}`;
      return { summary: `${label} promptini o'zgartirish`, preview };
    }
    default:
      return { summary: "Amal" };
  }
}

export async function chat(history: ChatMessage[]): Promise<{ reply: string; proposals: Proposal[] }> {
  const key = process.env.GROQ_API_KEY;
  if (!key) throw new Error("GROQ_API_KEY sozlanmagan.");
  const messages: Record<string, unknown>[] = [
    { role: "system", content: await systemPrompt() },
    ...history.slice(-12).map((m) => ({ role: m.role, content: clip(m.content, 6000) })),
  ];
  const model = process.env.GROQ_MODEL || "openai/gpt-oss-120b";
  const proposals: Proposal[] = [];
  let reply = "";
  // Agent sikli: model har aylanishda bitta-ikkita tool chaqirishi mumkin. Har taklif "ko'rsatildi" deb qaytariladi va model
  // so'rovdagi qolgan amallarni davom ettiradi. Hech narsa bajarilmaydi — faqat takliflar yig'iladi.
  for (let round = 0; round < 4; round++) {
    const res = await fetch(GROQ_URL, {
      method: "POST",
      headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
      body: JSON.stringify({
        model, messages, tools: TOOLS, tool_choice: "auto", temperature: 0.3, max_tokens: 8192,
        ...(model.startsWith("openai/gpt-oss") ? { reasoning_effort: "low" } : {}), // tezroq javob
      }),
      signal: AbortSignal.timeout(60_000),
    });
    if (!res.ok) throw new Error(`Groq xatosi (HTTP ${res.status}): ${clip(await res.text(), 300)}`);
    const data = await res.json();
    const msg = data.choices?.[0]?.message ?? {};
    if (typeof msg.content === "string" && msg.content.trim()) reply = msg.content.replace(/\*\*|__|`/g, "").trim(); // chat oddiy matn
    const calls: { id: string; function?: { name?: string; arguments?: string } }[] = msg.tool_calls ?? [];
    if (!calls.length) break;
    messages.push({ role: "assistant", content: msg.content ?? "", tool_calls: calls });
    for (const call of calls) {
      let note = "Taklif foydalanuvchiga ruxsat uchun ko'rsatildi (hali bajarilmagan).";
      try {
        const action = toAction(call.function?.name ?? "", JSON.parse(call.function?.arguments || "{}") as ToolArgs);
        if (action) {
          const d = await describe(action);
          proposals.push({ id: call.id || crypto.randomUUID(), action, ...d });
          if (d.error) note = `Bu taklifda xato: ${d.error}`;
        } else note = "Noma'lum tool.";
      } catch { note = "Argumentlar noto'g'ri JSON."; }
      messages.push({ role: "tool", tool_call_id: call.id, content: note + " So'rovda boshqa amallar qolgan bo'lsa ularni ham chaqiring, aks holda qisqa yakuniy javob yozing." });
    }
  }
  if (!reply) reply = proposals.length ? "Quyidagilarni taklif qilaman — ruxsat bersangiz bazaga yoziladi:" : "Tushunmadim, aniqroq yozing.";
  return { reply, proposals };
}

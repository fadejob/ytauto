import "server-only";
import { isGuest } from "./db";
import { getCatalog, getPrompts, listTopics, previewPrompt } from "./topics";
import { geminiGenerate, type GeminiContent } from "./gemini";
import { readBlocks } from "./channel/store";
import { musicCatalog } from "./music";
import { StatusText, type Action, type ChatMessage, type Proposal } from "./types";

const topicProps = {
  topic: { type: "string", description: "Video mavzusi — DOIM INGLIZ TILIDA (foydalanuvchi boshqa tilda yozsa ham tarjima qiling). Qisqa va aniq." },
  notes: { type: "string", description: "Mavzu tushuntirishi / ssenariy uchun ko'rsatmalar — DOIM INGLIZ TILIDA: nimalarni yoritish, ohang, auditoriya, muhim faktlar." },
  publish_at: { type: "string", description: "Ixtiyoriy chiqish vaqti 'YYYY-MM-DD HH:mm' (kanal zonasi). Foydalanuvchi aytmasa bermang — avtomatik tanlanadi." },
  music: {
    type: "string",
    description: "Fon musiqasi: 'auto' — kutubxonadan navbat bilan, 'none' — musiqasiz, yoki «Fon musiqasi kutubxonasi» ro'yxatidagi trek id'si. " +
      "Faqat foydalanuvchi musiqa haqida aytsa bering (kayfiyat aytsa — ro'yxatdan nomi mosini tanlang); aytmasa bu maydonni umuman bermang.",
  },
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
      description: "TAYYOR (arxivda turgan), lekin YouTube'ga hali yuklanmagan videoni qayta tayyorlamasdan YouTube'ga yuklashni taklif qiladi (ro'yxatda tayyor-yuklanmagan belgisi bo'lsa). Vaqti o'tib ketgan bo'lsa hozir + 1 soatga rejalashtiriladi.",
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
        "Butunlay qayta yozish so'ralsa 'text' ga to'liq yangi prompt. Ssenariy promptidagi {topic} {notes} {language} {language_name} {length_rule} {format} {fact_sheet} {channel_context} placeholder'larini saqlang.",
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

type MusicLib = Awaited<ReturnType<typeof musicCatalog>>;

/** Mavzuning musiqa tanlovi matn ko'rinishida (ro'yxat va taklif uchun). */
function musicLabel(v: string | undefined, lib: MusicLib): string {
  if (v === undefined) return "";
  if (!v) return lib.enabled && lib.tracks.length ? "avtomatik (kutubxonadan navbat bilan)" : "avtomatik (hozir musiqasiz)";
  if (v === "none") return "musiqasiz";
  const t = lib.tracks.find((x) => x.id === v);
  return t ? `«${t.title}»${t.creator ? ` — ${t.creator}` : ""}` : "kutubxonada yo'q trek";
}

/**
 * Model bergan qiymatni kutubxonaga moslaydi: 'auto' → "" (avtomatik), 'none', trek id; id o'rniga trek nomini yozsa ham topiladi.
 * Topilmasa qiymat o'zgarmaydi - taklifda xato ko'rsatiladi, bajarishda server ham rad etadi.
 */
function resolveMusic(raw: unknown, lib: MusicLib): string | undefined {
  if (raw === undefined || raw === null) return undefined;
  const v = String(raw).trim();
  if (!v) return undefined;
  const low = v.toLowerCase();
  if (["auto", "avtomatik", "default"].includes(low)) return "";
  if (["none", "no", "off", "musiqasiz"].includes(low)) return "none";
  const byId = lib.tracks.find((t) => t.id === low);
  if (byId) return byId.id;
  const byTitle = lib.tracks.find((t) => t.title.toLowerCase() === low)
    ?? lib.tracks.find((t) => low.includes(t.title.toLowerCase()) || t.title.toLowerCase().includes(low));
  return byTitle ? byTitle.id : low;
}

async function systemPrompt(lib: MusicLib): Promise<string> {
  const cat = await getCatalog();
  const [topics, prompts, blocks] = await Promise.all([listTopics(cat.youtube.timeZone), getPrompts(), readBlocks().catch(() => null)]);
  const channel = blocks?.enabled && blocks.script ? `\n\n=== Kanal konteksti (kanal tahlilidan; yangi mavzu va notes shu uslubga mos bo'lsin, chiqqan mavzularni takrorlamang) ===\n${blocks.script}` : "";
  const now = new Intl.DateTimeFormat("sv-SE", { timeZone: cat.youtube.timeZone, dateStyle: "short", timeStyle: "short" }).format(new Date());
  const list = topics
    .map((t) => `#${t.id} | ${t.topic} | ${StatusText[t.status]} | chiqish ${t.publishLocal.date} ${t.publishLocal.time}${t.editable ? " | editable" : ""}${t.deletable ? " | deletable" : ""}${t.publishNow ? " | hozir-chiqarish-kutilmoqda" : ""}${t.canUploadReady ? " | tayyor-yuklanmagan" : ""}${t.uploadRequested ? " | yuklash-kutilmoqda" : ""}${t.music ? ` | musiqa: ${musicLabel(t.music, lib)}` : ""}`)
    .join("\n");
  const music = lib.tracks.length
    ? lib.tracks.map((t) => `${t.id} | ${t.title}${t.creator ? ` — ${t.creator}` : ""}`).join("\n")
    : "(bo'sh — foydalanuvchi Sozlamalar → Fon musiqasi'dan trek qo'shishi kerak)";
  return `Siz YtAuto YouTube kanali yordamchisisiz. Foydalanuvchi bilan uning tilida (odatda o'zbekcha) qisqa va aniq gaplashing.
Vazifangiz: mavzular qo'shish/o'zgartirish/o'chirish${isGuest() ? "" : ", videoni hozir chiqarish"} va promptlarni o'zgartirishni TAKLIF qilish — buning uchun tool'larni chaqiring.${isGuest() ? "\nBu qo'shimcha token hisobi: «Hozir chiqarish» yo'q — videolar token bergan hisob kompyuterida ish vaqtida tayyorlanib, chiqish vaqtida YouTube'ga chiqadi. So'ralsa shuni tushuntiring." : ""}
Muhim qoidalar:
- Siz hech narsani o'zingiz bajarmaysiz: har bir tool chaqiruvi foydalanuvchiga "Ruxsat berish" tugmasi bilan ko'rsatiladi. Javobda "bajarildi" demang — "ruxsat bersangiz qo'shiladi" deng.
- Video, subtitr va YouTube title DOIM ingliz tilida. Shuning uchun topic va notes ni har doim INGLIZ tilida yozing, foydalanuvchi qaysi tilda so'rasa ham.
- Bir so'rovda eng ko'pi 5 ta mavzu. Har mavzu uchun notes'da yaxshi tushuntirish yozing (nimani yoritish, struktura, auditoriya).
- Faqat editable=true mavzuni o'zgartirish, deletable=true mavzuni o'chirish mumkin. Boshqasini so'rasa, nega mumkin emasligini tushuntiring.
- Mavzu raqami aniq bo'lmasa, ro'yxatdan eng mosini tanlang yoki so'rang.
- Prompt o'zgarishida promptdagi matndan aynan nusxa olib 'find' ga qo'ying.
- Fon musiqasi: mavzuga faqat quyidagi kutubxonadagi trekni (id bilan), 'none' (musiqasiz) yoki 'auto' (navbat bilan) qo'ya olasiz. Yangi musiqa qidirib qo'sha olmaysiz — kutubxonada mosi bo'lmasa, Sozlamalar → Fon musiqasi'dan qo'shishni ayting. Musiqa so'ralmasa 'music' maydonini bermang.
- Bitta xabarda bir nechta ish so'ralsa (masalan mavzu qo'shish VA promptni o'zgartirish), HAR BIRI uchun tool'ni shu javobning o'zida chaqiring — birortasini ham tashlab ketmang.

Hozirgi vaqt (${cat.youtube.timeZone}): ${now}. Standart chiqish soati: ${cat.youtube.publishTime}.

Mavzular (id | mavzu | holat | chiqish vaqti | belgilar; musiqa yozilmagan bo'lsa — avtomatik):
${list || "(hozircha yo'q)"}

Fon musiqasi kutubxonasi (trek id | nomi — muallif); umumiy avtomatik musiqa ${lib.enabled ? "yoqilgan" : "o'chiq"}:
${music}

=== Ssenariy prompti (key=script) ===
${prompts.script || "(bo'sh)"}

=== Montaj prompti (key=montage) ===
${prompts.montage || "(bo'sh)"}${channel}`;
}

type ToolTopic = { topic?: string; notes?: string; publish_at?: string; music?: string };
type ToolArgs = ToolTopic & { topics?: ToolTopic[]; id?: number; key?: string; text?: string; edits?: { find: string; replace: string }[] };

function toAction(name: string, args: ToolArgs, lib: MusicLib): Action | null {
  switch (name) {
    case "create_topics":
      return {
        type: "create_topics",
        topics: (args.topics ?? []).slice(0, 5).map((t) => ({
          topic: String(t.topic ?? ""), notes: String(t.notes ?? ""), publishAt: t.publish_at || undefined, music: resolveMusic(t.music, lib),
        })),
      };
    case "update_topic":
      return {
        type: "update_topic", id: Number(args.id), topic: args.topic ?? undefined, notes: args.notes ?? undefined, publishAt: args.publish_at || undefined,
        music: resolveMusic(args.music, lib),
      };
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

/** Kutubxonada yo'q trek tanlangan bo'lsa - taklifdagi xato matni. */
const badMusic = (vals: (string | undefined)[], lib: MusicLib) =>
  vals.some((v) => v && v !== "none" && !lib.tracks.some((t) => t.id === v))
    ? "Tanlangan fon musiqasi kutubxonada yo'q — Sozlamalar → Fon musiqasi'dan qo'shing yoki boshqasini tanlang."
    : undefined;

async function describe(a: Action, lib: MusicLib): Promise<Pick<Proposal, "summary" | "preview" | "error">> {
  switch (a.type) {
    case "create_topics":
      return {
        summary: `${a.topics.length} ta yangi mavzu qo'shish`,
        preview: a.topics.map((t, i) => `${i + 1}. ${t.topic}${t.publishAt ? `  (${t.publishAt})` : ""}${t.music !== undefined ? `\n   🎵 ${musicLabel(t.music, lib)}` : ""}\n   ${clip(t.notes ?? "", 400)}`).join("\n\n"),
        error: badMusic(a.topics.map((t) => t.music), lib),
      };
    case "update_topic":
      return {
        summary: `#${a.id} mavzusini o'zgartirish`,
        preview: [a.topic && `Mavzu: ${a.topic}`, a.notes && `Tushuntirish: ${clip(a.notes, 400)}`, a.publishAt && `Chiqish: ${a.publishAt}`,
          a.music !== undefined && `Fon musiqasi: ${musicLabel(a.music, lib)}`].filter(Boolean).join("\n"),
        error: badMusic([a.music], lib),
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

/** Gemini function calling: OpenAI uslubidagi tool ta'riflaridan functionDeclarations. */
const DECLARATIONS = TOOLS.map((t) => t.function);
/** Qo'shimcha (mehmon) tokenda «Hozir chiqarish» taklif qilinmaydi. */
const GUEST_DECLARATIONS = DECLARATIONS.filter((d) => d.name !== "publish_now");

export async function chat(history: ChatMessage[]): Promise<{ reply: string; proposals: Proposal[] }> {
  const contents: GeminiContent[] = history.slice(-12).map((m) => ({ role: m.role === "assistant" ? "model" : "user", parts: [{ text: clip(m.content, 6000) }] }));
  const lib = await musicCatalog();
  const base = {
    systemInstruction: { parts: [{ text: await systemPrompt(lib) }] },
    tools: [{ functionDeclarations: isGuest() ? GUEST_DECLARATIONS : DECLARATIONS }],
    toolConfig: { functionCallingConfig: { mode: "AUTO" } },
    generationConfig: { temperature: 0.3, maxOutputTokens: 8192 },
  };
  const proposals: Proposal[] = [];
  let reply = "";
  // Agent sikli: model har aylanishda bitta-ikkita tool chaqirishi mumkin. Har taklif "ko'rsatildi" deb qaytariladi va model
  // so'rovdagi qolgan amallarni davom ettiradi. Hech narsa bajarilmaydi — faqat takliflar yig'iladi.
  // Kalit limiti (429) tugasa geminiGenerate keyingi kalit bilan davom etadi.
  for (let round = 0; round < 4; round++) {
    const { data } = await geminiGenerate({ ...base, contents });
    if (data.promptFeedback?.blockReason) throw new Error(`Gemini so'rovni blokladi: ${data.promptFeedback.blockReason}`);
    const content = data.candidates?.[0]?.content;
    const parts = content?.parts ?? [];
    const text = parts.filter((p) => typeof p.text === "string" && !p.thought).map((p) => p.text).join("").trim();
    if (text) reply = text.replace(/\*\*|__|`/g, "").trim(); // chat oddiy matn
    const calls = parts.filter((p) => p.functionCall?.name).map((p) => p.functionCall!);
    if (!calls.length) break;
    // Model javobi o'zgarishsiz qaytariladi (thought signature'lar saqlanishi kerak).
    contents.push({ role: "model", parts });
    const responses: GeminiContent = { role: "user", parts: [] };
    for (const call of calls) {
      let note = "Taklif foydalanuvchiga ruxsat uchun ko'rsatildi (hali bajarilmagan).";
      try {
        const action = toAction(call.name ?? "", (call.args ?? {}) as ToolArgs, lib);
        if (action) {
          const d = await describe(action, lib);
          proposals.push({ id: call.id || crypto.randomUUID(), action, ...d });
          if (d.error) note = `Bu taklifda xato: ${d.error}`;
        } else note = "Noma'lum tool.";
      } catch { note = "Argumentlar noto'g'ri."; }
      responses.parts.push({
        functionResponse: {
          ...(call.id ? { id: call.id } : {}), name: call.name,
          response: { result: note + " So'rovda boshqa amallar qolgan bo'lsa ularni ham chaqiring, aks holda qisqa yakuniy javob yozing." },
        },
      });
    }
    contents.push(responses);
  }
  if (!reply) reply = proposals.length ? "Quyidagilarni taklif qilaman — ruxsat bersangiz bazaga yoziladi:" : "Tushunmadim, aniqroq yozing.";
  return { reply, proposals };
}

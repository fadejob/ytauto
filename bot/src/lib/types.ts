/** Server va mijoz uchun umumiy turlar (YtAuto C# JobStatus enum bilan bir xil raqamlar). */

export const Status = {
  Queued: 0, Scripting: 1, GeneratingVoice: 2, Aligning: 3, Editing: 4, Rendering: 5, Validating: 6, Archived: 7,
  Uploading: 8, Processing: 9, Scheduled: 10, Published: 11, Failed: 12, NeedsAttention: 13, Cancelled: 14,
} as const;

export const StatusText: Record<number, string> = {
  0: "Navbatda", 1: "Ssenariy yozilmoqda", 2: "Ovoz yaratilmoqda", 3: "Subtitr vaqtlari", 4: "Montaj", 5: "Render",
  6: "Sifat tekshiruvi", 7: "Arxivlandi", 8: "YouTube'ga yuklanmoqda", 9: "YouTube qayta ishlamoqda", 10: "Rejalashtirildi",
  11: "Chiqarildi", 12: "Xato (qayta urinadi)", 13: "E'tibor kerak", 14: "Bekor qilindi",
};

export type Topic = {
  id: number;
  topic: string;
  notes: string;
  title: string | null;
  status: number;
  publishAt: string; // ISO UTC
  publishLocal: { date: string; time: string };
  youtubeUrl: string | null;
  publishNow: boolean;
  claimed: boolean;
  error: string | null;
  /** Video hali tayyorlanmagan: tahrirlash/o'chirish mumkin. */
  editable: boolean;
  deletable: boolean;
  canPublishNow: boolean;
  /** Video tayyor (arxivda), lekin YouTube'ga yuklanmagan: "YouTube'ga yuklash" so'rovini yuborish mumkin. */
  canUploadReady: boolean;
  /** "YouTube'ga yuklash" so'rovi yuborilgan, kompyuter hali bajarmagan. */
  uploadRequested: boolean;
};

export type Catalog = {
  voiceProfiles: string[];
  templates: { name: string; kind: string }[];
  defaults: { voiceProfile: string; template: string; format: number };
  youtube: { publishTime: string; timeZone: string };
  known: boolean; // false — desktop ilova katalogni hali yubormagan (standart qiymatlar)
};

export type State = { topics: Topic[]; catalog: Catalog; viewer: { name: string; dev: boolean }; now: string; demo: boolean };

export type PromptKey = "script" | "montage";
export type Prompts = Record<PromptKey, string>;

export type NewTopic = { topic: string; notes?: string; publishAt?: string /* "YYYY-MM-DD HH:mm" kanal zonasi bo'yicha */ };

/** UI va AI bir xil amallardan foydalanadi; AI taklifi foydalanuvchi ruxsat bermaguncha bajarilmaydi. */
export type Action =
  | { type: "create_topics"; topics: NewTopic[] }
  | { type: "update_topic"; id: number; topic?: string; notes?: string; publishAt?: string }
  | { type: "delete_topic"; id: number }
  | { type: "publish_now"; id: number }
  | { type: "cancel_publish_now"; id: number }
  | { type: "upload_ready"; id: number }
  | { type: "cancel_upload_ready"; id: number }
  | { type: "update_prompt"; key: PromptKey; text?: string; edits?: { find: string; replace: string }[] };

export type ActionResult = { ok: boolean; message: string };

export type ChatMessage = { role: "user" | "assistant"; content: string };
export type Proposal = { id: string; action: Action; summary: string; preview?: string; error?: string };

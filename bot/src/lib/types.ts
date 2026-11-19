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
  canRetry: boolean;
  canCancel: boolean;
  canReschedule: boolean;
  canForceUpload: boolean;
  template: string;
  voice: string;
  /** 0 = 16:9, 1 = Shorts 9:16, 2 = Kvadrat 1:1 */
  format: number;
  /** Qo'lda kiritilgan subtitr matni va title (Gemini ishlatilmaydi); null - Gemini yozadi. */
  manualScript: string | null;
  manualTitle: string | null;
  /** Tayyor ovoz yuklangan (VoiceStudio ishlatilmaydi). */
  hasAudio: boolean;
};

/** Mavzu matni qayerdan keladi: Gemini yozadi / subtitr+title qo'lda / qo'lda + tayyor audio. */
export type ScriptSource = "gemini" | "manual" | "audio";

export const FormatText: Record<number, string> = { 0: "Gorizontal 16:9", 1: "Shorts 9:16", 2: "Kvadrat 1:1" };

/** Worker ishga tushishi (ytauto.worker_runs). */
export type WorkerRun = {
  runId: string; startedAt: string; finishedAt: string | null; outcome: string | null; jobId: number | null; message: string | null; trigger: string | null; machine: string | null;
  stats: RunStats | null;
};
export type JobEventRow = { id: number; at: string; level: string; stage: string | null; message: string };
export type JobStageRow = { stage: number; state: number; attempts: number; durationMs: number | null; error: string | null };
export type JobLogs = { id: number; topic: string; status: number; events: JobEventRow[]; stages: JobStageRow[] };

/** Bot boshqaradigan umumiy sozlamalar (online baza, shared_settings.config). Sirlar qaytarilmaydi: faqat "o'rnatilgan"mi. */
export type VoiceProfileCfg = { name: string; model: string; voice: string; language: string; speed: number };
export type BotConfig = {
  /** apiKeys - saqlangan kalitlar tartib bilan (faqat id va qisqa ko'rinish; kalitning o'zi qaytarilmaydi). */
  gemini: { model: string; temperature: number; useSearchGrounding: boolean; timeoutSec: number; apiKeys: GeminiKeyInfo[] };
  claude: { model: string; timeoutMinutes: number; maxBudgetUsd: number };
  /** Ovoz profillari (asosiy manba shu) va VoiceStudio sozlamalari. profiles bo'sh bo'lsa kompyuterdagi lokal ro'yxat ishlatiladi. */
  voice: { profiles: VoiceProfileCfg[]; settings: { baseUrl: string; startupTimeoutSec: number; requestTimeoutSec: number; chunkChars: number; pauseBetweenChunksSec: number; whisperModel: string; alignTimeoutSec: number } };
  limits: { maxRunAttempts: number; maxQaFixCycles: number; minFreeDiskGb: number; minFreeRamGbForTts: number };
  defaults: { language: string; format: number; template: string; voiceProfile: string };
  youtube: {
    clientId: string; clientSecretSet: boolean; publishTime: string; timeZoneId: string; categoryId: string; regionCode: string;
    madeForKids: boolean; containsSyntheticMedia: boolean; license: string; embeddable: boolean; publicStatsViewable: boolean;
    notifySubscribers: boolean; setRecordingDate: boolean; locationDescription: string; defaultTags: string; defaultDescription: string; minLeadMinutes: number;
  };
  openFilm: { renderWorkers: number; fps: number; loudnessLufs: number; burnSubtitles: boolean; lookFullAfterRender: boolean; renderTimeoutMinutes: number };
  /**
   * Video sifati: bepul manbalardan real rasm/logo/ikonlar, shablon uslub qo'llanmasi va shablonga moslik tekshiruvi.
   * pexelsKeySet/pixabayKeySet - sifatli stok foto kalitlari saqlanganmi (kalitning o'zi qaytarilmaydi).
   */
  visuals: { realMedia: boolean; maxMedia: number; templateStyleGuide: boolean; templateMotion: boolean; styleReview: boolean; minStyleScore: number; pexelsKeySet: boolean; pixabayKeySet: boolean };
};
export type GeminiKeyInfo = { id: string; hint: string };
/**
 * Saqlashda yuboriladi. gemini.apiKeys - qoldiriladigan kalitlar (id bo'yicha, shu tartibda), newApiKeys - qo'shiladigan yangi kalitlar.
 * clientSecret bo'sh bo'lsa mavjud sir o'zgarmaydi. visuals.pexelsApiKey/pixabayApiKey - yangi kalit; bo'sh bo'lsa
 * pexelsKeySet/pixabayKeySet true bo'lganda mavjud kalit qoladi, false bo'lsa o'chiriladi.
 */
export type BotConfigInput = {
  gemini: BotConfig["gemini"] & { newApiKeys?: string[] };
  limits: BotConfig["limits"];
  defaults: BotConfig["defaults"];
  youtube: BotConfig["youtube"] & { clientSecret?: string };
  openFilm: BotConfig["openFilm"];
  claude: BotConfig["claude"];
  voice: BotConfig["voice"];
  visuals: BotConfig["visuals"] & { pexelsApiKey?: string; pixabayApiKey?: string };
};

export type Catalog = {
  voiceProfiles: string[];
  templates: { name: string; kind: string }[];
  /** Botdan yuklangan, lekin hali birorta kompyuterga tushib o'rnatilmagan shablonlar: tanlab bo'lmaydi, "serverga yuklanmoqda" deb ko'rinadi. */
  pendingTemplates: PendingTemplate[];
  defaults: { voiceProfile: string; template: string; format: number };
  youtube: { publishTime: string; timeZone: string };
  known: boolean; // false — desktop ilova katalogni hali yubormagan (standart qiymatlar)
};

export type State = {
  topics: Topic[]; catalog: Catalog; viewer: { name: string; dev: boolean }; now: string; demo: boolean;
  /** Gemini kaliti bormi: yo'q bo'lsa mavzu qo'shish va AI yordamchi o'chiq, mavjud mavzular muzlatilgan. */
  gemini: { ready: boolean };
  /** guest - qo'shimcha token hisobi: «Hozir chiqarish» va kompyuter boshqaruvi yo'q; hostLabel - token bergan hisob kompyuteri. */
  account: { guest: boolean; hostLabel: string | null };
};

// ------------------------------------------------------------------ kanal konteksti
/** Bitta video tahlili. source: "video" - Gemini videoni ko'rib chiqdi; "metadata" - video ochiq emas, faqat sarlavha/tavsif/matn. */
export type VideoInsight = {
  videoId: string; title: string; publishedAt: string; durationSec: number; views: number; likes: number; comments: number; isShort: boolean;
  source: "video" | "metadata";
  summary: string;
  script: { hook: string; structure: string; tone: string; pacing: string; cta: string; transcriptExcerpt: string };
  voice: { gender: string; style: string; pace: string; energy: string };
  montage: { visualStyle: string; editingPace: string; onScreenText: string; transitions: string; music: string };
  subtitles: { present: boolean; style: string };
};

export type ChannelContext = {
  channel: { id: string; title: string; description: string; subscribers: number; videoCount: number; url: string };
  summary: string; niche: string; audience: string; tone: string;
  titlePatterns: string[]; recurringTopics: string[];
  scriptGuidelines: string[]; montageGuidelines: string[]; subtitleGuidelines: string[];
  voice: { gender: "female" | "male" | "unknown"; style: string; pace: string; recommendedProfile: string };
  doList: string[]; avoidList: string[];
  videos: VideoInsight[];
  recentTitles: string[];
};

export type Suggestion = { id: number; forDate: string; topic: string; notes: string; reason: string; status: "new" | "accepted" | "dismissed" };

/** Botdagi «Kanal» sahifasi. status "none" - hali tahlil qilinmagan. */
export type ChannelState = {
  connected: boolean; channelTitle: string;
  status: "none" | "idle" | "running" | "failed"; error: string | null; enabled: boolean;
  analyzedAt: string | null; startedAt: string | null;
  context: ChannelContext | null;
  /** Promptlarga qo'shiladigan ixcham matnlar (worker shuni ishlatadi). */
  blocks: { script: string; montage: string; voiceProfile: string } | null;
  suggestions: Suggestion[];
  suggestionsPending: boolean;
};

/**
 * «Mening ovozim» (shared_settings['voice.clone']): hisobning ovoz namunasi. status: pending - kompyuter hali VoiceStudio'ga
 * qo'shmagan; ready - qo'shildi (shu ovozda o'qiladi); error - qo'shib bo'lmadi (message). Worker status/message/machine/readyAt ni yozadi.
 */
export type VoiceClone = {
  version: string; profileName: string; model: string; durationSec: number;
  /** Namunada o'qilgan matn (botdagi tayyor matnlardan): VoiceStudio'ga ref_text bo'lib beriladi. */
  refText: string;
  uploadedAt: string; consentAt: string; consentBy: string;
  status: "pending" | "ready" | "error"; message: string; machine: string | null; readyAt: string | null;
};

/** Asosiy hisob yaratgan qo'shimcha token (mehmon videolari ko'rsatilmaydi). */
export type GuestToken = { id: string; label: string; createdAt: string; telegramUsers: number };

export type PromptKey = "script" | "montage";
export type Prompts = Record<PromptKey, string>;

export type NewTopic = {
  topic: string; notes?: string; publishAt?: string /* "YYYY-MM-DD HH:mm" kanal zonasi bo'yicha */; template?: string; format?: number; voice?: string;
  /** Subtitr (ovozda aytiladigan aniq matn) va title birga beriladi: Gemini chaqirilmaydi. */
  script?: string; title?: string;
  /** /api/audio orqali Storage'ga yuklangan tayyor ovoz yo'li: VoiceStudio chaqirilmaydi (script va title majburiy). */
  audioPath?: string;
};

/** UI va AI bir xil amallardan foydalanadi; AI taklifi foydalanuvchi ruxsat bermaguncha bajarilmaydi. */
export type Action =
  | { type: "create_topics"; topics: NewTopic[] }
  | { type: "update_topic"; id: number; topic?: string; notes?: string; publishAt?: string; template?: string; format?: number; voice?: string; script?: string; title?: string }
  | { type: "delete_topic"; id: number }
  | { type: "publish_now"; id: number }
  | { type: "cancel_publish_now"; id: number }
  | { type: "upload_ready"; id: number }
  | { type: "cancel_upload_ready"; id: number }
  | { type: "update_prompt"; key: PromptKey; text?: string; edits?: { find: string; replace: string }[]; reset?: boolean }
  | { type: "retry"; id: number }
  | { type: "retry_settings"; id: number }
  | { type: "cancel"; id: number }
  | { type: "reschedule"; id: number; publishAt: string }
  | { type: "force_new_upload"; id: number };

export type ActionResult = { ok: boolean; message: string };

export type ChatMessage = { role: "user" | "assistant"; content: string };
export type Proposal = { id: string; action: Action; summary: string; preview?: string; error?: string };

/** Worker ishga tushishidagi resurs va hajm statistikasi (desktop yozadi: worker_runs.stats_json). */
export type RunStats = {
  machine: string; at: string; durationSec: number;
  disk: { name: string; path: string; bytes: number }[]; diskTotalBytes: number;
  tools: { name: string; peakRamMb: number; cpuSeconds: number }[];
  system: { totalRamGb: number; peakUsedRamGb: number; diskRoot: string; freeDiskGbStart: number; freeDiskGbEnd: number };
};

/** Kompyuter holati (heartbeat): ytauto.machines. */
export type CheckItem = { name: string; level: "Ok" | "Warning" | "Failed"; message: string };
export type Machine = {
  name: string;
  /** Oxirgi yurak urishi (ilova, worker yoki Task Scheduler poll). */
  lastSeen: string;
  /** lastSeen yaqinda bo'lsa true: kompyuter yoniq va internetga ulangan. */
  online: boolean;
  app: { open: boolean; seen: string | null };
  worker: { state: "busy" | "idle" | "unknown"; jobId: number | null; detail: string | null; seen: string | null };
  version: string | null;
  /** Botdan "Hozir ishga tushirish" so'rovi yuborilgan, kompyuter hali olmagan. */
  run: { requested: boolean; requestedAt: string | null };
  /** Kerakli dasturlar holati (kompyuter yozadi: shared_settings tools.status.KOMPYUTER). */
  tools: { key: string; name: string; ok: boolean; detail: string }[] | null;
  toolsUpdatedAt: string | null;
  /** stale: tekshiruvdan keyin bot sozlamalari (Gemini, jadval, YouTube) o'zgargan — natija eskirgan. */
  check: { requested: boolean; requestedAt: string | null; finishedAt: string | null; durationSec: number | null; results: CheckItem[]; stale?: boolean };
};

/** Botdan yuklangan shablonlar (ytauto.template_uploads): har kompyuter uchun alohida yozuv. */
export type TemplateUpload = {
  id: number; name: string; kind: "Mp4Reference" | "OpenFilmProject"; machine: string;
  status: "pending" | "downloading" | "importing" | "done" | "error"; progress: number; message: string | null; createdAt: string; size: number;
};
export type PendingTemplate = { name: string; progress: number };
export type PreparedUpload = { groupId: string; path: string; uploadUrl: string };
export type PreparedAudio = { path: string; uploadUrl: string };

/** Shablon (ytauto.templates): "video" - tayyor video avtomatik shablon bo'lgan (fayl yuklanmagan), "upload" - bot orqali yuklangan. */
export type TemplateItem = { machine: string; name: string; kind: string; size: number; addedAt: string | null; source: "video" | "upload"; notes: string };

/** Ovoz ma'lumotlari (kompyuter bo'yicha): VoiceStudio dvigatellari va ovoz profillari. */
export type VoiceEngine = { id: string; displayName: string; available: boolean; usable: boolean; reason: string | null; voices: string[]; installedModels: string[]; knownModels: string[]; supportsCloning: boolean };
export type VoiceInfo = {
  machine: string; fetchedAtUtc: string | null; voiceStudioVersion: string; updatedAtUtc: string;
  engines: VoiceEngine[]; profiles: { name: string; model: string; voice: string; language: string; speed: number }[]; defaultProfile: string;
};

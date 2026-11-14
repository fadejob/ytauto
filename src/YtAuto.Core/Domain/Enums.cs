namespace YtAuto.Core.Domain;

/// <summary>Vazifaning umumiy holati (UI va navbat uchun).</summary>
public enum JobStatus
{
    Queued = 0,
    Scripting = 1,
    GeneratingVoice = 2,
    Aligning = 3,
    Editing = 4,
    Rendering = 5,
    Validating = 6,
    Archived = 7,
    Uploading = 8,
    Processing = 9,
    Scheduled = 10,
    Published = 11,
    Failed = 12,
    NeedsAttention = 13,
    Cancelled = 14,
}

/// <summary>Pipeline bosqichlari. Tartib muhim: resume shu tartibda tekshiradi.</summary>
public enum StageKind
{
    Script = 1,
    Voice = 2,
    Align = 3,
    Edit = 4,
    Render = 5,
    Validate = 6,
    Archive = 7,
    Upload = 8,
}

public enum StageState
{
    Pending = 0,
    Running = 1,
    Done = 2,
    Failed = 3,
    Skipped = 4,
}

/// <summary>Xato toifasi: retry qarorini belgilaydi.</summary>
public enum ErrorCategory
{
    /// <summary>Tarmoq, API limit, vaqtinchalik ishlamay qolish: cheklangan backoff bilan qayta urinish.</summary>
    Transient,
    /// <summary>Noto'g'ri sozlama, yo'q fayl/dastur: darhol tushunarli xato, foydalanuvchi aralashuvi kerak.</summary>
    Configuration,
    /// <summary>Sifat tekshiruvi o'tmadi (tuzatish limiti tugagan).</summary>
    Quality,
    /// <summary>Natija noaniq (masalan, upload holati noma'lum): ko'r-ko'rona takrorlanmaydi.</summary>
    Ambiguous,
    /// <summary>Kutilmagan ichki xato.</summary>
    Internal,
    /// <summary>Foydalanuvchi bekor qildi.</summary>
    Cancelled,
}

public enum VideoFormat
{
    Landscape16x9 = 0,
    Shorts9x16 = 1,
    Square1x1 = 2,
}

public enum TemplateKind
{
    None = 0,
    Mp4Reference = 1,
    OpenFilmProject = 2,
}

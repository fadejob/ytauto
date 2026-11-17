namespace YtAuto.Core.Scheduling;

/// <summary>
/// Jadval bo'yicha bitta ishga tushishda nechta video tayyorlanishi: navbatdagi BARCHA mavzular (chiqish vaqti uzoq bo'lsa ham).
/// Chiqish vaqti (deadline) faqat YouTube uchun: video oldindan tayyorlanib, YouTube'ga private + publishAt bilan yuklanadi va
/// vaqti kelganda YouTube o'zi public qiladi. Navbat chiqish vaqti eng yaqin mavzudan boshlanadi (ish oynasiga hammasi sig'masa,
/// shoshilinchlari birinchi tayyor bo'ladi). Videolar orasidagi tanaffus navbat uzunligiga qarab qisqaradi (<see cref="PauseFor"/>).
/// </summary>
public static class BatchPlanner
{
    /// <summary>Bitta ishga tushishdagi xavfsizlik chegarasi (cheksiz aylanishdan himoya); amalda ish oynasi cheklaydi.</summary>
    public const int MaxVideosPerRun = 50;
    /// <summary>Navbat qisqa bo'lsa (kompyuter dam oladi, YouTube upload limitlariga ham yaxshi).</summary>
    public static readonly TimeSpan PauseBetweenVideos = TimeSpan.FromMinutes(15);
    /// <summary>Navbat uzun bo'lsa: ish oynasiga ko'proq video sig'ishi uchun.</summary>
    public static readonly TimeSpan ShortPauseBetweenVideos = TimeSpan.FromMinutes(3);
    /// <summary>Shundan ko'p mavzu qolgan bo'lsa qisqa tanaffus.</summary>
    public const int LongQueueThreshold = 2;

    /// <summary>Ish oynasi (StartTime-EndTime, kanal vaqt zonasida): ichidagina yangi video boshlanadi. Yarim tundan oshadigan oyna ham qo'llanadi.</summary>
    public static bool InWorkWindow(YtAuto.Core.Domain.ScheduleSettings s, DateTimeOffset nowUtc)
    {
        var tz = TimeZones.Find(s.TimeZoneId);
        var now = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime);
        var start = TimeZones.ParseTime(s.StartTime, new TimeOnly(1, 0));
        var end = TimeZones.ParseTime(s.EndTime, new TimeOnly(4, 0));
        return start <= end ? now >= start && now < end : now >= start || now < end;
    }

    /// <summary>Navbatdagi har bir mavzu uchun video (xavfsizlik chegarasigacha).</summary>
    public static int VideosFor(int pendingTopics) => Math.Clamp(pendingTopics, 0, MaxVideosPerRun);

    /// <summary>Keyingi videodan oldingi tanaffus: navbatda ko'p mavzu qolgan bo'lsa qisqa.</summary>
    public static TimeSpan PauseFor(int remainingTopics) =>
        remainingTopics > LongQueueThreshold ? ShortPauseBetweenVideos : PauseBetweenVideos;

    public static string Describe(int pendingTopics)
    {
        var n = VideosFor(pendingTopics);
        return n switch
        {
            0 => "navbat bo'sh — video tayyorlanmaydi",
            1 => "1 ta video tayyorlanadi",
            _ => $"{n} ta video tayyorlanadi (chiqish vaqti yaqinlari birinchi; ish oynasi tugasa qolgani keyingi kechaga), " +
                 $"orasida {PauseFor(n - 1).TotalMinutes:0} daqiqa tanaffus",
        };
    }
}

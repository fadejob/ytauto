namespace YtAuto.Core.Scheduling;

/// <summary>
/// Jadval bo'yicha bitta ishga tushishda nechta video tayyorlanishi: navbatdagi BARCHA mavzular (chiqish vaqti uzoq bo'lsa ham).
/// Chiqish vaqti (deadline) faqat YouTube uchun: video oldindan tayyorlanib, YouTube'ga private + publishAt bilan yuklanadi va
/// vaqti kelganda YouTube o'zi public qiladi. Navbat chiqish vaqti eng yaqin mavzudan boshlanadi (ish oynasiga hammasi sig'masa,
/// shoshilinchlari birinchi tayyor bo'ladi). Har video tayyor bo'lgach worker <see cref="PauseBetweenVideos"/> (15 daqiqa) dam oladi —
/// kompyuter sovib oladi — so'ng keyingi videoni boshlaydi (navbat uzunligidan qat'i nazar).
/// </summary>
public static class BatchPlanner
{
    /// <summary>Bitta ishga tushishdagi xavfsizlik chegarasi (cheksiz aylanishdan himoya); amalda ish oynasi cheklaydi.</summary>
    public const int MaxVideosPerRun = 50;
    /// <summary>Har videodan keyingi tanaffus: kompyuter sovib oladi (YouTube upload limitlariga ham yaxshi).</summary>
    public static readonly TimeSpan PauseBetweenVideos = TimeSpan.FromMinutes(15);

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

    /// <summary>Keyingi videodan oldingi tanaffus: doim 15 daqiqa (navbatda nechta mavzu qolganidan qat'i nazar).</summary>
    public static TimeSpan PauseFor(int remainingTopics) => PauseBetweenVideos;

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

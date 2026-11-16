namespace YtAuto.Core.Scheduling;

/// <summary>
/// Jadval bo'yicha bitta ishga tushishda nechta video tayyorlanishi (navbatdagi mavzular soniga qarab):
/// 1-2 mavzu - 1 video; 3-4 mavzu - 2 video; 5 va undan ko'p - 3 video (eng ko'pi 3: 7 dan ortiq bo'lsa ham 4-5 video qilinmaydi).
/// Videolar orasida <see cref="PauseBetweenVideos"/> tanaffus qilinadi (kompyuter dam oladi, YouTube upload limitlariga ham yaxshi).
/// </summary>
public static class BatchPlanner
{
    public const int MaxVideosPerRun = 3;
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

    public static int VideosFor(int pendingTopics) => pendingTopics switch
    {
        <= 0 => 0,
        <= 2 => 1,
        <= 4 => 2,
        _ => MaxVideosPerRun,
    };

    public static string Describe(int pendingTopics)
    {
        var n = VideosFor(pendingTopics);
        return n switch
        {
            0 => "navbat bo'sh — video tayyorlanmaydi",
            1 => "1 ta video tayyorlanadi",
            _ => $"{n} ta video tayyorlanadi, orasida {PauseBetweenVideos.TotalMinutes:0} daqiqa tanaffus",
        };
    }
}

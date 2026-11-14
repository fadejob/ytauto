using YtAuto.Core.Domain;

namespace YtAuto.Core.Scheduling;

public static class TimeZones
{
    /// <summary>IANA yoki Windows ID'ni qabul qiladi ("Asia/Tashkent" yoki "West Asia Standard Time").</summary>
    public static TimeZoneInfo Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win);
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) return TimeZoneInfo.FindSystemTimeZoneById(iana);
            throw;
        }
    }

    public static TimeOnly ParseTime(string hhmm, TimeOnly fallback)
        => TimeOnly.TryParseExact(hhmm?.Trim() ?? "", new[] { "HH:mm", "H:mm" }, out var t) ? t : fallback;

    /// <summary>Zona bo'yicha sana + vaqtni UTC'ga o'tkazadi.</summary>
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static DateTimeOffset ToZone(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz);
}

/// <summary>
/// Yangi mavzu uchun YouTube chiqish sanasini taklif qiladi:
/// oxirgi tegishli (bekor qilinmagan) rejalashtirilgan vazifa sanasi + 2 kalendar kun;
/// hisoblangan sana o'tgan bo'lsa yoki oldingi vazifa bo'lmasa - bugun.
/// </summary>
public static class PublishDateSuggester
{
    public const int GapDays = 2;

    public static DateOnly SuggestDate(IEnumerable<(JobStatus Status, DateTimeOffset PublishAtUtc)> existing,
        DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var today = DateOnly.FromDateTime(TimeZones.ToZone(nowUtc, tz).DateTime);
        DateOnly? last = null;
        foreach (var (status, at) in existing)
        {
            if (status == JobStatus.Cancelled) continue;
            var d = DateOnly.FromDateTime(TimeZones.ToZone(at, tz).DateTime);
            if (last is null || d > last) last = d;
        }
        if (last is null) return today;
        var candidate = last.Value.AddDays(GapDays);
        return candidate < today ? today : candidate;
    }

    public static DateTimeOffset SuggestUtc(IEnumerable<(JobStatus Status, DateTimeOffset PublishAtUtc)> existing,
        DateTimeOffset nowUtc, YouTubeSettings yt)
    {
        var tz = TimeZones.Find(yt.TimeZoneId);
        var date = SuggestDate(existing, nowUtc, tz);
        var time = TimeZones.ParseTime(yt.PublishTime, new TimeOnly(18, 0));
        return TimeZones.ToUtc(date, time, tz);
    }
}

using YtAuto.Core.Domain;

namespace YtAuto.Infrastructure.YouTube;

/// <summary>
/// YouTube'ning standart video kategoriyalari (videoga biriktirish mumkin bo'lganlari). Kanal ulangan bo'lsa,
/// aniq ro'yxat videoCategories.list orqali olinadi; bu - API'ga ulanmasdan ham nom bilan tanlash uchun zaxira.
/// </summary>
public static class YouTubeCategories
{
    public static readonly IReadOnlyList<CategoryItem> Fallback = new List<CategoryItem>
    {
        new() { Id = "27", Title = "Ta'lim (Education)" },
        new() { Id = "28", Title = "Fan va texnologiya (Science & Technology)" },
        new() { Id = "26", Title = "Qo'llanma va uslub (Howto & Style)" },
        new() { Id = "22", Title = "Odamlar va bloglar (People & Blogs)" },
        new() { Id = "24", Title = "Ko'ngilochar (Entertainment)" },
        new() { Id = "25", Title = "Yangiliklar va siyosat (News & Politics)" },
        new() { Id = "1", Title = "Film va animatsiya (Film & Animation)" },
        new() { Id = "10", Title = "Musiqa (Music)" },
        new() { Id = "15", Title = "Uy hayvonlari va hayvonlar (Pets & Animals)" },
        new() { Id = "17", Title = "Sport (Sports)" },
        new() { Id = "19", Title = "Sayohat va tadbirlar (Travel & Events)" },
        new() { Id = "20", Title = "O'yinlar (Gaming)" },
        new() { Id = "23", Title = "Komediya (Comedy)" },
        new() { Id = "2", Title = "Avtomobillar (Autos & Vehicles)" },
        new() { Id = "29", Title = "Notijorat va faollik (Nonprofits & Activism)" },
    };
}

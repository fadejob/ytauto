namespace YtAuto.Core.Files;

/// <summary>
/// Fayl operatsiyalari faqat ruxsat etilgan ildiz ichida bajarilishini kafolatlaydi.
/// Symlink/junction (reparse point) orqali tashqariga chiqishga yo'l qo'yilmaydi.
/// </summary>
public static class PathGuard
{
    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsUnder(string root, string candidate)
    {
        var r = Full(root) + Path.DirectorySeparatorChar;
        var c = Full(candidate) + Path.DirectorySeparatorChar;
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase) && !string.Equals(r, c, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureUnder(string root, string candidate)
    {
        if (!IsUnder(root, candidate))
            throw new UnauthorizedAccessException($"Yo'l ruxsat etilgan papkadan tashqarida: {candidate} (ildiz: {root})");
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>
    /// Papkani xavfsiz o'chiradi: faqat <paramref name="root"/> ichida, reparse point'larga kirmaydi
    /// (link o'zi o'chiriladi, nishoni emas). Ildizning o'zi va undan yuqorisi hech qachon o'chirilmaydi.
    /// </summary>
    public static void SafeDeleteDirectory(string root, string dir, Action<string>? log = null)
    {
        EnsureUnder(root, dir);
        if (!Directory.Exists(dir)) return;
        // Yo'lning har bir bo'g'ini (root dan dir gacha) link emasligini tekshiramiz.
        var full = Full(dir); var rootFull = Full(root);
        var p = full;
        while (!string.Equals(p, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(p) && !string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Yo'lda symlink/junction bor, tozalash to'xtatildi: {p}");
            p = Path.GetDirectoryName(p) ?? rootFull;
        }
        if (IsReparsePoint(full)) { log?.Invoke($"link o'chirildi (nishoni emas): {full}"); Directory.Delete(full, false); return; }
        DeleteContents(full, log);
        Directory.Delete(full, false);
    }

    private static void DeleteContents(string dir, Action<string>? log)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Linkning o'zini olib tashlaymiz, ichiga kirmaymiz.
                if (entry is DirectoryInfo d) d.Delete(false); else entry.Delete();
                log?.Invoke($"link o'chirildi (nishoni emas): {entry.FullName}");
                continue;
            }
            if (entry is DirectoryInfo sub) { DeleteContents(sub.FullName, log); sub.Delete(false); }
            else
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
    }

    /// <summary>Papkani nusxalaydi (reparse point'larni o'tkazib yuboradi). Asl papka o'zgartirilmaydi.</summary>
    public static void CopyDirectory(string source, string dest, Func<string, bool>? include = null)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (include is not null && !include(entry.FullName)) continue;
            var target = Path.Combine(dest, entry.Name);
            if (entry is DirectoryInfo d) CopyDirectory(d.FullName, target, include);
            else File.Copy(entry.FullName, target, overwrite: true);
        }
    }
}

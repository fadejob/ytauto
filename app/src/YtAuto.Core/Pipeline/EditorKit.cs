using System.Reflection;
using System.Text;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Claude sahifalarida ishlatiladigan tayyor harakat/kompozitsiya to'plami (film/kit/: kit.js, kit.css, README.md). Exe ichida
/// joylangan; har montajda film/ ga yoziladi (Claude uni o'zgartirmaydi, faqat import qiladi).
/// </summary>
public static class EditorKit
{
    public const string Dir = "kit";
    private const string Prefix = "YtAuto.Kit.";

    /// <summary>Kit fayllari (kit/ ga nisbiy yo'l, "/" bilan): kit.js, kit.css, model3d.js, three/... .</summary>
    public static IReadOnlyList<string> Files =>
        typeof(EditorKit).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..].Replace('\\', '/')).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static Stream Open(string rel) =>
        typeof(EditorKit).Assembly.GetManifestResourceStream(Prefix + rel.Replace('/', '\\'))
        ?? typeof(EditorKit).Assembly.GetManifestResourceStream(Prefix + rel)
        ?? throw new InvalidOperationException("Kit fayli exe ichida topilmadi: " + rel);

    public static string Read(string rel)
    {
        using var r = new StreamReader(Open(rel), Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static void WriteTo(string filmDir)
    {
        foreach (var rel in Files)
        {
            var path = Path.Combine(filmDir, Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var src = Open(rel);
            using var dst = File.Create(path);
            src.CopyTo(dst);
        }
    }
}

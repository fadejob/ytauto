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
    private static readonly string[] Files = { "kit.js", "kit.css", "README.md" };

    public static string Read(string name)
    {
        var asm = typeof(EditorKit).Assembly;
        using var s = asm.GetManifestResourceStream("YtAuto.Kit." + name)
            ?? throw new InvalidOperationException("Kit fayli exe ichida topilmadi: " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static void WriteTo(string filmDir)
    {
        var dir = Path.Combine(filmDir, Dir);
        Directory.CreateDirectory(dir);
        foreach (var f in Files) File.WriteAllText(Path.Combine(dir, f), Read(f), new UTF8Encoding(false));
    }
}

using System.Security.Cryptography;
using System.Text;
using YtAuto.Core.Domain;

namespace YtAuto.Core.Files;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string Sha256Text(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p ?? "<null>").Append('␞');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..32];
    }

    /// <summary>Papka tarkibi "versiyasi": nisbiy yo'l + o'lcham + o'zgarish vaqti (fayllarni o'qimasdan).</summary>
    public static string DirectoryManifest(string dir, Func<string, bool>? include = null)
    {
        if (!Directory.Exists(dir)) return "missing";
        var entries = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => include is null || include(f))
            .Select(f => { var fi = new FileInfo(f); return $"{Path.GetRelativePath(dir, f).Replace('\\', '/')}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"; })
            .OrderBy(x => x, StringComparer.Ordinal);
        return Sha256Text(entries.ToArray());
    }
}

public static class FileOps
{
    /// <summary>
    /// Stream bilan nusxalaydi (.partial ga yozib, keyin nomini o'zgartiradi), so'ng o'lcham va SHA-256 ni solishtiradi.
    /// Mavjud boshqa faylni ustidan yozmaydi (agar u bir xil bo'lmasa, nom o'zgartiriladi).
    /// </summary>
    public static async Task<OutputFile> CopyVerifiedAsync(string source, string destination, string role, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var srcHash = Hashing.Sha256File(source);
        if (File.Exists(destination))
        {
            if (Hashing.Sha256File(destination) == srcHash)
                return new OutputFile { Role = role, Path = destination, Size = new FileInfo(destination).Length, Sha256 = srcHash };
            destination = UniquePath(destination);
        }
        var partial = destination + ".partial";
        await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await src.CopyToAsync(dst, 1 << 20, ct).ConfigureAwait(false);
            await dst.FlushAsync(ct).ConfigureAwait(false);
        }
        var dstHash = Hashing.Sha256File(partial);
        if (dstHash != srcHash || new FileInfo(partial).Length != new FileInfo(source).Length)
        {
            File.Delete(partial);
            throw new IOException($"Nusxa tekshiruvdan o'tmadi (SHA-256 farq qiladi): {destination}");
        }
        File.Move(partial, destination, overwrite: false);
        return new OutputFile { Role = role, Path = destination, Size = new FileInfo(destination).Length, Sha256 = dstHash };
    }

    public static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static OutputFile Describe(string path, string role, bool hash = true)
    {
        var fi = new FileInfo(path);
        return new OutputFile { Role = role, Path = fi.FullName, Size = fi.Length, Sha256 = hash ? Hashing.Sha256File(path) : null };
    }

    /// <summary>Resume uchun: fayl mavjud, o'lchami va (bo'lsa) hash'i mos.</summary>
    public static bool StillValid(OutputFile f, out string reason)
    {
        reason = "";
        if (!File.Exists(f.Path)) { reason = $"fayl yo'q: {f.Path}"; return false; }
        var len = new FileInfo(f.Path).Length;
        if (len != f.Size) { reason = $"o'lcham o'zgargan: {f.Path} ({len} != {f.Size})"; return false; }
        if (f.Sha256 is not null && len < 64L * 1024 * 1024 && Hashing.Sha256File(f.Path) != f.Sha256) { reason = $"tarkib o'zgargan: {f.Path}"; return false; }
        return true;
    }

    /// <summary>Faylning oxirgi qismini o'qiydi (katta loglarni to'liq yuklamaslik uchun).</summary>
    public static string ReadTail(string path, int maxBytes = 64 * 1024)
    {
        if (!File.Exists(path)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        var start = Math.Max(0, len - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[len - start];
        int read = 0; while (read < buf.Length) { var n = fs.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
        var s = Encoding.UTF8.GetString(buf, 0, read);
        if (start > 0) { var nl = s.IndexOf('\n'); if (nl >= 0) s = s[(nl + 1)..]; }
        return s;
    }

    public static string ReadHead(string path, int maxBytes = 256 * 1024)
    {
        if (!File.Exists(path)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[Math.Min(maxBytes, fs.Length)];
        int read = 0; while (read < buf.Length) { var n = fs.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    public static void WriteAllTextAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

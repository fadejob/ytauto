using System.Security.Cryptography;
using System.Text;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Security;

/// <summary>
/// Sirlar Windows DPAPI (CurrentUser) bilan shifrlangan fayllarda: faqat shu Windows foydalanuvchisi o'qiy oladi.
/// Har sir - alohida fayl; nom xeshlangan (fayl nomidan mazmun bilinmaydi).
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _dir;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YtAuto.v1.secret-store");

    public DpapiSecretStore(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

    private string FileFor(string name) =>
        Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..24].ToLowerInvariant() + ".bin");

    public string? Get(string name)
    {
        var f = FileFor(name);
        if (!File.Exists(f)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(f), Entropy, DataProtectionScope.CurrentUser);
            var s = Encoding.UTF8.GetString(plain);
            Redactor.Register(s);
            return s;
        }
        catch (CryptographicException) { return null; }
    }

    public void Set(string name, string? value)
    {
        var f = FileFor(name);
        if (string.IsNullOrEmpty(value)) { if (File.Exists(f)) File.Delete(f); return; }
        Redactor.Register(value);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var tmp = f + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, f, true);
    }
}

public sealed class ResourceMonitor : IResourceMonitor
{
    public double AvailableRamGb() => Processes.Native.Memory().Avail / 1073741824.0;
    public double TotalRamGb() => Processes.Native.Memory().Total / 1073741824.0;
    public double FreeDiskGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
        }
        catch { return 0; }
    }
}

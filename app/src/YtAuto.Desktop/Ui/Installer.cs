using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace YtAuto.Desktop.Ui;

/// <summary>
/// Birinchi ochilishda o'rnatish: yuklab olingan YtAuto.exe (masalan Downloads'dan) ochilganda foydalanuvchi doimiy papkani tanlaydi,
/// exe o'sha yerga nusxalanadi, ish stoli va Start menyusida yorliq yaratiladi va dastur o'sha joydan qayta ochiladi.
/// Shu tufayli Task Scheduler vazifalari doim doimiy joydagi exe'ga bog'lanadi. Yangi versiya yuklansa - xuddi shu oyna orqali yangilanadi.
/// </summary>
public static class Installer
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string MarkerPath => Path.Combine(LocalAppData, "YtAuto", "install.json");
    public static string DefaultDir => Path.Combine(LocalAppData, "Programs", "YtAuto");

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string? InstalledExe()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var p = JsonDocument.Parse(File.ReadAllText(MarkerPath)).RootElement.GetProperty("exe").GetString();
            return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>Dasturchi yig'masi (bin\Debug, bin\Release) - o'rnatish so'ralmaydi.</summary>
    private static bool IsDevBuild(string exe) =>
        exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /// <summary>true - joriy jarayon yopilishi kerak (o'rnatilgan nusxa ishga tushirildi yoki foydalanuvchi bekor qildi).</summary>
    public static bool OfferIfNeeded()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || IsDevBuild(self)) return false;
        var installed = InstalledExe();
        if (installed is not null && Same(installed, self)) return false;
        // Marker yo'q, lekin exe allaqachon doimiy papkada ishlayapti (qo'lda nusxalangan): shu joy o'rnatilgan deb yoziladi.
        if (installed is null && Same(Path.GetDirectoryName(self)!, DefaultDir)) { WriteMarker(self); return false; }

        var dlg = new InstallWindow(installed is not null ? Path.GetDirectoryName(installed)! : DefaultDir, update: installed is not null);
        if (dlg.ShowDialog() != true) return true; // bekor qilindi
        return Install(self, dlg.TargetDir, dlg.DesktopShortcut);
    }

    private static bool Install(string self, string dir, bool desktopShortcut)
    {
        try
        {
            var target = Path.Combine(dir, "YtAuto.exe");
            if (Same(target, self)) { WriteMarker(self); return false; }
            // O'rnatilgan nusxa ishlayotgan bo'lsa (masalan worker video tayyorlayapti) - majburan to'xtatilmaydi.
            if (File.Exists(target) && Process.GetProcessesByName("YtAuto").Any(p => { try { return Same(p.MainModule?.FileName ?? "", target); } catch { return false; } }))
            {
                MessageBox.Show("O'rnatilgan YtAuto hozir ishlayapti (oyna yoki fondagi worker, ehtimol video tayyorlanmoqda).\n\nU tugagach (yoki oynani yopib) qayta urinib ko'ring.",
                    "YtAuto — o'rnatish", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
            Directory.CreateDirectory(dir);
            File.Copy(self, target, overwrite: true);
            WriteMarker(target);
            TryShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "YtAuto.lnk"), target);
            if (desktopShortcut) TryShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "YtAuto.lnk"), target);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = dir });
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show($"Bu papkaga yozish uchun ruxsat yo'q:\n{dir}\n\nBoshqa papkani tanlang (masalan standart: {DefaultDir}).", "YtAuto — o'rnatish", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show("O'rnatib bo'lmadi: " + e.Message, "YtAuto — o'rnatish", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
    }

    private static void WriteMarker(string exe)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new { exe, installedAtUtc = DateTimeOffset.UtcNow }));
        }
        catch { }
    }

    private static void TryShortcut(string lnkPath, string target)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.Description = "YtAuto — sozlamalar";
            lnk.Save();
        }
        catch { /* yorliq ixtiyoriy */ }
    }
}

/// <summary>O'rnatish papkasini tanlash oynasi (kod bilan quriladi, XAML'siz).</summary>
public sealed class InstallWindow : Window
{
    private readonly TextBox _path;
    private readonly CheckBox _desktop;
    public string TargetDir => _path.Text.Trim();
    public bool DesktopShortcut => _desktop.IsChecked == true;

    public InstallWindow(string dir, bool update)
    {
        Title = update ? "YtAuto — yangilash" : "YtAuto — o'rnatish";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = update ? "YtAuto'ning yangi versiyasi" : "YtAuto'ni o'rnatish", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = update
                ? "O'rnatilgan nusxa shu papkada. «Yangilash» uni yangi versiya bilan almashtiradi (sozlamalar, baza va arxiv saqlanadi)."
                : "Dastur doimiy papkada turishi kerak: fondagi worker (Task Scheduler) shu joydagi exe'ni ishga tushiradi. Keyin papkani o'zgartirmang.",
        });
        root.Children.Add(new TextBlock { Text = "O'rnatish papkasi", Margin = new Thickness(0, 0, 0, 4) });
        var row = new DockPanel();
        var browse = new Button { Content = "Tanlash…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        _path = new TextBox { Text = dir, Padding = new Thickness(4) };
        row.Children.Add(browse); row.Children.Add(_path);
        root.Children.Add(row);
        browse.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "YtAuto qaysi papkaga o'rnatilsin?" };
            if (d.ShowDialog(this) == true) _path.Text = Path.Combine(d.FolderName, "YtAuto");
        };
        _desktop = new CheckBox { Content = "Ish stolida yorliq yaratish", IsChecked = !update, Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Add(_desktop);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = update ? "Yangilash" : "O'rnatish", IsDefault = true, Padding = new Thickness(16, 6, 16, 6) };
        var cancel = new Button { Content = "Bekor qilish", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(TargetDir) || !Path.IsPathFullyQualified(TargetDir))
            { MessageBox.Show(this, "To'liq papka yo'lini kiriting (masalan C:\\Programs\\YtAuto).", Title); return; }
            DialogResult = true;
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }
}

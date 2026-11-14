using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YtAuto.Core.Domain;

namespace YtAuto.Desktop.Ui;

public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();
    private static SolidColorBrush B(string hex)
    {
        if (!Cache.TryGetValue(hex, out var b)) { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); Cache[hex] = b; }
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is JobStatus s ? s switch
    {
        JobStatus.Published => B("#067647"),
        JobStatus.Scheduled => B("#2563EB"),
        JobStatus.Processing or JobStatus.Uploading => B("#7C3AED"),
        JobStatus.NeedsAttention => B("#B42318"),
        JobStatus.Failed => B("#DC6803"),
        JobStatus.Cancelled => B("#9CA3AF"),
        JobStatus.Queued => B("#4B5563"),
        _ => B("#0E7490"),
    } : B("#4B5563");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Thumbnail'ni faqat kerak bo'lganda, kichraytirilgan holda (DecodePixelWidth) yuklaydi va faylni bloklamaydi.
/// Virtualizatsiya bilan birga: ekranda ko'rinmagan kartalar uchun rasm yuklanmaydi.
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = parameter is string p && int.TryParse(p, out var w) ? w : 192;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : false;
}

public sealed class InverseBoolVis : IValueConverter
{
    public static readonly InverseBoolVis Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class EnumLists
{
    public static readonly IReadOnlyList<TemplateKind> TemplateKinds = Enum.GetValues<TemplateKind>();
}
